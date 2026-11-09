using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LightSvnBrowser
{
    /// <summary>Distinguishes confirmed results from interrupted submissions.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public enum SubmissionStatus { Committed, NoChanges, NotCommitted, Unknown }

    /// <summary>Describes an immutable submission stage for the UI.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public sealed class SubmissionProgress
    {
        public string Message { get; set; }
        public bool IsCommitting { get; set; }
    }

    /// <summary>Records the repository version from which a local download originated.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    [DataContract]
    public sealed class DownloadSource
    {
        [DataMember] public string RepositoryUrl { get; set; }
        [DataMember] public string Path { get; set; }
        [DataMember] public string LocalPath { get; set; }
        [DataMember] public string Revision { get; set; }
        [DataMember] public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>Persists the identity of a commit before it can reach the server.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    [DataContract]
    public sealed class SubmissionAttempt
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Path { get; set; }
        [DataMember] public string TargetUrl { get; set; }
        [DataMember] public string BaseRevision { get; set; }
        [DataMember] public string SnapshotRevision { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string LocalFile { get; set; }
        [DataMember] public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Returns the confirmed revision or the pending attempt requiring manual verification.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public sealed class SubmissionResult
    {
        public SubmissionStatus Status { get; set; }
        public string Revision { get; set; }
        public SvnEntry Entry { get; set; }
        public SubmissionAttempt Attempt { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Retains sanitized SVN diagnostics for distinguishing explicit commit rejections.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    internal sealed class SvnCommandException : IOException
    {
        internal string ServerError { get; private set; }

        /// <summary>Creates a sanitized command failure.</summary>
        /// <param name="message">User-facing failure.</param>
        /// <param name="serverError">Sanitized server diagnostic.</param>
        /// <returns>A command exception.</returns>
        internal SvnCommandException(string message, string serverError) : base(message) { ServerError = serverError; }
    }

    /// <summary>Implements single-file SVN submissions and durable result verification.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public sealed partial class SvnService
    {
        private const int EmptyCount = 0;
        private const int SingleEntryCount = 1;
        private const int SourceRecordLimit = 2000;
        private const long SourceFileLimit = 4L * 1024L * 1024L;
        private const long CommandOutputLimit = 1024L * 1024L;
        private const int CopyBufferSize = 65536;
        private const int MaximumMessageCharacters = 65536;
        private const string FileKind = "file";
        private const string ModifiedStatus = "modified";
        private const string NormalStatus = "normal";
        private const string NoneStatus = "none";
        private const string SpecialProperty = "svn:special";
        private const string SubmissionDirectoryName = "submissions";
        private const string DownloadSourcesName = "downloads.json";
        private const string AttemptSuffix = ".pending.json";
        private const string MarkerPrefix = "[LightSvnBrowser:";
        private const string MarkerSuffix = "]";
        private const string WorkingDirectoryName = "working-copy";
        private const string SnapshotName = "content.snapshot";
        private const string MessageName = "message.txt";
        private const string HeadRevision = "HEAD";
        private const string BaseRevisionKeyword = "BASE";
        private static readonly object SourceGate = new object();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> SubmissionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private const string PostCommitHookError = "E165007";
        private const string ClientCleanupError = "E200030";
        private const string HookError = "E165001";
        private const string PreCommitHook = "pre-commit";
        private const string StartCommitHook = "start-commit";
        private const string PostCommitHook = "post-commit";
        private static readonly string[] DefiniteRejectionCodes = { "E160028", "E155011", "E175013", "E215004", "E170001", "E195013", "E160037", "E155004", "E155007" };

        /// <summary>Holds one remote file and the repository snapshot at which it was read.</summary>
        /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
        private sealed class RemoteFileState
        {
            internal SvnEntry Entry;
            internal string SnapshotRevision;
        }

        /// <summary>Reads fresh metadata for a single existing repository file.</summary>
        /// <param name="repository">Repository credentials and root URL.</param>
        /// <param name="path">Repository-relative file path.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>Current file metadata.</returns>
        public async Task<SvnEntry> GetLatestFileAsync(Repository repository, string path, CancellationToken cancellationToken)
        {
            return (await ReadRemoteFileAsync(repository, path, cancellationToken).ConfigureAwait(false)).Entry;
        }

        /// <summary>Finds the recorded origin of a local file for the selected remote target.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="path">Selected remote file.</param>
        /// <param name="localFile">Local file selected for submission.</param>
        /// <returns>A matching origin, or null when the origin is unknown.</returns>
        public DownloadSource GetDownloadSource(Repository repository, string path, string localFile)
        {
            string targetUrl = BuildUrl(repository, path);
            string localPath = System.IO.Path.GetFullPath(localFile);
            lock (SourceGate)
            {
                return ReadSources().LastOrDefault(item => String.Equals(BuildSourceUrl(item), targetUrl, StringComparison.Ordinal) &&
                    String.Equals(item.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Restores an unresolved attempt, including after the application has restarted.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="path">Selected file.</param>
        /// <returns>The pending attempt, or null.</returns>
        public SubmissionAttempt GetPendingSubmission(Repository repository, string path)
        {
            string targetUrl = BuildUrl(repository, path);
            return ReadPendingSubmission(targetUrl, targetUrl, path);
        }

        /// <summary>Reads and validates the pending record for an exact submission target.</summary>
        /// <param name="pendingKey">Key identifying the submission type and target.</param>
        /// <param name="targetUrl">Expected canonical target URL.</param>
        /// <param name="path">Expected repository-relative path.</param>
        /// <returns>The pending attempt, or null.</returns>
        private SubmissionAttempt ReadPendingSubmission(string pendingKey, string targetUrl, string path)
        {
            string file = PendingPath(pendingKey);
            if (!File.Exists(file)) return null;
            SubmissionAttempt attempt = ReadSubmissionJson<SubmissionAttempt>(file, SourceFileLimit);
            Guid identity;
            if (attempt == null || !Guid.TryParseExact(attempt.Id, "N", out identity) ||
                !String.Equals(attempt.TargetUrl, targetUrl, StringComparison.Ordinal) ||
                !String.Equals(attempt.Path, NormalizePath(path), StringComparison.Ordinal))
                throw new InvalidDataException("待核实的提交记录无效。为避免重复提交，本次操作已停止，请保留 submissions 目录供排查。");
            ValidateRevision(attempt.BaseRevision);
            ValidateRevision(attempt.SnapshotRevision);
            return attempt;
        }

        /// <summary>Submits exactly one file from a frozen local snapshot, preserving its SVN properties.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="expectedEntry">File version explicitly reviewed by the user.</param>
        /// <param name="localFile">Local replacement content.</param>
        /// <param name="message">Required commit description.</param>
        /// <param name="progress">Preparation and irreversible commit stages.</param>
        /// <param name="cancellationToken">Cancels preparation only.</param>
        /// <returns>A confirmed result or an unresolved attempt.</returns>
        public async Task<SubmissionResult> SubmitFileAsync(Repository repository, SvnEntry expectedEntry, string localFile, string message,
            IProgress<SubmissionProgress> progress, CancellationToken cancellationToken)
        {
            ValidateFile(expectedEntry);
            if (String.IsNullOrWhiteSpace(message)) throw new ArgumentException("请填写提交说明。", "message");
            if (message.Length > MaximumMessageCharacters) throw new ArgumentException("提交说明不能超过 65,536 个字符。", "message");
            string path = NormalizePath(expectedEntry.Path);
            string targetUrl = BuildUrl(repository, path);
            string localPath = System.IO.Path.GetFullPath(localFile);
            SemaphoreSlim gate = SubmissionLocks.GetOrAdd(PendingPath(targetUrl), key => new SemaphoreSlim(SingleEntryCount, SingleEntryCount));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string attemptDirectory = null;
            bool keepAttempt = false;
            try
            {
                if (GetPendingSubmission(repository, path) != null) throw new InvalidOperationException("这个文件有一次提交结果待核实，请先核实结果，不能重复提交。");
                DownloadSource origin = GetDownloadSource(repository, path, localPath);
                if (origin != null && !String.Equals(origin.Revision, expectedEntry.Revision, StringComparison.Ordinal))
                    throw new InvalidOperationException("本地文件基于 r" + origin.Revision + "，仓库文件已更新为 r" + expectedEntry.Revision + "。请先查看最新版并处理修改，再提交。");
                ReportSubmission(progress, "正在检查版本并准备单文件副本…", false);
                RemoteFileState current = await ReadRemoteFileAsync(repository, path, cancellationToken).ConfigureAwait(false);
                if (!String.Equals(current.Entry.Revision, expectedEntry.Revision, StringComparison.Ordinal))
                    throw new InvalidOperationException("仓库文件已从 r" + expectedEntry.Revision + " 更新为 r" + current.Entry.Revision + "。请刷新版本并处理修改后再提交。");
                SubmissionAttempt attempt = new SubmissionAttempt
                {
                    Id = Guid.NewGuid().ToString("N"), Path = path, TargetUrl = targetUrl, BaseRevision = expectedEntry.Revision,
                    SnapshotRevision = current.SnapshotRevision, Message = message.Trim(), LocalFile = localPath, CreatedUtc = DateTime.UtcNow
                };
                attemptDirectory = System.IO.Path.Combine(SubmissionRoot(), attempt.Id);
                Directory.CreateDirectory(attemptDirectory);
                string snapshotPath = System.IO.Path.Combine(attemptDirectory, SnapshotName);
                await FreezeLocalFileAsync(localPath, snapshotPath, cancellationToken).ConfigureAwait(false);
                string workingCopy = System.IO.Path.Combine(attemptDirectory, WorkingDirectoryName);
                int separator = path.LastIndexOf('/');
                string parent = separator < EmptyCount ? String.Empty : path.Substring(EmptyCount, separator);
                string fileName = separator < EmptyCount ? path : path.Substring(separator + SingleEntryCount);
                await RunSubmissionCommandAsync(repository, new List<string> { "checkout", "--depth", "empty", "--ignore-externals", "--revision", current.SnapshotRevision,
                    "--", BuildUrl(repository, parent) + "@" + current.SnapshotRevision, workingCopy }, cancellationToken).ConfigureAwait(false);
                string workingFile = System.IO.Path.Combine(workingCopy, fileName);
                await RunSubmissionCommandAsync(repository, new List<string> { "update", "--depth", "empty", "--ignore-externals", "--revision", current.SnapshotRevision,
                    "--", workingFile + "@" }, cancellationToken).ConfigureAwait(false);
                await ValidateWorkingFileAsync(repository, workingFile, cancellationToken).ConfigureAwait(false);
                File.SetAttributes(workingFile, File.GetAttributes(workingFile) & ~FileAttributes.ReadOnly);
                File.Copy(snapshotPath, workingFile, true);
                string status = await ReadWorkingStatusAsync(repository, workingFile, cancellationToken).ConfigureAwait(false);
                if (String.Equals(status, NormalStatus, StringComparison.Ordinal))
                {
                    RecordDownloadSource(repository, current.Entry, localPath);
                    _store.Log("submission_no_changes", "repository=" + repository.Id + " path=" + path + " revision=" + current.Entry.Revision);
                    return new SubmissionResult { Status = SubmissionStatus.NoChanges, Entry = current.Entry, Revision = current.Entry.Revision, Message = "文件内容与仓库一致，无需提交。" };
                }
                if (!String.Equals(status, ModifiedStatus, StringComparison.Ordinal)) throw new InvalidDataException("单文件副本状态异常，已停止提交：" + status);
                string messagePath = System.IO.Path.Combine(attemptDirectory, MessageName);
                File.WriteAllText(messagePath, attempt.Message + Environment.NewLine + Environment.NewLine + SubmissionMarker(attempt), new UTF8Encoding(false));
                cancellationToken.ThrowIfCancellationRequested();
                ReportSubmission(progress, "正在提交新版本，请等待结果；此阶段不能撤销…", true);
                cancellationToken.ThrowIfCancellationRequested();
                WriteSubmissionJson(PendingPath(targetUrl), attempt);
                keepAttempt = true;
                _store.Log("submission_commit_start", "repository=" + repository.Id + " path=" + path + " attempt=" + attempt.Id + " base=" + attempt.BaseRevision);
                byte[] output;
                try
                {
                    output = await RunSubmissionCommandAsync(repository, new List<string> { "commit", "--depth", "empty", "--file", messagePath,
                        "--encoding", "UTF-8", "--", workingFile + "@" }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    SvnCommandException rejected = error as SvnCommandException;
                    if (rejected != null && IsDefiniteRejection(rejected.ServerError))
                    {
                        DeletePending(targetUrl);
                        keepAttempt = false;
                        _store.Log("submission_rejected", "repository=" + repository.Id + " path=" + path + " attempt=" + attempt.Id + " error=" + error.Message);
                        return new SubmissionResult { Status = SubmissionStatus.NotCommitted, Message = error.Message };
                    }
                    _store.Log("submission_unknown", "repository=" + repository.Id + " path=" + path + " attempt=" + attempt.Id + " error=" + error.Message);
                    return new SubmissionResult { Status = SubmissionStatus.Unknown, Attempt = attempt, Message = "提交结果待核实。服务器可能已经保存，请先核实结果，不要重复提交。\r\n" + error.Message };
                }
                SvnEntry committedEntry = null;
                string committedRevision = null;
                try
                {
                    committedEntry = await ReadCommittedWorkingEntryAsync(repository, path, workingFile).ConfigureAwait(false);
                    committedRevision = committedEntry.Revision;
                }
                catch (Exception error)
                {
                    Match revisionMatch = Regex.Match(Encoding.UTF8.GetString(output), @"Committed revision\s+(\d+)", RegexOptions.CultureInvariant);
                    if (revisionMatch.Success) committedRevision = revisionMatch.Groups[SingleEntryCount].Value;
                    _store.Log("submission_metadata_unavailable", "repository=" + repository.Id + " path=" + path + " attempt=" + attempt.Id + " error=" + error.Message);
                }
                keepAttempt = !TryDeleteResolvedPending(targetUrl);
                if (!String.IsNullOrEmpty(committedRevision)) RecordDownloadSource(repository, new SvnEntry { Path = path, Revision = committedRevision }, localPath);
                _store.Log("submission_committed", "repository=" + repository.Id + " path=" + path + " attempt=" + attempt.Id + " revision=" + committedRevision);
                return new SubmissionResult { Status = SubmissionStatus.Committed, Entry = committedEntry, Revision = committedRevision,
                    Message = String.IsNullOrEmpty(committedRevision) ? "新版本已提交成功，版本信息暂未读取。" : "已提交新版本 r" + committedRevision + "。" };
            }
            finally
            {
                if (!keepAttempt && attemptDirectory != null) CleanupAttemptDirectory(attemptDirectory);
                gate.Release();
            }
        }

        /// <summary>Checks the server log for the exact marker of an interrupted commit without resubmitting.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="attempt">The durable pending attempt.</param>
        /// <param name="cancellationToken">Cancels this read-only verification.</param>
        /// <returns>Committed only when the exact marker is found; otherwise Unknown.</returns>
        public async Task<SubmissionResult> VerifySubmissionAsync(Repository repository, SubmissionAttempt attempt, CancellationToken cancellationToken)
        {
            if (attempt == null) throw new ArgumentNullException("attempt");
            string targetUrl = BuildUrl(repository, attempt.Path);
            SemaphoreSlim gate = SubmissionLocks.GetOrAdd(PendingPath(targetUrl), key => new SemaphoreSlim(SingleEntryCount, SingleEntryCount));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SubmissionAttempt pending = GetPendingSubmission(repository, attempt.Path);
                if (pending == null || !String.Equals(pending.Id, attempt.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException("待核实记录已变化，请重新打开提交窗口。");
                _store.Log("submission_verify_start", "repository=" + repository.Id + " path=" + attempt.Path + " attempt=" + attempt.Id);
                string revision = await FindSubmissionRevisionAsync(repository, pending, cancellationToken).ConfigureAwait(false);
                if (revision == null)
                {
                    _store.Log("submission_verify_unresolved", "repository=" + repository.Id + " path=" + attempt.Path + " attempt=" + attempt.Id);
                    return new SubmissionResult { Status = SubmissionStatus.Unknown, Attempt = pending,
                        Message = "暂未在可读取的提交记录中找到本次提交，仍不能确认它未提交。请稍后再次核实，或联系仓库管理员核对；不会重复提交。" };
                }
                SvnEntry entry = null;
                string workingFile = System.IO.Path.Combine(SubmissionRoot(), pending.Id, WorkingDirectoryName, System.IO.Path.GetFileName(pending.Path));
                try
                {
                    SvnEntry localEntry = await ReadCommittedWorkingEntryAsync(repository, pending.Path, workingFile).ConfigureAwait(false);
                    if (String.Equals(localEntry.Revision, revision, StringComparison.Ordinal)) entry = localEntry;
                }
                catch (Exception error) { _store.Log("submission_verify_metadata_unavailable", "attempt=" + attempt.Id + " error=" + error.Message); }
                if (TryDeleteResolvedPending(targetUrl)) CleanupAttemptDirectory(System.IO.Path.Combine(SubmissionRoot(), pending.Id));
                RecordDownloadSource(repository, new SvnEntry { Path = pending.Path, Revision = revision }, pending.LocalFile);
                _store.Log("submission_verified", "repository=" + repository.Id + " path=" + attempt.Path + " attempt=" + attempt.Id + " revision=" + revision);
                return new SubmissionResult { Status = SubmissionStatus.Committed, Entry = entry, Revision = revision, Message = "已核实提交成功，版本 r" + revision + "。" };
            }
            finally { gate.Release(); }
        }

        /// <summary>Reads the commit revision associated with a pending attempt's exact log marker.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="pending">Validated pending record.</param>
        /// <param name="cancellationToken">Cancels the history lookup.</param>
        /// <returns>The matching revision, or null when the marker is absent.</returns>
        private async Task<string> FindSubmissionRevisionAsync(Repository repository, SubmissionAttempt pending, CancellationToken cancellationToken)
        {
            byte[] bytes = await RunSubmissionCommandAsync(repository, new List<string> { "log", "--xml", "--revision", HeadRevision + ":" + pending.SnapshotRevision,
                "--search", "*LightSvnBrowser:" + pending.Id + "*",
                "--", pending.TargetUrl + "@" + HeadRevision }, cancellationToken).ConfigureAwait(false);
            XElement found = ParseXml(bytes).Descendants("logentry").FirstOrDefault(logEntry =>
                ((string)logEntry.Element("msg") ?? String.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => String.Equals(line, SubmissionMarker(pending), StringComparison.Ordinal)));
            if (found == null) return null;
            string revision = (string)found.Attribute("revision");
            ValidateRevision(revision);
            return revision;
        }

        /// <summary>Reads an authoritative remote file at one repository snapshot.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="path">Relative file path.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>File metadata with its global snapshot revision.</returns>
        private async Task<RemoteFileState> ReadRemoteFileAsync(Repository repository, string path, CancellationToken cancellationToken)
        {
            string relative = NormalizePath(path);
            string url = BuildUrl(repository, relative);
            XElement info = ParseXml(await RunSubmissionCommandAsync(repository, new List<string> { "info", "--xml", "--revision", HeadRevision,
                "--", url + "@HEAD" }, cancellationToken).ConfigureAwait(false)).Descendants("entry").SingleOrDefault();
            if (info == null || !String.Equals((string)info.Attribute("kind"), FileKind, StringComparison.Ordinal)) throw new FileNotFoundException("目标文件已删除、移动或变成文件夹，请重新定位后提交。");
            string snapshot = (string)info.Attribute("revision");
            ValidateRevision(snapshot);
            long size;
            bool hasSize = Int64.TryParse((string)info.Attribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out size) && size >= EmptyCount;
            XElement commit = info.Element("commit");
            if (!hasSize)
            {
                XElement listed = ParseXml(await RunSubmissionCommandAsync(repository, new List<string> { "list", "--xml", "--revision", snapshot,
                    "--depth", "empty", "--", url + "@" + snapshot }, cancellationToken).ConfigureAwait(false)).Descendants("entry").SingleOrDefault();
                if (listed == null || !String.Equals((string)listed.Attribute("kind"), FileKind, StringComparison.Ordinal) ||
                    !Int64.TryParse((string)listed.Element("size"), NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < EmptyCount)
                    throw new InvalidDataException("SVN 返回的文件信息不完整，请刷新后重试。");
                commit = listed.Element("commit");
            }
            SvnEntry entry = MetadataFromCommit(relative, size, commit);
            return new RemoteFileState { Entry = entry, SnapshotRevision = snapshot };
        }

        /// <summary>Creates display metadata from an SVN commit node.</summary>
        /// <param name="path">Relative target path.</param>
        /// <param name="size">Repository content length.</param>
        /// <param name="commit">SVN commit node.</param>
        /// <returns>Validated file metadata.</returns>
        private static SvnEntry MetadataFromCommit(string path, long size, XElement commit)
        {
            string revision = commit == null ? null : (string)commit.Attribute("revision");
            ValidateRevision(revision);
            DateTime modified;
            if (!DateTime.TryParse((string)commit.Element("date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out modified))
                modified = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            return new SvnEntry { Path = path, Name = path.Split('/').Last(), Revision = revision, Size = size,
                Author = (string)commit.Element("author") ?? String.Empty, ModifiedUtc = modified, IsDirectory = false };
        }

        /// <summary>Copies local bytes while denying concurrent writes to the opened source.</summary>
        /// <param name="sourcePath">Local replacement content.</param>
        /// <param name="snapshotPath">Program-owned frozen snapshot.</param>
        /// <param name="cancellationToken">Cancels preparation.</param>
        /// <returns>Completion of the snapshot copy.</returns>
        private static async Task FreezeLocalFileAsync(string sourcePath, string snapshotPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("请选择存在的本地文件。", sourcePath);
            if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != EmptyCount) throw new IOException("请选择实际文件，不支持链接文件。");
            using (FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, true))
            using (FileStream destination = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, true))
            {
                await source.CopyToAsync(destination, CopyBufferSize, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Rejects special files before replacing a working copy's content.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="workingFile">Exact local working file.</param>
        /// <param name="cancellationToken">Cancels preparation.</param>
        /// <returns>Completion of type and property validation.</returns>
        private async Task ValidateWorkingFileAsync(Repository repository, string workingFile, CancellationToken cancellationToken)
        {
            if (!File.Exists(workingFile) || (File.GetAttributes(workingFile) & FileAttributes.ReparsePoint) != EmptyCount)
                throw new IOException("目标不是普通文件，不能提交新版本。");
            byte[] properties = await RunSubmissionCommandAsync(repository, new List<string> { "proplist", "--xml", "--depth", "empty", "--", workingFile + "@" }, cancellationToken).ConfigureAwait(false);
            if (ParseXml(properties).Descendants("property").Any(item => String.Equals((string)item.Attribute("name"), SpecialProperty, StringComparison.Ordinal)))
                throw new IOException("暂不支持更新 SVN 链接文件，请使用完整 SVN 客户端处理。");
        }

        /// <summary>Checks only the intended file for normalized SVN changes.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="workingFile">Exact local target.</param>
        /// <param name="cancellationToken">Cancels preparation.</param>
        /// <returns>The working file's SVN content state.</returns>
        private async Task<string> ReadWorkingStatusAsync(Repository repository, string workingFile, CancellationToken cancellationToken)
        {
            byte[] bytes = await RunSubmissionCommandAsync(repository, new List<string> { "status", "--xml", "--depth", "empty", "--", workingFile + "@" }, cancellationToken).ConfigureAwait(false);
            List<XElement> entries = ParseXml(bytes).Descendants("wc-status").ToList();
            if (entries.Count == EmptyCount) return NormalStatus;
            if (entries.Count != SingleEntryCount) throw new InvalidDataException("单文件工作副本出现额外目标，已停止提交。");
            string propertyStatus = (string)entries[EmptyCount].Attribute("props");
            if (!String.Equals(propertyStatus, NoneStatus, StringComparison.Ordinal) && !String.Equals(propertyStatus, NormalStatus, StringComparison.Ordinal))
                throw new InvalidDataException("工作副本属性发生变化，已停止提交。");
            return (string)entries[EmptyCount].Attribute("item");
        }

        /// <summary>Reads the exact committed revision and normalized byte size from the local working copy.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="relativePath">Remote relative path.</param>
        /// <param name="workingFile">Committed local working copy file.</param>
        /// <returns>Metadata for the submitted revision, without requesting HEAD.</returns>
        private async Task<SvnEntry> ReadCommittedWorkingEntryAsync(Repository repository, string relativePath, string workingFile)
        {
            byte[] info = await RunSubmissionCommandAsync(repository, new List<string> { "info", "--xml", "--", workingFile + "@" }, CancellationToken.None).ConfigureAwait(false);
            XElement entry = ParseXml(info).Descendants("entry").SingleOrDefault();
            if (entry == null) throw new InvalidDataException("无法读取已提交版本。");
            using (CountingSink sink = new CountingSink())
            {
                await RunAsync(repository, new List<string> { "cat", "--revision", BaseRevisionKeyword, "--", workingFile + "@" }, sink,
                    Int64.MaxValue, DownloadTimeoutMilliseconds, CancellationToken.None).ConfigureAwait(false);
                return MetadataFromCommit(relativePath, sink.Length, entry.Element("commit"));
            }
        }

        /// <summary>Runs one isolated SVN command and returns bounded output.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="arguments">Explicit SVN arguments.</param>
        /// <param name="cancellationToken">Command cancellation.</param>
        /// <returns>Command output bytes.</returns>
        private async Task<byte[]> RunSubmissionCommandAsync(Repository repository, IList<string> arguments, CancellationToken cancellationToken)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                await RunAsync(repository, arguments, stream, CommandOutputLimit, DownloadTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                return stream.ToArray();
            }
        }

        /// <summary>Stores the source version after a successful download or confirmed submission.</summary>
        /// <param name="repository">Selected repository.</param>
        /// <param name="entry">File and source revision.</param>
        /// <param name="localPath">Local download path.</param>
        /// <returns>No value.</returns>
        private void RecordDownloadSource(Repository repository, SvnEntry entry, string localPath)
        {
            try
            {
                string fullPath = System.IO.Path.GetFullPath(localPath);
                lock (SourceGate)
                {
                    List<DownloadSource> records = ReadSources();
                    records.RemoveAll(item => String.Equals(item.LocalPath, fullPath, StringComparison.OrdinalIgnoreCase));
                    records.Add(new DownloadSource { RepositoryUrl = BuildUrl(repository, String.Empty), Path = NormalizePath(entry.Path),
                        LocalPath = fullPath, Revision = entry.Revision, UpdatedUtc = DateTime.UtcNow });
                    WriteSubmissionJson(System.IO.Path.Combine(_store.DataDirectory, DownloadSourcesName), records.OrderByDescending(item => item.UpdatedUtc).Take(SourceRecordLimit).ToList());
                }
            }
            catch (Exception error) { _store.Log("download_source_unavailable", "repository=" + repository.Id + " path=" + entry.Path + " error=" + error.Message); }
        }

        /// <summary>Reads bounded local provenance without hiding corrupt records as unknown origins.</summary>
        /// <returns>All recorded download origins.</returns>
        private List<DownloadSource> ReadSources()
        {
            string file = System.IO.Path.Combine(_store.DataDirectory, DownloadSourcesName);
            return File.Exists(file) ? (ReadSubmissionJson<List<DownloadSource>>(file, SourceFileLimit) ?? new List<DownloadSource>()) : new List<DownloadSource>();
        }

        /// <summary>Reconstructs a credential-free target identity from a source record.</summary>
        /// <param name="source">One download record.</param>
        /// <returns>The canonical target URL.</returns>
        private static string BuildSourceUrl(DownloadSource source) { return BuildUrl(new Repository { Url = source.RepositoryUrl }, source.Path); }

        /// <summary>Returns the program-owned submission directory.</summary>
        /// <returns>Absolute submission directory.</returns>
        private string SubmissionRoot()
        {
            string directory = System.IO.Path.Combine(_store.DataDirectory, SubmissionDirectoryName);
            Directory.CreateDirectory(directory);
            return directory;
        }

        /// <summary>Returns the stable pending-record path for a remote file.</summary>
        /// <param name="targetUrl">Canonical target URL.</param>
        /// <returns>The pending record's absolute path.</returns>
        private string PendingPath(string targetUrl) { return System.IO.Path.Combine(SubmissionRoot(), AppStore.Hash(targetUrl) + AttemptSuffix); }

        /// <summary>Builds the exact log marker used for manual reconciliation.</summary>
        /// <param name="attempt">Submission identity.</param>
        /// <returns>Unique visible log marker.</returns>
        private static string SubmissionMarker(SubmissionAttempt attempt) { return MarkerPrefix + attempt.Id + MarkerSuffix; }

        /// <summary>Persists one JSON file atomically before a commit can start.</summary>
        /// <param name="path">Program-owned record path.</param>
        /// <param name="value">Serializable metadata.</param>
        /// <param name="replaceExisting">Whether a record already at the destination may be replaced.</param>
        /// <returns>No value.</returns>
        private static void WriteSubmissionJson<T>(string path, T value, bool replaceExisting = true)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                    stream.Flush(true);
                }
                if (replaceExisting && File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { AppStore.TryDeleteFile(temporary); }
        }

        /// <summary>Reads a bounded local metadata file.</summary>
        /// <param name="path">Metadata path.</param>
        /// <param name="limit">Maximum accepted bytes.</param>
        /// <returns>Deserialized metadata.</returns>
        private static T ReadSubmissionJson<T>(string path, long limit)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length > limit) throw new InvalidDataException("本地提交元数据超过容量上限，请保留文件供排查：" + path);
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
            }
        }

        /// <summary>Removes a resolved target's durable pending marker.</summary>
        /// <param name="targetUrl">Exact target URL.</param>
        /// <returns>No value.</returns>
        private void DeletePending(string targetUrl) { AppStore.DeleteFile(PendingPath(targetUrl)); }

        /// <summary>Reports an explicit preparation or commit stage.</summary>
        /// <param name="progress">Optional observer.</param>
        /// <param name="message">User-facing stage.</param>
        /// <param name="isCommitting">Whether irreversible submission is starting.</param>
        /// <returns>No value.</returns>
        private static void ReportSubmission(IProgress<SubmissionProgress> progress, string message, bool isCommitting)
        {
            if (progress != null) progress.Report(new SubmissionProgress { Message = message, IsCommitting = isCommitting });
        }

        /// <summary>Recognizes server rejection while prioritizing failures that can occur after a successful commit.</summary>
        /// <param name="error">Sanitized SVN diagnostic chain.</param>
        /// <returns>True only for explicit rejection before a commit completes.</returns>
        private static bool IsDefiniteRejection(string error)
        {
            if (error.IndexOf(PostCommitHookError, StringComparison.Ordinal) >= EmptyCount ||
                error.IndexOf(ClientCleanupError, StringComparison.Ordinal) >= EmptyCount ||
                error.IndexOf(PostCommitHook, StringComparison.OrdinalIgnoreCase) >= EmptyCount) return false;
            if (DefiniteRejectionCodes.Any(code => error.IndexOf(code, StringComparison.Ordinal) >= EmptyCount)) return true;
            return error.IndexOf(HookError, StringComparison.Ordinal) >= EmptyCount &&
                (error.IndexOf(PreCommitHook, StringComparison.OrdinalIgnoreCase) >= EmptyCount || error.IndexOf(StartCommitHook, StringComparison.OrdinalIgnoreCase) >= EmptyCount);
        }

        /// <summary>Keeps a confirmed result successful even when local pending-record cleanup fails.</summary>
        /// <param name="targetUrl">Resolved remote target.</param>
        /// <returns>Whether the local pending marker was removed.</returns>
        private bool TryDeleteResolvedPending(string targetUrl)
        {
            try { DeletePending(targetUrl); return true; }
            catch (Exception error) { _store.Log("submission_resolved_record_retained", "target=" + targetUrl + " error=" + error.Message); return false; }
        }

        /// <summary>Deletes only a GUID-named child of the verified submission directory.</summary>
        /// <param name="directory">Attempt directory to clean.</param>
        /// <returns>No value.</returns>
        private void CleanupAttemptDirectory(string directory)
        {
            try
            {
                string fullPath = System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                string root = System.IO.Path.GetFullPath(SubmissionRoot()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                Guid identity;
                if (!String.Equals(System.IO.Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(System.IO.Path.GetFileName(fullPath), "N", out identity))
                    throw new IOException("拒绝清理提交目录之外的路径。");
                if (!Directory.Exists(fullPath)) return;
                Stack<string> pending = new Stack<string>();
                List<string> directories = new List<string>();
                pending.Push(fullPath);
                while (pending.Count > EmptyCount)
                {
                    string item = pending.Pop();
                    if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != EmptyCount) throw new IOException("提交目录中存在链接，已保留目录供排查。");
                    directories.Add(item);
                    foreach (string child in Directory.GetDirectories(item)) pending.Push(child);
                }
                foreach (string item in directories)
                    foreach (string file in Directory.GetFiles(item))
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != EmptyCount) throw new IOException("提交目录中存在链接文件，已保留目录供排查。");
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                Directory.Delete(fullPath, true);
            }
            catch (Exception error) { _store.Log("submission_cleanup_unavailable", "directory=" + directory + " error=" + error.Message); }
        }

        /// <summary>Counts normalized pristine bytes without storing another file body.</summary>
        /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
        private sealed class CountingSink : Stream
        {
            private long count;
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return count; } }
            public override long Position { get { return count; } set { throw new NotSupportedException(); } }
            /// <summary>Completes an in-memory count flush.</summary>
            /// <returns>No value.</returns>
            public override void Flush() { }
            /// <summary>Rejects reads from the counting sink.</summary>
            /// <param name="buffer">Unused buffer.</param><param name="offset">Unused offset.</param><param name="length">Unused count.</param>
            /// <returns>No value.</returns>
            public override int Read(byte[] buffer, int offset, int length) { throw new NotSupportedException(); }
            /// <summary>Rejects seeking.</summary>
            /// <param name="offset">Unused offset.</param><param name="origin">Unused origin.</param>
            /// <returns>No value.</returns>
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            /// <summary>Rejects length changes.</summary>
            /// <param name="value">Unused length.</param>
            /// <returns>No value.</returns>
            public override void SetLength(long value) { throw new NotSupportedException(); }
            /// <summary>Counts discarded bytes.</summary>
            /// <param name="buffer">Discarded bytes.</param><param name="offset">Buffer offset.</param><param name="length">Byte count.</param>
            /// <returns>No value.</returns>
            public override void Write(byte[] buffer, int offset, int length) { count += length; }
        }
    }
}
