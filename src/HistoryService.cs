using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LightSvnBrowser
{
    /// <summary>记录一次 SVN 提交的版本、作者、时间和说明。</summary>
    /// <remarks>Author: Codex, date: 2026-11-09.</remarks>
    internal sealed class SvnRevision
    {
        internal string Revision { get; set; }
        internal string Author { get; set; }
        internal DateTime? CommittedUtc { get; set; }
        internal string Message { get; set; }
    }

    /// <summary>保留一页提交记录和继续读取同一历史快照所需的版本。</summary>
    /// <remarks>Author: Codex, date: 2026-11-09.</remarks>
    internal sealed class SvnHistoryPage
    {
        internal List<SvnRevision> Entries { get; set; }
        internal string PegRevision { get; set; }
        internal string NextRevision { get; set; }
    }

    public sealed partial class SvnService
    {
        private const int HistoryPageSize = 50;
        private const string EmptyRepositoryRevision = "0";
        private const string FirstHistoryRevision = "1";

        /// <summary>按版本倒序读取目标路径的提交记录，并固定后续分页的定位版本。</summary>
        /// <param name="repository">当前仓库。</param>
        /// <param name="relativePath">仓库内的目标路径，空路径表示配置的仓库根目录。</param>
        /// <param name="previousPage">用于继续读取同一目标的上一页，首页传 null。</param>
        /// <param name="cancellationToken">取消读取的令牌。</param>
        /// <returns>提交记录和下一页游标。</returns>
        internal async Task<SvnHistoryPage> GetHistoryAsync(Repository repository, string relativePath,
            SvnHistoryPage previousPage, CancellationToken cancellationToken)
        {
            string path = NormalizePath(relativePath);
            string url = BuildUrl(repository, path);
            string pegRevision;
            string startRevision;
            if (previousPage == null)
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    await RunAsync(repository, new[] { "info", "--xml", "--revision", HeadRevision, "--", url + "@" + HeadRevision },
                        stream, MaxXmlBytes, ListTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                    XElement entry = ParseXml(stream.ToArray()).Descendants("entry").SingleOrDefault();
                    pegRevision = entry == null ? null : (string)entry.Attribute("revision");
                }
                ValidateRevision(pegRevision);
                startRevision = pegRevision;
            }
            else
            {
                pegRevision = previousPage.PegRevision;
                startRevision = previousPage.NextRevision;
                ValidateRevision(pegRevision);
                ValidateRevision(startRevision);
            }

            SvnHistoryPage page = new SvnHistoryPage { Entries = new List<SvnRevision>(), PegRevision = pegRevision };
            if (!String.Equals(pegRevision, EmptyRepositoryRevision, StringComparison.Ordinal))
            {
                string limit = (HistoryPageSize + 1).ToString(CultureInfo.InvariantCulture);
                using (MemoryStream stream = new MemoryStream())
                {
                    await RunAsync(repository, new[] { "log", "--xml", "--limit", limit, "--revision",
                        startRevision + ":" + FirstHistoryRevision, "--", url + "@" + pegRevision },
                        stream, MaxXmlBytes, ListTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                    foreach (XElement entry in ParseXml(stream.ToArray()).Descendants("logentry"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string revision = (string)entry.Attribute("revision");
                        ValidateRevision(revision);
                        if (page.Entries.Count == HistoryPageSize)
                        {
                            page.NextRevision = revision;
                            break;
                        }

                        DateTime timestamp;
                        DateTime? committedUtc = DateTime.TryParse((string)entry.Element("date"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp)
                            ? timestamp : (DateTime?)null;
                        page.Entries.Add(new SvnRevision
                        {
                            Revision = revision,
                            Author = (string)entry.Element("author") ?? String.Empty,
                            CommittedUtc = committedUtc,
                            Message = (string)entry.Element("msg") ?? String.Empty
                        });
                    }
                }
            }

            _store.Log("svn_history_complete", "repository=" + repository.Id + " path=" + path + " peg=" + pegRevision
                + " start=" + startRevision + " entries=" + page.Entries.Count.ToString(CultureInfo.InvariantCulture));
            return page;
        }
    }
}
