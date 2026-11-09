using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LightSvnBrowser
{
    /// <summary>显示指定仓库路径的分页提交记录和完整提交说明。</summary>
    /// <remarks>Author: Codex, date: 2026-11-09.</remarks>
    internal sealed class RevisionHistoryDialog : Form
    {
        private const int NoSelection = 0;
        private const int FirstSelection = 0;
        private const int SummaryColumn = 3;
        private const int NoLineBreak = -1;
        private readonly SvnService _service;
        private readonly Repository _repository;
        private readonly string _relativePath;
        private readonly ListView _revisions;
        private readonly TextBox _message;
        private readonly Label _status;
        private readonly Button _refresh;
        private readonly Button _loadMore;
        private SvnHistoryPage _page;
        private CancellationTokenSource _operation;

        /// <summary>创建固定仓库路径的提交记录窗口。</summary>
        /// <param name="service">当前 SVN 客户端。</param>
        /// <param name="repository">当前仓库。</param>
        /// <param name="relativePath">当前目录或选中的文件、文件夹。</param>
        /// <param name="fontSize">应用字号。</param>
        /// <returns>用于查看提交记录的对话框。</returns>
        internal RevisionHistoryDialog(SvnService service, Repository repository, string relativePath, float fontSize)
        {
            _service = service;
            _repository = repository;
            _relativePath = relativePath;
            DialogStyle.Apply(this, "提交记录", new Size(860, 600));
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = new Size(620, 440);

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            Label target = new Label
            {
                Text = repository.Name + "  /  " + (String.IsNullOrEmpty(relativePath) ? "仓库根目录" : relativePath),
                Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false,
                Margin = Padding.Empty
            };
            TextBox address = DialogStyle.TextField(SvnService.BuildDisplayUrl(repository, relativePath), 0);
            address.Dock = DockStyle.Fill;
            address.ReadOnly = true;
            address.BackColor = Color.FromArgb(248, 250, 252);
            address.AccessibleName = "提交记录目标地址";
            address.Margin = Padding.Empty;
            _revisions = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
                MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = Padding.Empty,
                AccessibleName = "SVN 提交记录", TabIndex = 1
            };
            _revisions.Columns.Add("版本", 76);
            _revisions.Columns.Add("作者", 126);
            _revisions.Columns.Add("提交时间", 164);
            _revisions.Columns.Add("提交说明", 350);
            _revisions.SelectedIndexChanged += delegate { ShowSelectedMessage(); };
            _revisions.SizeChanged += delegate { ResizeSummaryColumn(); };

            Label details = new Label { Text = "完整提交说明", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };
            _message = DialogStyle.TextField(String.Empty, 2);
            _message.Dock = DockStyle.Fill;
            _message.ReadOnly = true;
            _message.BackColor = Color.FromArgb(248, 250, 252);
            _message.Multiline = true;
            _message.ScrollBars = ScrollBars.Vertical;
            _message.AccessibleName = "完整提交说明";
            _message.Margin = Padding.Empty;
            _status = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                ForeColor = DialogStyle.Muted, Margin = Padding.Empty, AccessibleName = "提交记录读取状态"
            };

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty
            };
            Button close = DialogStyle.Button("关闭", false);
            close.DialogResult = DialogResult.Cancel;
            _loadMore = DialogStyle.Button("加载更多", true);
            _loadMore.Enabled = false;
            _loadMore.Click += async delegate { await LoadHistoryAsync(true); };
            _refresh = DialogStyle.Button("刷新", false);
            _refresh.Click += async delegate { await LoadHistoryAsync(false); };
            actions.Controls.AddRange(new Control[] { close, _loadMore, _refresh });
            layout.Controls.Add(target, 0, 0);
            layout.Controls.Add(address, 0, 1);
            layout.Controls.Add(_revisions, 0, 2);
            layout.Controls.Add(details, 0, 3);
            layout.Controls.Add(_message, 0, 4);
            layout.Controls.Add(_status, 0, 5);
            layout.Controls.Add(actions, 0, 6);
            Controls.Add(layout);
            CancelButton = close;
            FormClosed += delegate { if (_operation != null) _operation.Cancel(); };
            Shown += async delegate
            {
                ResizeSummaryColumn();
                _revisions.Focus();
                await LoadHistoryAsync(false);
            };
            UiTypography.Apply(this, fontSize);
        }

        /// <summary>读取首页或下一页，仅在读取成功后更新当前列表。</summary>
        /// <param name="append">是否把新的一页接在现有记录后面。</param>
        /// <returns>读取操作完成的任务。</returns>
        private async Task LoadHistoryAsync(bool append)
        {
            if (_operation != null || (append && (_page == null || _page.NextRevision == null))) return;
            using (CancellationTokenSource operation = new CancellationTokenSource())
            {
                _operation = operation;
                _refresh.Enabled = false;
                _loadMore.Enabled = false;
                _status.Text = "正在读取提交记录…";
                try
                {
                    SvnHistoryPage page = await _service.GetHistoryAsync(_repository, _relativePath, append ? _page : null, operation.Token);
                    if (operation.IsCancellationRequested || IsDisposed) return;
                    _revisions.BeginUpdate();
                    try
                    {
                        if (!append) _revisions.Items.Clear();
                        foreach (SvnRevision entry in page.Entries)
                        {
                            string time = entry.CommittedUtc.HasValue
                                ? entry.CommittedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";
                            string summary = entry.Message.TrimStart();
                            int lineEnd = summary.IndexOfAny(new[] { '\r', '\n' });
                            if (lineEnd != NoLineBreak) summary = summary.Substring(0, lineEnd);
                            _revisions.Items.Add(new ListViewItem(new[] { "r" + entry.Revision, entry.Author, time, summary }) { Tag = entry });
                        }
                        if (!append && _revisions.Items.Count > NoSelection) _revisions.Items[FirstSelection].Selected = true;
                    }
                    finally { _revisions.EndUpdate(); }
                    _page = page;
                    ShowSelectedMessage();
                    _status.Text = _revisions.Items.Count == NoSelection ? "此路径暂无提交记录。"
                        : "已显示 " + _revisions.Items.Count.ToString(CultureInfo.InvariantCulture) + " 条记录"
                            + (page.NextRevision == null ? "，已到最早一条。" : "，可继续加载更早的记录。");
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    if (!operation.IsCancellationRequested && !IsDisposed)
                    {
                        _status.Text = "读取提交记录失败。";
                        MessageBox.Show(this, error.Message, "读取提交记录失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                finally
                {
                    _operation = null;
                    if (!IsDisposed)
                    {
                        _refresh.Enabled = true;
                        _loadMore.Enabled = _page != null && _page.NextRevision != null;
                    }
                }
            }
        }

        /// <summary>显示当前选择的完整提交说明并保留多行排版。</summary>
        /// <returns>无返回值。</returns>
        private void ShowSelectedMessage()
        {
            if (_revisions.SelectedItems.Count == NoSelection)
            {
                _message.Clear();
                return;
            }
            SvnRevision entry = (SvnRevision)_revisions.SelectedItems[FirstSelection].Tag;
            _message.Text = entry.Message.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        /// <summary>将列表可用空间分配给提交说明列。</summary>
        /// <returns>无返回值。</returns>
        private void ResizeSummaryColumn()
        {
            if (UiTypography.IsApplying(this)) return;
            int usedWidth = _revisions.Columns[0].Width + _revisions.Columns[1].Width + _revisions.Columns[2].Width;
            int minimum = (int)Math.Round(160 * Font.SizeInPoints / UiFontSettings.DefaultSize);
            _revisions.Columns[SummaryColumn].Width = Math.Max(minimum,
                _revisions.ClientSize.Width - usedWidth - SystemInformation.VerticalScrollBarWidth);
        }
    }
}
