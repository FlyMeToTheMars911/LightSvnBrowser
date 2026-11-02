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
        private const string FavoritesKey = "favorites";
        private const string RepositoriesKey = "repositories";
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
        private SvnService service;
        private FilenameSearchEngine searchEngine;
        private TreeView navigation;
        private ListView files;
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
        private Button back;
        private Button forward;
        private Button editRepository;
        private Button removeRepository;
        private Button openFile;
        private Button download;
        private Button favorite;
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
        private List<FileItem> displayedEntries = new List<FileItem>();
        private readonly List<NavigationState> backHistory = new List<NavigationState>();
        private readonly List<NavigationState> forwardHistory = new List<NavigationState>();
        private readonly List<Button> operationButtons = new List<Button>();

        private enum ViewKind { Repositories, Directory, Search, Favorites }

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
            header.Resize += delegate { settings.Location = new Point(header.ClientSize.Width - settings.Width - 20, 19); };
            settings.Click += delegate { ShowSettings(); };
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel searchBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Padding = new Padding(18, 10, 18, 10), Margin = Padding.Empty, BackColor = Color.FromArgb(248, 250, 252) };
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            searchBar.Controls.Add(new Label { Text = "文件名搜索", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
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
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            Button add = MakeButton("＋ 添加 SVN 仓库", true);
            add.Dock = DockStyle.Fill;
            add.Click += delegate { AddOrEditRepository(null); };
            sidebar.Controls.Add(add, 0, 0);
            navigation = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = sidebar.BackColor, ForeColor = Ink, HideSelection = false, FullRowSelect = true, ShowLines = false, ShowRootLines = false, ItemHeight = 32, Indent = 17, Margin = new Padding(0, 15, 0, 8), AccessibleName = "仓库和收藏导航" };
            navigation.AfterSelect += async delegate(object sender, TreeViewEventArgs args)
            {
                if (rebuildingNavigation || demoMode) return;
                NavigationItem item = args.Node.Tag as NavigationItem;
                if (item == null) return;
                if (item.Kind == FavoritesKey) ShowFavorites();
                else if (item.Repository != null) await NavigateAsync(item.Repository, String.Empty);
                else ShowRepositories(item.Group);
            };
            ContextMenuStrip treeMenu = new ContextMenuStrip();
            treeMenu.Items.Add("添加仓库", null, delegate { AddOrEditRepository(null); });
            treeMenu.Items.Add("编辑选中仓库", null, delegate { if (SelectedRepository() != null) AddOrEditRepository(SelectedRepository()); });
            treeMenu.Items.Add("删除选中仓库", null, delegate { DeleteRepository(); });
            navigation.ContextMenuStrip = treeMenu;
            navigation.NodeMouseClick += delegate(object sender, TreeNodeMouseClickEventArgs args) { if (args.Button == MouseButtons.Right) navigation.SelectedNode = args.Node; };
            sidebar.Controls.Add(navigation, 0, 1);
            sidebar.Controls.Add(new Label { Text = "只保存目录索引\n查看文件时按需获取", ForeColor = Muted, AutoSize = true, Margin = new Padding(6, 8, 0, 0) }, 0, 2);
            outer.Panel1.Controls.Add(sidebar);

            TableLayoutPanel workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.White, Padding = new Padding(18, 10, 18, 0), Margin = Padding.Empty };
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.Panel2.Controls.Add(workspace);
            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, Margin = Padding.Empty };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            pageTitle = new Label { Text = "我的仓库", Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            toolbar.Controls.Add(pageTitle, 0, 0);
            back = MakeButton("后退", false);
            back.Width = 60;
            back.Click += delegate { GoBack(); };
            toolbar.Controls.Add(back, 1, 0);
            forward = MakeButton("前进", false);
            forward.Width = 60;
            forward.Click += delegate { GoForward(); };
            toolbar.Controls.Add(forward, 2, 0);
            up = MakeButton("上一级", false);
            up.Click += async delegate { await UpAsync(); };
            toolbar.Controls.Add(up, 3, 0);
            Button refresh = MakeButton("刷新", false);
            refresh.Click += async delegate
            {
                if (currentRepository != null && !showingFavorites) await NavigateAsync(currentRepository, currentPath);
                else if (showingFavorites) ShowFavorites(); else ShowRepositories();
            };
            toolbar.Controls.Add(refresh, 4, 0);
            editRepository = MakeButton("编辑仓库", false);
            editRepository.Click += delegate { if (SelectedRepository() != null) AddOrEditRepository(SelectedRepository()); };
            toolbar.Controls.Add(editRepository, 5, 0);
            removeRepository = MakeButton("删除仓库", false);
            removeRepository.Click += delegate { DeleteRepository(); };
            toolbar.Controls.Add(removeRepository, 6, 0);
            operationButtons.Add(refresh);
            workspace.Controls.Add(toolbar, 0, 0);

            address = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(248, 250, 252), Margin = new Padding(0, 5, 0, 4), AccessibleName = "当前路径" };
            workspace.Controls.Add(address, 0, 1);
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
            files.SelectedIndexChanged += async delegate { if (!suppressSelectionPreview) await ShowSelectionAsync(); };
            files.KeyDown += async delegate(object sender, KeyEventArgs args)
            {
                if (args.Alt && (args.KeyCode == Keys.Left || args.KeyCode == Keys.Right)) return;
                if (args.KeyCode == Keys.Back) { args.SuppressKeyPress = true; await UpAsync(); }
                else if (args.Control && args.KeyCode == Keys.D) { args.SuppressKeyPress = true; EditFavorite(); }
            };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("临时查看 / 打开文件夹", null, async delegate { await ActivateSelectionAsync(); });
            menu.Items.Add("下载到…", null, async delegate { await DownloadSelectionAsync(); });
            menu.Items.Add("收藏 / 编辑标签备注", null, delegate { EditFavorite(); });
            menu.Items.Add("取消收藏", null, delegate { RemoveFavorite(); });
            menu.Items.Add("复制 SVN 地址", null, delegate { CopySelectedUrl(); });
            files.ContextMenuStrip = menu;
            content.Panel1.Controls.Add(files);

            TableLayoutPanel detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(15, 8, 4, 10), BackColor = Color.FromArgb(248, 250, 252) };
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
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
            detailNotes = new Label { Text = "收藏支持标签与备注。", Dock = DockStyle.Fill, ForeColor = Muted, AutoEllipsis = true, Padding = new Padding(0, 8, 0, 0) };
            detail.Controls.Add(detailNotes, 0, 3);
            Panel previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
            preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Consolas", 9F), Text = "目录浏览与文件名搜索不会下载整库。\r\n\r\n文本和图片可在此预览。\r\nOffice / PDF 临时交给本机默认程序查看。", AccessibleName = "文件预览内容" };
            image = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Visible = false };
            previewHost.Controls.Add(preview);
            previewHost.Controls.Add(image);
            detail.Controls.Add(previewHost, 0, 4);
            content.Panel2.Controls.Add(detail);
            content.SizeChanged += delegate
            {
                const int preferredDetailWidth = 300;
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
                TreeNode favoriteNode = new TreeNode("★ 我的收藏  (" + store.Config.Favorites.Count + ")") { Tag = new NavigationItem { Kind = FavoritesKey } };
                navigation.Nodes.Add(favoriteNode);
                TreeNode root = new TreeNode("▣ 我的仓库") { Tag = new NavigationItem { Kind = RepositoriesKey, Group = String.Empty } };
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
                            groupNode = new TreeNode(part) { Tag = new NavigationItem { Kind = "group", Group = group } };
                            parent.Nodes.Add(groupNode);
                            groups[group] = groupNode;
                        }
                        parent = groupNode;
                    }
                    TreeNode node = new TreeNode(repository.Name) { Tag = new NavigationItem { Kind = "repository", Repository = repository }, ToolTipText = repository.Url };
                    parent.Nodes.Add(node);
                    if (repository.Id == selectedRepositoryId) navigation.SelectedNode = node;
                }
                root.ExpandAll();
                if (showingFavorites) navigation.SelectedNode = favoriteNode;
                else if (navigation.SelectedNode == null) navigation.SelectedNode = root;
            }
            finally { navigation.EndUpdate(); rebuildingNavigation = false; }
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
            CancelOperation();
            CancelPreview();
            showingFavorites = false;
            search.Text = String.Empty;
            currentRepository = null;
            currentPath = String.Empty;
            pageTitle.Text = String.IsNullOrEmpty(group) ? "我的仓库" : group;
            address.Text = "数据目录：" + store.DataDirectory;
            SetColumns(new[] { "仓库名称", "分组", "SVN 地址", "登录用户" }, new[] { 170, 130, 340, 130 });
            foreach (Repository repository in store.Config.Repositories.Where(r => String.IsNullOrEmpty(group) || String.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase) || (r.Group ?? String.Empty).StartsWith(group + "/", StringComparison.OrdinalIgnoreCase)))
            {
                ListViewItem row = new ListViewItem(repository.Name, 0) { Tag = repository };
                row.SubItems.Add(repository.Group ?? String.Empty);
                row.SubItems.Add(repository.Url);
                row.SubItems.Add(repository.Username ?? String.Empty);
                files.Items.Add(row);
            }
            indexStatus.Text = store.Config.Repositories.Count == 0 ? "点击左侧“添加 SVN 仓库”。分组填写“团队/产品”即可形成文件夹。" : "双击仓库进入目录；在左侧按文件夹组织多个地址。";
            ClearDetails();
            CommitView(ViewKind.Repositories, group);
            UpdateActions();
        }

        private void SetColumns(string[] names, int[] widths)
        {
            bool previousSuppression = suppressSelectionPreview;
            suppressSelectionPreview = true;
            files.Items.Clear();
            files.Columns.Clear();
            for (int index = 0; index < names.Length; index++) files.Columns.Add(names[index], widths[index]);
            suppressSelectionPreview = previousSuppression;
        }

        private void CaptureViewPosition()
        {
            if (restoringHistory || currentView == null) return;
            currentView.SelectedKey = files.SelectedItems.Count == 0 ? null : ItemKey(files.SelectedItems[0].Tag);
            try { currentView.TopIndex = files.TopItem == null ? 0 : files.TopItem.Index; }
            catch (InvalidOperationException) { currentView.TopIndex = 0; }
            currentView.ColumnWidths = files.Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray();
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
            NavigationState next = new NavigationState
            {
                Kind = kind, RepositoryId = currentRepository == null ? null : currentRepository.Id,
                Path = currentPath, Group = group ?? String.Empty, Query = executedQuery ?? search.Text, Scope = executedScope ?? scope.SelectedIndex,
                Title = pageTitle.Text, Address = address.Text, IndexStatus = indexStatus.Text, Status = status.Text,
                Entries = kind == ViewKind.Directory || kind == ViewKind.Search ? displayedEntries : null
            };
            if (!restoringHistory && currentView != null)
            {
                bool repeated = currentView.Kind == kind && currentView.RepositoryId == next.RepositoryId &&
                    currentView.Path == next.Path && currentView.Group == next.Group && currentView.Query == next.Query && currentView.Scope == next.Scope;
                if (!repeated)
                {
                    backHistory.Add(currentView);
                    if (backHistory.Count > MaximumHistoryViews) backHistory.RemoveAt(0);
                    forwardHistory.Clear();
                }
            }
            currentView = next;
            UpdateActions();
        }

        private void ClearHistory()
        {
            currentView = null;
            backHistory.Clear();
            forwardHistory.Clear();
        }

        private void GoBack() { RestoreHistory(backHistory, forwardHistory); }

        private void GoForward() { RestoreHistory(forwardHistory, backHistory); }

        private void RestoreHistory(List<NavigationState> source, List<NavigationState> destination)
        {
            if (source.Count == 0 || operation != null) return;
            CaptureViewPosition();
            CancelPreview();
            NavigationState target = source[source.Count - 1];
            source.RemoveAt(source.Count - 1);
            if (currentView != null) destination.Add(currentView);
            restoringHistory = true;
            try
            {
                search.Text = target.Query ?? String.Empty;
                scope.SelectedIndex = target.Scope;
                if (target.Kind == ViewKind.Repositories) ShowRepositories(target.Group);
                else if (target.Kind == ViewKind.Favorites) ShowFavorites(target.Query);
                else
                {
                    currentRepository = store.Config.Repositories.FirstOrDefault(repository => repository.Id == target.RepositoryId);
                    currentPath = target.Path ?? String.Empty;
                    showingFavorites = false;
                    PopulateEntries(target.Entries ?? new List<FileItem>(), true);
                    ClearDetails();
                }
                currentView = target;
                search.Text = target.Query ?? String.Empty;
                pageTitle.Text = target.Title;
                address.Text = target.Address;
                indexStatus.Text = target.IndexStatus;
                status.Text = target.Status;
                RestoreViewPosition(target);
                SelectNavigationForView(target);
                store.Log("navigation.history", "kind=" + target.Kind + "; back=" + backHistory.Count + "; forward=" + forwardHistory.Count);
            }
            finally { restoringHistory = false; UpdateActions(); }
        }

        private void RestoreViewPosition(NavigationState view)
        {
            suppressSelectionPreview = true;
            try
            {
                if (view.ColumnWidths != null && view.ColumnWidths.Length == files.Columns.Count)
                    for (int index = 0; index < view.ColumnWidths.Length; index++) files.Columns[index].Width = view.ColumnWidths[index];
                foreach (ListViewItem row in files.Items)
                    if (view.SelectedKey != null && ItemKey(row.Tag) == view.SelectedKey) { row.Selected = true; row.Focused = true; break; }
                if (files.Items.Count > 0) files.TopItem = files.Items[Math.Min(Math.Max(view.TopIndex, 0), files.Items.Count - 1)];
                FileItem selected = SelectedFile();
                if (selected != null)
                {
                    detailTitle.Text = selected.Favorite == null ? selected.Entry.Name : selected.Favorite.Name;
                    detailMeta.Text = selected.Repository.Name + "\n" + selected.Entry.Path;
                    SetPreviewText("已恢复上次列表和选择位置。\r\n双击可临时查看，或继续选择其他搜索结果。");
                }
            }
            finally { suppressSelectionPreview = false; }
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
                        else if (view.Kind == ViewKind.Search && view.RepositoryId == null && item.Kind == RepositoriesKey) selected = node;
                        else if (view.Kind == ViewKind.Repositories && item.Repository == null && item.Group == (view.Group ?? String.Empty) && item.Kind != FavoritesKey) selected = node;
                        else if (view.RepositoryId != null && item.Repository != null && item.Repository.Id == view.RepositoryId) selected = node;
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

        private async Task NavigateAsync(Repository repository, string relativePath)
        {
            if (repository == null) return;
            CaptureViewPosition();
            CancelPreview();
            string targetPath = relativePath ?? String.Empty;
            await RunOperationAsync("正在读取目录…", async token =>
            {
                List<SvnEntry> entries = await service.ListAsync(repository, targetPath, false, token);
                token.ThrowIfCancellationRequested();
                currentRepository = repository;
                currentPath = targetPath;
                showingFavorites = false;
                pageTitle.Text = repository.Name;
                address.Text = SvnService.BuildUrl(repository, targetPath);
                PopulateEntries(entries.Select(e => new FileItem { Repository = repository, Entry = e }));
                DateTime? indexUpdated = store.GetIndexUpdatedUtc(repository.Id);
                indexStatus.Text = indexUpdated == null ? "目录按需读取。首次搜索会建立文件名索引。" : "搜索索引更新于 " + indexUpdated.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "；新增文件后可点击“更新搜索索引”。";
                ClearDetails();
                status.Text = entries.Count + " 项 · 双击文件临时查看；下载时自行选择位置。";
                CommitView(ViewKind.Directory);
            });
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
                SetColumns(new[] { "名称", "类型", "大小", "修改时间", "所在路径" }, new[] { 210, 72, 85, 145, 350 });
                files.Items.AddRange(rows);
            }
            finally { files.EndUpdate(); suppressSelectionPreview = previousSuppression; }
            UpdateActions();
        }

        private async Task UpAsync()
        {
            if (currentRepository == null || showingFavorites) return;
            if (String.IsNullOrEmpty(currentPath)) { ShowRepositories(); return; }
            int separator = currentPath.LastIndexOf('/');
            await NavigateAsync(currentRepository, separator < 0 ? String.Empty : currentPath.Substring(0, separator));
        }

        private List<Repository> SearchRepositories()
        {
            const int allRepositoriesScope = 1;
            if (scope.SelectedIndex == allRepositoriesScope) return store.Config.Repositories.ToList();
            Repository repository = currentRepository ?? SelectedRepository();
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
            List<Repository> repositories = SearchRepositories();
            if (repositories.Count == 0) { MessageBox.Show(this, "请选择一个仓库，或把搜索范围切换为“所有仓库”。", "文件名搜索"); return; }
            if (query.Length == 0) { MessageBox.Show(this, "请输入文件名关键词。多个关键词可用空格分隔。", "文件名搜索"); return; }
            const int currentDirectoryScope = 2;
            string pathPrefix = scope.SelectedIndex == currentDirectoryScope ? currentPath : String.Empty;
            string scopeName = scope.Text;
            int queryScope = scope.SelectedIndex;
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
                pageTitle.Text = "搜索结果 · " + query;
                address.Text = scopeName + (pathPrefix.Length == 0 ? String.Empty : " / " + pathPrefix);
                Stopwatch renderTimer = Stopwatch.StartNew();
                PopulateEntries(result.Matches.Select(match => new FileItem { Repository = match.Repository, Entry = match.Entry }), true);
                renderTimer.Stop();
                indexStatus.Text = "索引最早更新于 " + result.OldestIndexUpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "；新增文件后可更新索引。";
                status.Text = "找到 " + result.TotalCount + " 项 · 搜索 " + result.ElapsedMilliseconds + " ms，显示 " + renderTimer.ElapsedMilliseconds + " ms" + (result.TotalCount > ResultLimit ? " · 仅展示前 " + ResultLimit + " 项。" : String.Empty);
                ClearDetails();
                CommitView(ViewKind.Search, null, query, queryScope);
            });
        }

        private async Task RefreshIndexAsync()
        {
            List<Repository> repositories = SearchRepositories();
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
                status.Text = "已更新 " + repositories.Count + " 个仓库，共 " + count + " 项目录信息。";
            });
        }

        private void ShowFavorites(string query = null)
        {
            CaptureViewPosition();
            CancelOperation();
            CancelPreview();
            showingFavorites = true;
            search.Text = query ?? String.Empty;
            currentRepository = null;
            currentPath = String.Empty;
            pageTitle.Text = "我的收藏";
            address.Text = "个人收藏 · 支持按名称、标签、备注搜索";
            SetColumns(new[] { "名称", "类型", "标签", "备注", "所在路径" }, new[] { 190, 70, 140, 200, 350 });
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
            indexStatus.Text = "收藏保存文件入口，打开时读取最新版本。Ctrl+D 可编辑标签和备注。";
            status.Text = files.Items.Count + " 个收藏 · 在顶部搜索框查找名称、标签或备注。";
            ClearDetails();
            CommitView(ViewKind.Favorites);
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
            Favorite saved = store.Config.Favorites.FirstOrDefault(f => f.RepositoryId == item.Repository.Id && f.Path == item.Entry.Path);
            detailTitle.Text = item.Favorite == null ? item.Entry.Name : item.Favorite.Name;
            detailMeta.Text = item.Repository.Name + "\n" + (item.Entry.IsDirectory ? "文件夹" : FormatSize(item.Entry.Size)) +
                (String.IsNullOrEmpty(item.Entry.Revision) ? String.Empty : " · r" + item.Entry.Revision) + "\n" + item.Entry.Path;
            detailNotes.Text = saved == null ? "尚未收藏。可添加标签与备注，方便下次查找。" : "标签：" + saved.Tags + "\n备注：" + saved.Note;
            favorite.Text = saved == null ? "收藏" : "编辑收藏";
            SetPreviewText(item.Entry.IsDirectory ? "双击进入文件夹。\r\n文件夹也可以添加到收藏。" : "双击临时查看。\r\n\r\nOffice / PDF 使用本机默认程序打开临时副本。\r\n需要保留文件时，请点击“下载到…”。");
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

        private async Task ActivateSelectionAsync()
        {
            if (files.SelectedItems.Count == 0) return;
            Repository repository = files.SelectedItems[0].Tag as Repository;
            if (repository != null) { await NavigateAsync(repository, String.Empty); return; }
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
                        viewer.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 11F), Text = content, BackColor = Color.White });
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

        private async Task ResolveFavoriteEntryAsync(FileItem item, CancellationToken token)
        {
            if (item.Favorite == null) return;
            int separator = item.Favorite.Path.LastIndexOf('/');
            string parent = separator < 0 ? String.Empty : item.Favorite.Path.Substring(0, separator);
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
            using (FavoriteDialog dialog = new FavoriteDialog(draft))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (existing != null) store.Config.Favorites[store.Config.Favorites.IndexOf(existing)] = dialog.Result;
                else store.Config.Favorites.Add(dialog.Result);
                store.Save();
                store.Log("favorite.save", "repository=" + item.Repository.Id + "; favorite=" + dialog.Result.Id);
                BuildNavigation(currentRepository == null ? null : currentRepository.Id);
                if (showingFavorites) ShowFavorites(search.Text.Trim());
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
            if (showingFavorites) ShowFavorites();
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
                    store.Save();
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
                    if (operation == pending) { operation = null; SetBusy(false, null); }
                    ShowError(error);
                }
            }
            finally
            {
                if (operation == pending) { operation = null; if (!IsDisposed) SetBusy(false, null); }
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
            if (up != null) up.Enabled = currentRepository != null && !showingFavorites && !busy;
            if (back != null) back.Enabled = backHistory.Count > 0 && !busy;
            if (forward != null) forward.Enabled = forwardHistory.Count > 0 && !busy;
            if (editRepository != null) editRepository.Enabled = SelectedRepository() != null && !busy;
            if (removeRepository != null) removeRepository.Enabled = SelectedRepository() != null && !busy;
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
