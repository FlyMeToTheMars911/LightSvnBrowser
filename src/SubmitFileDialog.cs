using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LightSvnBrowser
{
    /// <summary>在固定 SVN 目标下核对本地文件并显式提交单文件新版本。</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public sealed class SubmitFileDialog : Form
    {
        private readonly AppStore _store;
        private readonly SvnService _service;
        private readonly Repository _repository;
        private readonly string _relativePath;
        private readonly TextBox _targetPath;
        private readonly Label _serverMeta;
        private readonly TextBox _localFile;
        private readonly Label _localMeta;
        private readonly Label _nameNotice;
        private readonly Label _sourceStatus;
        private readonly CheckBox _acknowledge;
        private readonly TextBox _message;
        private readonly Button _chooseLocal;
        private readonly Button _refresh;
        private readonly Button _downloadLatest;
        private readonly Button _verify;
        private readonly Button _submit;
        private readonly Button _cancel;
        private readonly TextBox _status;
        private readonly Color _warningColor = Color.FromArgb(146, 64, 14);
        private CancellationTokenSource _operation;
        private SvnEntry _latest;
        private SubmissionAttempt _pending;
        private bool _busy;
        private bool _committing;
        private bool _verifying;
        private bool _unresolved;
        private bool _sourceUnknown;
        private bool _sourceStale;
        private bool _localValid;
        private bool _closeAfterCancel;

        /// <summary>成功提交或确认内容未变化后的结果，供主窗口刷新文件视图。</summary>
        public SubmissionResult Result { get; private set; }

        /// <summary>创建提交确认窗口；构造期间只建立界面，不访问服务器。</summary>
        /// <param name="store">当前数据目录的配置与日志存储。</param>
        /// <param name="service">SVN 单文件提交服务。</param>
        /// <param name="repository">选中文件所属仓库。</param>
        /// <param name="relativePath">仓库内固定目标文件路径。</param>
        /// <param name="fontSize">界面字号。</param>
        /// <returns>不返回值。</returns>
        public SubmitFileDialog(AppStore store, SvnService service, Repository repository, string relativePath, float fontSize)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (service == null) throw new ArgumentNullException("service");
            if (repository == null) throw new ArgumentNullException("repository");
            if (String.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("请选择需要更新的 SVN 文件。", "relativePath");
            _store = store;
            _service = service;
            _repository = repository;
            _relativePath = relativePath;

            DialogStyle.Apply(this, "提交新版本", new Size(760, 848));
            TableLayoutPanel fields = DialogStyle.CreateFields();
            DialogStyle.AddField(fields, "目标仓库", MakeLabel(repository.Name, 26));
            _targetPath = ReadOnlyField(relativePath + Environment.NewLine + SvnService.BuildDisplayUrl(repository, relativePath), 0);
            _targetPath.Multiline = true;
            _targetPath.Height = 60;
            _targetPath.ScrollBars = ScrollBars.Horizontal;
            _targetPath.WordWrap = false;
            _targetPath.AccessibleName = "固定 SVN 目标路径和地址";
            DialogStyle.AddField(fields, "目标文件", _targetPath);
            _serverMeta = MakeLabel("尚未读取服务器版本。", 48);
            _serverMeta.AccessibleName = "服务器文件版本";
            DialogStyle.AddField(fields, "服务器版本", _serverMeta);

            FlowLayoutPanel serverActions = new FlowLayoutPanel { Height = 36, WrapContents = false, Margin = Padding.Empty };
            _refresh = DialogStyle.Button("刷新版本", false);
            _refresh.Click += async delegate { await RefreshLatestAsync(); };
            _downloadLatest = DialogStyle.Button("下载最新版…", false);
            _downloadLatest.Width = 128;
            _downloadLatest.Click += async delegate { await DownloadLatestAsync(); };
            serverActions.Controls.Add(_refresh);
            serverActions.Controls.Add(_downloadLatest);
            DialogStyle.AddField(fields, String.Empty, serverActions);

            _localFile = ReadOnlyField(String.Empty, 1);
            _localFile.AccessibleName = "待提交的本地文件";
            _chooseLocal = DialogStyle.Button("选择文件…", false);
            _chooseLocal.Click += ChooseLocalFile;
            DialogStyle.AddField(fields, "本地文件", DialogStyle.WithAction(_localFile, _chooseLocal));
            _localMeta = MakeLabel("请选择已在本地保存的文件。", 25);
            DialogStyle.AddField(fields, "文件信息", _localMeta);
            _nameNotice = MakeLabel("仅更新以上目标文件；本地文件名不同也不会重命名 SVN 文件。", 42);
            DialogStyle.AddField(fields, String.Empty, _nameNotice);

            _sourceStatus = MakeLabel("选择本地文件并读取服务器版本后，检查来源版本。", 65);
            _sourceStatus.AccessibleName = "本地文件来源版本核对";
            DialogStyle.AddField(fields, "来源核对", _sourceStatus);
            _acknowledge = new CheckBox
            {
                Text = "我已核对服务器最新版，并确认提交所选文件的完整内容。",
                Height = 48, Enabled = false, AutoSize = false, TabIndex = 3,
                AccessibleName = "未知来源文件完整内容确认"
            };
            _acknowledge.CheckedChanged += delegate { UpdateAvailability(); };
            DialogStyle.AddField(fields, String.Empty, _acknowledge);

            _message = DialogStyle.TextField(String.Empty, 4);
            _message.Multiline = true;
            _message.Height = 70;
            _message.ScrollBars = ScrollBars.Vertical;
            _message.AcceptsReturn = true;
            _message.AccessibleName = "提交说明（必填）";
            _message.TextChanged += delegate { UpdateAvailability(); };
            DialogStyle.AddField(fields, "提交说明 *", _message);
            _status = ReadOnlyField("正在准备读取服务器文件信息…", 5);
            _status.Multiline = true;
            _status.Height = 80;
            _status.ScrollBars = ScrollBars.Vertical;
            _status.WordWrap = true;
            _status.AccessibleName = "提交操作状态";
            DialogStyle.AddField(fields, "操作状态", _status);
            Controls.Add(fields);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Padding = new Padding(0, 10, 0, 0), TabIndex = 100
            };
            _cancel = DialogStyle.Button("取消", false);
            _cancel.Click += CancelClicked;
            _submit = DialogStyle.Button("提交新版本", true);
            _submit.Width = 122;
            _submit.Enabled = false;
            _submit.Click += async delegate { await SubmitAsync(); };
            _verify = DialogStyle.Button("核实结果", false);
            _verify.Enabled = false;
            _verify.Click += async delegate { await VerifyAsync(); };
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_submit);
            buttons.Controls.Add(_verify);
            Controls.Add(buttons);
            CancelButton = _cancel;
            Shown += async delegate { await InitializeAsync(); };
            FormClosing += ClosingDialog;
            UiTypography.Apply(this, fontSize);
            UpdateAvailability();
        }

        /// <summary>创建可复制、不可编辑的路径字段。</summary>
        /// <param name="text">需要显示的完整路径。</param>
        /// <param name="tabIndex">键盘导航顺序。</param>
        /// <returns>只读文本框。</returns>
        private static TextBox ReadOnlyField(string text, int tabIndex)
        {
            TextBox field = DialogStyle.TextField(text, tabIndex);
            field.ReadOnly = true;
            field.BackColor = Color.FromArgb(248, 250, 252);
            return field;
        }

        /// <summary>创建固定区域内自动换行的说明文本。</summary>
        /// <param name="text">说明内容。</param>
        /// <param name="height">默认字号下的区域高度。</param>
        /// <returns>可换行标签。</returns>
        private static Label MakeLabel(string text, int height)
        {
            return new Label { Text = text, Height = height, AutoSize = false, ForeColor = DialogStyle.Muted, Padding = new Padding(0, 4, 0, 0) };
        }

        /// <summary>优先恢复本地待核实记录，仅在没有记录时读取服务器版本。</summary>
        /// <returns>初始化完成任务。</returns>
        private async Task InitializeAsync()
        {
            try
            {
                _unresolved = true;
                _pending = _service.GetPendingSubmission(_repository, _relativePath);
                _unresolved = _pending != null;
                if (_pending != null)
                {
                    _localFile.Text = _pending.LocalFile ?? String.Empty;
                    _message.Text = _pending.Message ?? String.Empty;
                    _serverMeta.Text = "上次提交基于 r" + _pending.BaseRevision + "；结果尚未核实。";
                    _unresolved = true;
                    _status.Text = "发现上次未确认结果的提交。请点击“核实结果”；确认前不能再次提交。";
                    _store.Log("submission.dialog_pending", "repository=" + _repository.Id + "; attempt=" + _pending.Id);
                    UpdateAvailability();
                    return;
                }
                await RefreshLatestAsync();
            }
            catch (Exception error) { ReportFailure("初始化", error); }
        }

        /// <summary>主动获取固定目标最新版本，始终保留本地文件原始来源记录。</summary>
        /// <returns>读取完成任务。</returns>
        private async Task RefreshLatestAsync()
        {
            if (_busy || _unresolved) return;
            BeginOperation("正在读取服务器最新文件信息…", false);
            _latest = null;
            _acknowledge.Checked = false;
            try
            {
                _latest = await _service.GetLatestFileAsync(_repository, _relativePath, _operation.Token);
                _serverMeta.Text = "r" + _latest.Revision + "  ·  " + (_latest.Author ?? String.Empty) + Environment.NewLine
                    + _latest.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                _status.Text = "已读取最新版本。请选择本地文件并填写提交说明。";
                EvaluateSource();
            }
            catch (OperationCanceledException) { _status.Text = "读取已取消；可保留输入后重试。"; }
            catch (Exception error) { ReportFailure("读取版本", error); }
            finally { EndOperation(); }
        }

        /// <summary>通过文件选择器选择本地已保存的完整文件。</summary>
        /// <param name="sender">选择按钮。</param>
        /// <param name="args">点击事件。</param>
        /// <returns>不返回值。</returns>
        private void ChooseLocalFile(object sender, EventArgs args)
        {
            using (OpenFileDialog chooser = new OpenFileDialog
            {
                Title = "选择要提交到 SVN 的本地文件", Filter = "所有文件|*.*", CheckFileExists = true,
                Multiselect = false, RestoreDirectory = true, InitialDirectory = _store.DataDirectory
            })
            {
                if (File.Exists(_localFile.Text)) chooser.FileName = _localFile.Text;
                if (chooser.ShowDialog(this) != DialogResult.OK) return;
                SetLocalFile(chooser.FileName);
            }
        }

        /// <summary>更新本地文件信息并重新核验来源，选择新文件后必须重新确认未知来源。</summary>
        /// <param name="path">本地文件绝对路径。</param>
        /// <returns>不返回值。</returns>
        private void SetLocalFile(string path)
        {
            _localFile.Text = path ?? String.Empty;
            _acknowledge.Checked = false;
            EvaluateSource();
            UpdateAvailability();
        }

        /// <summary>核对来源记录与目标文件版本；已知旧版本不能通过勾选确认绕过。</summary>
        /// <returns>不返回值。</returns>
        private void EvaluateSource()
        {
            _sourceUnknown = false;
            _sourceStale = false;
            _localValid = false;
            _sourceStatus.ForeColor = DialogStyle.Muted;
            _nameNotice.ForeColor = DialogStyle.Muted;
            _nameNotice.Text = "仅更新以上目标文件；本地文件名不同也不会重命名 SVN 文件。";
            try
            {
                if (!File.Exists(_localFile.Text))
                {
                    _localMeta.Text = "请选择存在的本地文件。";
                    _sourceStatus.Text = "尚未选择可提交的本地文件。";
                    return;
                }
                FileInfo file = new FileInfo(_localFile.Text);
                _localMeta.Text = file.Length.ToString("N0", CultureInfo.CurrentCulture) + " 字节  ·  "
                    + file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                _localValid = true;
                if (!String.Equals(file.Name, Path.GetFileName(_relativePath), StringComparison.OrdinalIgnoreCase))
                {
                    _nameNotice.Text = "本地“" + file.Name + "”将更新 SVN“" + Path.GetFileName(_relativePath) + "”；SVN 文件名保持不变。";
                    _nameNotice.ForeColor = _warningColor;
                }
                if (_latest == null)
                {
                    _sourceStatus.Text = "请先读取服务器版本，再核对本地文件来源。";
                    return;
                }
                DownloadSource source = _service.GetDownloadSource(_repository, _relativePath, _localFile.Text);
                _sourceUnknown = source == null;
                _sourceStale = source != null && !String.Equals(source.Revision, _latest.Revision, StringComparison.Ordinal);
                if (_sourceStale)
                {
                    _sourceStatus.ForeColor = _warningColor;
                    _sourceStatus.Text = "本地来源为 r" + source.Revision + "，服务器已为 r" + _latest.Revision
                        + "，暂不能提交。请下载最新版并整合修改，再选择相应文件；刷新版本不会改变原始来源。";
                }
                else if (_sourceUnknown)
                {
                    _sourceStatus.ForeColor = _warningColor;
                    _sourceStatus.Text = "来源版本未知，无法判断是否包含他人的更新。请先核对服务器最新版；提交会使用所选文件的完整内容。";
                }
                else _sourceStatus.Text = "本地下载来源为 r" + source.Revision + "，与当前服务器文件版本相同。提交前仍会再次核对服务器版本。";
            }
            catch (Exception error)
            {
                _localValid = false;
                _sourceStatus.Text = "无法核对本地文件：" + error.Message;
                _sourceStatus.ForeColor = _warningColor;
                _store.Log("submission.dialog_source_failed", "repository=" + _repository.Id + "; type=" + error.GetType().Name);
            }
        }

        /// <summary>主动另存服务器最新版；成功后选中下载文件并重新核对来源。</summary>
        /// <returns>下载完成任务。</returns>
        private async Task DownloadLatestAsync()
        {
            if (_busy || _unresolved || _latest == null) return;
            using (SaveFileDialog chooser = new SaveFileDialog
            {
                Title = "下载服务器最新版并另存", FileName = Path.GetFileName(_relativePath),
                InitialDirectory = _store.DataDirectory, OverwritePrompt = true, RestoreDirectory = true, Filter = "所有文件|*.*"
            })
            {
                if (chooser.ShowDialog(this) != DialogResult.OK) return;
                BeginOperation("正在下载已显示版本的文件…", false);
                try
                {
                    _latest = await _service.GetLatestFileAsync(_repository, _relativePath, _operation.Token);
                    _serverMeta.Text = "r" + _latest.Revision + "  ·  " + (_latest.Author ?? String.Empty) + Environment.NewLine
                        + _latest.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    _acknowledge.Checked = false;
                    await _service.DownloadAsync(_repository, _latest, chooser.FileName, _operation.Token);
                    SetLocalFile(chooser.FileName);
                    _status.Text = "最新版已下载并选中。请在本地编辑、保存后再提交。";
                }
                catch (OperationCanceledException) { _status.Text = "下载已取消，现有输入已保留。"; }
                catch (Exception error) { ReportFailure("下载最新版", error); }
                finally { EndOperation(); }
            }
        }

        /// <summary>在当前确认信息下提交单文件；进入服务器提交阶段后禁止关闭和取消。</summary>
        /// <returns>提交完成任务。</returns>
        private async Task SubmitAsync()
        {
            if (_busy || _unresolved) return;
            EvaluateSource();
            UpdateAvailability();
            if (!_submit.Enabled) return;
            BeginOperation("正在准备单文件提交，可取消准备操作…", false);
            try
            {
                IProgress<SubmissionProgress> progress = new DialogProgress(this);
                SubmissionResult result = await _service.SubmitFileAsync(_repository, _latest, _localFile.Text,
                    _message.Text.Trim(), progress, _operation.Token);
                HandleResult(result);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "提交准备已取消，所选文件和提交说明已保留。";
                RestorePending();
            }
            catch (Exception error)
            {
                _latest = null;
                ReportFailure("提交", error);
                RestorePending();
            }
            finally { EndOperation(); }
        }

        /// <summary>仅由用户主动触发核实待确认提交，核实期间禁止关闭。</summary>
        /// <returns>核实完成任务。</returns>
        private async Task VerifyAsync()
        {
            if (_busy || _pending == null) return;
            BeginOperation("正在核实上次提交结果，请等待…", true);
            try { HandleResult(await _service.VerifySubmissionAsync(_repository, _pending, _operation.Token)); }
            catch (Exception error) { ReportFailure("核实结果", error); }
            finally { EndOperation(); }
        }

        /// <summary>显示后端提交结果；不确定结果保持输入并阻止重复提交。</summary>
        /// <param name="result">后端提交或核实结果。</param>
        /// <returns>不返回值。</returns>
        private void HandleResult(SubmissionResult result)
        {
            if (result.Status == SubmissionStatus.Committed || result.Status == SubmissionStatus.NoChanges)
            {
                Result = result;
                _busy = false;
                _committing = false;
                _verifying = false;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _pending = result.Attempt;
            _unresolved = result.Status == SubmissionStatus.Unknown;
            _latest = null;
            _acknowledge.Checked = false;
            if (_unresolved)
            {
                if (_pending == null) _pending = _service.GetPendingSubmission(_repository, _relativePath);
                _status.Text = "提交结果尚未确认，不能再次提交。" + Environment.NewLine + result.Message + " 请点击“核实结果”。";
                _sourceStatus.Text = "本地文件和提交说明已保留；结果确认前禁止更换文件或重复提交。";
            }
            else
            {
                _pending = null;
                _status.Text = "已确认未提交。" + result.Message + " 请刷新服务器版本，重新核对后再提交。";
            }
        }

        /// <summary>发生异常后仅从磁盘恢复待核实记录，不自动访问服务器。</summary>
        /// <returns>不返回值。</returns>
        private void RestorePending()
        {
            try
            {
                _pending = _service.GetPendingSubmission(_repository, _relativePath);
                _unresolved = _pending != null;
                if (_unresolved) _status.Text += Environment.NewLine + "存在待核实提交；请点击“核实结果”，确认前不能再次提交。";
            }
            catch (Exception error)
            {
                _unresolved = true;
                _status.Text += Environment.NewLine + "无法读取提交记录，请保留本地文件并检查数据目录。";
                _store.Log("submission.dialog_pending_failed", "repository=" + _repository.Id + "; type=" + error.GetType().Name);
            }
        }

        /// <summary>开始串行操作并创建准备阶段取消令牌。</summary>
        /// <param name="message">当前阶段提示。</param>
        /// <param name="verifying">是否进入禁止关闭的结果核实阶段。</param>
        /// <returns>不返回值。</returns>
        private void BeginOperation(string message, bool verifying)
        {
            _busy = true;
            _committing = false;
            _verifying = verifying;
            _operation = new CancellationTokenSource();
            _status.Text = message;
            UpdateAvailability();
        }

        /// <summary>释放操作令牌并在取消关闭请求后安全退出窗口。</summary>
        /// <returns>不返回值。</returns>
        private void EndOperation()
        {
            if (_operation != null) { _operation.Dispose(); _operation = null; }
            _busy = false;
            _committing = false;
            _verifying = false;
            if (IsDisposed) return;
            UpdateAvailability();
            if (_closeAfterCancel) { _closeAfterCancel = false; Close(); }
        }

        /// <summary>按准备、提交和待核实状态统一维护按钮可用性。</summary>
        /// <returns>不返回值。</returns>
        private void UpdateAvailability()
        {
            bool editable = !_busy && !_unresolved;
            _chooseLocal.Enabled = editable;
            _message.ReadOnly = !editable;
            _refresh.Enabled = editable;
            _downloadLatest.Enabled = editable && _latest != null;
            _acknowledge.Enabled = editable && _latest != null && _localValid && _sourceUnknown && !_sourceStale;
            _submit.Enabled = editable && _latest != null && _localValid && !_sourceStale
                && (!_sourceUnknown || _acknowledge.Checked) && !String.IsNullOrWhiteSpace(_message.Text);
            _verify.Enabled = !_busy && _unresolved && _pending != null;
            _cancel.Enabled = !_busy || (!_committing && !_verifying);
            _cancel.Text = _busy ? "取消操作" : (_unresolved ? "关闭" : "取消");
        }

        /// <summary>展示操作失败并保留所有用户输入，不自动发起重试。</summary>
        /// <param name="operation">失败操作名称。</param>
        /// <param name="error">已经由后端处理敏感信息的异常。</param>
        /// <returns>不返回值。</returns>
        private void ReportFailure(string operation, Exception error)
        {
            _status.Text = operation + "失败：" + error.Message + Environment.NewLine + "本次操作已停止，没有自动重试；本地文件和说明已保留。";
            _store.Log("submission.dialog_failed", "repository=" + _repository.Id + "; operation=" + operation + "; type=" + error.GetType().Name);
            UpdateAvailability();
        }

        /// <summary>取消准备或关闭空闲窗口，不取消已经开始的服务器提交。</summary>
        /// <param name="sender">取消按钮。</param>
        /// <param name="args">点击事件。</param>
        /// <returns>不返回值。</returns>
        private void CancelClicked(object sender, EventArgs args)
        {
            if (_busy)
            {
                if (!_committing && !_verifying && _operation != null) _operation.Cancel();
                return;
            }
            DialogResult = DialogResult.Cancel;
            Close();
        }

        /// <summary>关闭窗口时等待准备取消完成，并保护提交与核实阶段。</summary>
        /// <param name="sender">当前窗口。</param>
        /// <param name="args">关闭事件。</param>
        /// <returns>不返回值。</returns>
        private void ClosingDialog(object sender, FormClosingEventArgs args)
        {
            if (!_busy) return;
            args.Cancel = true;
            if (_committing || _verifying) return;
            _closeAfterCancel = true;
            if (_operation != null) _operation.Cancel();
        }

        /// <summary>同步传递提交阶段，确保后台进入提交前主线程已经禁止取消和关闭。</summary>
        /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
        private sealed class DialogProgress : IProgress<SubmissionProgress>
        {
            private readonly SubmitFileDialog _dialog;

            /// <summary>绑定需要接收提交阶段的窗口。</summary>
            /// <param name="dialog">提交确认窗口。</param>
            /// <returns>不返回值。</returns>
            internal DialogProgress(SubmitFileDialog dialog) { _dialog = dialog; }

            /// <summary>在界面线程同步更新提交阶段和可操作状态。</summary>
            /// <param name="value">后端阶段信息。</param>
            /// <returns>不返回值。</returns>
            public void Report(SubmissionProgress value)
            {
                if (_dialog.IsDisposed) return;
                if (_dialog.InvokeRequired) { _dialog.Invoke(new Action<SubmissionProgress>(Report), value); return; }
                _dialog._committing = _dialog._committing || value.IsCommitting;
                _dialog._status.Text = value.Message;
                _dialog.UpdateAvailability();
            }
        }
    }
}
