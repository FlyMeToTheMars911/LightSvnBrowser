using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LightSvnBrowser
{
    /// <summary>Lets the user review and commit a mixed selection of local files and folders into one SVN directory.</summary>
    /// <remarks>Author: Codex, date: 2026-11-09.</remarks>
    public sealed class AddItemsDialog : Form, IProgress<SubmissionProgress>
    {
        private const int EmptySelection = 0;
        private readonly AppStore _store;
        private readonly SvnService _service;
        private readonly Repository _repository;
        private readonly string _path;
        private readonly ListBox _items;
        private readonly TextBox _message;
        private readonly TextBox _status;
        private readonly Button _chooseFiles;
        private readonly Button _chooseFolders;
        private readonly Button _remove;
        private readonly Button _submit;
        private readonly Button _verify;
        private readonly Button _cancel;
        private CancellationTokenSource _operation;
        private SubmissionAttempt _pending;
        private bool _pendingUnreadable;
        private bool _committing;
        private bool _closeAfterCancel;

        /// <summary>Confirmed commit result to use when refreshing the destination listing.</summary>
        public SubmissionResult Result { get; private set; }

        /// <summary>Creates the selection and review dialog for a fixed directory.</summary>
        /// <param name="store">Application data and settings.</param>
        /// <param name="service">SVN service.</param>
        /// <param name="repository">Destination repository.</param>
        /// <param name="path">Destination directory relative to the configured repository URL.</param>
        /// <param name="fontSize">Configured UI font size.</param>
        /// <returns>No value.</returns>
        public AddItemsDialog(AppStore store, SvnService service, Repository repository, string path, float fontSize)
        {
            _store = store;
            _service = service;
            _repository = repository;
            _path = path;
            DialogStyle.Apply(this, "新增到当前目录", new Size(760, 602));
            TableLayoutPanel fields = DialogStyle.CreateFields();

            TextBox target = DialogStyle.TextField(repository.Name + "  /  " + (String.IsNullOrEmpty(path) ? "根目录" : path)
                + Environment.NewLine + SvnService.BuildDisplayUrl(repository, path), 0);
            target.ReadOnly = true;
            target.TabStop = false;
            target.Multiline = true;
            target.WordWrap = false;
            target.ScrollBars = ScrollBars.Horizontal;
            target.Height = 58;
            target.AccessibleName = "新增目标目录和地址";
            DialogStyle.AddField(fields, "目标目录", target);

            FlowLayoutPanel selectionActions = new FlowLayoutPanel { Height = 36, WrapContents = false };
            _chooseFiles = DialogStyle.Button("选择文件…", false);
            _chooseFiles.Width = 112;
            _chooseFiles.Click += delegate
            {
                using (OpenFileDialog picker = new OpenFileDialog
                {
                    Title = "选择要提交到 SVN 的本地文件", Filter = "所有文件|*.*",
                    CheckFileExists = true, Multiselect = true, RestoreDirectory = true, InitialDirectory = _store.DataDirectory
                })
                {
                    if (picker.ShowDialog(this) == DialogResult.OK) AddPaths(picker.FileNames);
                }
            };
            _chooseFolders = DialogStyle.Button("选择文件夹…", false);
            _chooseFolders.Width = 132;
            _chooseFolders.Click += delegate
            {
                try { AddPaths(FolderSelectionDialog.SelectFolders(Handle)); }
                catch (Exception error) { _status.Text = "无法打开文件夹选择窗口：" + error.Message; }
            };
            _remove = DialogStyle.Button("移除所选", false);
            _remove.Click += delegate
            {
                foreach (string selected in _items.SelectedItems.Cast<string>().ToArray()) _items.Items.Remove(selected);
                UpdateAvailability();
            };
            selectionActions.Controls.Add(_chooseFiles);
            selectionActions.Controls.Add(_chooseFolders);
            selectionActions.Controls.Add(_remove);
            DialogStyle.AddField(fields, "本地内容", selectionActions);

            _items = new ListBox
            {
                Height = 148, IntegralHeight = false, HorizontalScrollbar = true, AllowDrop = true,
                SelectionMode = SelectionMode.MultiExtended, TabIndex = 1, AccessibleName = "待提交的本地文件和文件夹"
            };
            _items.SelectedIndexChanged += delegate { UpdateAvailability(); };
            _items.DragEnter += delegate(object sender, DragEventArgs args)
            {
                args.Effect = _chooseFiles.Enabled && args.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            _items.DragDrop += delegate(object sender, DragEventArgs args)
            {
                string[] paths = args.Data.GetData(DataFormats.FileDrop) as string[];
                if (_chooseFiles.Enabled && paths != null) AddPaths(paths);
            };
            DialogStyle.AddField(fields, String.Empty, _items);
            DialogStyle.AddHint(fields, "可多选或拖入文件和文件夹。文件夹内的所有内容会一并提交（排除 SVN 管理目录）。");

            _message = DialogStyle.TextField(String.Empty, 2);
            _message.Multiline = true;
            _message.Height = 70;
            _message.ScrollBars = ScrollBars.Vertical;
            _message.AcceptsReturn = true;
            _message.AccessibleName = "提交说明（必填）";
            _message.TextChanged += delegate { UpdateAvailability(); };
            DialogStyle.AddField(fields, "提交说明 *", _message);

            _status = DialogStyle.TextField("所选内容将在同一次提交中新增，目标目录中的同名项会阻止提交。", 3);
            _status.Multiline = true;
            _status.ReadOnly = true;
            _status.TabStop = false;
            _status.Height = 62;
            _status.ScrollBars = ScrollBars.Vertical;
            _status.AccessibleName = "新增提交状态";
            DialogStyle.AddField(fields, "操作状态", _status);
            Controls.Add(fields);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Padding = new Padding(0, 10, 0, 0)
            };
            _cancel = DialogStyle.Button("取消", false);
            _cancel.Click += delegate
            {
                if (_operation == null) { DialogResult = DialogResult.Cancel; Close(); }
                else if (!_committing) _operation.Cancel();
            };
            _submit = DialogStyle.Button("提交新增", true);
            _submit.Click += async delegate
            {
                string[] paths = _items.Items.Cast<string>().ToArray();
                string message = _message.Text;
                await RunOperationAsync(token => _service.AddItemsAsync(_repository, _path, paths, message, this, token), false);
            };
            _verify = DialogStyle.Button("核实结果", false);
            _verify.Click += async delegate
            {
                await RunOperationAsync(token => _service.VerifyAddItemsAsync(_repository, _pending, token), true);
            };
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_submit);
            buttons.Controls.Add(_verify);
            Controls.Add(buttons);
            CancelButton = _cancel;
            _cancel.DialogResult = DialogResult.None;
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (_operation == null) return;
                args.Cancel = true;
                if (_committing) return;
                _closeAfterCancel = true;
                _operation.Cancel();
            };
            RestorePending();
            UpdateAvailability();
            UiTypography.Apply(this, fontSize);
        }

        /// <summary>Adds paths to the selection, preserving previous selections.</summary>
        /// <param name="paths">Paths chosen with a picker or dropped onto the list.</param>
        /// <returns>No value.</returns>
        private void AddPaths(IEnumerable<string> paths)
        {
            HashSet<string> selected = new HashSet<string>(_items.Items.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
                if (selected.Add(path)) _items.Items.Add(path);
            UpdateAvailability();
        }

        /// <summary>Runs one submission or verification and updates the visible result.</summary>
        /// <param name="operation">Selected operation.</param>
        /// <param name="verifying">Whether this operation is verification.</param>
        /// <returns>Completion of the current operation.</returns>
        private async Task RunOperationAsync(Func<CancellationToken, Task<SubmissionResult>> operation, bool verifying)
        {
            if (_operation != null) return;
            _operation = new CancellationTokenSource();
            _status.Text = verifying ? "正在核实上次提交…" : "正在准备提交…";
            UpdateAvailability();
            try
            {
                SubmissionResult result = await operation(_operation.Token);
                _status.Text = result.Message;
                if (result.Status == SubmissionStatus.Committed) Result = result;
                else RestorePending();
            }
            catch (OperationCanceledException) { _status.Text = "操作已取消，所选内容和提交说明已保留。"; }
            catch (Exception error)
            {
                _status.Text = error.Message;
                _store.Log("add_items_dialog_failed", "repository=" + _repository.Id + " path=" + _path + " type=" + error.GetType().Name);
                RestorePending();
            }
            finally
            {
                _operation.Dispose();
                _operation = null;
                _committing = false;
                if (Result != null) DialogResult = DialogResult.OK;
                if (Result != null || _closeAfterCancel) Close();
                else UpdateAvailability();
            }
        }

        /// <summary>Restores any unresolved batch before allowing another submission for this directory.</summary>
        /// <returns>No value.</returns>
        private void RestorePending()
        {
            try
            {
                _pending = _service.GetPendingAddItems(_repository, _path);
                _pendingUnreadable = false;
                if (_pending == null) return;
                _message.Text = _pending.Message;
                _status.Text = "该目录存在待核实的新增提交。请点击“核实结果”。" + Environment.NewLine + _status.Text;
            }
            catch (Exception error)
            {
                _pendingUnreadable = true;
                _status.Text = "无法读取待核实的提交记录：" + error.Message;
                _store.Log("add_items_pending_unavailable", "repository=" + _repository.Id + " path=" + _path + " type=" + error.GetType().Name);
            }
        }

        /// <summary>Enables controls according to the selection and operation state.</summary>
        /// <returns>No value.</returns>
        private void UpdateAvailability()
        {
            bool idle = _operation == null;
            bool editable = idle && _pending == null && !_pendingUnreadable;
            _chooseFiles.Enabled = _chooseFolders.Enabled = _items.Enabled = editable;
            _remove.Enabled = editable && _items.SelectedItems.Count > EmptySelection;
            _message.ReadOnly = !editable;
            _submit.Enabled = editable && _items.Items.Count > EmptySelection && !String.IsNullOrWhiteSpace(_message.Text);
            _verify.Enabled = idle && _pending != null && !_pendingUnreadable;
            _cancel.Enabled = !_committing;
            _cancel.Text = idle ? (_pending != null || _pendingUnreadable ? "关闭" : "取消") : "取消操作";
        }

        /// <summary>Synchronously updates the UI before the service starts the irreversible commit.</summary>
        /// <param name="value">Current submission stage.</param>
        /// <returns>No value.</returns>
        void IProgress<SubmissionProgress>.Report(SubmissionProgress value)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<SubmissionProgress>(((IProgress<SubmissionProgress>)this).Report), value);
                return;
            }
            _committing = value.IsCommitting;
            _status.Text = value.Message;
            UpdateAvailability();
        }
    }
}
