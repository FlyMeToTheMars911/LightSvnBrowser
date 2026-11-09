using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LightSvnBrowser
{
    /// <summary>Remembers the last successfully visited directory for each configured SVN repository.</summary>
    /// <remarks>Author: Codex, date: 2026-11-09.</remarks>
    internal sealed class RepositoryNavigationStore
    {
        private const int MaxRecordBytes = 64 * 1024;
        private static readonly Encoding RecordEncoding = new UTF8Encoding(false, true);
        private readonly AppStore store;
        private readonly string directory;
        private readonly Dictionary<string, string> sessionPaths = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Uses optional navigation records kept separately from application settings.</summary>
        /// <param name="store">Application data directory and diagnostic log.</param>
        /// <returns>No value.</returns>
        internal RepositoryNavigationStore(AppStore store)
        {
            this.store = store;
            directory = Path.Combine(store.DataDirectory, "navigation");
        }

        /// <summary>Returns the last visited relative path or the repository root.</summary>
        /// <param name="repository">Configured repository.</param>
        /// <returns>The remembered path, or an empty string for the repository root.</returns>
        internal string GetPath(Repository repository)
        {
            string record = RecordPath(repository);
            string relativePath;
            if (sessionPaths.TryGetValue(record, out relativePath)) return relativePath;
            try
            {
                using (FileStream stream = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, RecordEncoding, false))
                {
                    if (stream.Length > MaxRecordBytes) throw new InvalidDataException("Navigation record is too large.");
                    relativePath = reader.ReadToEnd();
                }
                SvnService.BuildUrl(repository, relativePath);
                relativePath = relativePath.Replace('\\', '/').TrimEnd('/');
                sessionPaths[record] = relativePath;
                return relativePath;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
                    error is UriFormatException || error is InvalidDataException)) throw;
                LogFailure("read", repository, error);
            }
            return String.Empty;
        }

        /// <summary>Records a successfully loaded repository directory.</summary>
        /// <param name="repository">Configured repository.</param>
        /// <param name="relativePath">Relative path used to load the directory.</param>
        /// <returns>No value.</returns>
        internal void RememberPath(Repository repository, string relativePath)
        {
            string record = RecordPath(repository);
            string path = (relativePath ?? String.Empty).Replace('\\', '/').TrimEnd('/');
            sessionPaths[record] = path;
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = RecordEncoding.GetBytes(path);
                if (bytes.Length > MaxRecordBytes) throw new InvalidDataException("Navigation record is too large.");
                Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(record)) File.Replace(temporary, record, null);
                else
                {
                    try { File.Move(temporary, record); }
                    catch (IOException)
                    {
                        if (!File.Exists(record)) throw;
                        File.Replace(temporary, record, null);
                    }
                }
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidDataException)) throw;
                LogFailure("write", repository, error);
            }
            finally { AppStore.TryDeleteFile(temporary); }
        }

        /// <summary>Removes the navigation record for a deleted or repointed repository.</summary>
        /// <param name="repository">Repository with its original configured URL.</param>
        /// <returns>No value.</returns>
        internal void Forget(Repository repository)
        {
            string record = RecordPath(repository);
            sessionPaths.Remove(record);
            if (!AppStore.TryDeleteFile(record)) store.Log("navigation.path_store", "repository=" + repository.Id + " action=delete failed");
        }

        private string RecordPath(Repository repository)
        {
            string root = SvnService.BuildUrl(repository, String.Empty);
            return Path.Combine(directory, AppStore.Hash(repository.Id + "\n" + root) + ".txt");
        }

        private void LogFailure(string action, Repository repository, Exception error)
        {
            store.Log("navigation.path_store", "repository=" + repository.Id + " action=" + action + " error=" + error.GetType().Name);
        }
    }
}
