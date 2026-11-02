using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LightSvnBrowser
{
    public sealed class FilenameSearchMatch
    {
        public Repository Repository { get; set; }
        public SvnEntry Entry { get; set; }
    }

    public sealed class FilenameSearchResult
    {
        public List<FilenameSearchMatch> Matches { get; set; }
        public int TotalCount { get; set; }
        public DateTime OldestIndexUpdatedUtc { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public sealed class FilenameSearchEngine
    {
        private const int CancellationInterval = 128;
        private readonly AppStore _store;
        private readonly SvnService _service;

        public FilenameSearchEngine(AppStore store, SvnService service)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (service == null) throw new ArgumentNullException("service");
            _store = store;
            _service = service;
        }

        public Task<FilenameSearchResult> SearchAsync(IList<Repository> repositories, string query, string pathPrefix, int limit, Action<string> progress, CancellationToken token)
        {
            if (repositories == null) throw new ArgumentNullException("repositories");
            if (limit < 1) throw new ArgumentOutOfRangeException("limit", "搜索显示上限必须大于零。");
            string[] terms = (query ?? String.Empty).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (terms.Length == 0) throw new ArgumentException("请输入文件名关键词。", "query");
            string prefix = (pathPrefix ?? String.Empty).Replace('\\', '/').TrimEnd('/');
            if (prefix.StartsWith("/", StringComparison.Ordinal) || prefix.Split('/').Any(part => part == "." || part == ".."))
                throw new ArgumentException("搜索目录必须是仓库内的相对路径。", "pathPrefix");
            string prefixBoundary = prefix.Length == 0 ? String.Empty : prefix + "/";
            Repository[] selectedRepositories = repositories.ToArray();
            if (selectedRepositories.Any(repository => repository == null)) throw new ArgumentException("搜索仓库不能为空。", "repositories");
            CandidateComparer comparer = new CandidateComparer(CultureInfo.CurrentCulture.CompareInfo);
            return Task.Run(async delegate
            {
                Stopwatch timer = Stopwatch.StartNew();
                List<Candidate> heap = new List<Candidate>(limit);
                int total = 0;
                DateTime oldest = DateTime.MaxValue;
                for (int repositoryIndex = 0; repositoryIndex < selectedRepositories.Length; repositoryIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    Repository repository = selectedRepositories[repositoryIndex];
                    if (progress != null) progress("正在读取“" + repository.Name + "”的本地文件名索引…");
                    token.ThrowIfCancellationRequested();
                    IndexSnapshot snapshot = _store.LoadIndex(repository.Id);
                    token.ThrowIfCancellationRequested();
                    if (snapshot == null)
                    {
                        if (progress != null) progress("首次建立“" + repository.Name + "”的目录索引，只读取文件名和属性…");
                        snapshot = await _service.BuildIndexAsync(repository, token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    if (snapshot.UpdatedUtc < oldest) oldest = snapshot.UpdatedUtc;
                    if (progress != null) progress("正在搜索“" + repository.Name + "”的文件名…");
                    for (int entryIndex = 0; entryIndex < snapshot.Entries.Count; entryIndex++)
                    {
                        if (entryIndex % CancellationInterval == 0) token.ThrowIfCancellationRequested();
                        SvnEntry entry = snapshot.Entries[entryIndex];
                        if (entry == null || entry.Name == null) continue;
                        if (prefixBoundary.Length > 0 && (entry.Path == null || !entry.Path.StartsWith(prefixBoundary, StringComparison.Ordinal))) continue;
                        bool matches = true;
                        for (int termIndex = 0; termIndex < terms.Length; termIndex++)
                        {
                            if (entry.Name.IndexOf(terms[termIndex], StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            matches = false;
                            break;
                        }
                        if (!matches) continue;
                        total = checked(total + 1);
                        Candidate candidate = new Candidate { Repository = repository, Entry = entry, RepositoryOrdinal = repositoryIndex };
                        KeepBest(heap, candidate, limit, comparer);
                    }
                }
                token.ThrowIfCancellationRequested();
                heap.Sort(comparer);
                List<FilenameSearchMatch> result = new List<FilenameSearchMatch>(heap.Count);
                foreach (Candidate candidate in heap)
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(new FilenameSearchMatch { Repository = candidate.Repository, Entry = candidate.Entry });
                }
                timer.Stop();
                _store.Log("filename_search_complete", "repositories=" + selectedRepositories.Length + " total=" + total + " shown=" + result.Count + " elapsed_ms=" + timer.ElapsedMilliseconds);
                return new FilenameSearchResult
                {
                    Matches = result, TotalCount = total, OldestIndexUpdatedUtc = oldest == DateTime.MaxValue ? DateTime.MinValue : oldest,
                    ElapsedMilliseconds = timer.ElapsedMilliseconds
                };
            }, token);
        }

        private struct Candidate
        {
            public Repository Repository;
            public SvnEntry Entry;
            public int RepositoryOrdinal;
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            private readonly CompareInfo _names;
            public CandidateComparer(CompareInfo names) { _names = names; }
            public int Compare(Candidate left, Candidate right)
            {
                if (left.Entry.IsDirectory != right.Entry.IsDirectory) return left.Entry.IsDirectory ? -1 : 1;
                int order = _names.Compare(left.Entry.Name, right.Entry.Name, CompareOptions.IgnoreCase);
                if (order != 0) return order;
                order = _names.Compare(left.Repository.Name, right.Repository.Name, CompareOptions.IgnoreCase);
                if (order != 0) return order;
                order = StringComparer.Ordinal.Compare(left.Entry.Path, right.Entry.Path);
                if (order != 0) return order;
                order = StringComparer.Ordinal.Compare(left.Repository.Id, right.Repository.Id);
                return order != 0 ? order : left.RepositoryOrdinal.CompareTo(right.RepositoryOrdinal);
            }
        }

        private static void KeepBest(List<Candidate> heap, Candidate candidate, int limit, IComparer<Candidate> comparer)
        {
            if (heap.Count < limit)
            {
                heap.Add(candidate);
                int child = heap.Count - 1;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (comparer.Compare(heap[child], heap[parent]) <= 0) break;
                    Swap(heap, child, parent);
                    child = parent;
                }
                return;
            }
            if (comparer.Compare(candidate, heap[0]) >= 0) return;
            heap[0] = candidate;
            int position = 0;
            while (true)
            {
                int child = position * 2 + 1;
                if (child >= heap.Count) break;
                int right = child + 1;
                if (right < heap.Count && comparer.Compare(heap[right], heap[child]) > 0) child = right;
                if (comparer.Compare(heap[position], heap[child]) >= 0) break;
                Swap(heap, position, child);
                position = child;
            }
        }

        private static void Swap(List<Candidate> heap, int left, int right)
        {
            Candidate temporary = heap[left];
            heap[left] = heap[right];
            heap[right] = temporary;
        }
    }
}
