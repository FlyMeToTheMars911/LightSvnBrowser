using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LightSvnBrowser
{
    /// <summary>统一配置对话框的排版和控件样式。</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    internal static class DialogStyle
    {
        internal static readonly Color Accent = Color.FromArgb(37, 99, 235);
        internal static readonly Color Muted = Color.FromArgb(100, 116, 139);
        internal const int LabelWidth = 104;

        internal static void Apply(Form form, string title, Size size)
        {
            form.Text = title;
            form.ClientSize = size;
            form.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            form.BackColor = Color.White;
            form.ForeColor = Color.FromArgb(30, 41, 59);
            form.AutoScaleDimensions = new SizeF(96F, 96F);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterParent;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.ShowIcon = false;
            form.Padding = new Padding(22, 18, 22, 16);
        }

        internal static TableLayoutPanel CreateFields()
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.Margin = Padding.Empty;
            table.Padding = Padding.Empty;
            return table;
        }

        internal static void AddField(TableLayoutPanel table, string title, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label label = new Label();
            label.Text = title;
            label.AutoSize = true;
            label.Margin = new Padding(0, 6, 8, 14);
            table.Controls.Add(label, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 0, 0, 12);
            table.Controls.Add(control, 1, row);
        }

        internal static void AddHint(TableLayoutPanel table, string text)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label hint = new Label();
            hint.Text = text;
            hint.AutoSize = true;
            hint.ForeColor = Muted;
            hint.Margin = new Padding(0, 0, 0, 14);
            table.Controls.Add(hint, 1, row);
        }

        internal static TextBox TextField(string value, int tabIndex)
        {
            TextBox box = new TextBox();
            box.Text = value ?? string.Empty;
            box.TabIndex = tabIndex;
            box.BorderStyle = BorderStyle.FixedSingle;
            return box;
        }

        internal static Button Button(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(96, 34);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(203, 213, 225);
            button.BackColor = primary ? Accent : Color.White;
            button.ForeColor = primary ? Color.White : Color.FromArgb(51, 65, 85);
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(8, 0, 0, 0);
            return button;
        }

        internal static void AddButtons(Form form, Button save)
        {
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.Dock = DockStyle.Bottom;
            bar.Height = 46;
            bar.Padding = new Padding(0, 10, 0, 0);
            bar.WrapContents = false;
            bar.TabIndex = 100;
            Button cancel = Button("取消", false);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.TabIndex = 1;
            save.TabIndex = 0;
            bar.Controls.Add(cancel);
            bar.Controls.Add(save);
            form.Controls.Add(bar);
            form.AcceptButton = save;
            form.CancelButton = cancel;
        }

        internal static TableLayoutPanel WithAction(Control field, Control action)
        {
            TableLayoutPanel panel = new TableLayoutPanel();
            panel.ColumnCount = 2;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104F));
            panel.RowCount = 1;
            panel.Height = 38;
            panel.Margin = Padding.Empty;
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(0, 2, 8, 0);
            action.Dock = DockStyle.Fill;
            action.Margin = Padding.Empty;
            panel.Controls.Add(field, 0, 0);
            panel.Controls.Add(action, 1, 0);
            return panel;
        }

        internal static void Error(Form owner, string message, Control focus)
        {
            MessageBox.Show(owner, message, "请检查填写内容", MessageBoxButtons.OK, MessageBoxIcon.Information);
            focus.Focus();
        }
    }

    /// <summary>编辑仓库连接配置；保存配置时不访问 SVN 服务器。</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public sealed class RepositoryDialog : Form
    {
        private const string HttpScheme = "http";
        private const string HttpsScheme = "https";
        private const string SvnScheme = "svn";
        private const string FileScheme = "file";
        private const string SshScheme = "svn+ssh";
        private readonly AppStore _store;
        private readonly Repository _original;
        private readonly TextBox _name;
        private readonly ComboBox _group;
        private readonly TextBox _url;
        private readonly CheckBox _allowIpCertificateNameMismatch;
        private readonly TextBox _username;
        private readonly TextBox _password;
        private readonly bool _passwordUnavailable;

        public Repository Result { get; private set; }

        public RepositoryDialog(AppStore store, Repository existing, IEnumerable<string> groups)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            _original = existing;
            DialogStyle.Apply(this, existing == null ? "添加仓库" : "编辑仓库", new Size(654, 470));
            TableLayoutPanel fields = DialogStyle.CreateFields();
            _name = DialogStyle.TextField(existing == null ? string.Empty : existing.Name, 0);
            _group = new ComboBox();
            _group.DropDownStyle = ComboBoxStyle.DropDown;
            _group.TabIndex = 1;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (groups != null)
            {
                foreach (string group in groups)
                {
                    if (!string.IsNullOrWhiteSpace(group) && seen.Add(group.Trim())) _group.Items.Add(group.Trim());
                }
            }
            _group.Text = existing == null ? string.Empty : existing.Group ?? string.Empty;
            _url = DialogStyle.TextField(existing == null ? string.Empty : SvnService.FormatDisplayUrl(existing.Url), 2);
            _allowIpCertificateNameMismatch = new CheckBox();
            _allowIpCertificateNameMismatch.Text = "允许 IP 地址与证书域名不一致（仅此仓库）";
            _allowIpCertificateNameMismatch.AutoSize = true;
            _allowIpCertificateNameMismatch.TabIndex = 3;
            _allowIpCertificateNameMismatch.Checked = existing != null && existing.AllowIpCertificateNameMismatch;
            _url.TextChanged += delegate { UpdateIpCertificateOption(); };
            UpdateIpCertificateOption();
            _username = DialogStyle.TextField(existing == null ? string.Empty : existing.Username, 4);
            _password = DialogStyle.TextField(string.Empty, 0);
            _password.UseSystemPasswordChar = true;
            if (existing != null && !string.IsNullOrEmpty(existing.ProtectedPassword))
            {
                try
                {
                    _password.Text = store.Unprotect(existing.ProtectedPassword) ?? string.Empty;
                }
                catch (Exception exception)
                {
                    _passwordUnavailable = true;
                    store.Log("WARN", "打开仓库设置时无法解密已保存密码：" + exception.GetType().Name);
                }
            }
            CheckBox showPassword = new CheckBox();
            showPassword.Text = "显示密码";
            showPassword.AutoSize = true;
            showPassword.TabIndex = 1;
            showPassword.CheckedChanged += delegate { _password.UseSystemPasswordChar = !showPassword.Checked; };
            TableLayoutPanel passwordPanel = DialogStyle.WithAction(_password, showPassword);
            passwordPanel.TabIndex = 5;
            DialogStyle.AddField(fields, "仓库名称 *", _name);
            DialogStyle.AddField(fields, "分组路径", _group);
            DialogStyle.AddHint(fields, "可填写多级分组，如：团队/产品；留空则放在未分组中。");
            DialogStyle.AddField(fields, "SVN 地址 *", _url);
            DialogStyle.AddHint(fields, "支持 http、https、svn 和 file 地址。保存时仅记录配置，不连接服务器。");
            DialogStyle.AddField(fields, "HTTPS 证书", _allowIpCertificateNameMismatch);
            DialogStyle.AddHint(fields, "仅适用于 HTTPS 的 IP 地址；仅放行主机名不匹配，仍验证证书有效期与签发机构。");
            DialogStyle.AddField(fields, "用户名", _username);
            DialogStyle.AddField(fields, "密码", passwordPanel);
            DialogStyle.AddHint(fields, _passwordUnavailable
                ? "原密码无法解密。留空可保留原记录，或输入新密码替换。"
                : "无需认证时可留空。密码在本机加密保存。");
            Controls.Add(fields);
            Button save = DialogStyle.Button("保存", true);
            save.Click += SaveClicked;
            DialogStyle.AddButtons(this, save);
            Shown += delegate { _name.Focus(); };
            UiTypography.Apply(this, store.Config.UiFontSize);
        }

        private void SaveClicked(object sender, EventArgs args)
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                DialogStyle.Error(this, "请填写仓库名称。", _name);
                return;
            }
            string normalizedUrl;
            string error;
            if (!ValidateUrl(_url.Text, out normalizedUrl, out error))
            {
                DialogStyle.Error(this, error, _url);
                return;
            }
            if (_original != null && String.Equals(_url.Text.Trim(), SvnService.FormatDisplayUrl(_original.Url).Trim(), StringComparison.Ordinal))
                normalizedUrl = _original.Url;
            try
            {
                string protectedPassword = _passwordUnavailable && string.IsNullOrEmpty(_password.Text)
                    ? _original.ProtectedPassword
                    : (string.IsNullOrEmpty(_password.Text) ? string.Empty : _store.Protect(_password.Text));
                Result = new Repository
                {
                    Id = _original == null || string.IsNullOrEmpty(_original.Id) ? Guid.NewGuid().ToString("N") : _original.Id,
                    Name = _name.Text.Trim(),
                    Group = NormalizeGroup(_group.Text),
                    Url = normalizedUrl,
                    AllowIpCertificateNameMismatch = _allowIpCertificateNameMismatch.Enabled && _allowIpCertificateNameMismatch.Checked,
                    Username = _username.Text.Trim(),
                    ProtectedPassword = protectedPassword
                };
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                _store.Log("ERROR", "保存仓库设置时密码加密失败：" + exception.GetType().Name);
                MessageBox.Show(this, "无法加密保存密码，请检查本机用户环境后再保存。", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>仅在 HTTPS 的 IP 地址下启用单仓库证书名称兼容选项。</summary>
        /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
        private void UpdateIpCertificateOption()
        {
            Uri uri;
            bool isHttpsIp = Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out uri)
                && string.Equals(uri.Scheme, HttpsScheme, StringComparison.OrdinalIgnoreCase)
                && (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6);
            _allowIpCertificateNameMismatch.Enabled = isHttpsIp;
            if (!isHttpsIp) _allowIpCertificateNameMismatch.Checked = false;
        }

        private static string NormalizeGroup(string value)
        {
            string[] parts = (value ?? string.Empty).Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> names = new List<string>();
            foreach (string part in parts)
            {
                if (!string.IsNullOrWhiteSpace(part)) names.Add(part.Trim());
            }
            return string.Join("/", names.ToArray());
        }

        private static bool ValidateUrl(string value, out string normalized, out string error)
        {
            normalized = string.Empty;
            error = "请填写完整的 SVN 地址，如 https://svn.example.com/svn/docs。";
            Uri uri;
            if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out uri)) return false;
            if (string.Equals(uri.Scheme, SshScheme, StringComparison.OrdinalIgnoreCase))
            {
                error = "当前版本暂不支持 svn+ssh，请使用 http、https、svn 或 file 地址。";
                return false;
            }
            bool supported = string.Equals(uri.Scheme, HttpScheme, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, HttpsScheme, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, SvnScheme, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, FileScheme, StringComparison.OrdinalIgnoreCase);
            if (!supported)
            {
                error = "SVN 地址仅支持 http、https、svn 和 file 协议。";
                return false;
            }
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "SVN 地址中不能包含用户名、密码、查询参数或 # 片段。请在下方单独填写用户名和密码。";
                return false;
            }
            if (!uri.IsFile && string.IsNullOrWhiteSpace(uri.Host)) return false;
            normalized = uri.AbsoluteUri.TrimEnd('/');
            if (uri.IsFile)
            {
                try
                {
                    string localPath = uri.LocalPath;
                    string root = Path.GetPathRoot(localPath);
                    if (string.Equals(localPath.TrimEnd('\\', '/'), (root ?? string.Empty).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = uri.AbsoluteUri;
                    }
                }
                catch (ArgumentException)
                {
                    error = "file 地址中的路径无效，请填写完整的本地仓库或共享目录地址。";
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>编辑个人收藏的名称、标签和备注。</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public sealed class FavoriteDialog : Form
    {
        private readonly Favorite _original;
        private readonly TextBox _name;
        private readonly TextBox _tags;
        private readonly TextBox _note;

        public Favorite Result { get; private set; }

        public FavoriteDialog(Favorite existing, float uiFontSize = UiFontSettings.DefaultSize)
        {
            if (existing == null) throw new ArgumentNullException("existing");
            _original = existing;
            DialogStyle.Apply(this, "编辑收藏", new Size(654, 380));
            TableLayoutPanel fields = DialogStyle.CreateFields();
            _name = DialogStyle.TextField(existing.Name, 0);
            TextBox path = DialogStyle.TextField(existing.Path, 1);
            path.ReadOnly = true;
            path.BackColor = Color.FromArgb(248, 250, 252);
            path.TabStop = false;
            _tags = DialogStyle.TextField(existing.Tags, 2);
            _note = DialogStyle.TextField(existing.Note, 3);
            _note.Multiline = true;
            _note.ScrollBars = ScrollBars.Vertical;
            _note.AcceptsReturn = true;
            _note.Height = 118;
            DialogStyle.AddField(fields, "收藏名称 *", _name);
            DialogStyle.AddField(fields, "仓库内路径", path);
            DialogStyle.AddField(fields, "标签", _tags);
            DialogStyle.AddHint(fields, "多个标签用逗号分隔，如：需求,接口,常用。");
            DialogStyle.AddField(fields, "备注", _note);
            Controls.Add(fields);
            Button save = DialogStyle.Button("保存", true);
            save.Click += SaveClicked;
            DialogStyle.AddButtons(this, save);
            Shown += delegate { _name.Focus(); _name.SelectAll(); };
            UiTypography.Apply(this, uiFontSize);
        }

        private void SaveClicked(object sender, EventArgs args)
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                DialogStyle.Error(this, "请填写收藏名称。", _name);
                return;
            }
            List<string> tags = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string tag in _tags.Text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = tag.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed)) tags.Add(trimmed);
            }
            Result = new Favorite
            {
                Id = string.IsNullOrEmpty(_original.Id) ? Guid.NewGuid().ToString("N") : _original.Id,
                RepositoryId = _original.RepositoryId,
                Path = _original.Path,
                Name = _name.Text.Trim(),
                Tags = string.Join(",", tags.ToArray()),
                Note = _note.Text.Trim(),
                IsDirectory = _original.IsDirectory
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>配置数据目录、SVN 客户端和按需下载缓存。</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public sealed class SettingsDialog : Form
    {
        private const int MinimumCacheMb = 32;
        private const int MaximumCacheMb = 2048;
        private const int MinimumCacheDays = 1;
        private const int MaximumCacheDays = 90;
        private const string ExecutableExtension = ".exe";
        private readonly AppStore _store;
        private readonly TextBox _dataDirectory;
        private readonly TextBox _svnExecutable;
        private readonly NumericUpDown _cacheLimit;
        private readonly NumericUpDown _cacheDays;
        private readonly NumericUpDown _uiFontSize;

        public string RequestedDataDirectory { get; private set; }
        public string SvnExecutable { get; private set; }
        public int CacheLimitMb { get; private set; }
        public int CacheDays { get; private set; }
        public float UiFontSize { get; private set; }

        public SettingsDialog(AppStore store)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            DialogStyle.Apply(this, "设置", new Size(696, 470));
            TableLayoutPanel fields = DialogStyle.CreateFields();
            _dataDirectory = DialogStyle.TextField(store.DataDirectory, 0);
            _dataDirectory.ReadOnly = true;
            _dataDirectory.BackColor = Color.FromArgb(248, 250, 252);
            _dataDirectory.TabStop = false;
            Button chooseData = DialogStyle.Button("更换目录", false);
            chooseData.TabIndex = 0;
            chooseData.Click += ChooseDataDirectory;
            TableLayoutPanel dataPanel = DialogStyle.WithAction(_dataDirectory, chooseData);
            dataPanel.TabIndex = 0;
            DialogStyle.AddField(fields, "数据目录", dataPanel);
            DialogStyle.AddHint(fields, "配置、收藏、日志和缓存保存在此目录。默认使用程序目录下的 data；也可通过 --data-dir 或环境变量指定。更换目录将在保存后由主窗口处理。");
            _svnExecutable = DialogStyle.TextField(store.Config.SvnExecutable, 0);
            Button chooseSvn = DialogStyle.Button("浏览…", false);
            chooseSvn.TabIndex = 1;
            chooseSvn.Click += ChooseSvnExecutable;
            TableLayoutPanel svnPanel = DialogStyle.WithAction(_svnExecutable, chooseSvn);
            svnPanel.TabIndex = 1;
            DialogStyle.AddField(fields, "SVN 客户端", svnPanel);
            DialogStyle.AddHint(fields, "可指定 svn.exe 路径；留空时自动查找系统或程序随附的命令行客户端。");
            _cacheLimit = new NumericUpDown();
            _cacheLimit.Minimum = MinimumCacheMb;
            _cacheLimit.Maximum = MaximumCacheMb;
            _cacheLimit.Increment = 32;
            _cacheLimit.Value = Math.Max(MinimumCacheMb, Math.Min(MaximumCacheMb, store.Config.CacheLimitMb));
            _cacheLimit.TabIndex = 2;
            _cacheDays = new NumericUpDown();
            _cacheDays.Minimum = MinimumCacheDays;
            _cacheDays.Maximum = MaximumCacheDays;
            _cacheDays.Value = Math.Max(MinimumCacheDays, Math.Min(MaximumCacheDays, store.Config.CacheDays));
            _cacheDays.TabIndex = 3;
            DialogStyle.AddField(fields, "缓存上限 / MB", _cacheLimit);
            DialogStyle.AddField(fields, "保留天数 / 天", _cacheDays);
            DialogStyle.AddHint(fields, "仅缓存按需打开的文件。启动或查看文件时自动清理过期及超额缓存。");
            _uiFontSize = new NumericUpDown();
            _uiFontSize.Minimum = (decimal)UiFontSettings.MinimumSize;
            _uiFontSize.Maximum = (decimal)UiFontSettings.MaximumSize;
            _uiFontSize.DecimalPlaces = 1;
            _uiFontSize.Increment = 0.5M;
            _uiFontSize.Value = (decimal)Math.Max(UiFontSettings.MinimumSize, Math.Min(UiFontSettings.MaximumSize, store.Config.UiFontSize));
            _uiFontSize.TabIndex = 0;
            Button resetFont = DialogStyle.Button("恢复默认", false);
            resetFont.TabIndex = 1;
            resetFont.Click += delegate { _uiFontSize.Value = (decimal)UiFontSettings.DefaultSize; };
            TableLayoutPanel fontPanel = DialogStyle.WithAction(_uiFontSize, resetFont);
            fontPanel.TabIndex = 4;
            DialogStyle.AddField(fields, "界面字号 / pt", fontPanel);
            DialogStyle.AddHint(fields, "字号范围 8–16 pt，默认 9 pt。保存后立即应用到主窗口及对话框。");
            Controls.Add(fields);
            Button save = DialogStyle.Button("保存", true);
            save.Click += SaveClicked;
            DialogStyle.AddButtons(this, save);
            UiTypography.Apply(this, store.Config.UiFontSize);
        }

        private void ChooseDataDirectory(object sender, EventArgs args)
        {
            using (FolderBrowserDialog chooser = new FolderBrowserDialog())
            {
                chooser.Description = "选择数据保存目录（保存设置后处理目录变更）";
                chooser.ShowNewFolderButton = true;
                if (Directory.Exists(_dataDirectory.Text)) chooser.SelectedPath = _dataDirectory.Text;
                if (chooser.ShowDialog(this) != DialogResult.OK) return;
                _dataDirectory.Text = chooser.SelectedPath;
            }
        }

        private void ChooseSvnExecutable(object sender, EventArgs args)
        {
            using (OpenFileDialog chooser = new OpenFileDialog())
            {
                chooser.Title = "选择 SVN 命令行客户端";
                chooser.Filter = "SVN 客户端 (svn.exe)|svn.exe|可执行文件 (*.exe)|*.exe";
                chooser.CheckFileExists = true;
                chooser.Multiselect = false;
                if (File.Exists(_svnExecutable.Text)) chooser.FileName = _svnExecutable.Text;
                if (chooser.ShowDialog(this) != DialogResult.OK) return;
                _svnExecutable.Text = chooser.FileName;
            }
        }

        private void SaveClicked(object sender, EventArgs args)
        {
            string executable = _svnExecutable.Text.Trim();
            if (!string.IsNullOrEmpty(executable))
            {
                try
                {
                    executable = Path.GetFullPath(executable);
                    if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ExecutableExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        DialogStyle.Error(this, "请选择存在的 SVN 命令行客户端 exe 文件，或留空以自动查找。", _svnExecutable);
                        return;
                    }
                }
                catch (Exception exception)
                {
                    _store.Log("WARN", "设置中的 SVN 客户端路径无效：" + exception.GetType().Name);
                    DialogStyle.Error(this, "SVN 客户端路径无效，请重新选择。", _svnExecutable);
                    return;
                }
            }
            string selectedDirectory = Path.GetFullPath(_dataDirectory.Text);
            string currentDirectory = Path.GetFullPath(_store.DataDirectory);
            RequestedDataDirectory = string.Equals(selectedDirectory.TrimEnd('\\', '/'), currentDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? null : selectedDirectory;
            SvnExecutable = executable;
            CacheLimitMb = Decimal.ToInt32(_cacheLimit.Value);
            CacheDays = Decimal.ToInt32(_cacheDays.Value);
            UiFontSize = Decimal.ToSingle(_uiFontSize.Value);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
