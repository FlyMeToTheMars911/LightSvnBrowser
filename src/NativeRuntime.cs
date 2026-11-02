using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LightSvnBrowser
{
    public static class NativeRuntime
    {
        private const string ResourceName = "LightSvnBrowser.SvnRuntime.zip";
        private const string RuntimeVersionDirectory = "svn-1.14.1";
        private const string ExecutableName = "svn.exe";
        private const long MaximumEntryLength = 40L * 1024L * 1024L;
        private const long MaximumArchiveLength = 128L * 1024L * 1024L;
        private const int MutexTimeoutMilliseconds = 30000;
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> CheckedRuntimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> AllowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "svn.exe", "libsvn_tsvn.dll", "libapr_tsvn.dll", "libaprutil_tsvn.dll", "libsasl.dll", "intl3_tsvn.dll",
            "saslANONYMOUS.dll", "saslCRAMMD5.dll", "saslDIGESTMD5.dll", "saslGSSAPI.dll", "saslLOGIN.dll",
            "saslNTLM.dll", "saslPLAIN.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll",
            "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll", "vcruntime140.dll", "vcruntime140_1.dll",
            "concrt140.dll", "vcomp140.dll", "vcamp140.dll", "vccorlib140.dll",
            "Subversion license.txt", "apr License.txt", "CyrusSASL License.txt", "OpenSSL License.txt",
            "TortoiseSVN License.txt", "THIRD-PARTY-NOTICES.md", "RUNTIME-SOURCE.txt"
        };

        public static string Resolve(string dataDirectory, string configuredExecutable)
        {
            if (!String.IsNullOrWhiteSpace(configuredExecutable))
            {
                string explicitPath = Path.GetFullPath(configuredExecutable.Trim());
                if (!File.Exists(explicitPath))
                {
                    throw new FileNotFoundException("指定的 SVN 程序不存在，请修改设置中的程序路径：" + explicitPath, explicitPath);
                }
                return explicitPath;
            }

            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (resource != null)
                {
                    return EnsureEmbeddedRuntime(dataDirectory, resource);
                }
            }

            string systemExecutable = FindSystemExecutable();
            if (systemExecutable != null)
            {
                return systemExecutable;
            }
            throw new FileNotFoundException("当前构建未内置 SVN，系统中也未找到 svn.exe。请在设置中指定 SVN 程序路径，或使用包含运行时的正式构建。");
        }

        private static string EnsureEmbeddedRuntime(string dataDirectory, Stream resource)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory))
            {
                throw new ArgumentException("请先选择用于保存 SVN 运行时和配置的数据目录。", "dataDirectory");
            }
            string dataRoot = Path.GetFullPath(dataDirectory);
            string runtimeParent = Path.Combine(dataRoot, "runtime");
            string runtimePath = Path.Combine(runtimeParent, RuntimeVersionDirectory);
            string executablePath = Path.Combine(runtimePath, ExecutableName);
            lock (SyncRoot)
            {
                if (CheckedRuntimes.Contains(runtimePath) && File.Exists(executablePath))
                {
                    return executablePath;
                }
                Directory.CreateDirectory(dataRoot);
                EnsureOrdinaryDirectory(dataRoot);
                Directory.CreateDirectory(runtimeParent);
                EnsureOrdinaryDirectory(runtimeParent);
                string mutexName = "Local\\LightSvnBrowser.Runtime." + HashText(runtimePath.ToUpperInvariant());
                using (Mutex mutex = new Mutex(false, mutexName))
                {
                    bool ownsMutex = false;
                    try
                    {
                        try { ownsMutex = mutex.WaitOne(MutexTimeoutMilliseconds); }
                        catch (AbandonedMutexException) { ownsMutex = true; }
                        if (!ownsMutex)
                        {
                            throw new IOException("等待另一实例准备 SVN 运行时超时，请稍后重试。");
                        }
                        using (ZipArchive archive = new ZipArchive(resource, ZipArchiveMode.Read, true))
                        {
                            ValidateArchive(archive);
                            if (!RuntimeMatches(archive, runtimePath))
                            {
                                ExtractRuntime(archive, runtimeParent, runtimePath);
                            }
                        }
                        CheckedRuntimes.Add(runtimePath);
                        return executablePath;
                    }
                    finally
                    {
                        if (ownsMutex) { mutex.ReleaseMutex(); }
                    }
                }
            }
        }

        private static void ValidateArchive(ZipArchive archive)
        {
            long totalLength = 0;
            HashSet<string> entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName;
                bool flatFile = name == Path.GetFileName(name) && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;
                if (!flatFile || !AllowedFiles.Contains(name) || !entries.Add(name))
                {
                    throw new InvalidDataException("内置 SVN 压缩包包含不允许的文件或重复条目：" + name);
                }
                if (entry.Length < 0 || entry.Length > MaximumEntryLength)
                {
                    throw new InvalidDataException("内置 SVN 压缩包中的文件大小异常：" + name);
                }
                totalLength += entry.Length;
                if (totalLength > MaximumArchiveLength)
                {
                    throw new InvalidDataException("内置 SVN 压缩包解压后大小超过限制。");
                }
            }
            if (!entries.Contains(ExecutableName))
            {
                throw new InvalidDataException("内置 SVN 压缩包缺少 svn.exe。");
            }
        }

        private static bool RuntimeMatches(ZipArchive archive, string runtimePath)
        {
            if (!Directory.Exists(runtimePath)) { return false; }
            EnsureOrdinaryDirectory(runtimePath);
            if (Directory.GetDirectories(runtimePath).Length != 0) { return false; }
            string[] actualFiles = Directory.GetFiles(runtimePath);
            if (actualFiles.Length != archive.Entries.Count) { return false; }
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string filePath = SafeEntryPath(runtimePath, entry.FullName);
                if (!File.Exists(filePath)) { return false; }
                FileInfo file = new FileInfo(filePath);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { return false; }
                if (file.Length != entry.Length) { return false; }
                using (Stream entryStream = entry.Open())
                using (Stream fileStream = File.OpenRead(filePath))
                using (SHA256 hash = SHA256.Create())
                {
                    string expected = Convert.ToBase64String(hash.ComputeHash(entryStream));
                    string actual = Convert.ToBase64String(hash.ComputeHash(fileStream));
                    if (!String.Equals(expected, actual, StringComparison.Ordinal)) { return false; }
                }
            }
            return true;
        }

        private static void ExtractRuntime(ZipArchive archive, string runtimeParent, string runtimePath)
        {
            string stagingPath = Path.Combine(runtimeParent, ".extract-" + Guid.NewGuid().ToString("N"));
            string previousPath = null;
            Directory.CreateDirectory(stagingPath);
            try
            {
                EnsureOrdinaryDirectory(stagingPath);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destination = SafeEntryPath(stagingPath, entry.FullName);
                    using (Stream source = entry.Open())
                    using (FileStream target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(target);
                    }
                }
                if (Directory.Exists(runtimePath))
                {
                    EnsureOrdinaryDirectory(runtimePath);
                    previousPath = Path.Combine(runtimeParent, ".previous-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(runtimePath, previousPath);
                }
                try { Directory.Move(stagingPath, runtimePath); }
                catch
                {
                    if (previousPath != null && !Directory.Exists(runtimePath))
                    {
                        Directory.Move(previousPath, runtimePath);
                        previousPath = null;
                    }
                    throw;
                }
            }
            finally
            {
                TryDeleteDirectory(stagingPath);
                if (previousPath != null) { TryDeleteDirectory(previousPath); }
            }
        }

        private static string SafeEntryPath(string directory, string name)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string destination = Path.GetFullPath(Path.Combine(root, name));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name) != name)
            {
                throw new InvalidDataException("内置 SVN 文件路径超出数据目录。");
            }
            return destination;
        }

        private static void EnsureOrdinaryDirectory(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("SVN 运行时目录不能是符号链接或联接点：" + path);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) { return; }
                EnsureOrdinaryDirectory(path);
                // Delete only flat runtime payloads; never traverse unexpected nested links or folders.
                if (Directory.GetDirectories(path).Length != 0) { return; }
                foreach (string file in Directory.GetFiles(path)) { File.Delete(file); }
                Directory.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string HashText(string value)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", String.Empty);
            }
        }

        private static string FindSystemExecutable()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string rawDirectory in path.Split(Path.PathSeparator))
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (directory.Length == 0) { continue; }
                try
                {
                    string candidate = Path.Combine(directory, ExecutableName);
                    if (File.Exists(candidate)) { return Path.GetFullPath(candidate); }
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            string[] roots = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string root in roots)
            {
                if (String.IsNullOrEmpty(root)) { continue; }
                string candidate = Path.Combine(root, "TortoiseSVN", "bin", ExecutableName);
                if (File.Exists(candidate)) { return candidate; }
            }
            return null;
        }
    }
}
