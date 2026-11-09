using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LightSvnBrowser
{
    public sealed class MainForm : Form
    {
        private const int ResultLimit = 5000;
        private const int TextPreviewLimit = 2 * 1024 * 1024;
        private const int ImagePreviewLimit = 20 * 1024 * 1024;
        private const int MaximumHistoryViews = 30;
        private const int UnsortedColumn = -1;
        private const int SizeColumn = 2;
        private const int ModifiedColumn = 3;
        private const string FavoritesKey = "favorites";
        private const string RepositoriesKey = "repositories";
        private const string SearchKey = "search";
        private const string RepositoryKey = "repository";
        private const string GroupKey = "group";
        private const int AllRepositoriesScope = 1;
        private const int CurrentDirectoryScope = 2;
        private static readonly Color Accent = Color.FromArgb(37, 99, 235);
        private static readonly Color Ink = Color.FromArgb(30, 41, 59);
        private static readonly Color Muted = Color.FromArgb(100, 116, 139);
        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".xml", ".json", ".yaml", ".yml", ".ini", ".conf", ".config",
            ".properties", ".sql", ".java", ".cs", ".js", ".ts", ".py", ".go", ".c", ".cpp", ".h", ".css", ".html", ".htm", ".bat", ".cmd", ".ps1", ".sh"
        };
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff" };
        private static readonly HashSet<string> ExternalViewExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".wps", ".et", ".dps", ".vsd", ".vsdx", ".xmind" };

        private readonly AppStore store;
        private readonly RepositoryNavigationStore recentDirectories;
        private SvnService service;
        private FilenameSearchEngine searchEngine;
        private TreeView navigation;
        private ListView files;
        private Label searchLabel;
        private TextBox search;
        private ComboBox scope;
        private TextBox address;
        private TextBox preview;
        private PictureBox image;
        private Label detailTitle;
        private Label detailMeta;
        private Label detailNotes;
        private Label status;
        private Label indexStatus;
        private Label pageTitle;
        private ProgressBar progress;
        private Button cancel;
        private Button up;
        private Button goToRoot;
        private Button back;
        private Button forward;
        private Button editRepository;
        private Button removeRepository;
        private Button openFile;
        private Button download;
        private Button favorite;
        private Button submitFile;
        private Button addItems;
        private Button directoryHistory;
        private ToolStripMenuItem submitFileMenuItem;
        private ToolStripMenuItem locateMenuItem;
        private ToolStripMenuItem selectionHistoryMenuItem;
        private CancellationTokenSource operation;
        private CancellationTokenSource previewOperation;
        private Repository currentRepository;
        private string currentPath = String.Empty;
        private bool showingFavorites;
        private bool rebuildingNavigation;
        private bool suppressSelectionPreview;
        private bool demoMode;
        private Image currentImage;
        private bool restoringHistory;
        private NavigationState currentView;
        private NavigationState repositoryView;
        private NavigationState favoritesView;
        private NavigationState searchView;
        private List<FileItem> displayedEntries = new List<FileItem>();
        private ColumnLayout columnLayout;
        private int sortColumn = UnsortedColumn;
        private bool sortDescending;
        private readonly Dictionary<ColumnLayout, ColumnSortState> columnSorts = new Dictionary<ColumnLayout, ColumnSortState>();
        private readonly List<NavigationState> backHistory = new List<NavigationState>();
        private readonly List<NavigationState> forwardHistory = new List<NavigationState>();
        private readonly List<Button> operationButtons = new List<Button>();

        private enum ViewKind { Repositories, Directory, Search, Favorites }
        private enum ColumnLayout { Repositories, Files, Favorites }

        /// <summary>Stores one list layout's session sorting preference.</summary>
        /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
        private sealed class ColumnSortState
        {
            public int Column = UnsortedColumn;
            public bool Descending;
        }

        /// <summary>Compares visible rows using source metadata for size and modification time.</summary>
        /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
        private sealed class FileRowComparer : System.Collections.IComparer
        {
            private readonly ColumnLayout layout;
            private readonly int column;
            private readonly bool descending;

            public FileRowComparer(ColumnLayout layout, int column, bool descending)
            {
                this.layout = layout;
                this.column = column;
                this.descending = descending;
            }

            public int Compare(object left, object right)
            {
                ListViewItem first = (ListViewItem)left;
                ListViewItem second = (ListViewItem)right;
                int result;
                if (layout == ColumnLayout.Files && column == SizeColumn)
                    result = ((FileItem)first.Tag).Entry.Size.CompareTo(((FileItem)second.Tag).Entry.Size);
                else if (layout == ColumnLayout.Files && column == ModifiedColumn)
                    result = ((FileItem)first.Tag).Entry.ModifiedUtc.CompareTo(((FileItem)second.Tag).Entry.ModifiedUtc);
                else result = StringComparer.CurrentCultureIgnoreCase.Compare(first.SubItems[column].Text, second.SubItems[column].Text);
                if (result == 0) result = StringComparer.CurrentCultureIgnoreCase.Compare(first.Text, second.Text);
                if (result == 0) result = StringComparer.Ordinal.Compare(ItemKey(first.Tag), ItemKey(second.Tag));
                return descending ? -Math.Sign(result) : Math.Sign(result);
            }
        }

        private sealed class NavigationState
        {
            public ViewKind Kind;
            public string RepositoryId;
            public string Path;
            public string Group;
            public string Query;
            public int Scope;
            public string Title;
            public string Address;
            public string IndexStatus;
            public string Status;
            public List<FileItem> Entries;
            public string SelectedKey;
            public int TopIndex;
            public int[] ColumnWidths;
            public int SortColumn = UnsortedColumn;
            public bool SortDescending;
        }

        private sealed class NavigationItem
        {
            public string Kind;
            public string Group;
            public Repository Repository;
        }

        private sealed class FileItem
        {
            public Repository Repository;
            public SvnEntry Entry;
            public Favorite Favorite;
        }

        public MainForm(AppStore appStore, SvnService svnService)
        {
            store = appStore;
            recentDirectories = new RepositoryNavigationStore(store);
            service = svnService;
            searchEngine = new FilenameSearchEngine(store, service);
            Text = "Light SVN Browser · 轻量 SVN 文档浏览器";
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;
            ForeColor = Ink;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1320, 850);
            MinimumSize = new Size(1080, 700);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            BuildLayout();
            BuildNavigation(null);
            ShowRepositories();
            UiTypography.Apply(this, store.Config.UiFontSize);
            KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Alt && args.KeyCode == Keys.Left) { args.SuppressKeyPress = true; GoBack(); }
                else if (args.Alt && args.KeyCode == Keys.Right) { args.SuppressKeyPress = true; GoForward(); }
            };
            FormClosing += delegate
            {
                CancelOperation();
                CancelPreview();
                DisposeImage();
            };
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.RowCount = 4;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.Margin = Padding.Empty;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            Controls.Add(root);

            Panel header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 42), Margin = Padding.Empty };
            Label title = new Label { Text = "LIGHT SVN BROWSER", ForeColor = Color.White, Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), AutoSize = true, Location = new Point(22, 11) };
            Label subtitle = new Label { Text = "搜索文档 · 临时查看 · 收藏常用资料", ForeColor = Color.FromArgb(148, 163, 184), AutoSize = true, Location = new Point(24, 43) };
            Button settings = MakeButton("设置 / 数据目录", false);
            settings.Width = 136;
            settings.Height = 32;
            settings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(settings);
            header.Resize += delegate
            {
                if (UiTypography.IsApplying(this)) return;
                float ratio = Font.SizeInPoints / UiFontSettings.DefaultSize;
                settings.Location = new Point(header.ClientSize.Width - settings.Width - (int)Math.Round(20 * ratio), (int)Math.Round(19 * ratio));
            };
            settings.Click += delegate { ShowSettings(); };
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel searchBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Padding = new Padding(18, 10, 18, 10), Margin = Padding.Empty, BackColor = Color.FromArgb(248, 250, 252) };
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            searchLabel = new Label { Text = "文件名搜索", AutoSize = true, Anchor = AnchorStyles.Left };
            searchBar.Controls.Add(searchLabel, 0, 0);
            search = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 10, 0), Font = new Font(Font.FontFamily, 10F), AccessibleName = "文件名关键词" };
            search.KeyDown += async delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter) { args.SuppressKeyPress = true; await SearchAsync(); }
            };
            searchBar.Controls.Add(search, 1, 0);
            scope = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 10, 0), AccessibleName = "搜索范围" };
            scope.Items.AddRange(new object[] { "当前仓库", "所有仓库", "当前目录及子目录" });
            scope.SelectedIndex = 0;
            searchBar.Controls.Add(scope, 2, 0);
            Button searchButton = MakeButton("搜索", true);
            searchButton.Dock = DockStyle.Fill;
            searchButton.Click += async delegate { await SearchAsync(); };
            searchBar.Controls.Add(searchButton, 3, 0);
            Button rebuildIndex = MakeButton("更新搜索索引", false);
            rebuildIndex.Dock = DockStyle.Fill;
            rebuildIndex.Click += async delegate { await RefreshIndexAsync(); };
            searchBar.Controls.Add(rebuildIndex, 4, 0);
            operationButtons.Add(searchButton);
            operationButtons.Add(rebuildIndex);
            root.Controls.Add(searchBar, 0, 1);

            SplitContainer outer = new SplitContainer { Size = new Size(1280, 650), Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 246, Panel1MinSize = 205, Panel2MinSize = 700, SplitterWidth = 1, BackColor = Color.FromArgb(226, 232, 240), Margin = Padding.Empty };
            root.Controls.Add(outer, 0, 2);
            TableLayoutPanel sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(14), BackColor = Color.FromArgb(248, 250, 252), Margin = Padding.Empty };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            Button add = MakeButton("＋ 添加 SVN 仓库", true);
            add.Dock = DockStyle.Fill;
            add.Click += delegate { AddOrEditRepository(null); };
            sidebar.Controls.Add(add, 0, 0);
            navigation = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = sidebar.BackColor, ForeColor = Ink, HideSelection = false, FullRowSelect = true, ShowLines = false, ShowRootLines = false, ItemHeight = 32, Indent = 17, Margin = new Padding(0, 15, 0, 8), AccessibleName = "收藏、搜索和仓库导航" };
            navigation.AfterSelect += async delegate(object sender, TreeViewEventArgs args)
            {
                if (rebuildingNavigation || demoMode) return;
                NavigationItem item = args.Node.Tag as NavigationItem;
                if (item == null) return;
                if (item.Kind == FavoritesKey) SwitchView(favoritesView, ViewKind.Favorites);
                else if (item.Kind == SearchKey) SwitchView(searchView, ViewKind.Search);
                else if (item.Kind == RepositoriesKey && !IsRepositoryView(currentView)) SwitchView(repositoryView, ViewKind.Repositories);
                else if (item.Repository != null) await OpenRepositoryAsync(item.Repository);
                else ShowRepositories(item.Group);
            };
            ContextMenuStrip treeMenu = new ContextMenuStrip();
            treeMenu.Items.Add("查看仓库列表", null, delegate { ShowRepositories(); });
            treeMenu.Items.Add("添加仓库", null, delegate { AddOrEditRepository(null); });
            treeMenu.Items.Add("编辑选中仓库", null, delegate { if (SelectedRepository() != null) AddOrEditRepository(SelectedRepository()); });
            treeMenu.Items.Add("删除选中仓库", null, delegate { DeleteRepository(); });
            navigation.ContextMenuStrip = treeMenu;
            navigation.NodeMouseClick += delegate(object sender, TreeNodeMouseClickEventArgs args) { if (args.Button == MouseButtons.Right) navigation.SelectedNode = args.Node; };
            sidebar.Controls.Add(navigation, 0, 1);
            sidebar.Controls.Add(new Label { Text = "只保存目录索引\n查看文件时按需获取", ForeColor = Muted, AutoSize = true, Margin = new Padding(6, 8, 0, 0) }, 0, 2);
            outer.Panel1.Controls.Add(sidebar);

            TableLayoutPanel workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.White, Padding = new Padding(18, 10, 18, 0), Margin = Padding.Empty };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.Panel2.Controls.Add(workspace);
            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, Margin = Padding.Empty };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            pageTitle = new Label { Text = "我的仓库", Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            toolbar.Controls.Add(pageTitle, 0, 0);
            goToRoot = MakeButton("回到根目录", false);
            goToRoot.Click += async delegate
            {
                Repository repository = RootNavigationRepository();
                if (repository != null) await NavigateAsync(repository, String.Empty);
            };
            toolbar.Controls.Add(goToRoot, 1, 0);
            back = MakeButton("后退", false);
            back.Width = 60;
            back.Click += delegate { GoBack(); };
            toolbar.Controls.Add(back, 2, 0);
            forward = MakeButton("前进", false);
            forward.Width = 60;
            forward.Click += delegate { GoForward(); };
            toolbar.Controls.Add(forward, 3, 0);
            up = MakeButton("上一级", false);
            up.Click += async delegate { await UpAsync(); };
            toolbar.Controls.Add(up, 4, 0);
            Button refresh = MakeButton("刷新", false);
            refresh.Click += async delegate { await RefreshViewAsync(); };
            toolbar.Controls.Add(refresh, 5, 0);
            editRepository = MakeButton("编辑仓库", false);
            editRepository.Click += delegate { if (SelectedRepository() != null) AddOrEditRepository(SelectedRepository()); };
            toolbar.Controls.Add(editRepository, 6, 0);
            removeRepository = MakeButton("删除仓库", false);
            removeRepository.Click += delegate { DeleteRepository(); };
            toolbar.Controls.Add(removeRepository, 7, 0);
            operationButtons.Add(refresh);
            workspace.Controls.Add(toolbar, 0, 0);

            address = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(248, 250, 252), Margin = new Padding(0, 5, 0, 4), AccessibleName = "当前路径" };
            TableLayoutPanel locationBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            locationBar.Controls.Add(address, 0, 0);
            addItems = MakeButton("新增…", false);
            addItems.Dock = DockStyle.Fill;
            addItems.Click += async delegate { await AddToDirectoryAsync(); };
            locationBar.Controls.Add(addItems, 1, 0);
            directoryHistory = MakeButton("当前目录记录…", false);
            directoryHistory.Dock = DockStyle.Fill;
            directoryHistory.Click += delegate { if (CanUseDirectoryActions()) ShowRevisionHistory(currentRepository, currentPath); };
            locationBar.Controls.Add(directoryHistory, 2, 0);
            workspace.Controls.Add(locationBar, 0, 1);
            indexStatus = new Label { Text = "添加仓库后即可浏览；首次搜索会建立文件名索引。", ForeColor = Muted, AutoSize = true, Anchor = AnchorStyles.Left };
            workspace.Controls.Add(indexStatus, 0, 2);

            SplitContainer content = new SplitContainer { Size = new Size(1000, 540), Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterDistance = 620, Panel1MinSize = 360, Panel2MinSize = 260, SplitterWidth = 6, BackColor = Color.White, Margin = Padding.Empty };
            workspace.Controls.Add(content, 0, 3);
            files = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.None, GridLines = false, AccessibleName = "文件列表" };
            ImageList icons = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
            icons.Images.Add(DrawIcon(true));
            icons.Images.Add(DrawIcon(false));
            files.SmallImageList = icons;
            files.ItemActivate += async delegate { await ActivateSelectionAsync(); };
            files.ColumnClick += delegate(object sender, ColumnClickEventArgs args) { SortByColumn(args.Column); };
            files.SelectedIndexChanged += async delegate { if (!suppressSelectionPreview) await ShowSelectionAsync(); };
            files.MouseDown += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button != MouseButtons.Right) return;
                ListViewItem target = files.GetItemAt(args.X, args.Y);
                if (target == null) files.SelectedItems.Clear();
                else { target.Selected = true; target.Focused = true; }
                UpdateActions();
            };
            files.KeyDown += async delegate(object sender, KeyEventArgs args)
            {
                if (args.Alt && (args.KeyCode == Keys.Left || args.KeyCode == Keys.Right)) return;
                if (args.KeyCode == Keys.Back) { args.SuppressKeyPress = true; await UpAsync(); }
                else if (args.Control && args.KeyCode == Keys.D) { args.SuppressKeyPress = true; EditFavorite(); }
            };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("临时查看 / 打开文件夹", null, async delegate { await ActivateSelectionAsync(); });
            locateMenuItem = new ToolStripMenuItem("跳转到仓库中的位置");
            locateMenuItem.Click += async delegate { await LocateSelectionAsync(); };
            menu.Items.Add(locateMenuItem);
            selectionHistoryMenuItem = new ToolStripMenuItem("查看提交记录…");
            selectionHistoryMenuItem.Click += delegate
            {
                FileItem item = SelectedFile();
                if (item != null) ShowRevisionHistory(item.Repository, item.Entry.Path);
            };
            menu.Items.Add(selectionHistoryMenuItem);
            menu.Items.Add("下载到…", null, async delegate { await DownloadSelectionAsync(); });
            submitFileMenuItem = new ToolStripMenuItem("提交新版本…");
            submitFileMenuItem.Click += async delegate { await SubmitSelectionAsync(); };
            menu.Items.Add(submitFileMenuItem);
            menu.Opening += delegate { UpdateActions(); };
            menu.Items.Add("收藏 / 编辑标签备注", null, delegate { EditFavorite(); });
            menu.Items.Add("取消收藏", null, delegate { RemoveFavorite(); });
            menu.Items.Add("复制 SVN 地址", null, delegate { CopySelectedUrl(); });
            files.ContextMenuStrip = menu;
            content.Panel1.Controls.Add(files);

            TableLayoutPanel detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(15, 8, 4, 10), BackColor = Color.FromArgb(248, 250, 252) };
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailTitle = new Label { Text = "文件预览", Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), Dock = DockStyle.Fill, AutoEllipsis = true };
            detail.Controls.Add(detailTitle, 0, 0);
            detailMeta = new Label { Text = "选择文件查看详情。\n双击临时打开，按需另存到本地。", ForeColor = Muted, Dock = DockStyle.Fill, AutoEllipsis = true };
            detail.Controls.Add(detailMeta, 0, 1);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            openFile = MakeButton("临时查看", true);
            openFile.Width = 87;
            openFile.Click += async delegate { await ActivateSelectionAsync(); };
            favorite = MakeButton("收藏", false);
            favorite.Width = 64;
            favorite.Click += delegate { EditFavorite(); };
            download = MakeButton("下载到…", false);
            download.Width = 90;
            download.Click += async delegate { await DownloadSelectionAsync(); };
            actions.Controls.Add(openFile);
            actions.Controls.Add(favorite);
            actions.Controls.Add(download);
            detail.Controls.Add(actions, 0, 2);
            FlowLayoutPanel submissionActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            submitFile = MakeButton("提交新版本…", false);
            submitFile.Width = 128;
            submitFile.Click += async delegate { await SubmitSelectionAsync(); };
            submissionActions.Controls.Add(submitFile);
            detail.Controls.Add(submissionActions, 0, 3);
            detailNotes = new Label { Text = "收藏支持标签与备注。", Dock = DockStyle.Fill, ForeColor = Muted, AutoEllipsis = true, Padding = new Padding(0, 8, 0, 0) };
            detail.Controls.Add(detailNotes, 0, 4);
            Panel previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
            preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Consolas", 9F), Text = "目录浏览与文件名搜索不会下载整库。\r\n\r\n文本和图片可在此预览。\r\nOffice / PDF 临时交给本机默认程序查看。", AccessibleName = "文件预览内容" };
            image = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Visible = false };
            previewHost.Controls.Add(preview);
            previewHost.Controls.Add(image);
            detail.Controls.Add(previewHost, 0, 5);
            content.Panel2.Controls.Add(detail);
            content.SizeChanged += delegate
            {
                if (UiTypography.IsApplying(this)) return;
                int preferredDetailWidth = (int)Math.Round(300 * Font.SizeInPoints / UiFontSettings.DefaultSize);
                if (content.ClientSize.Width > content.Panel1MinSize + content.Panel2MinSize + content.SplitterWidth)
                    content.SplitterDistance = Math.Max(content.Panel1MinSize, content.ClientSize.Width - preferredDetailWidth - content.SplitterWidth);
            };

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(18, 2, 12, 2), BackColor = Color.FromArgb(241, 245, 249), Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            status = new Label { Text = "就绪 · 数据目录：" + store.DataDirectory, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted };
            progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false, Margin = new Padding(5, 7, 5, 6) };
            cancel = MakeButton("取消", false);
            cancel.Dock = DockStyle.Fill;
            cancel.Visible = false;
            cancel.Click += delegate { CancelOperation(); };
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(progress, 1, 0);
            footer.Controls.Add(cancel, 2, 0);
            root.Controls.Add(footer, 0, 3);
            UpdateActions();
        }

        private Button MakeButton(string text, bool primary)
        {
            Button button = new Button { Text = text, AutoSize = false, Height = 32, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, Margin = new Padding(3, 3, 3, 3), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(203, 213, 225);
            button.FlatAppearance.BorderSize = 1;
            return button;
        }

        private static Bitmap DrawIcon(bool folder)
        {
            Bitmap bitmap = new Bitmap(20, 20);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (folder)
                {
                    using (Brush brush = new SolidBrush(Color.FromArgb(245, 183, 65)))
                    {
                        graphics.FillRectangle(brush, 2, 4, 7, 4);
                        graphics.FillRectangle(brush, 2, 7, 16, 10);
                    }
                }
                else
                {
                    using (Brush brush = new SolidBrush(Color.FromArgb(226, 232, 240))) graphics.FillRectangle(brush, 4, 2, 12, 16);
                    using (Pen pen = new Pen(Color.FromArgb(100, 116, 139)))
                    {
                        graphics.DrawRectangle(pen, 4, 2, 12, 16);
                        graphics.DrawLine(pen, 7, 7, 13, 7);
                        graphics.DrawLine(pen, 7, 10, 13, 10);
                        graphics.DrawLine(pen, 7, 13, 11, 13);
                    }
                }
            }
            return bitmap;
        }

        private void BuildNavigation(string selectedRepositoryId)
        {
            rebuildingNavigation = true;
            navigation.BeginUpdate();
            try
            {
                navigation.Nodes.Clear();
                navigation.Nodes.Add(new TreeNode("★ 我的收藏  (" + store.Config.Favorites.Count + ")") { Name = FavoritesKey, Tag = new NavigationItem { Kind = FavoritesKey } });
                navigation.Nodes.Add(new TreeNode("⌕ 搜索结果") { Name = SearchKey, Tag = new NavigationItem { Kind = SearchKey } });
                TreeNode root = new TreeNode("▣ 我的仓库") { Name = RepositoriesKey, Tag = new NavigationItem { Kind = RepositoriesKey, Group = String.Empty } };
                navigation.Nodes.Add(root);
                Dictionary<string, TreeNode> groups = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
                groups[String.Empty] = root;
                foreach (Repository repository in store.Config.Repositories.OrderBy(r => r.Group).ThenBy(r => r.Name))
                {
                    string group = String.Empty;
                    TreeNode parent = root;
                    foreach (string part in (repository.Group ?? String.Empty).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        group = group.Length == 0 ? part : group + "/" + part;
                        TreeNode groupNode;
                        if (!groups.TryGetValue(group, out groupNode))
                        {
                            groupNode = new TreeNode(part) { Tag = new NavigationItem { Kind = GroupKey, Group = group } };
                            parent.Nodes.Add(groupNode);
                            groups[group] = groupNode;
                        }
                        parent = groupNode;
                    }
                    TreeNode node = new TreeNode(repository.Name) { Tag = new NavigationItem { Kind = RepositoryKey, Repository = repository }, ToolTipText = SvnService.BuildDisplayUrl(repository, String.Empty) };
                    parent.Nodes.Add(node);
                    if (repository.Id == selectedRepositoryId) navigation.SelectedNode = node;
                }
                root.ExpandAll();
                if (navigation.SelectedNode == null) navigation.SelectedNode = root;
            }
            finally { navigation.EndUpdate(); rebuildingNavigation = false; }
            if (currentView != null && !demoMode) SelectNavigationForView(currentView);
            UpdateActions();
        }

        private Repository SelectedRepository()
        {
            NavigationItem item = navigation.SelectedNode == null ? null : navigation.SelectedNode.Tag as NavigationItem;
            return item != null && item.Repository != null ? item.Repository : currentRepository;
        }

        private void AddOrEditRepository(Repository existing)
        {
            using (RepositoryDialog dialog = new RepositoryDialog(store, existing, store.Config.Repositories.Select(r => r.Group).Distinct()))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                CancelOperation();
                CancelPreview();
                Repository result = dialog.Result;
                if (existing != null)
                {
                    int index = store.Config.Repositories.FindIndex(r => r.Id == existing.Id);
                    store.Config.Repositories[index] = result;
                    store.DeleteIndex(existing.Id);
                }
                else store.Config.Repositories.Add(result);
                store.Save();
                if (existing != null && SvnService.BuildUrl(existing, String.Empty) != SvnService.BuildUrl(result, String.Empty))
                    recentDirectories.Forget(existing);
                ClearHistory();
                store.Log("repository.save", "id=" + result.Id + "; edited=" + (existing != null));
                currentRepository = result;
                currentPath = String.Empty;
                showingFavorites = false;
                BuildNavigation(null);
                ShowRepositories();
                status.Text = "仓库已保存，选择左侧仓库开始浏览。密码仅供当前 Windows 账户解密。";
            }
        }

        private void DeleteRepository()
        {
            Repository repository = SelectedRepository();
            if (repository == null) return;
            if (MessageBox.Show(this, "从本工具移除“" + repository.Name + "”及其收藏和搜索索引？\nSVN 中的文件不会受影响。", "移除仓库", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            CancelOperation();
            CancelPreview();
            store.Config.Repositories.RemoveAll(r => r.Id == repository.Id);
            store.Config.Favorites.RemoveAll(f => f.RepositoryId == repository.Id);
            store.DeleteIndex(repository.Id);
            store.Save();
            recentDirectories.Forget(repository);
            ClearHistory();
            store.Log("repository.remove", "id=" + repository.Id);
            currentRepository = null;
            currentPath = String.Empty;
            showingFavorites = false;
            BuildNavigation(null);
            ShowRepositories();
        }

        private void ShowRepositories(string group = null)
        {
            CaptureViewPosition();
            CancelViewOperation();
            CancelPreview();
            showingFavorites = false;
            search.Text = String.Empty;
            currentRepository = null;
            currentPath = String.Empty;
            pageTitle.Text = String.IsNullOrEmpty(group) ? "我的仓库" : group;
            address.Text = "数据目录：" + store.DataDirectory;
            SetColumns(ColumnLayout.Repositories, new[] { "仓库名称", "分组", "SVN 地址", "登录用户" }, new[] { 170, 130, 340, 130 });
            foreach (Repository repository in store.Config.Repositories.Where(r => String.IsNullOrEmpty(group) || String.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase) || (r.Group ?? String.Empty).StartsWith(group + "/", StringComparison.OrdinalIgnoreCase)))
            {
                ListViewItem row = new ListViewItem(repository.Name, 0) { Tag = repository };
                row.SubItems.Add(repository.Group ?? String.Empty);
                row.SubItems.Add(SvnService.BuildDisplayUrl(repository, String.Empty));
                row.SubItems.Add(repository.Username ?? String.Empty);
                files.Items.Add(row);
            }
            if (!restoringHistory) ApplyColumnSort(false);
            indexStatus.Text = store.Config.Repositories.Count == 0 ? "点击左侧“添加 SVN 仓库”。分组填写“团队/产品”即可形成文件夹。" : "双击仓库进入目录；在左侧按文件夹组织多个地址。";
            status.Text = files.Items.Count + " 个仓库 · 数据目录：" + store.DataDirectory;
            ClearDetails();
            CommitView(ViewKind.Repositories, group);
            UpdateActions();
        }

        private void SetColumns(ColumnLayout layout, string[] names, int[] widths)
        {
            bool previousSuppression = suppressSelectionPreview;
            suppressSelectionPreview = true;
            files.ListViewItemSorter = null;
            files.Items.Clear();
            files.Columns.Clear();
            columnLayout = layout;
            ColumnSortState saved;
            columnSorts.TryGetValue(layout, out saved);
            sortColumn = saved == null ? UnsortedColumn : saved.Column;
            sortDescending = saved != null && saved.Descending;
            for (int index = 0; index < names.Length; index++)
                files.Columns.Add(new ColumnHeader { Text = names[index], Tag = names[index], Width = (int)Math.Round(widths[index] * Font.SizeInPoints / UiFontSettings.DefaultSize) });
            suppressSelectionPreview = previousSuppression;
        }

        private void SortByColumn(int column)
        {
            if (operation != null || column < 0 || column >= files.Columns.Count) return;
            sortDescending = column == sortColumn && !sortDescending;
            sortColumn = column;
            Stopwatch timer = Stopwatch.StartNew();
            ApplyColumnSort(true);
            CaptureViewPosition();
            store.Log("ui.sort", "layout=" + columnLayout + "; column=" + column + "; descending=" + sortDescending + "; rows=" + files.Items.Count + "; ms=" + timer.ElapsedMilliseconds);
        }

        private void ApplyColumnSort(bool updateView)
        {
            bool previousSuppression = suppressSelectionPreview;
            suppressSelectionPreview = true;
            files.BeginUpdate();
            try
            {
                ListViewItem selected = files.SelectedItems.Count == 0 ? null : files.SelectedItems[0];
                if (sortColumn != UnsortedColumn)
                {
                    files.ListViewItemSorter = new FileRowComparer(columnLayout, sortColumn, sortDescending);
                    files.Sort();
                    files.ListViewItemSorter = null;
                }
                for (int index = 0; index < files.Columns.Count; index++)
                    files.Columns[index].Text = (string)files.Columns[index].Tag + (index == sortColumn ? (sortDescending ? " ▼" : " ▲") : String.Empty);
                if (selected != null) { selected.Selected = true; selected.Focused = true; selected.EnsureVisible(); }
                else if (files.Items.Count > 0) files.TopItem = files.Items[0];
                if (columnLayout == ColumnLayout.Files)
                    displayedEntries = files.Items.Cast<ListViewItem>().Select(row => (FileItem)row.Tag).ToList();
                columnSorts[columnLayout] = new ColumnSortState { Column = sortColumn, Descending = sortDescending };
                if (updateView && currentView != null)
                {
                    currentView.SortColumn = sortColumn;
                    currentView.SortDescending = sortDescending;
                    if (columnLayout == ColumnLayout.Files) currentView.Entries = displayedEntries;
                }
            }
            finally { files.EndUpdate(); suppressSelectionPreview = previousSuppression; }
        }

        private void CaptureViewPosition()
        {
            if (restoringHistory || currentView == null) return;
            currentView.SelectedKey = files.SelectedItems.Count == 0 ? null : ItemKey(files.SelectedItems[0].Tag);
            try { currentView.TopIndex = files.TopItem == null ? 0 : files.TopItem.Index; }
            catch (InvalidOperationException) { currentView.TopIndex = 0; }
            currentView.ColumnWidths = files.Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray();
            currentView.SortColumn = sortColumn;
            currentView.SortDescending = sortDescending;
        }

        private static string ItemKey(object item)
        {
            Repository repository = item as Repository;
            if (repository != null) return repository.Id;
            FileItem file = item as FileItem;
            return file == null ? null : file.Repository.Id + "\n" + file.Entry.Path;
        }

        private void CommitView(ViewKind kind, string group = null, string executedQuery = null, int? executedScope = null)
        {
            if (restoringHistory) return;
            NavigationState next = new NavigationState
            {
                Kind = kind, RepositoryId = currentRepository == null ? null : currentRepository.Id,
                Path = currentPath, Group = group ?? String.Empty, Query = executedQuery ?? search.Text, Scope = executedScope ?? scope.SelectedIndex,
                Title = pageTitle.Text, Address = address.Text, IndexStatus = indexStatus.Text, Status = status.Text,
                Entries = kind == ViewKind.Directory || kind == ViewKind.Search ? displayedEntries : null,
                SortColumn = sortColumn, SortDescending = sortDescending
            };
            if (currentView != null)
            {
                bool repeated = currentView.Kind == kind && currentView.RepositoryId == next.RepositoryId &&
                    currentView.Path == next.Path && currentView.Group == next.Group &&
                    (IsRepositoryView(next) || (currentView.Query == next.Query && currentView.Scope == next.Scope));
                if (!repeated) RecordViewChange();
            }
            currentView = next;
            RememberView(next);
            SelectNavigationForView(next);
            UpdateActions();
        }

        private static bool IsRepositoryView(NavigationState view)
        {
            return view != null && (view.Kind == ViewKind.Repositories || view.Kind == ViewKind.Directory);
        }

        private void RememberView(NavigationState view)
        {
            if (IsRepositoryView(view))
            {
                repositoryView = view;
                if (view.Kind == ViewKind.Directory && currentRepository != null)
                    recentDirectories.RememberPath(currentRepository, view.Path);
            }
            else if (view.Kind == ViewKind.Favorites) favoritesView = view;
            else searchView = view;
        }

        private void RecordViewChange()
        {
            if (currentView == null) return;
            backHistory.Add(currentView);
            if (backHistory.Count > MaximumHistoryViews) backHistory.RemoveAt(0);
            forwardHistory.Clear();
        }

        private void SwitchView(NavigationState target, ViewKind kind)
        {
            if (target == currentView && target != null) return;
            if (target == null)
            {
                if (kind == ViewKind.Favorites) ShowFavorites();
                else if (kind == ViewKind.Search) ShowEmptySearch();
                else ShowRepositories();
                return;
            }
            CaptureViewPosition();
            RecordViewChange();
            RestoreView(target);
            store.Log("navigation.workspace", "kind=" + target.Kind + "; repository=" + target.RepositoryId + "; path=" + target.Path);
        }

        private void ClearHistory()
        {
            currentView = null;
            repositoryView = null;
            favoritesView = null;
            searchView = null;
            backHistory.Clear();
            forwardHistory.Clear();
        }

        private void GoBack() { RestoreHistory(backHistory, forwardHistory); }

        private void GoForward() { RestoreHistory(forwardHistory, backHistory); }

        private void RestoreHistory(List<NavigationState> source, List<NavigationState> destination)
        {
            if (source.Count == 0 || operation != null) return;
            CaptureViewPosition();
            NavigationState target = source[source.Count - 1];
            source.RemoveAt(source.Count - 1);
            if (currentView != null) destination.Add(currentView);
            RestoreView(target);
            store.Log("navigation.history", "kind=" + target.Kind + "; back=" + backHistory.Count + "; forward=" + forwardHistory.Count);
        }

        private void RestoreView(NavigationState target)
        {
            CancelViewOperation();
            CancelPreview();
            restoringHistory = true;
            try
            {
                search.Text = target.Query ?? String.Empty;
                scope.SelectedIndex = target.Scope;
                if (target.Kind == ViewKind.Repositories) ShowRepositories(target.Group);
                else if (target.Kind == ViewKind.Favorites) ShowFavorites(target.Query);
                else
                {
                    currentRepository = FindViewRepository(target);
                    currentPath = target.Path ?? String.Empty;
                    showingFavorites = false;
                    PopulateEntries(target.Entries ?? new List<FileItem>(), true);
                    ClearDetails();
                }
                sortColumn = target.SortColumn;
                sortDescending = target.SortDescending;
                ApplyColumnSort(false);
                currentView = target;
                RememberView(target);
                search.Text = target.Query ?? String.Empty;
                pageTitle.Text = target.Title;
                address.Text = target.Address;
                indexStatus.Text = target.IndexStatus;
                status.Text = target.Status;
                RestoreViewPosition(target);
                SelectNavigationForView(target);
            }
            finally { restoringHistory = false; UpdateActions(); }
        }

        private void RestoreViewPosition(NavigationState view, bool updateDetails = true)
        {
            bool previousSuppression = suppressSelectionPreview;
            suppressSelectionPreview = true;
            try
            {
                if (view.ColumnWidths != null && view.ColumnWidths.Length == files.Columns.Count)
                    for (int index = 0; index < view.ColumnWidths.Length; index++) files.Columns[index].Width = view.ColumnWidths[index];
                foreach (ListViewItem row in files.Items)
                    if (view.SelectedKey != null && ItemKey(row.Tag) == view.SelectedKey) { row.Selected = true; row.Focused = true; break; }
                if (files.Items.Count > 0) files.TopItem = files.Items[Math.Min(Math.Max(view.TopIndex, 0), files.Items.Count - 1)];
                FileItem selected = SelectedFile();
                if (selected != null && updateDetails)
                {
                    SetSelectionDetails(selected);
                }
            }
            finally { suppressSelectionPreview = previousSuppression; }
        }

        private void SelectNavigationForView(NavigationState view)
        {
            rebuildingNavigation = true;
            try
            {
                TreeNode selected = null;
                foreach (TreeNode root in navigation.Nodes)
                {
                    foreach (TreeNode node in EnumerateNodes(root))
                    {
                        NavigationItem item = node.Tag as NavigationItem;
                        if (item == null) continue;
                        if (view.Kind == ViewKind.Favorites && item.Kind == FavoritesKey) selected = node;
                        else if (view.Kind == ViewKind.Search && item.Kind == SearchKey) selected = node;
                        else if (view.Kind == ViewKind.Repositories && (item.Kind == RepositoriesKey || item.Kind == GroupKey) && item.Group == view.Group) selected = node;
                        else if (view.Kind == ViewKind.Directory && item.Repository != null && item.Repository.Id == view.RepositoryId) selected = node;
                    }
                }
                if (selected != null) navigation.SelectedNode = selected;
            }
            finally { rebuildingNavigation = false; }
        }

        private static IEnumerable<TreeNode> EnumerateNodes(TreeNode root)
        {
            yield return root;
            foreach (TreeNode child in root.Nodes)
                foreach (TreeNode descendant in EnumerateNodes(child)) yield return descendant;
        }

        private Task NavigateAsync(Repository repository, string relativePath)
        {
            return LoadDirectoryAsync(repository, relativePath);
        }

        private Task OpenRepositoryAsync(Repository repository)
        {
            return NavigateAsync(repository, recentDirectories.GetPath(repository));
        }

        private async Task LoadDirectoryAsync(Repository repository, string relativePath, SvnEntry locate = null)
        {
            if (repository == null) return;
            CaptureViewPosition();
            CancelPreview();
            string targetPath = relativePath ?? String.Empty;
            await RunOperationAsync(locate == null ? "正在读取目录…" : "正在定位仓库位置…", async token =>
            {
                List<SvnEntry> entries = await service.ListAsync(repository, targetPath, false, token);
                token.ThrowIfCancellationRequested();
                if (locate != null && !entries.Any(entry => entry.Path == locate.Path && entry.IsDirectory == locate.IsDirectory))
                    throw new FileNotFoundException("目标已删除、移动或类型已变化，请更新搜索索引或收藏后再定位。");
                NavigationState previous = repositoryView;
                if (!IsRepositoryView(currentView))
                {
                    search.Text = previous == null ? String.Empty : previous.Query;
                    if (previous != null) scope.SelectedIndex = previous.Scope;
                    if (previous != null && previous.Kind == ViewKind.Directory)
                        columnSorts[ColumnLayout.Files] = new ColumnSortState { Column = previous.SortColumn, Descending = previous.SortDescending };
                }
                currentRepository = repository;
                currentPath = targetPath;
                showingFavorites = false;
                pageTitle.Text = repository.Name;
                address.Text = SvnService.BuildDisplayUrl(repository, targetPath);
                PopulateEntries(entries.Select(e => new FileItem { Repository = repository, Entry = e }));
                DateTime? indexUpdated = store.GetIndexUpdatedUtc(repository.Id);
                indexStatus.Text = indexUpdated == null ? "目录按需读取。首次搜索会建立文件名索引。" : "搜索索引更新于 " + indexUpdated.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "；其他客户端的改动可手动更新索引。";
                ClearDetails();
                status.Text = entries.Count + " 项 · 双击文件临时查看；下载时自行选择位置。";
                CommitView(ViewKind.Directory);
                if (previous != null && previous.Kind == ViewKind.Directory && previous.RepositoryId == repository.Id && previous.Path == targetPath)
                    RestoreViewPosition(previous);
                if (locate != null)
                {
                    ListViewItem row = files.Items.Cast<ListViewItem>().First(item => ((FileItem)item.Tag).Entry.Path == locate.Path);
                    suppressSelectionPreview = true;
                    try
                    {
                        files.SelectedItems.Clear();
                        row.Selected = true;
                        row.Focused = true;
                        row.EnsureVisible();
                        files.Focus();
                    }
                    finally { suppressSelectionPreview = false; }
                    SetSelectionDetails((FileItem)row.Tag);
                    CaptureViewPosition();
                    status.Text = "已定位 · " + locate.Path;
                    store.Log("navigation.locate", "repository=" + repository.Id + "; path=" + locate.Path);
                }
            });
        }

        private Task LocateSelectionAsync()
        {
            FileItem item = SelectedFile();
            if (!CanLocate(item)) return Task.FromResult(false);
            string path = item.Entry.Path;
            return LoadDirectoryAsync(item.Repository, ParentPath(path), String.IsNullOrEmpty(path) ? null : item.Entry);
        }

        private void PopulateEntries(IEnumerable<FileItem> entries)
        {
            PopulateEntries(entries, false);
        }

        private void PopulateEntries(IEnumerable<FileItem> entries, bool alreadySorted)
        {
            displayedEntries = alreadySorted ? entries.ToList() : entries.OrderByDescending(e => e.Entry.IsDirectory).ThenBy(e => e.Entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            ListViewItem[] rows = new ListViewItem[displayedEntries.Count];
            for (int position = 0; position < displayedEntries.Count; position++)
            {
                FileItem item = displayedEntries[position];
                SvnEntry entry = item.Entry;
                ListViewItem row = new ListViewItem(entry.Name, entry.IsDirectory ? 0 : 1) { Tag = item };
                row.SubItems.Add(entry.IsDirectory ? "文件夹" : Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant());
                row.SubItems.Add(entry.IsDirectory ? String.Empty : FormatSize(entry.Size));
                row.SubItems.Add(entry.ModifiedUtc == DateTime.MinValue ? String.Empty : entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                row.SubItems.Add(item.Repository.Name + "/" + entry.Path);
                rows[position] = row;
            }
            bool previousSuppression = suppressSelectionPreview;
            suppressSelectionPreview = true;
            files.BeginUpdate();
            try
            {
                SetColumns(ColumnLayout.Files, new[] { "名称", "类型", "大小", "修改时间", "所在路径" }, new[] { 210, 72, 85, 145, 350 });
                files.Items.AddRange(rows);
                if (!restoringHistory) ApplyColumnSort(false);
            }
            finally { files.EndUpdate(); suppressSelectionPreview = previousSuppression; }
            UpdateActions();
        }

        private async Task UpAsync()
        {
            if (!CanUseDirectoryActions()) return;
            if (String.IsNullOrEmpty(currentPath)) { ShowRepositories(); return; }
            await NavigateAsync(currentRepository, ParentPath(currentPath));
        }

        private static string ParentPath(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator < 0 ? String.Empty : path.Substring(0, separator);
        }

        private async Task RefreshViewAsync()
        {
            if (currentView == null) return;
            NavigationState previous = currentView;
            if (previous.Kind == ViewKind.Directory) await NavigateAsync(currentRepository, currentPath);
            else if (previous.Kind == ViewKind.Favorites) ShowFavorites(previous.Query);
            else if (previous.Kind == ViewKind.Repositories)
            {
                ShowRepositories(previous.Group);
                RestoreViewPosition(previous);
            }
            else if (!String.IsNullOrWhiteSpace(previous.Query))
            {
                search.Text = previous.Query;
                scope.SelectedIndex = previous.Scope;
                await ExecuteSearchAsync(previous.Query, previous.Scope, previous, previous);
            }
        }

        private void ShowEmptySearch()
        {
            CaptureViewPosition();
            CancelViewOperation();
            CancelPreview();
            currentRepository = FindViewRepository(repositoryView);
            currentPath = currentRepository == null ? String.Empty : repositoryView.Path;
            showingFavorites = false;
            search.Text = String.Empty;
            if (currentRepository == null) scope.SelectedIndex = AllRepositoriesScope;
            pageTitle.Text = "搜索结果";
            address.Text = "文件名搜索";
            PopulateEntries(Enumerable.Empty<FileItem>());
            indexStatus.Text = "选择搜索范围并输入文件名关键词；首次搜索会建立文件名索引。";
            status.Text = "等待搜索。";
            ClearDetails();
            CommitView(ViewKind.Search);
            search.Focus();
        }

        private Repository FindViewRepository(NavigationState view)
        {
            return view == null ? null : store.Config.Repositories.FirstOrDefault(repository => repository.Id == view.RepositoryId);
        }

        private List<Repository> SearchRepositories(int searchScope, Repository repository)
        {
            if (searchScope == AllRepositoriesScope) return store.Config.Repositories.ToList();
            return repository == null ? new List<Repository>() : new List<Repository> { repository };
        }

        private async Task SearchAsync()
        {
            string query = search.Text.Trim();
            if (showingFavorites)
            {
                ShowFavorites(query);
                return;
            }
            await ExecuteSearchAsync(query, scope.SelectedIndex, repositoryView);
        }

        private async Task ExecuteSearchAsync(string query, int queryScope, NavigationState origin, NavigationState previous = null)
        {
            Repository repository = FindViewRepository(origin);
            List<Repository> repositories = SearchRepositories(queryScope, repository);
            if (repositories.Count == 0) { MessageBox.Show(this, "请选择一个仓库，或把搜索范围切换为“所有仓库”。", "文件名搜索"); return; }
            if (query.Length == 0) { MessageBox.Show(this, "请输入文件名关键词。多个关键词可用空格分隔。", "文件名搜索"); return; }
            string originPath = repository == null ? String.Empty : origin.Path;
            string pathPrefix = queryScope == CurrentDirectoryScope ? originPath : String.Empty;
            string scopeName = (string)scope.Items[queryScope];
            CaptureViewPosition();
            CancelPreview();
            await RunOperationAsync("正在搜索文件名…", async token =>
            {
                IProgress<string> searchProgress = new Progress<string>(message =>
                {
                    if (!token.IsCancellationRequested && !IsDisposed) status.Text = message;
                });
                FilenameSearchResult result = await searchEngine.SearchAsync(repositories, query, pathPrefix, ResultLimit, searchProgress.Report, token);
                token.ThrowIfCancellationRequested();
                currentRepository = repository;
                currentPath = originPath;
                pageTitle.Text = "搜索结果 · " + query;
                address.Text = scopeName + (queryScope == AllRepositoriesScope ? String.Empty : " · " + repositories[0].Name) +
                    (pathPrefix.Length == 0 ? String.Empty : " / " + pathPrefix);
                Stopwatch renderTimer = Stopwatch.StartNew();
                if (currentView.Kind != ViewKind.Search && searchView != null)
                    columnSorts[ColumnLayout.Files] = new ColumnSortState { Column = searchView.SortColumn, Descending = searchView.SortDescending };
                PopulateEntries(result.Matches.Select(match => new FileItem { Repository = match.Repository, Entry = match.Entry }), true);
                renderTimer.Stop();
                indexStatus.Text = "索引最早更新于 " + result.OldestIndexUpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "；其他客户端的改动可手动更新索引。";
                status.Text = "找到 " + result.TotalCount + " 项 · 搜索 " + result.ElapsedMilliseconds + " ms，显示 " + renderTimer.ElapsedMilliseconds + " ms" + (result.TotalCount > ResultLimit ? " · 仅展示前 " + ResultLimit + " 项。" : String.Empty);
                ClearDetails();
                CommitView(ViewKind.Search, null, query, queryScope);
                if (previous != null) RestoreViewPosition(previous);
            });
        }

        private async Task RefreshIndexAsync()
        {
            NavigationState origin = currentView != null && currentView.Kind == ViewKind.Search && !String.IsNullOrEmpty(currentView.Query) ? currentView : repositoryView;
            List<Repository> repositories = SearchRepositories(scope.SelectedIndex, FindViewRepository(origin));
            if (repositories.Count == 0) { MessageBox.Show(this, "请选择仓库，或把范围设为“所有仓库”。", "更新索引"); return; }
            await RunOperationAsync("正在更新目录索引…", async token =>
            {
                int count = 0;
                foreach (Repository repository in repositories)
                {
                    status.Text = "正在扫描“" + repository.Name + "”的文件名；不下载文件内容…";
                    IndexSnapshot snapshot = await service.BuildIndexAsync(repository, token);
                    count += snapshot.Entries.Count;
                }
                token.ThrowIfCancellationRequested();
                indexStatus.Text = "搜索索引已更新：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "。";
                if (currentView != null) currentView.IndexStatus = indexStatus.Text;
                status.Text = "已更新 " + repositories.Count + " 个仓库，共 " + count + " 项目录信息。";
            });
        }

        private void ShowFavorites(string query = null)
        {
            CaptureViewPosition();
            NavigationState previous = currentView;
            CancelViewOperation();
            CancelPreview();
            showingFavorites = true;
            search.Text = query ?? String.Empty;
            currentRepository = null;
            currentPath = String.Empty;
            pageTitle.Text = "我的收藏";
            address.Text = "个人收藏 · 支持按名称、标签、备注搜索";
            SetColumns(ColumnLayout.Favorites, new[] { "名称", "类型", "标签", "备注", "所在路径" }, new[] { 190, 70, 140, 200, 350 });
            foreach (Favorite saved in store.Config.Favorites)
            {
                Repository repository = store.Config.Repositories.FirstOrDefault(r => r.Id == saved.RepositoryId);
                if (repository == null) continue;
                if (!String.IsNullOrWhiteSpace(query) && (saved.Name + " " + saved.Tags + " " + saved.Note + " " + saved.Path).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                SvnEntry entry = new SvnEntry { Name = Path.GetFileName(saved.Path), Path = saved.Path, IsDirectory = saved.IsDirectory, Revision = "HEAD" };
                FileItem item = new FileItem { Repository = repository, Entry = entry, Favorite = saved };
                ListViewItem row = new ListViewItem(saved.Name, saved.IsDirectory ? 0 : 1) { Tag = item };
                row.SubItems.Add(saved.IsDirectory ? "文件夹" : "文件");
                row.SubItems.Add(saved.Tags ?? String.Empty);
                row.SubItems.Add((saved.Note ?? String.Empty).Replace("\r", " ").Replace("\n", " "));
                row.SubItems.Add(repository.Name + "/" + saved.Path);
                files.Items.Add(row);
            }
            if (!restoringHistory) ApplyColumnSort(false);
            indexStatus.Text = "收藏保存文件入口，打开时读取最新版本。Ctrl+D 可编辑标签和备注。";
            status.Text = files.Items.Count + " 个收藏 · 在顶部搜索框查找名称、标签或备注。";
            ClearDetails();
            CommitView(ViewKind.Favorites);
            if (!restoringHistory && previous != null && previous.Kind == ViewKind.Favorites && previous.Query == search.Text)
                RestoreViewPosition(previous);
            UpdateActions();
        }

        private FileItem SelectedFile()
        {
            return files.SelectedItems.Count == 0 ? null : files.SelectedItems[0].Tag as FileItem;
        }

        private async Task ShowSelectionAsync()
        {
            CancelPreview();
            FileItem item = SelectedFile();
            UpdateActions();
            if (item == null) { ClearDetails(); return; }
            SetSelectionDetails(item);
            if (item.Entry.IsDirectory || demoMode) return;
            string extension = Path.GetExtension(item.Entry.Path);
            bool isText = TextExtensions.Contains(extension) || extension.Length == 0;
            bool isImage = ImageExtensions.Contains(extension);
            if (!isText && !isImage) return;
            if (isText && item.Entry.Size > TextPreviewLimit) { SetPreviewText("文本较大，请双击在临时查看窗口中打开，或下载后查看。"); return; }
            if (isImage && item.Entry.Size > ImagePreviewLimit) { SetPreviewText("图片较大，请使用“临时查看”。"); return; }
            CancellationTokenSource pending = new CancellationTokenSource();
            previewOperation = pending;
            try
            {
                await Task.Delay(250, pending.Token);
                await ResolveFavoriteEntryAsync(item, pending.Token);
                string path = await service.FetchAsync(item.Repository, item.Entry, pending.Token);
                pending.Token.ThrowIfCancellationRequested();
                if (isImage) ShowImage(path);
                else SetPreviewText(ReadText(path, TextPreviewLimit));
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!pending.IsCancellationRequested && !IsDisposed)
                {
                    SetPreviewText("预览失败：" + error.Message + "\r\n\r\n请检查仓库登录信息或文件是否已移动。程序不会自动重试。");
                    store.Log("preview.error", "repository=" + item.Repository.Id + "; " + error.Message);
                }
            }
            finally
            {
                if (previewOperation == pending) previewOperation = null;
                pending.Dispose();
            }
        }

        private void SetSelectionDetails(FileItem item)
        {
            Favorite saved = store.Config.Favorites.FirstOrDefault(f => f.RepositoryId == item.Repository.Id && f.Path == item.Entry.Path);
            detailTitle.Text = item.Favorite == null ? item.Entry.Name : item.Favorite.Name;
            detailMeta.Text = item.Repository.Name + "\n" + (item.Entry.IsDirectory ? "文件夹" : FormatSize(item.Entry.Size)) +
                (String.IsNullOrEmpty(item.Entry.Revision) ? String.Empty : " · r" + item.Entry.Revision) + "\n" + item.Entry.Path;
            detailNotes.Text = saved == null ? "尚未收藏。可添加标签与备注，方便下次查找。" : "标签：" + saved.Tags + "\n备注：" + saved.Note;
            favorite.Text = saved == null ? "收藏" : "编辑收藏";
            SetPreviewText(item.Entry.IsDirectory ? "双击进入文件夹。\r\n文件夹也可以添加到收藏。" : "双击临时查看。\r\n\r\nOffice / PDF 使用本机默认程序打开临时副本。\r\n需要保留文件时，请点击“下载到…”。");
        }

        private async Task ActivateSelectionAsync()
        {
            if (files.SelectedItems.Count == 0) return;
            Repository repository = files.SelectedItems[0].Tag as Repository;
            if (repository != null) { await OpenRepositoryAsync(repository); return; }
            FileItem item = SelectedFile();
            if (item == null) return;
            if (item.Entry.IsDirectory) { await NavigateAsync(item.Repository, item.Entry.Path); return; }
            string extension = Path.GetExtension(item.Entry.Path);
            if (!ImageExtensions.Contains(extension) && !TextExtensions.Contains(extension) && extension.Length > 0 && !ExternalViewExtensions.Contains(extension))
            {
                SetPreviewText("这种文件格式暂未提供预览。\r\n请使用“下载到…”保存后自行处理。");
                status.Text = "暂不支持预览 " + extension + " 文件；可选择下载。";
                return;
            }
            CancelPreview();
            await RunOperationAsync("正在获取临时副本…", async token =>
            {
                await ResolveFavoriteEntryAsync(item, token);
                string path = await service.FetchAsync(item.Repository, item.Entry, token);
                token.ThrowIfCancellationRequested();
                if (ImageExtensions.Contains(extension))
                {
                    ShowImage(path);
                    using (Form viewer = new Form { Text = "临时查看 · " + item.Entry.Name, Size = new Size(1000, 740), StartPosition = FormStartPosition.CenterParent })
                    using (Image loaded = Image.FromFile(path))
                    {
                        viewer.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = loaded, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(241, 245, 249) });
                        viewer.ShowDialog(this);
                    }
                }
                else if (TextExtensions.Contains(extension) || extension.Length == 0)
                {
                    string content = ReadText(path, TextPreviewLimit);
                    SetPreviewText(content);
                    using (Form viewer = new Form { Text = "临时查看（只读） · " + item.Entry.Name, Size = new Size(1000, 740), StartPosition = FormStartPosition.CenterParent, Font = Font })
                    {
                        viewer.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 11F * store.Config.UiFontSize / UiFontSettings.DefaultSize), Text = content, BackColor = Color.White });
                        viewer.ShowDialog(this);
                    }
                }
                else if (ExternalViewExtensions.Contains(extension))
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) }); }
                    catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("本机没有可打开这种文件的默认程序。请安装相应阅读软件，或选择“下载到…”。"); }
                    store.Log("preview.external", "repository=" + item.Repository.Id + "; extension=" + extension);
                }
                else
                {
                    SetPreviewText("这种文件格式暂未提供预览。\r\n请使用“下载到…”保存后自行处理。");
                    status.Text = "暂不支持预览 " + extension + " 文件；可选择下载。";
                    return;
                }
                status.Text = "正在临时查看 · " + item.Entry.Name + " · 临时副本只读，缓存可自动清理。";
            });
        }

        private async Task DownloadSelectionAsync()
        {
            FileItem item = SelectedFile();
            if (item == null || item.Entry.IsDirectory) return;
            using (SaveFileDialog dialog = new SaveFileDialog { Title = "选择文件下载位置", FileName = Path.GetFileName(item.Entry.Path), InitialDirectory = store.DataDirectory, OverwritePrompt = true, RestoreDirectory = true, Filter = "所有文件|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string destination = dialog.FileName;
                await RunOperationAsync("正在下载文件…", async token =>
                {
                    await ResolveFavoriteEntryAsync(item, token);
                    await service.DownloadAsync(item.Repository, item.Entry, destination, token);
                    token.ThrowIfCancellationRequested();
                    status.Text = "文件已保存：" + destination;
                });
            }
        }

        /// <summary>Opens the single-file submission dialog for the selected repository file.</summary>
        /// <remarks>Params: none.</remarks>
        /// <returns>A task that completes after the dialog and preview refresh finish.</returns>
        private async Task SubmitSelectionAsync()
        {
            FileItem item = SelectedFile();
            if (item == null || item.Entry.IsDirectory || operation != null || demoMode) return;
            CancelPreview();
            try
            {
                using (SubmitFileDialog dialog = new SubmitFileDialog(store, service, item.Repository, item.Entry.Path, store.Config.UiFontSize))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result == null) return;
                    SubmissionResult result = dialog.Result;
                    if (result.Entry != null)
                    {
                        RefreshSubmittedFile(item.Repository, item.Entry.Path, result.Entry);
                        await ShowSelectionAsync();
                    }
                    else SetPreviewText("提交结果已确认。请刷新目录以读取文件的最新信息。");
                    status.Text = result.Message;
                    store.Log("submission.ui.complete", "repository=" + item.Repository.Id + "; path=" + item.Entry.Path + "; status=" + result.Status + "; revision=" + result.Revision);
                }
            }
            catch (Exception error)
            {
                store.Log("submission.ui.error", "repository=" + item.Repository.Id + "; path=" + item.Entry.Path + "; error=" + error.Message);
                ShowError(error);
            }
        }

        private async Task AddToDirectoryAsync()
        {
            if (!CanUseDirectoryActions()) return;
            CancelPreview();
            Repository repository = currentRepository;
            string path = currentPath;
            using (AddItemsDialog dialog = new AddItemsDialog(store, service, repository, path, store.Config.UiFontSize))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result == null) return;
                SubmissionResult result = dialog.Result;
                store.Log("addition.ui.complete", "repository=" + repository.Id + "; path=" + path + "; revision=" + result.Revision);
                NavigationState submittedView = currentView;
                await NavigateAsync(repository, path);
                if (currentView.Kind == ViewKind.Directory && currentView.RepositoryId == repository.Id && currentView.Path == path)
                {
                    string refreshMessage = currentView == submittedView ? " 目录尚未刷新，可点击“刷新”重新读取。" : String.Empty;
                    status.Text = result.Message + refreshMessage;
                }
            }
        }

        private void ShowRevisionHistory(Repository repository, string path)
        {
            if (repository == null || operation != null || demoMode) return;
            CancelPreview();
            try
            {
                using (RevisionHistoryDialog dialog = new RevisionHistoryDialog(service, repository, path, store.Config.UiFontSize))
                    dialog.ShowDialog(this);
            }
            catch (Exception error)
            {
                store.Log("history.ui.error", "repository=" + repository.Id + "; path=" + path + "; error=" + error.Message);
                ShowError(error);
            }
        }

        /// <summary>Refreshes the submitted file in visible rows and saved navigation views.</summary>
        /// <param name="repository">The repository owning the submitted file.</param>
        /// <param name="path">The unchanged repository-relative target path.</param>
        /// <param name="entry">The confirmed metadata of the submitted revision.</param>
        /// <returns>None.</returns>
        private void RefreshSubmittedFile(Repository repository, string path, SvnEntry entry)
        {
            IEnumerable<FileItem> remembered = RememberedViews().Where(view => view.Entries != null).SelectMany(view => view.Entries);
            IEnumerable<FileItem> visible = files.Items.Cast<ListViewItem>().Select(row => row.Tag as FileItem).Where(file => file != null);
            foreach (FileItem file in remembered.Concat(displayedEntries).Concat(visible))
            {
                if (String.Equals(file.Repository.Id, repository.Id, StringComparison.Ordinal) &&
                    String.Equals(file.Entry.Path, path, StringComparison.Ordinal)) file.Entry = entry;
            }
            if (columnLayout == ColumnLayout.Files)
            {
                foreach (ListViewItem row in files.Items)
                {
                    FileItem file = row.Tag as FileItem;
                    if (file == null || !String.Equals(file.Repository.Id, repository.Id, StringComparison.Ordinal) ||
                        !String.Equals(file.Entry.Path, path, StringComparison.Ordinal)) continue;
                    row.SubItems[SizeColumn].Text = FormatSize(entry.Size);
                    row.SubItems[ModifiedColumn].Text = entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                }
                ApplyColumnSort(true);
            }
        }

        private async Task ResolveFavoriteEntryAsync(FileItem item, CancellationToken token)
        {
            if (item.Favorite == null) return;
            string parent = ParentPath(item.Favorite.Path);
            List<SvnEntry> siblings = await service.ListAsync(item.Repository, parent, false, token);
            token.ThrowIfCancellationRequested();
            SvnEntry latest = siblings.FirstOrDefault(e => String.Equals(e.Path, item.Favorite.Path, StringComparison.Ordinal));
            if (latest == null || latest.IsDirectory != item.Favorite.IsDirectory)
                throw new FileNotFoundException("收藏的文件已删除、移动或类型已变化，请重新定位后更新收藏。");
            item.Entry = latest;
        }

        private void EditFavorite()
        {
            FileItem item = SelectedFile();
            if (item == null) return;
            Favorite existing = store.Config.Favorites.FirstOrDefault(f => f.RepositoryId == item.Repository.Id && f.Path == item.Entry.Path);
            Favorite draft = existing ?? new Favorite { Id = Guid.NewGuid().ToString("N"), RepositoryId = item.Repository.Id, Path = item.Entry.Path, Name = item.Entry.Name, IsDirectory = item.Entry.IsDirectory, Tags = String.Empty, Note = String.Empty };
            using (FavoriteDialog dialog = new FavoriteDialog(draft, store.Config.UiFontSize))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (existing != null) store.Config.Favorites[store.Config.Favorites.IndexOf(existing)] = dialog.Result;
                else store.Config.Favorites.Add(dialog.Result);
                store.Save();
                store.Log("favorite.save", "repository=" + item.Repository.Id + "; favorite=" + dialog.Result.Id);
                BuildNavigation(currentRepository == null ? null : currentRepository.Id);
                if (showingFavorites) ShowFavorites(currentView.Query);
                else { detailNotes.Text = "标签：" + dialog.Result.Tags + "\n备注：" + dialog.Result.Note; favorite.Text = "编辑收藏"; }
                status.Text = "收藏已保存，可从左侧“我的收藏”快速访问。";
            }
        }

        private void RemoveFavorite()
        {
            FileItem item = SelectedFile();
            if (item == null) return;
            int removed = store.Config.Favorites.RemoveAll(f => f.RepositoryId == item.Repository.Id && f.Path == item.Entry.Path);
            if (removed == 0) return;
            store.Save();
            BuildNavigation(currentRepository == null ? null : currentRepository.Id);
            if (showingFavorites) ShowFavorites(currentView.Query);
            else { favorite.Text = "收藏"; detailNotes.Text = "尚未收藏。"; }
            status.Text = "已取消收藏。";
        }

        private void CopySelectedUrl()
        {
            FileItem item = SelectedFile();
            if (item != null) Clipboard.SetText(SvnService.BuildUrl(item.Repository, item.Entry.Path));
        }

        private void ShowSettings()
        {
            using (SettingsDialog dialog = new SettingsDialog(store))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    CancelOperation();
                    CancelPreview();
                    string executable = NativeRuntime.Resolve(store.DataDirectory, dialog.SvnExecutable);
                    store.Config.SvnExecutable = dialog.SvnExecutable;
                    store.Config.CacheLimitMb = dialog.CacheLimitMb;
                    store.Config.CacheDays = dialog.CacheDays;
                    ApplyUiFontSize(dialog.UiFontSize);
                    store.CleanupCache();
                    service = new SvnService(store, executable);
                    searchEngine = new FilenameSearchEngine(store, service);
                    if (!String.IsNullOrWhiteSpace(dialog.RequestedDataDirectory) && !String.Equals(Path.GetFullPath(dialog.RequestedDataDirectory).TrimEnd('\\'), store.DataDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        SwitchDataDirectory(dialog.RequestedDataDirectory);
                    else status.Text = "设置已保存 · 数据目录：" + store.DataDirectory;
                }
                catch (Exception error) { ShowError(error); }
            }
        }

        private void ApplyUiFontSize(float fontSize)
        {
            float previous = store.Config.UiFontSize;
            store.Config.UiFontSize = fontSize;
            try { store.Save(); }
            catch { store.Config.UiFontSize = previous; throw; }
            CaptureViewPosition();
            CancelPreview();
            bool previousSuppression = suppressSelectionPreview;
            suppressSelectionPreview = true;
            try
            {
                UiTypography.Apply(this, fontSize);
                float ratio = fontSize / previous;
                foreach (NavigationState view in RememberedViews())
                    if (view.ColumnWidths != null)
                        for (int index = 0; index < view.ColumnWidths.Length; index++) view.ColumnWidths[index] = (int)Math.Round(view.ColumnWidths[index] * ratio);
                if (currentView != null) RestoreViewPosition(currentView, false);
            }
            finally { suppressSelectionPreview = previousSuppression; }
            store.Log("ui.font.changed", "size_pt=" + fontSize.ToString(CultureInfo.InvariantCulture));
        }

        private IEnumerable<NavigationState> RememberedViews()
        {
            return backHistory.Concat(forwardHistory).Concat(new[] { currentView, repositoryView, favoritesView, searchView })
                .Where(view => view != null).Distinct();
        }

        private void SwitchDataDirectory(string target)
        {
            string fullPath = Path.GetFullPath(target);
            AppStore next = new AppStore(fullPath);
            if (next.Config.Repositories.Count == 0 && next.Config.Favorites.Count == 0)
            {
                next.Config.Repositories.AddRange(store.Config.Repositories);
                next.Config.Favorites.AddRange(store.Config.Favorites);
                next.Config.CacheDays = store.Config.CacheDays;
                next.Config.CacheLimitMb = store.Config.CacheLimitMb;
                next.Config.SvnExecutable = store.Config.SvnExecutable;
                next.Config.UiFontSize = store.Config.UiFontSize;
                next.Save();
            }
            string exe = Application.ExecutablePath;
            string shortcutPath = CreateDataDirectoryShortcut(fullPath, exe);
            next.Log("data.switch", "launch with configured data directory");
            Process.Start(new ProcessStartInfo(exe, "--data-dir " + QuoteLaunchArgument(fullPath)) { UseShellExecute = false, WorkingDirectory = fullPath });
            MessageBox.Show(this, "已在新数据目录启动窗口，并创建快捷方式：\n" + shortcutPath + "\n\n以后请使用该快捷方式启动。直接双击原 EXE 仍使用 EXE 旁的 data 目录。\n原目录的数据保留，缓存不会搬迁。", "数据目录已切换", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }

        public static string CreateDataDirectoryShortcut(string fullPath, string exe)
        {
            Directory.CreateDirectory(fullPath);
            string shortcutPath = Path.Combine(fullPath, "启动 Light SVN Browser.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "--data-dir " + QuoteLaunchArgument(fullPath) });
                shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { fullPath });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            return shortcutPath;
        }

        private static string QuoteLaunchArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + new string('\\', value.Reverse().TakeWhile(character => character == '\\').Count()) + "\"";
        }

        private async Task RunOperationAsync(string message, Func<CancellationToken, Task> action)
        {
            CancelOperation();
            CancellationTokenSource pending = new CancellationTokenSource();
            operation = pending;
            SetBusy(true, message);
            try { await action(pending.Token); }
            catch (OperationCanceledException) { if (operation == pending) status.Text = "操作已取消。"; }
            catch (Exception error)
            {
                if (!pending.IsCancellationRequested && !IsDisposed)
                {
                    store.Log("operation.error", error.Message);
                    if (operation == pending)
                    {
                        operation = null;
                        SetBusy(false, null);
                        if (currentView != null) SelectNavigationForView(currentView);
                    }
                    ShowError(error);
                }
            }
            finally
            {
                if (operation == pending)
                {
                    operation = null;
                    if (!IsDisposed)
                    {
                        SetBusy(false, null);
                        if (currentView != null) SelectNavigationForView(currentView);
                    }
                }
                pending.Dispose();
            }
        }

        private void SetBusy(bool busy, string message)
        {
            progress.Visible = busy;
            cancel.Visible = busy;
            foreach (Button button in operationButtons) button.Enabled = !busy;
            if (message != null) status.Text = message;
            UpdateActions();
        }

        private void CancelOperation()
        {
            if (operation != null) operation.Cancel();
        }

        private void CancelViewOperation()
        {
            if (operation == null) return;
            CancelOperation();
            operation = null;
            SetBusy(false, null);
        }

        private void CancelPreview()
        {
            if (previewOperation != null) previewOperation.Cancel();
        }

        private void UpdateActions()
        {
            FileItem item = files == null ? null : SelectedFile();
            bool busy = operation != null;
            if (openFile != null) openFile.Enabled = item != null && !busy;
            if (favorite != null) favorite.Enabled = item != null && !busy;
            if (download != null) download.Enabled = item != null && !item.Entry.IsDirectory && !busy;
            bool canSubmit = item != null && !item.Entry.IsDirectory && !busy && !demoMode;
            if (submitFile != null) submitFile.Enabled = canSubmit;
            if (submitFileMenuItem != null) submitFileMenuItem.Enabled = canSubmit;
            bool directoryActions = CanUseDirectoryActions();
            if (up != null) up.Enabled = directoryActions;
            if (goToRoot != null) goToRoot.Enabled = RootNavigationRepository() != null;
            if (addItems != null) addItems.Enabled = directoryActions;
            if (directoryHistory != null) directoryHistory.Enabled = directoryActions;
            if (locateMenuItem != null)
            {
                locateMenuItem.Visible = currentView != null && !IsRepositoryView(currentView);
                locateMenuItem.Enabled = CanLocate(item);
            }
            if (selectionHistoryMenuItem != null) selectionHistoryMenuItem.Enabled = item != null && !busy && !demoMode;
            if (scope != null) scope.Enabled = !showingFavorites;
            if (searchLabel != null) searchLabel.Text = showingFavorites ? "筛选收藏" : "文件名搜索";
            if (back != null) back.Enabled = backHistory.Count > 0 && !busy;
            if (forward != null) forward.Enabled = forwardHistory.Count > 0 && !busy;
            bool canManageRepository = IsRepositoryView(currentView) && SelectedRepository() != null && !busy && !demoMode;
            if (editRepository != null) editRepository.Enabled = canManageRepository;
            if (removeRepository != null) removeRepository.Enabled = canManageRepository;
        }

        private bool CanUseDirectoryActions()
        {
            return currentView != null && currentView.Kind == ViewKind.Directory && currentRepository != null && operation == null && !demoMode;
        }

        private Repository RootNavigationRepository()
        {
            if (currentView == null || operation != null || demoMode) return null;
            if (currentView.Kind == ViewKind.Directory && !String.IsNullOrEmpty(currentPath)) return currentRepository;
            if (currentView.Kind == ViewKind.Repositories && files.SelectedItems.Count > 0) return files.SelectedItems[0].Tag as Repository;
            return null;
        }

        private bool CanLocate(FileItem item)
        {
            return item != null && currentView != null && !IsRepositoryView(currentView) && operation == null && !demoMode;
        }

        private void ClearDetails()
        {
            CancelPreview();
            detailTitle.Text = "文件预览";
            detailMeta.Text = "选择文件查看详情。\n双击临时打开，按需另存到本地。";
            detailNotes.Text = "收藏支持标签与备注。";
            favorite.Text = "收藏";
            SetPreviewText("文本与图片可在此预览。\r\n\r\nOffice / PDF 临时交给本机默认程序查看。\r\n\r\n文件只会按需保存到配置目录内的缓存。\r\n需要保留时再点击“下载到…”。");
            UpdateActions();
        }

        private void SetPreviewText(string content)
        {
            DisposeImage();
            image.Visible = false;
            preview.Visible = true;
            preview.Text = content;
        }

        private void ShowImage(string path)
        {
            DisposeImage();
            using (Image source = Image.FromFile(path)) currentImage = new Bitmap(source);
            image.Image = currentImage;
            preview.Visible = false;
            image.Visible = true;
            image.BringToFront();
        }

        private void DisposeImage()
        {
            if (image != null) image.Image = null;
            if (currentImage != null) { currentImage.Dispose(); currentImage = null; }
        }

        private void ShowError(Exception error)
        {
            status.Text = "操作未完成：" + error.Message;
            MessageBox.Show(this, error.Message + "\n\n本次操作已停止，程序不会自动重试。", "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static string FormatSize(long size)
        {
            const double kilobyte = 1024;
            const double megabyte = kilobyte * 1024;
            if (size >= megabyte) return (size / megabyte).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
            if (size >= kilobyte) return (size / kilobyte).ToString("0.##", CultureInfo.InvariantCulture) + " KB";
            return size + " B";
        }

        private static string ReadText(string path, int limit)
        {
            byte[] bytes;
            bool truncated;
            using (FileStream input = File.OpenRead(path))
            {
                truncated = input.Length > limit;
                bytes = new byte[Math.Min(input.Length, limit)];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = input.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) break;
                    offset += read;
                }
            }
            Encoding encoding = new UTF8Encoding(false, true);
            string content;
            const int bomLength = 2;
            const byte unicodeMarker = 0xff;
            const byte unicodeSecondMarker = 0xfe;
            const byte bigEndianMarker = 0xfe;
            const byte bigEndianSecondMarker = 0xff;
            if (bytes.Length >= bomLength && bytes[0] == unicodeMarker && bytes[1] == unicodeSecondMarker) content = Encoding.Unicode.GetString(bytes);
            else if (bytes.Length >= bomLength && bytes[0] == bigEndianMarker && bytes[1] == bigEndianSecondMarker) content = Encoding.BigEndianUnicode.GetString(bytes);
            else
            {
                try { content = encoding.GetString(bytes); }
                catch (DecoderFallbackException) { content = Encoding.GetEncoding(936).GetString(bytes); }
            }
            return content.TrimStart('\ufeff') + (truncated ? "\r\n\r\n[仅展示前 2 MB，下载可获取完整文件]" : String.Empty);
        }

        /// <summary>Renders local sample data for the offline UI smoke check without connecting to a repository.</summary>
        /// <returns>No return value.</returns>
        public void RenderDemo()
        {
            demoMode = true;
            Repository repository = new Repository { Id = "demo-repo", Name = "产品历史文档", Group = "研发资料/产品组", Url = "https://svn.example.invalid/docs", Username = "team.reader" };
            store.Config.Repositories.Add(repository);
            store.Config.Repositories.Add(new Repository { Id = "demo-api", Name = "接口与开发规范", Group = "研发资料/公共资料", Url = "https://svn.example.invalid/api" });
            store.Config.Favorites.Add(new Favorite { Id = "demo-fav", RepositoryId = repository.Id, Path = "设计文档/回访参数配置.md", Name = "回访参数配置.md", Tags = "回访, 参数", Note = "常用参数与配置说明", IsDirectory = false });
            currentRepository = repository;
            BuildNavigation(repository.Id);
            pageTitle.Text = "产品历史文档";
            address.Text = repository.Url + "/设计文档";
            indexStatus.Text = "搜索索引更新于 2026-11-02 10:30 · 文件名索引，不下载整库";
            DateTime modified = new DateTime(2026, 11, 2, 2, 30, 0, DateTimeKind.Utc);
            PopulateEntries(new[]
            {
                new FileItem { Repository = repository, Entry = new SvnEntry { Name = "历史版本", Path = "设计文档/历史版本", IsDirectory = true, ModifiedUtc = modified, Revision = "1728" } },
                new FileItem { Repository = repository, Entry = new SvnEntry { Name = "回访参数配置.md", Path = "设计文档/回访参数配置.md", Size = 8192, ModifiedUtc = modified, Revision = "1729", Author = "team.reader" } },
                new FileItem { Repository = repository, Entry = new SvnEntry { Name = "系统接口设计说明.docx", Path = "设计文档/系统接口设计说明.docx", Size = 280000, ModifiedUtc = modified, Revision = "1729" } },
                new FileItem { Repository = repository, Entry = new SvnEntry { Name = "业务参数清单.xlsx", Path = "设计文档/业务参数清单.xlsx", Size = 45100, ModifiedUtc = modified, Revision = "1727" } },
                new FileItem { Repository = repository, Entry = new SvnEntry { Name = "部署与运维手册.pdf", Path = "设计文档/部署与运维手册.pdf", Size = 1200000, ModifiedUtc = modified, Revision = "1726" } }
            });
            detailTitle.Text = "回访参数配置.md";
            detailMeta.Text = "产品历史文档\n8 KB · r1729\n设计文档/回访参数配置.md";
            detailNotes.Text = "标签：回访, 参数\n备注：常用参数与配置说明";
            SetPreviewText("# 回访参数配置\r\n\r\n## 使用说明\r\n\r\n这里展示按需读取的文档内容。\r\n\r\n- 搜索只建立文件名索引\r\n- 收藏仅保存文件入口\r\n- 查看使用临时只读副本\r\n\r\n需要保留文件时，选择“下载到…”。");
            status.Text = "5 项 · 示例数据目录：程序目录\\data";
        }
    }
}
