using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace LightSvnBrowser
{
    [DataContract]
    public sealed class Repository
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Group { get; set; }
        [DataMember] public string Url { get; set; }
        [DataMember] public string Username { get; set; }
        [DataMember] public string ProtectedPassword { get; set; }
        [DataMember] public bool AllowIpCertificateNameMismatch { get; set; }
    }

    [DataContract]
    public sealed class Favorite
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string RepositoryId { get; set; }
        [DataMember] public string Path { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Tags { get; set; }
        [DataMember] public string Note { get; set; }
        [DataMember] public bool IsDirectory { get; set; }
    }

    public static class UiFontSettings
    {
        public const float DefaultSize = 9F;
        public const float MinimumSize = 8F;
        public const float MaximumSize = 16F;
    }

    [DataContract]
    public sealed class AppConfig
    {
        public AppConfig() { SetDefaults(); }
        [DataMember] public List<Repository> Repositories { get; set; }
        [DataMember] public List<Favorite> Favorites { get; set; }
        [DataMember] public int CacheLimitMb { get; set; }
        [DataMember] public int CacheDays { get; set; }
        [DataMember] public string SvnExecutable { get; set; }
        [DataMember] public float UiFontSize { get; set; }
        [OnDeserializing] private void BeforeDeserialize(StreamingContext context) { SetDefaults(); }
        private void SetDefaults()
        {
            Repositories = new List<Repository>();
            Favorites = new List<Favorite>();
            CacheLimitMb = 256;
            CacheDays = 7;
            SvnExecutable = String.Empty;
            UiFontSize = UiFontSettings.DefaultSize;
        }
    }

    [DataContract]
    public sealed class SvnEntry
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Path { get; set; }
        [DataMember] public string Revision { get; set; }
        [DataMember] public string Author { get; set; }
        [DataMember] public bool IsDirectory { get; set; }
        [DataMember] public long Size { get; set; }
        [DataMember] public DateTime ModifiedUtc { get; set; }
    }

    [DataContract]
    public sealed class IndexSnapshot
    {
        [DataMember] public string RepositoryId { get; set; }
        [DataMember] public string Revision { get; set; }
        [DataMember] public DateTime UpdatedUtc { get; set; }
        [DataMember] public List<SvnEntry> Entries { get; set; }
    }

    public sealed class AppStore
    {
        private const int MaxConfigBytes = 16 * 1024 * 1024;
        private const long MaxIndexBytes = 128L * 1024L * 1024L;
        private const int MaxLogBytes = 1024 * 1024;
        private const int MaxLogFiles = 8;
        private const int MaxCacheMb = 16384;
        private const int MaxCacheDays = 365;
        private const int MaxLoadedIndexes = 2;
        private readonly object _gate = new object();
        private readonly object _indexReadGate = new object();
        private readonly Dictionary<string, LoadedIndex> _loadedIndexes = new Dictionary<string, LoadedIndex>(StringComparer.Ordinal);
        private long _indexAccessCounter;
        private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("LightSvnBrowser.Password.v1");
        public string DataDirectory { get; private set; }
        public string CacheDirectory { get; private set; }
        public string IndexDirectory { get; private set; }
        public string LogDirectory { get; private set; }
        public string SvnConfigDirectory { get; private set; }
        public AppConfig Config { get; private set; }

        private sealed class LoadedIndex
        {
            public DateTime LastWriteUtc;
            public long Length;
            public long Access;
            public IndexSnapshot Snapshot;
        }

        public AppStore(string dataDir)
        {
            if (String.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("请选择数据保存目录。", "dataDir");
            DataDirectory = System.IO.Path.GetFullPath(dataDir);
            CacheDirectory = System.IO.Path.Combine(DataDirectory, "cache");
            IndexDirectory = System.IO.Path.Combine(DataDirectory, "indexes");
            LogDirectory = System.IO.Path.Combine(DataDirectory, "logs");
            SvnConfigDirectory = System.IO.Path.Combine(DataDirectory, "svn-config");
            foreach (string directory in new[] { DataDirectory, CacheDirectory, IndexDirectory, LogDirectory, SvnConfigDirectory,
                System.IO.Path.Combine(DataDirectory, "temp") }) Directory.CreateDirectory(directory);
            string configPath = System.IO.Path.Combine(DataDirectory, "settings.json");
            try
            {
                if (File.Exists(configPath) && new FileInfo(configPath).Length > MaxConfigBytes)
                    throw new InvalidDataException("配置文件过大。");
                Config = File.Exists(configPath) ? ReadJson<AppConfig>(configPath) : new AppConfig();
                ValidateConfig();
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("无法读取配置文件 " + configPath + "。原文件未覆盖；请检查文件，或从 settings.json.bak 恢复。", ex);
            }
            File.WriteAllText(System.IO.Path.Combine(SvnConfigDirectory, "config"),
                "[auth]\r\nstore-passwords = no\r\nstore-auth-creds = no\r\n", new UTF8Encoding(false));
            File.WriteAllText(System.IO.Path.Combine(SvnConfigDirectory, "servers"),
                "[global]\r\nstore-passwords = no\r\nstore-plaintext-passwords = no\r\nstore-auth-creds = no\r\n", new UTF8Encoding(false));
            CleanupCache();
        }

        public void Save()
        {
            lock (_gate)
            {
                ValidateConfig();
                WriteJsonAtomic(System.IO.Path.Combine(DataDirectory, "settings.json"), Config);
            }
        }

        private void ValidateConfig()
        {
            if (Config == null || Config.Repositories == null || Config.Favorites == null)
                throw new InvalidDataException("配置中的仓库或收藏列表无效。");
            if (Single.IsNaN(Config.UiFontSize) || Single.IsInfinity(Config.UiFontSize) || Config.UiFontSize < UiFontSettings.MinimumSize || Config.UiFontSize > UiFontSettings.MaximumSize)
                throw new InvalidDataException("界面字号须为 8 至 16 磅的有效数值。");
            if (Config.CacheLimitMb < 1 || Config.CacheLimitMb > MaxCacheMb || Config.CacheDays < 1 || Config.CacheDays > MaxCacheDays)
                throw new InvalidDataException("缓存容量须为 1 至 16384 MB，保留天数须为 1 至 365 天。");
            HashSet<string> identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (Repository repository in Config.Repositories)
            {
                if (repository == null || String.IsNullOrWhiteSpace(repository.Id) || !identifiers.Add(repository.Id))
                    throw new InvalidDataException("仓库标识不能为空或重复。");
                SvnService.BuildUrl(repository, String.Empty);
            }
        }

        public string Protect(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            try { return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, PasswordEntropy, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        public string Unprotect(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            byte[] bytes = null;
            try
            {
                const string prefix = "dpapi:";
                if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException();
                bytes = ProtectedData.Unprotect(Convert.FromBase64String(value.Substring(prefix.Length)), PasswordEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) { throw new InvalidDataException("无法解密 SVN 密码，请在当前 Windows 用户下重新输入。", ex); }
            finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        }

        public IndexSnapshot LoadIndex(string repoId)
        {
            string path = IndexPath(repoId);
            lock (_indexReadGate)
            {
                try
                {
                    FileInfo before = new FileInfo(path);
                    lock (_gate)
                    {
                        if (!before.Exists) { _loadedIndexes.Remove(repoId); return null; }
                        LoadedIndex loaded;
                        if (_loadedIndexes.TryGetValue(repoId, out loaded) && loaded.LastWriteUtc == before.LastWriteTimeUtc && loaded.Length == before.Length)
                        {
                            loaded.Access = ++_indexAccessCounter;
                            return loaded.Snapshot;
                        }
                        _loadedIndexes.Remove(repoId);
                    }
                    if (before.Length > MaxIndexBytes) throw new InvalidDataException("文件名索引超过 128 MB 上限。");
                    IndexSnapshot snapshot;
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        snapshot = (IndexSnapshot)new DataContractJsonSerializer(typeof(IndexSnapshot)).ReadObject(stream);
                    if (snapshot == null || snapshot.Entries == null || !String.Equals(snapshot.RepositoryId, repoId, StringComparison.Ordinal))
                        throw new InvalidDataException("索引内容与仓库不匹配。");
                    FileInfo after = new FileInfo(path);
                    if (!after.Exists || before.LastWriteTimeUtc != after.LastWriteTimeUtc || before.Length != after.Length)
                        throw new IOException("索引在读取期间发生变化，请重新搜索。");
                    lock (_gate) { RememberIndex(repoId, snapshot, after); }
                    return snapshot;
                }
                catch (Exception ex)
                {
                    lock (_gate) { _loadedIndexes.Remove(repoId); }
                    throw new InvalidDataException("无法读取文件名索引，请重新建立索引。原文件未覆盖：" + path, ex);
                }
            }
        }

        public DateTime? GetIndexUpdatedUtc(string repoId)
        {
            FileInfo file = new FileInfo(IndexPath(repoId));
            return file.Exists ? (DateTime?)file.LastWriteTimeUtc : null;
        }

        private void RememberIndex(string repoId, IndexSnapshot snapshot, FileInfo file)
        {
            _loadedIndexes[repoId] = new LoadedIndex { Snapshot = snapshot, LastWriteUtc = file.LastWriteTimeUtc, Length = file.Length, Access = ++_indexAccessCounter };
            if (_loadedIndexes.Count > MaxLoadedIndexes)
                _loadedIndexes.Remove(_loadedIndexes.OrderBy(pair => pair.Value.Access).First().Key);
        }

        public void SaveIndex(IndexSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Entries == null || String.IsNullOrWhiteSpace(snapshot.RepositoryId))
                throw new ArgumentException("索引快照不完整。", "snapshot");
            lock (_indexReadGate)
            {
                string path = IndexPath(snapshot.RepositoryId);
                WriteJsonAtomic(path, snapshot);
                lock (_gate) { RememberIndex(snapshot.RepositoryId, snapshot, new FileInfo(path)); }
            }
            Log("index_saved", "repository=" + snapshot.RepositoryId + " entries=" + snapshot.Entries.Count.ToString(CultureInfo.InvariantCulture));
        }

        internal bool MergeAddedIndexEntries(string repositoryId, long revision, string directory, List<SvnEntry> roots, List<SvnEntry> additions)
        {
            lock (_indexReadGate)
            {
                IndexSnapshot snapshot = LoadIndex(repositoryId);
                if (snapshot == null) return false;
                if (ReadIndexRevision(snapshot.Revision) >= revision) return true;

                HashSet<string> newerPaths = new HashSet<string>(snapshot.Entries
                    .Where(entry => entry != null && ReadIndexRevision(entry.Revision) > revision)
                    .Select(entry => entry.Path), StringComparer.Ordinal);
                HashSet<string> affectedRoots = new HashSet<string>(roots
                    .Where(entry => !HasIndexedAncestor(entry.Path, newerPaths))
                    .Select(entry => entry.Path), StringComparer.Ordinal);
                if (affectedRoots.Count == 0) return true;

                string prefix = directory.Length == 0 ? String.Empty : directory + "/";
                List<SvnEntry> merged = snapshot.Entries.Where(entry => entry == null || newerPaths.Contains(entry.Path) ||
                    !IsUnderAddedRoot(entry.Path, prefix, affectedRoots)).ToList();
                merged.AddRange(additions.Where(entry => !HasIndexedAncestor(entry.Path, newerPaths) &&
                    IsUnderAddedRoot(entry.Path, prefix, affectedRoots)));
                SaveIndex(new IndexSnapshot
                {
                    RepositoryId = repositoryId, Revision = snapshot.Revision, UpdatedUtc = DateTime.UtcNow, Entries = merged
                });
                Log("index_additions_merged", "repository=" + repositoryId + " revision=" + revision + " roots=" + affectedRoots.Count);
                return true;
            }
        }

        private static long ReadIndexRevision(string revision)
        {
            long number;
            return Int64.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : 0L;
        }

        private static bool HasIndexedAncestor(string path, HashSet<string> paths)
        {
            while (!paths.Contains(path))
            {
                int separator = path.LastIndexOf('/');
                if (separator < 0) return false;
                path = path.Substring(0, separator);
            }
            return true;
        }

        private static bool IsUnderAddedRoot(string path, string prefix, HashSet<string> roots)
        {
            if (String.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            int separator = path.IndexOf('/', prefix.Length);
            return roots.Contains(separator < 0 ? path : path.Substring(0, separator));
        }

        public void DeleteIndex(string repoId)
        {
            lock (_indexReadGate)
            {
                string path = IndexPath(repoId);
                try { DeleteFile(path); DeleteFile(path + ".bak"); }
                finally { lock (_gate) { _loadedIndexes.Remove(repoId); } }
            }
        }

        public void CleanupCache()
        {
            lock (_gate)
            {
                try
                {
                    DateTime expiredBefore = DateTime.UtcNow.AddDays(-Config.CacheDays);
                    List<FileInfo> files = new DirectoryInfo(CacheDirectory).GetFiles().ToList();
                    foreach (FileInfo file in files.Where(f => f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) TryDeleteFile(file.FullName);
                    }
                    files = files.Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (FileInfo file in files.Where(f => f.LastAccessTimeUtc < expiredBefore)) TryDeleteFile(file.FullName);
                    files = files.Where(f => File.Exists(f.FullName)).OrderBy(f => f.LastAccessTimeUtc).ToList();
                    long total = files.Sum(f => f.Length);
                    long limit = (long)Config.CacheLimitMb * 1024L * 1024L;
                    foreach (FileInfo file in files)
                    {
                        if (total <= limit) break;
                        if (TryDeleteFile(file.FullName)) total -= file.Length;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void Log(string eventName, string message)
        {
            lock (_gate)
            {
                try
                {
                    string path = System.IO.Path.Combine(LogDirectory, "app-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes)
                    {
                        string rotated = path + ".1";
                        if (File.Exists(rotated)) File.Delete(rotated);
                        File.Move(path, rotated);
                    }
                    string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + CleanLog(eventName) + " " + CleanLog(message);
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                    foreach (FileInfo old in new DirectoryInfo(LogDirectory).GetFiles("app-*.log*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxLogFiles)) old.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string CleanLog(string value)
        {
            string clean = (value ?? String.Empty).Replace('\r', ' ').Replace('\n', ' ');
            const int maxLength = 4000;
            return clean.Length > maxLength ? clean.Substring(0, maxLength) : clean;
        }

        private string IndexPath(string repoId)
        {
            if (String.IsNullOrWhiteSpace(repoId)) throw new ArgumentException("仓库标识为空。", "repoId");
            return System.IO.Path.Combine(IndexDirectory, Hash(repoId) + ".json");
        }

        internal static string Hash(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", String.Empty).ToLowerInvariant();
        }

        private static T ReadJson<T>(string path)
        {
            using (FileStream stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static void WriteJsonAtomic<T>(string path, T value)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { TryDeleteFile(temporary); }
        }

        internal static void DeleteFile(string path)
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }

        internal static bool TryDeleteFile(string path)
        {
            try { DeleteFile(path); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public sealed partial class SvnService
    {
        private const string HttpsScheme = "https";
        private const string CertificateFailuresOption = "--trust-server-cert-failures";
        private const string CertificateNameMismatch = "cn-mismatch";
        private const string CertificateNameErrorText = "different hostname";
        private const long MaxXmlBytes = 64L * 1024L * 1024L;
        private const long MaxPreviewBytes = 64L * 1024L * 1024L;
        private const int ListTimeoutMilliseconds = 120000;
        private const int PreviewTimeoutMilliseconds = 180000;
        private const int DownloadTimeoutMilliseconds = 600000;
        private const int MaxSvnErrorCharacters = 65536;
        private const int Gb18030CodePage = 54936;
        private static readonly Encoding SvnErrorUtf8 = new UTF8Encoding(true, true);
        private static readonly Encoding SvnErrorGb18030 = Encoding.GetEncoding(Gb18030CodePage,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private static readonly Regex EscapedNonAsciiBytes = new Regex(@"(?:%[89A-Fa-f][0-9A-Fa-f])+");
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> IndexUpdateLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly AppStore _store;
        private readonly string _executable;
        private readonly object _cacheGate = new object();
        private readonly Dictionary<string, SemaphoreSlim> _cacheLocks = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        public SvnService(AppStore store, string executable)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            _executable = FindExecutable(executable);
        }

        private static string FindExecutable(string executable)
        {
            List<string> candidates = new List<string>();
            if (!String.IsNullOrWhiteSpace(executable)) candidates.Add(executable);
            else
            {
                foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                    if (!String.IsNullOrWhiteSpace(root)) candidates.Add(System.IO.Path.Combine(root, "TortoiseSVN", "bin", "svn.exe"));
                foreach (string item in (Environment.GetEnvironmentVariable("PATH") ?? String.Empty).Split(';'))
                    if (!String.IsNullOrWhiteSpace(item))
                        try { candidates.Add(System.IO.Path.Combine(item.Trim().Trim('"'), "svn.exe")); } catch (ArgumentException) { }
            }
            foreach (string candidate in candidates)
                if (File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
            throw new FileNotFoundException("没有找到 svn.exe，请安装带命令行工具的 TortoiseSVN，或在设置中选择 svn.exe。需要 SVN 1.10 或更高版本。 ");
        }

        public static string BuildUrl(Repository repository, string path)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            Uri uri;
            if (String.IsNullOrWhiteSpace(repository.Url) || !Uri.TryCreate(repository.Url.Trim(), UriKind.Absolute, out uri))
                throw new ArgumentException("SVN 仓库地址无效。");
            string scheme = uri.Scheme.ToLowerInvariant();
            string[] allowed = { "http", "https", "svn", "file" };
            if (!allowed.Contains(scheme) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) || !String.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("仓库地址只支持 HTTP(S)、SVN 或 file 协议；不要在地址中填写账号、密码、查询参数或锚点。当前版本不支持 SVN+SSH。");
            string relative = NormalizePath(path);
            string root = uri.AbsoluteUri.TrimEnd('/');
            return relative.Length == 0 ? root : root + "/" + String.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
        }

        /// <summary>Shows Unicode path names while preserving escaped ASCII characters in the address.</summary>
        /// <param name="repository">The configured SVN repository.</param>
        /// <param name="path">The repository-relative path.</param>
        /// <returns>The display address with percent-encoded UTF-8 decoded.</returns>
        internal static string BuildDisplayUrl(Repository repository, string path)
        {
            return EscapedNonAsciiBytes.Replace(BuildUrl(repository, path), match => Uri.UnescapeDataString(match.Value));
        }

        private static string NormalizePath(string path)
        {
            string normalized = (path ?? String.Empty).Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Any(Char.IsControl))
                throw new ArgumentException("文件路径必须是仓库内的相对路径。");
            normalized = normalized.TrimEnd('/');
            if (normalized.Length == 0) return normalized;
            if (normalized.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOf(':') >= 0))
                throw new ArgumentException("文件路径包含无效的目录段。");
            return normalized;
        }

        public Task<List<SvnEntry>> ListAsync(Repository repository, string relativePath, bool recursive, CancellationToken cancellationToken)
        {
            return ListAtRevisionAsync(repository, relativePath, recursive, "HEAD", cancellationToken);
        }

        private async Task<List<SvnEntry>> ListAtRevisionAsync(Repository repository, string relativePath, bool recursive, string revision, CancellationToken cancellationToken)
        {
            string relative = NormalizePath(relativePath);
            string url = BuildUrl(repository, relative);
            List<string> arguments = new List<string> { "list", "--xml", "--revision", revision, "--depth", recursive ? "infinity" : "immediates", "--", url + "@" + revision };
            byte[] bytes;
            using (MemoryStream stream = new MemoryStream())
            {
                await RunAsync(repository, arguments, stream, MaxXmlBytes, ListTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                bytes = stream.ToArray();
            }
            List<SvnEntry> entries = new List<SvnEntry>();
            XmlReaderSettings settings = SafeXmlSettings();
            using (MemoryStream input = new MemoryStream(bytes))
            using (XmlReader reader = XmlReader.Create(input, settings))
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || !String.Equals(reader.LocalName, "entry", StringComparison.Ordinal)) continue;
                    XElement item;
                    using (XmlReader entryReader = reader.ReadSubtree()) item = XElement.Load(entryReader);
                    string name = (string)item.Element("name");
                    if (String.IsNullOrEmpty(name)) throw new InvalidDataException("SVN 返回了空文件名。");
                    string fullPath = NormalizePath(relative.Length == 0 ? name : relative + "/" + name);
                    XElement commit = item.Element("commit");
                    long size;
                    Int64.TryParse((string)item.Element("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    DateTime modified;
                    if (!DateTime.TryParse(commit == null ? null : (string)commit.Element("date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out modified))
                        modified = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
                    entries.Add(new SvnEntry
                    {
                        Name = name.TrimEnd('/').Split('/').Last(), Path = fullPath,
                        IsDirectory = String.Equals((string)item.Attribute("kind"), "dir", StringComparison.Ordinal),
                        Size = size, Revision = commit == null ? null : (string)commit.Attribute("revision"),
                        Author = commit == null ? String.Empty : ((string)commit.Element("author") ?? String.Empty), ModifiedUtc = modified
                    });
                }
            }
            _store.Log("svn_list_complete", "repository=" + repository.Id + " path=" + relative + " recursive=" + recursive + " entries=" + entries.Count);
            return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public async Task<IndexSnapshot> BuildIndexAsync(Repository repository, CancellationToken cancellationToken)
        {
            SemaphoreSlim gate = GetIndexUpdateLock(repository.Id);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await BuildIndexSnapshotAsync(repository, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        private SemaphoreSlim GetIndexUpdateLock(string repositoryId)
        {
            return IndexUpdateLocks.GetOrAdd(_store.IndexDirectory + "\n" + AppStore.Hash(repositoryId), key => new SemaphoreSlim(1, 1));
        }

        private async Task<IndexSnapshot> BuildIndexSnapshotAsync(Repository repository, CancellationToken cancellationToken)
        {
            _store.Log("index_start", "repository=" + repository.Id);
            string revision;
            using (MemoryStream stream = new MemoryStream())
            {
                await RunAsync(repository, new List<string> { "info", "--xml", "--revision", "HEAD", "--", BuildUrl(repository, String.Empty) + "@HEAD" }, stream, MaxXmlBytes, ListTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                XElement entry = ParseXml(stream.ToArray()).Descendants("entry").FirstOrDefault();
                revision = entry == null ? null : (string)entry.Attribute("revision");
            }
            ValidateRevision(revision);
            List<SvnEntry> entries = await ListAtRevisionAsync(repository, String.Empty, true, revision, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            IndexSnapshot snapshot = new IndexSnapshot { RepositoryId = repository.Id, Revision = revision, UpdatedUtc = DateTime.UtcNow, Entries = entries };
            _store.SaveIndex(snapshot);
            return snapshot;
        }

        public async Task<string> FetchAsync(Repository repository, SvnEntry entry, CancellationToken cancellationToken)
        {
            ValidateFile(entry);
            long limit = Math.Min(MaxPreviewBytes, (long)_store.Config.CacheLimitMb * 1024L * 1024L);
            if (entry.Size > limit) throw new IOException("文件超过临时查看上限（" + (limit / 1024L / 1024L).ToString(CultureInfo.InvariantCulture) + " MB），请使用下载另存功能。");
            string extension = System.IO.Path.GetExtension(entry.Path);
            if (extension.Length > 16 || extension.Any(c => !Char.IsLetterOrDigit(c) && c != '.')) extension = String.Empty;
            string identity = repository.Id + "\n" + repository.Url + "\n" + repository.Username + "\n" + repository.ProtectedPassword + "\n" + NormalizePath(entry.Path) + "\n" + entry.Revision;
            string path = System.IO.Path.Combine(_store.CacheDirectory, AppStore.Hash(identity) + extension);
            SemaphoreSlim cacheLock;
            lock (_cacheGate)
            {
                if (!_cacheLocks.TryGetValue(path, out cacheLock)) { cacheLock = new SemaphoreSlim(1, 1); _cacheLocks.Add(path, cacheLock); }
            }
            await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path) && new FileInfo(path).Length == entry.Size)
                {
                    TouchCache(path);
                    _store.Log("cache_hit", "repository=" + repository.Id + " path=" + entry.Path + " revision=" + entry.Revision);
                    return path;
                }
                _store.CleanupCache();
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    await FetchToFileAsync(repository, entry, temporary, limit, PreviewTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                    AppStore.DeleteFile(path);
                    File.Move(temporary, path);
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    TouchCache(path);
                    _store.CleanupCache();
                    _store.Log("cache_created", "repository=" + repository.Id + " path=" + entry.Path + " revision=" + entry.Revision);
                    return path;
                }
                finally { AppStore.TryDeleteFile(temporary); }
            }
            finally { cacheLock.Release(); }
        }

        public async Task DownloadAsync(Repository repository, SvnEntry entry, string targetPath, CancellationToken cancellationToken)
        {
            ValidateFile(entry);
            string target = System.IO.Path.GetFullPath(targetPath);
            string destinationDirectory = System.IO.Path.GetDirectoryName(target);
            if (!Directory.Exists(destinationDirectory)) throw new DirectoryNotFoundException("保存目录不存在。");
            string staged = System.IO.Path.Combine(_store.CacheDirectory, Guid.NewGuid().ToString("N") + ".part");
            string destinationStage = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await FetchToFileAsync(repository, entry, staged, Int64.MaxValue, DownloadTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                using (FileStream source = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                using (FileStream destination = new FileStream(destinationStage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await CopyOutputAsync(source, destination, Int64.MaxValue, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.SetAttributes(destinationStage, File.GetAttributes(destinationStage) & ~FileAttributes.ReadOnly);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(target)) File.Replace(destinationStage, target, null);
                else File.Move(destinationStage, target);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                RecordDownloadSource(repository, entry, target);
                _store.Log("download_complete", "repository=" + repository.Id + " path=" + entry.Path + " target=" + target);
            }
            finally { AppStore.TryDeleteFile(staged); AppStore.TryDeleteFile(destinationStage); }
        }

        private async Task FetchToFileAsync(Repository repository, SvnEntry entry, string path, long maxBytes, int timeout, CancellationToken cancellationToken)
        {
            string url = BuildUrl(repository, entry.Path);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await RunAsync(repository, new List<string> { "cat", "--revision", entry.Revision, "--", url + "@HEAD" }, stream, maxBytes, timeout, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length != entry.Size) throw new IOException("文件大小与目录信息不一致，请刷新目录后重试。");
            }
        }

        private static void ValidateFile(SvnEntry entry)
        {
            if (entry == null || entry.IsDirectory || String.IsNullOrEmpty(entry.Path) || entry.Size < 0) throw new ArgumentException("请选择有效的文件。");
            NormalizePath(entry.Path);
            ValidateRevision(entry.Revision);
        }

        private static void ValidateRevision(string revision)
        {
            long numeric;
            if (!Int64.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out numeric) || numeric < 0)
                throw new InvalidDataException("文件版本信息无效，请刷新目录。");
        }

        private static void TouchCache(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
                finally { if (File.Exists(path)) File.SetAttributes(path, attributes | FileAttributes.ReadOnly); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static XDocument ParseXml(byte[] bytes)
        {
            using (MemoryStream stream = new MemoryStream(bytes))
            using (XmlReader reader = XmlReader.Create(stream, SafeXmlSettings())) return XDocument.Load(reader);
        }

        private static XmlReaderSettings SafeXmlSettings()
        {
            return new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes };
        }

        private async Task RunAsync(Repository repository, IList<string> arguments, Stream destination, long maxBytes, int timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string password = _store.Unprotect(repository.ProtectedPassword);
            if (password.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("SVN 密码不能包含换行符或空字符。");
            List<string> options = new List<string> { "--non-interactive", "--no-auth-cache", "--config-dir", _store.SvnConfigDirectory };
            Uri repositoryUri = new Uri(BuildUrl(repository, String.Empty));
            IPAddress repositoryAddress;
            if (repository.AllowIpCertificateNameMismatch &&
                String.Equals(repositoryUri.Scheme, HttpsScheme, StringComparison.OrdinalIgnoreCase) &&
                IPAddress.TryParse(repositoryUri.Host.Trim('[', ']'), out repositoryAddress))
            {
                options.Add(CertificateFailuresOption);
                options.Add(CertificateNameMismatch);
                _store.Log("svn_certificate_name_exception", "repository=" + repository.Id + "; host=" + repositoryUri.Host + "; allowed=" + CertificateNameMismatch);
            }
            bool hasCredentials = !String.IsNullOrEmpty(repository.Username) || password.Length > 0;
            if (hasCredentials)
            {
                if (String.IsNullOrWhiteSpace(repository.Username)) throw new ArgumentException("已填写密码时必须填写 SVN 用户名。");
                options.Add("--username"); options.Add(repository.Username); options.Add("--password-from-stdin");
            }
            int separator = arguments.IndexOf("--");
            List<string> allArguments = arguments.ToList();
            allArguments.InsertRange(separator < 0 ? allArguments.Count : separator, options);
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = _executable, Arguments = String.Join(" ", allArguments.Select(QuoteArgument)), UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = _store.DataDirectory
            };
            string temporaryDirectory = System.IO.Path.Combine(_store.DataDirectory, "temp");
            start.EnvironmentVariables["TEMP"] = temporaryDirectory;
            start.EnvironmentVariables["TMP"] = temporaryDirectory;
            string operation = arguments.Count == 0 ? "unknown" : arguments[0];
            _store.Log("svn_start", "operation=" + operation + " repository=" + repository.Id);
            using (Process process = new Process { StartInfo = start })
            using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(timeout);
                try
                {
                    if (!process.Start()) throw new IOException("无法启动 SVN 命令行工具。");
                    using (timeoutSource.Token.Register(delegate { KillProcess(process); }))
                    {
                        Task<string> errorTask = ReadErrorAsync(process.StandardError.BaseStream);
                        Task outputTask = CopyOutputAsync(process.StandardOutput.BaseStream, destination, maxBytes, timeoutSource.Token);
                        Task monitorOutput = outputTask.ContinueWith(delegate(Task task) { if (task.IsFaulted || task.IsCanceled) KillProcess(process); }, TaskScheduler.Default);
                        Task waitTask = Task.Run(delegate { process.WaitForExit(); });
                        try
                        {
                            if (hasCredentials)
                            {
                                await process.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
                                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                            }
                            process.StandardInput.Close();
                            await Task.WhenAll(outputTask, errorTask, waitTask, monitorOutput).ConfigureAwait(false);
                        }
                        catch
                        {
                            KillProcess(process);
                            try { Task.WhenAll(errorTask, waitTask).GetAwaiter().GetResult(); } catch { }
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                            if (timeoutSource.IsCancellationRequested) throw new TimeoutException("SVN 请求超时，已停止当前请求，没有自动重试。");
                            throw;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (timeoutSource.IsCancellationRequested) throw new TimeoutException("SVN 请求超时，已停止当前请求，没有自动重试。");
                        if (process.ExitCode != 0)
                        {
                            string error = errorTask.Result;
                            if (password.Length > 0) error = error.Replace(password, "[已隐藏]");
                            if (!repository.AllowIpCertificateNameMismatch && error.IndexOf(CertificateNameErrorText, StringComparison.OrdinalIgnoreCase) >= 0)
                                error += "\r\n若此仓库必须通过 IP 访问，可在“编辑仓库”中勾选“允许 IP 地址与证书域名不一致（仅此仓库）”。";
                            throw new SvnCommandException("SVN 请求失败（" + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "），未自动重试。\r\n" + error.Trim(), error);
                        }
                    }
                    _store.Log("svn_complete", "operation=" + operation + " repository=" + repository.Id);
                }
                catch (Exception ex)
                {
                    KillProcess(process);
                    string safeMessage = ex.Message;
                    if (password.Length > 0) safeMessage = safeMessage.Replace(password, "[已隐藏]");
                    _store.Log("svn_failed", "operation=" + operation + " repository=" + repository.Id + " error=" + safeMessage);
                    throw;
                }
            }
        }

        private static async Task CopyOutputAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > maxBytes) throw new IOException("SVN 返回内容超过容量上限，请缩小索引目录或使用下载另存功能。");
                await destination.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadErrorAsync(Stream source)
        {
            const int maxBytes = (MaxSvnErrorCharacters + 1) * 4;
            byte[] buffer = new byte[4096];
            using (MemoryStream captured = new MemoryStream())
            {
                int count;
                while ((count = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    int remaining = maxBytes - (int)captured.Length;
                    if (remaining > 0) captured.Write(buffer, 0, Math.Min(count, remaining));
                }
                if (captured.Length == 0) return String.Empty;

                try { return DecodeError(captured, SvnErrorUtf8); }
                catch (DecoderFallbackException) { }
                try { return DecodeError(captured, SvnErrorGb18030); }
                catch (DecoderFallbackException) { return DecodeError(captured, Encoding.Default); }
            }
        }

        private static string DecodeError(MemoryStream captured, Encoding encoding)
        {
            captured.Position = 0;
            using (StreamReader reader = new StreamReader(captured, encoding, true, 4096, true))
            {
                char[] text = new char[Math.Min((int)captured.Length, MaxSvnErrorCharacters)];
                int length = reader.ReadBlock(text, 0, text.Length);
                return new string(text, 0, length);
            }
        }

        private static void KillProcess(Process process)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }

        private static string QuoteArgument(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(character);
            }
            result.Append('\\', slashes * 2); result.Append('"');
            return result.ToString();
        }
    }
}
