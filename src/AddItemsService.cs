using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LightSvnBrowser
{
    /// <summary>Stages local additions in an isolated working copy and submits them in one SVN revision.</summary>
    /// <remarks>Author: Codex, date: 2026-11-09.</remarks>
    public sealed partial class SvnService
    {
        private const string AddItemsPendingPrefix = "add-items\n";
        private const string DirectoryKind = "dir";
        private const string AddedStatus = "added";
        private const string SvnAdministrativeName = ".svn";
        private const string SvnLegacyAdministrativeName = "_svn";

        /// <summary>Reads a pending batch for the selected directory.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="path">Destination directory, relative to the selected repository.</param>
        /// <returns>The unresolved attempt, or null.</returns>
        public SubmissionAttempt GetPendingAddItems(Repository repository, string path)
        {
            string targetUrl = BuildUrl(repository, path);
            return ReadPendingSubmission(AddItemsPendingPrefix + targetUrl, targetUrl, path);
        }

        /// <summary>Commits the selected local files and directory trees as new children of the target directory.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="path">Destination directory, relative to the selected repository.</param>
        /// <param name="localPaths">Selected local paths.</param>
        /// <param name="message">Required commit description.</param>
        /// <param name="progress">Optional preparation and commit stage observer.</param>
        /// <param name="cancellationToken">Cancels preparation before the commit starts.</param>
        /// <returns>The commit result, including a pending attempt if the outcome is uncertain.</returns>
        public async Task<SubmissionResult> AddItemsAsync(Repository repository, string path, IEnumerable<string> localPaths,
            string message, IProgress<SubmissionProgress> progress, CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(message)) throw new ArgumentException("请填写提交说明。", "message");
            if (message.Length > MaximumMessageCharacters) throw new ArgumentException("提交说明不能超过 65,536 个字符。", "message");
            string relative = NormalizePath(path);
            string targetUrl = BuildUrl(repository, relative);
            string pendingKey = AddItemsPendingPrefix + targetUrl;
            List<string> sources = ResolveAddSources(localPaths);
            SemaphoreSlim gate = SubmissionLocks.GetOrAdd(PendingPath(pendingKey), key => new SemaphoreSlim(SingleEntryCount, SingleEntryCount));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string attemptDirectory = null;
            bool keepAttempt = false;
            try
            {
                if (GetPendingAddItems(repository, relative) != null)
                    throw new InvalidOperationException("该目录有一批新增提交待核实，请先核实结果。");

                _store.Log("add_items_prepare", "repository=" + repository.Id + " path=" + relative + " selected=" + sources.Count);
                ReportSubmission(progress, "正在检查目标目录及同名项…", false);
                XElement info = ParseXml(await RunSubmissionCommandAsync(repository, new List<string> { "info", "--xml", "--revision", HeadRevision,
                    "--", targetUrl + "@" + HeadRevision }, cancellationToken).ConfigureAwait(false)).Descendants("entry").SingleOrDefault();
                if (info == null || !String.Equals((string)info.Attribute("kind"), DirectoryKind, StringComparison.Ordinal))
                    throw new IOException("目标路径已不再是可用的 SVN 目录。请刷新后重新选择位置。");
                string snapshot = (string)info.Attribute("revision");
                ValidateRevision(snapshot);
                List<SvnEntry> existing = await ListAtRevisionAsync(repository, relative, false, snapshot, cancellationToken).ConfigureAwait(false);
                HashSet<string> existingNames = new HashSet<string>(existing.Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
                string conflict = sources.Select(Path.GetFileName).FirstOrDefault(existingNames.Contains);
                if (conflict != null) throw new IOException("目标目录中已存在同名项“" + conflict + "”，请调整本地名称后再提交。");

                SubmissionAttempt attempt = new SubmissionAttempt
                {
                    Id = Guid.NewGuid().ToString("N"), Path = relative, TargetUrl = targetUrl, BaseRevision = snapshot,
                    SnapshotRevision = snapshot, Message = message.Trim(), CreatedUtc = DateTime.UtcNow
                };
                attemptDirectory = Path.Combine(SubmissionRoot(), attempt.Id);
                string workingCopy = Path.Combine(attemptDirectory, WorkingDirectoryName);
                foreach (string source in sources)
                {
                    if (workingCopy.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("所选目录包含程序的临时工作区，请选择更具体的目录。");
                }
                Directory.CreateDirectory(attemptDirectory);
                ReportSubmission(progress, "正在准备临时工作副本并复制所选内容…", false);
                await RunSubmissionCommandAsync(repository, new List<string> { "checkout", "--quiet", "--depth", "empty", "--ignore-externals",
                    "--revision", snapshot, "--", targetUrl + "@" + snapshot, workingCopy }, cancellationToken).ConfigureAwait(false);

                int staged = EmptyCount;
                foreach (string source in sources)
                    staged += await CopyAddItemAsync(source, Path.Combine(workingCopy, Path.GetFileName(source)), cancellationToken).ConfigureAwait(false);
                await RunSubmissionCommandAsync(repository, new List<string> { "add", "--quiet", "--force", "--no-ignore", "--no-auto-props",
                    "--depth", "infinity", "--", workingCopy + "@" }, cancellationToken).ConfigureAwait(false);
                using (MemoryStream status = new MemoryStream())
                {
                    await RunAsync(repository, new List<string> { "status", "--xml", "--depth", "infinity", "--", workingCopy + "@" },
                        status, MaxXmlBytes, DownloadTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                    List<XElement> entries = ParseXml(status.ToArray()).Descendants("wc-status").ToList();
                    if (entries.Count != staged || entries.Any(item => !String.Equals((string)item.Attribute("item"), AddedStatus, StringComparison.Ordinal)))
                        throw new InvalidDataException("临时工作副本的新增清单与所选内容不符，提交已停止。");
                }

                string messagePath = Path.Combine(attemptDirectory, MessageName);
                File.WriteAllText(messagePath, attempt.Message + Environment.NewLine + Environment.NewLine + SubmissionMarker(attempt), new UTF8Encoding(false));
                cancellationToken.ThrowIfCancellationRequested();
                ReportSubmission(progress, "正在提交新增内容，请等待 SVN 返回结果…", true);
                cancellationToken.ThrowIfCancellationRequested();
                WriteSubmissionJson(PendingPath(pendingKey), attempt, false);
                keepAttempt = true;
                _store.Log("add_items_commit_start", "repository=" + repository.Id + " path=" + relative + " attempt=" + attempt.Id + " entries=" + staged);
                try
                {
                    await RunSubmissionCommandAsync(repository, new List<string> { "commit", "--quiet", "--depth", "infinity",
                        "--file", messagePath, "--encoding", "UTF-8", "--", workingCopy + "@" }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    SvnCommandException rejected = error as SvnCommandException;
                    if (rejected != null && IsDefiniteRejection(rejected.ServerError))
                    {
                        keepAttempt = !TryDeleteResolvedPending(pendingKey);
                        _store.Log("add_items_rejected", "repository=" + repository.Id + " path=" + relative + " attempt=" + attempt.Id + " error=" + error.Message);
                        return new SubmissionResult { Status = SubmissionStatus.NotCommitted, Message = error.Message };
                    }
                    _store.Log("add_items_unknown", "repository=" + repository.Id + " path=" + relative + " attempt=" + attempt.Id + " error=" + error.Message);
                    return new SubmissionResult { Status = SubmissionStatus.Unknown, Attempt = attempt,
                        Message = "提交结果待核实。服务器可能已保存，请核实结果后再操作。\r\n" + error.Message };
                }

                string revision = null;
                try
                {
                    using (MemoryStream committed = new MemoryStream())
                    {
                        await RunAsync(repository, new List<string> { "info", "--xml", "--depth", "immediates", "--", workingCopy + "@" },
                            committed, MaxXmlBytes, DownloadTimeoutMilliseconds, CancellationToken.None).ConfigureAwait(false);
                        XElement entry = ParseXml(committed.ToArray()).Descendants("entry")
                            .FirstOrDefault(item => !String.Equals((string)item.Element("url"), targetUrl, StringComparison.Ordinal)
                                && !String.Equals((string)item.Attribute("revision"), snapshot, StringComparison.Ordinal));
                        string value = entry == null ? null : (string)entry.Attribute("revision");
                        ValidateRevision(value);
                        revision = value;
                    }
                }
                catch (Exception error)
                {
                    _store.Log("add_items_revision_unavailable", "repository=" + repository.Id + " path=" + relative + " attempt=" + attempt.Id + " error=" + error.Message);
                }
                string indexMessage = await UpdateIndexAfterAddAsync(repository, attempt, revision, progress).ConfigureAwait(false);
                keepAttempt = !TryDeleteResolvedPending(pendingKey);
                _store.Log("add_items_committed", "repository=" + repository.Id + " path=" + relative + " attempt=" + attempt.Id + " revision=" + revision);
                return new SubmissionResult { Status = SubmissionStatus.Committed, Revision = revision,
                    Message = (revision == null ? "已提交新增内容。" : "已提交新增内容，版本 r" + revision + "。") + indexMessage };
            }
            finally
            {
                if (!keepAttempt && attemptDirectory != null) CleanupAttemptDirectory(attemptDirectory);
                gate.Release();
            }
        }

        /// <summary>Checks a pending batch's exact commit marker in SVN history.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="attempt">Previously loaded pending attempt.</param>
        /// <param name="cancellationToken">Cancels the read-only lookup.</param>
        /// <returns>The matching revision, or an unresolved result if the marker could not be confirmed.</returns>
        public async Task<SubmissionResult> VerifyAddItemsAsync(Repository repository, SubmissionAttempt attempt, CancellationToken cancellationToken)
        {
            if (attempt == null) throw new ArgumentNullException("attempt");
            string pendingKey = AddItemsPendingPrefix + BuildUrl(repository, attempt.Path);
            SemaphoreSlim gate = SubmissionLocks.GetOrAdd(PendingPath(pendingKey), key => new SemaphoreSlim(SingleEntryCount, SingleEntryCount));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SubmissionAttempt pending = GetPendingAddItems(repository, attempt.Path);
                if (pending == null || !String.Equals(pending.Id, attempt.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException("待核实记录已变化，请重新打开新增提交窗口。");
                _store.Log("add_items_verify_start", "repository=" + repository.Id + " path=" + pending.Path + " attempt=" + pending.Id);
                string revision = await FindSubmissionRevisionAsync(repository, pending, cancellationToken).ConfigureAwait(false);
                if (revision == null)
                    return new SubmissionResult { Status = SubmissionStatus.Unknown, Attempt = pending,
                        Message = "暂未找到本次提交的标记，结果仍待确认。请稍后核实，或请仓库管理员核对。" };
                string indexMessage = await UpdateIndexAfterAddAsync(repository, pending, revision, null).ConfigureAwait(false);
                if (TryDeleteResolvedPending(pendingKey)) CleanupAttemptDirectory(Path.Combine(SubmissionRoot(), pending.Id));
                _store.Log("add_items_verified", "repository=" + repository.Id + " path=" + pending.Path + " attempt=" + pending.Id + " revision=" + revision);
                return new SubmissionResult { Status = SubmissionStatus.Committed, Revision = revision, Message = "已核实提交成功，版本 r" + revision + "。" + indexMessage };
            }
            finally { gate.Release(); }
        }

        private async Task<string> UpdateIndexAfterAddAsync(Repository repository, SubmissionAttempt attempt, string revision,
            IProgress<SubmissionProgress> progress)
        {
            SemaphoreSlim gate = GetIndexUpdateLock(repository.Id);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.GetIndexUpdatedUtc(repository.Id).HasValue) return String.Empty;
                ReportSubmission(progress, "提交成功，正在增量更新搜索索引…", true);
                string directory = NormalizePath(attempt.Path);
                string committedRevision = revision ?? await FindSubmissionRevisionAsync(repository, attempt, CancellationToken.None).ConfigureAwait(false);
                ValidateRevision(committedRevision);
                long revisionNumber = Int64.Parse(committedRevision, CultureInfo.InvariantCulture);
                List<SvnEntry> children = await ListAtRevisionAsync(repository, directory, false, committedRevision, CancellationToken.None).ConfigureAwait(false);
                List<SvnEntry> roots = children.Where(entry => entry.Revision == committedRevision).ToList();
                if (roots.Count == EmptyCount) throw new InvalidDataException("未找到本次提交的新增条目。");

                List<SvnEntry> additions = new List<SvnEntry>(roots);
                foreach (SvnEntry folder in roots.Where(entry => entry.IsDirectory))
                    additions.AddRange(await ListAtRevisionAsync(repository, folder.Path, true, committedRevision, CancellationToken.None).ConfigureAwait(false));
                bool updated = _store.MergeAddedIndexEntries(repository.Id, revisionNumber, directory, roots, additions);
                return updated ? " 搜索索引已更新。" : String.Empty;
            }
            catch (Exception error)
            {
                _store.Log("index_additions_failed", "repository=" + repository.Id + " path=" + attempt.Path + " attempt=" + attempt.Id + " error=" + error.Message);
                return " 搜索索引更新失败，可点击“更新搜索索引”重新建立。";
            }
            finally { gate.Release(); }
        }

        /// <summary>Normalizes sources and rejects duplicate SVN destination names.</summary>
        /// <param name="localPaths">Local source paths.</param>
        /// <returns>Absolute local paths for unique destination names.</returns>
        private static List<string> ResolveAddSources(IEnumerable<string> localPaths)
        {
            if (localPaths == null) throw new ArgumentNullException("localPaths");
            List<string> sources = new List<string>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in localPaths)
            {
                if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("本地路径不能为空。", "localPaths");
                string fullPath = Path.GetFullPath(path);
                if (String.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("请选择盘符或网络共享下的文件和文件夹。");
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(fullPath);
                if (IsSvnAdministrativeName(name)) throw new IOException("不能提交 SVN 管理目录或同名文件“" + name + "”。");
                if (!names.Add(name)) throw new IOException("所选项目有同名项“" + name + "”，请选择不同名称的文件或文件夹。");
                sources.Add(fullPath);
            }
            if (sources.Count == EmptyCount) throw new ArgumentException("请选择要提交的本地文件或文件夹。", "localPaths");
            return sources;
        }

        /// <summary>Copies a selected tree without following links or including SVN administrative metadata.</summary>
        /// <param name="source">Selected file or directory.</param>
        /// <param name="destination">New location inside the isolated working copy.</param>
        /// <param name="cancellationToken">Cancels staging.</param>
        /// <returns>The number of staged SVN nodes.</returns>
        private static async Task<int> CopyAddItemAsync(string source, string destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(source);
            if ((attributes & FileAttributes.ReparsePoint) != EmptyCount)
                throw new IOException("不能提交链接或联接点：“" + source + "”。请选择实际文件或目录。");
            if ((attributes & FileAttributes.Directory) == EmptyCount)
            {
                await FreezeLocalFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
                return SingleEntryCount;
            }
            Directory.CreateDirectory(destination);
            int copied = SingleEntryCount;
            foreach (string child in Directory.EnumerateFileSystemEntries(source))
            {
                string name = Path.GetFileName(child);
                if (!IsSvnAdministrativeName(name))
                    copied += await CopyAddItemAsync(child, Path.Combine(destination, name), cancellationToken).ConfigureAwait(false);
            }
            return copied;
        }

        /// <summary>Checks SVN administrative names reserved by the working copy.</summary>
        /// <param name="name">Local file or directory name.</param>
        /// <returns>Whether SVN reserves the name for working copy metadata.</returns>
        private static bool IsSvnAdministrativeName(string name)
        {
            return String.Equals(name, SvnAdministrativeName, StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, SvnLegacyAdministrativeName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
