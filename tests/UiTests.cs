using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LightSvnBrowser
{
    /// <summary>Runs real WinForms controls off screen; its child-process mode substitutes an offline SVN CLI.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    internal static class UiTests
    {
        private const string Payload = "offline preview content";
        private const float DefaultUiFontSize = 9F;
        private const float MaximumUiFontSize = 16F;
        private const float FractionalUiFontSize = 12.5F;
        private const float FontPointTolerance = 0.15F;
        private const string SortLargeName = "sort-A-large.pdf";
        private const string SortMediumName = "sort-B-medium.md";
        private const string SortSmallName = "sort-C-small.txt";
        private const string SortTinyName = "sort-D-tiny.txt";
        private const string SortThreeGbName = "sort-E-three-gb.txt";
        private const string SortFolderName = "sort-Z-folder";
        private static int passed;
        private static int failed;
        private static string runDirectory;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && new[] { "list", "info", "cat" }.Contains(args[0])) return FakeSvn(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Control.CheckForIllegalCrossThreadCalls = true;
            runDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-test-data"));
            Directory.CreateDirectory(runDirectory);
            using (Form host = new Form
            {
                Text = "Offline UI test host", ShowInTaskbar = false, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1)
            })
            {
                host.Shown += delegate
                {
                    host.BeginInvoke(new MethodInvoker(delegate
                    {
                        try { RunSuite(); }
                        finally { host.Close(); }
                    }));
                };
                Application.Run(host);
            }
            Console.WriteLine("UI tests: " + passed + " passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }

        private static void RunSuite()
        {
            Run("repository dialog clones and encrypts without changing config", RepositoryDialogSave);
            Run("IP certificate exception is opt-in and scoped to one repository", RepositoryIpCertificateOption);
            Run("IP certificate exception is cleared for DNS and non-HTTPS URLs", RepositoryIpCertificateUrlChanges);
            Run("favorite dialog keeps identity and normalizes tags", FavoriteDialogSave);
            Run("settings returns directory changes without moving data", SettingsDialogSave);
            Run("font settings load, return fractional values and cancel without changing configuration", SettingsFontSize);
            Run("main font changes scale all text, preserve navigation and restore without accumulation", MainFontSize);
            Run("saved font size is restored when the main window is reopened", MainFontSizeRestart);
            Run("font size endpoints render the main window and all dialogs without clipped controls", FontSizeScreenshots);
            Run("nested groups contain descendants without matching sibling prefixes", NestedGroups);
            Run("favorite aliases preserve real file names", FavoriteAlias);
            Run("favorite preview resolves latest metadata and keeps alias", FavoriteLatestPreview);
            Run("favorite search matches names, tags, notes and paths", FavoriteSearch);
            Run("filename search uses stored index and directory boundary", FilenameSearch);
            Run("repository headers sort every column and preserve selection without SVN", RepositoryColumnSorting);
            Run("directory and search sorting uses real sizes and timestamps with global row order", EntryColumnSorting);
            Run("favorite columns sort tags notes paths and retain independent schema preferences", FavoriteColumnSorting);
            Run("submission entry is scoped to one file and refresh preserves sort selection and history", SubmissionEntryAndRefresh);
            Run("submission confirmation keeps a fixed target and retains inputs after preparation failure", SubmissionDialogValidation);
            Run("submission dialog renders at font range endpoints without clipped controls", SubmissionDialogFontSizes);
            Run("history restores sorted arrows selection and originally unsorted snapshots", SortingHistory);
            Run("cancelled navigation cannot overwrite newer directory", NavigationRace);
            Run("switching to favorites cancels pending navigation", NavigationToFavorites);
            Run("data-directory shortcut keeps executable and quoted arguments", DataDirectoryShortcut);
            Run("history restores search rows, scope, selection, scroll and columns without SVN", HistorySearchRestore);
            Run("history records executed search rather than later unsubmitted input", HistoryExecutedQuery);
            Run("new navigation after going back clears forward history", HistoryBranch);
            Run("failed and cancelled navigation do not commit history", HistoryFailedAndCancelled);
            Run("history has bounded depth and disables unavailable navigation", HistoryLimit);
            Run("Alt Left and Alt Right restore repository groups", HistoryKeyboard);
        }

        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                failed++;
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                Console.WriteLine("FAIL " + name + ": " + error);
            }
        }

        private static AppStore NewStore(string name)
        {
            return new AppStore(Path.Combine(runDirectory, name + "-" + Guid.NewGuid().ToString("N")));
        }

        private static Repository AddRepository(AppStore store, string id, string name, string group)
        {
            Repository repository = new Repository { Id = id, Name = name, Group = group, Url = "https://offline.invalid/" + id, Username = String.Empty, ProtectedPassword = String.Empty };
            store.Config.Repositories.Add(repository);
            return repository;
        }

        private static MainForm OpenMain(AppStore store)
        {
            MainForm form = new MainForm(store, new SvnService(store, Application.ExecutablePath));
            OpenOffscreen(form);
            return form;
        }

        private static void OpenOffscreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
        }

        private static T Field<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        }

        private static void SetField(object instance, string name, object value)
        {
            instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
        }

        private static object Call(object instance, string name, params object[] args)
        {
            MethodInfo method = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).First(m => m.Name == name && m.GetParameters().Length == args.Length);
            return method.Invoke(instance, args);
        }

        private static void Pump(Task task)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            if (!task.IsCompleted) throw new Exception("UI operation timed out.");
            task.GetAwaiter().GetResult();
            Application.DoEvents();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void RepositoryDialogSave()
        {
            AppStore store = NewStore("repository");
            Repository original = AddRepository(store, "original", "原名称", "团队/旧组");
            original.ProtectedPassword = store.Protect("old-password");
            using (RepositoryDialog dialog = new RepositoryDialog(store, original, new[] { "团队/旧组" }))
            {
                OpenOffscreen(dialog);
                Field<TextBox>(dialog, "_name").Text = " 新名称 ";
                Field<ComboBox>(dialog, "_group").Text = " /团队\\产品// ";
                Field<TextBox>(dialog, "_url").Text = "https://offline.invalid/docs/";
                Field<TextBox>(dialog, "_username").Text = " reader ";
                Field<TextBox>(dialog, "_password").Text = "new-password";
                Call(dialog, "SaveClicked", null, EventArgs.Empty);
                Assert(dialog.DialogResult == DialogResult.OK, "Repository dialog did not save.");
                Assert(original.Name == "原名称" && original.Group == "团队/旧组", "Existing object was mutated.");
                Assert(dialog.Result.Id == original.Id && dialog.Result.Name == "新名称", "Identity or name was lost.");
                Assert(dialog.Result.Group == "团队/产品", "Group normalization failed.");
                Assert(dialog.Result.Url == "https://offline.invalid/docs", "URL trailing slash was not normalized.");
                Assert(dialog.Result.Username == "reader" && store.Unprotect(dialog.Result.ProtectedPassword) == "new-password", "Credential fields did not round-trip.");
                Assert(store.Config.Repositories[0] == original && !File.Exists(Path.Combine(store.DataDirectory, "settings.json")), "Dialog changed stored config.");
            }
        }

        private static void FavoriteDialogSave()
        {
            Favorite original = new Favorite { Id = "favorite-id", RepositoryId = "repo", Path = "设计/manual.txt", Name = "manual.txt", Tags = "old", Note = "old note", IsDirectory = false };
            using (FavoriteDialog dialog = new FavoriteDialog(original))
            {
                OpenOffscreen(dialog);
                Field<TextBox>(dialog, "_name").Text = " 常用手册 ";
                Field<TextBox>(dialog, "_tags").Text = " 常用，接口,常用;API;api ";
                Field<TextBox>(dialog, "_note").Text = " 第一行\r\n第二行 ";
                Call(dialog, "SaveClicked", null, EventArgs.Empty);
                Assert(dialog.Result != original && original.Name == "manual.txt" && original.Tags == "old", "Favorite editor mutated its input.");
                Assert(dialog.Result.Id == original.Id && dialog.Result.RepositoryId == original.RepositoryId && dialog.Result.Path == original.Path, "Favorite identity/path was lost.");
                Assert(dialog.Result.Name == "常用手册" && dialog.Result.Tags == "常用,接口,API", "Favorite name or tags are incorrect.");
                Assert(dialog.Result.Note == "第一行\r\n第二行", "Multiline notes did not round-trip.");
            }
        }

        private static void RepositoryIpCertificateOption()
        {
            AppStore store = NewStore("ip-certificate");
            Repository other = AddRepository(store, "other", "其他仓库", "团队");
            Repository saved;
            using (RepositoryDialog dialog = new RepositoryDialog(store, null, new string[0]))
            {
                OpenOffscreen(dialog);
                CheckBox option = Field<CheckBox>(dialog, "_allowIpCertificateNameMismatch");
                Assert(!option.Checked && !option.Enabled, "New repository did not default to certificate validation.");
                Field<TextBox>(dialog, "_name").Text = "IP 仓库";
                Field<TextBox>(dialog, "_url").Text = "https://192.0.2.23/svn/docs/";
                Assert(option.Enabled && !option.Checked, "IP URL should enable the option without automatically opting in.");
                option.Checked = true;
                TableLayoutPanel fields = ControlTree(dialog).OfType<TableLayoutPanel>().First();
                Assert(fields.Controls.Cast<Control>().All(c => c.Bottom <= fields.ClientSize.Height), "Repository dialog clips a field or hint.");
                using (Bitmap bitmap = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                    bitmap.Save(Path.Combine(runDirectory, "repository-ip-dialog.png"));
                }
                Call(dialog, "SaveClicked", null, EventArgs.Empty);
                saved = dialog.Result;
                Assert(saved.AllowIpCertificateNameMismatch && !other.AllowIpCertificateNameMismatch, "Certificate exception leaked to another repository.");
            }
            using (RepositoryDialog dialog = new RepositoryDialog(store, saved, new string[0]))
            {
                OpenOffscreen(dialog);
                CheckBox option = Field<CheckBox>(dialog, "_allowIpCertificateNameMismatch");
                Assert(option.Enabled && option.Checked, "Editing did not retain the saved certificate option.");
                Field<TextBox>(dialog, "_name").Text = "IP 仓库改名";
                Call(dialog, "SaveClicked", null, EventArgs.Empty);
                Assert(dialog.Result.AllowIpCertificateNameMismatch && dialog.Result != saved && saved.Name == "IP 仓库", "Editing did not clone and preserve the option.");
            }
            using (RepositoryDialog dialog = new RepositoryDialog(store, other, new string[0]))
            {
                Assert(!Field<CheckBox>(dialog, "_allowIpCertificateNameMismatch").Checked, "Unrelated repository became opted in.");
            }
        }

        private static void RepositoryIpCertificateUrlChanges()
        {
            AppStore store = NewStore("ip-url-changes");
            Repository original = AddRepository(store, "repo", "IP 仓库", "团队");
            original.Url = "https://[2001:db8::23]/svn/docs";
            original.AllowIpCertificateNameMismatch = true;
            using (RepositoryDialog dialog = new RepositoryDialog(store, original, new string[0]))
            {
                OpenOffscreen(dialog);
                CheckBox option = Field<CheckBox>(dialog, "_allowIpCertificateNameMismatch");
                TextBox url = Field<TextBox>(dialog, "_url");
                Assert(option.Enabled && option.Checked, "HTTPS IPv6 did not retain the saved option.");
                url.Text = "https://svn.example.invalid/docs";
                Assert(!option.Enabled && !option.Checked, "Switching to DNS retained the exception.");
                url.Text = "http://192.0.2.23/docs";
                Assert(!option.Enabled && !option.Checked, "HTTP incorrectly enabled a certificate exception.");
                url.Text = "https://192.0.2.23/docs";
                Assert(option.Enabled && !option.Checked, "Returning to an IP URL silently opted in again.");
                url.Text = "https://svn.example.invalid/docs";
                Call(dialog, "SaveClicked", null, EventArgs.Empty);
                Assert(!dialog.Result.AllowIpCertificateNameMismatch && original.AllowIpCertificateNameMismatch, "DNS save kept the exception or mutated the original object.");
            }
        }

        private static void SettingsDialogSave()
        {
            AppStore store = NewStore("settings");
            string target = Path.Combine(runDirectory, "not-created-" + Guid.NewGuid().ToString("N"));
            using (SettingsDialog dialog = new SettingsDialog(store))
            {
                OpenOffscreen(dialog);
                Field<TextBox>(dialog, "_dataDirectory").Text = target;
                Field<NumericUpDown>(dialog, "_cacheLimit").Value = 512;
                Field<NumericUpDown>(dialog, "_cacheDays").Value = 14;
                Call(dialog, "SaveClicked", null, EventArgs.Empty);
                Assert(dialog.RequestedDataDirectory == target && !Directory.Exists(target), "Settings moved or created the target before main-window handling.");
                Assert(dialog.CacheLimitMb == 512 && dialog.CacheDays == 14, "Settings values were lost.");
                Assert(store.Config.CacheLimitMb == 256 && store.Config.CacheDays == 7, "Settings dialog changed config directly.");
            }
            using (SettingsDialog dialog = new SettingsDialog(store))
            {
                Call(dialog, "SaveClicked", null, EventArgs.Empty);
                Assert(String.IsNullOrEmpty(dialog.RequestedDataDirectory), "Unchanged data directory was reported as changed.");
            }
        }

        private static void SettingsFontSize()
        {
            const decimal MinimumFontSize = 8M;
            const decimal MaximumFontSize = 16M;
            const decimal FontIncrement = 0.5M;
            const decimal SavedFontSize = 14.5M;
            AppStore store = NewStore("font-settings");
            store.Config.UiFontSize = FractionalUiFontSize;
            store.Save();
            string settingsPath = Path.Combine(store.DataDirectory, "settings.json");
            string unchangedSettings = File.ReadAllText(settingsPath);
            using (SettingsDialog dialog = new SettingsDialog(store))
            {
                OpenOffscreen(dialog);
                NumericUpDown size = Field<NumericUpDown>(dialog, "_uiFontSize");
                Assert(size.Value == (decimal)FractionalUiFontSize, "Settings did not load the saved fractional font size.");
                Assert(size.Minimum == MinimumFontSize && size.Maximum == MaximumFontSize && size.Increment == FontIncrement && size.DecimalPlaces == 1,
                    "Font size bounds, half-point increment or decimal display changed.");
                AssertNear(dialog.Font.SizeInPoints, FractionalUiFontSize, "Settings did not use the current application font size.");
                Button restore = ControlTree(dialog).OfType<Button>().Single(button => button.Text == "恢复默认");
                size.Value = MaximumFontSize;
                restore.PerformClick();
                Assert(size.Value == (decimal)DefaultUiFontSize, "Restore default did not reset the font-size input.");
                Assert(store.Config.UiFontSize == FractionalUiFontSize && File.ReadAllText(settingsPath) == unchangedSettings,
                    "Editing or restoring defaults changed saved settings before Save.");
                size.Value = SavedFontSize;
                Call(dialog, "SaveClicked", null, EventArgs.Empty);
                Assert(dialog.DialogResult == DialogResult.OK && dialog.UiFontSize == (float)SavedFontSize, "Saved settings did not return the selected fractional size.");
                Assert(store.Config.UiFontSize == FractionalUiFontSize && File.ReadAllText(settingsPath) == unchangedSettings,
                    "Settings dialog wrote application configuration directly.");
            }
            using (SettingsDialog dialog = new SettingsDialog(store))
            {
                OpenOffscreen(dialog);
                Field<NumericUpDown>(dialog, "_uiFontSize").Value = MaximumFontSize;
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
                Assert(store.Config.UiFontSize == FractionalUiFontSize && File.ReadAllText(settingsPath) == unchangedSettings,
                    "Cancelling the font-size edit changed memory or disk configuration.");
            }
        }

        private sealed class FontSnapshot
        {
            public float Points;
            public string Family;
            public FontStyle Style;
        }

        private static IEnumerable<Control> ControlTree(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls)
                foreach (Control descendant in ControlTree(child)) yield return descendant;
        }

        private static Dictionary<Control, FontSnapshot> SnapshotFonts(Form form)
        {
            return ControlTree(form).ToDictionary(control => control, control => new FontSnapshot
            {
                Points = control.Font.SizeInPoints,
                Family = control.Font.FontFamily.Name,
                Style = control.Font.Style
            });
        }

        private static void AssertNear(float actual, float expected, string message)
        {
            Assert(Math.Abs(actual - expected) <= FontPointTolerance,
                message + " Expected=" + expected.ToString(CultureInfo.InvariantCulture) + ", actual=" + actual.ToString(CultureInfo.InvariantCulture));
        }

        private static void AssertScaledFonts(Dictionary<Control, FontSnapshot> baseline, float ratio)
        {
            foreach (KeyValuePair<Control, FontSnapshot> item in baseline)
            {
                AssertNear(item.Key.Font.SizeInPoints, item.Value.Points * ratio, "Incorrect font scale for " + item.Key.GetType().Name + " / " + item.Key.AccessibleName);
                Assert(item.Key.Font.FontFamily.Name == item.Value.Family && item.Key.Font.Style == item.Value.Style,
                    "Font scaling changed font family/style for " + item.Key.GetType().Name);
            }
        }

        private static void MainFontSize()
        {
            AppStore store = NewStore("font-main");
            PrepareHistorySearch(store);
            using (MainForm form = OpenMain(store))
            {
                TextBox query = Field<TextBox>(form, "search");
                ListView list = Field<ListView>(form, "files");
                TreeView navigation = Field<TreeView>(form, "navigation");
                TextBox preview = Field<TextBox>(form, "preview");
                query.Text = "Target folder";
                Field<ComboBox>(form, "scope").SelectedIndex = 1;
                Pump((Task)Call(form, "SearchAsync"));
                SetField(form, "suppressSelectionPreview", true);
                list.Items[65].Selected = true;
                list.Items[65].Focused = true;
                list.TopItem = list.Items[50];
                SetField(form, "suppressSelectionPreview", false);
                Call(form, "ShowRepositories", "Temporary font history view");
                Call(form, "GoBack");
                preview.Text = "Font preview: 中文 / ASCII / 12.5 pt\r\nThe text must remain after changing the font.";
                string previewText = preview.Text;
                string[] expectedRows = VisibleRows(list);
                int selected = list.SelectedIndices[0];
                int topIndex = list.TopItem.Index;
                int backDepth = HistoryCount(form, "backHistory");
                int forwardDepth = HistoryCount(form, "forwardHistory");
                string addressText = Field<TextBox>(form, "address").Text;
                Dictionary<Control, FontSnapshot> baseline = SnapshotFonts(form);
                int initialTreeHeight = navigation.ItemHeight;
                int initialBackHeight = Field<Button>(form, "back").Height;
                Action assertView = delegate
                {
                    Assert(VisibleRows(list).SequenceEqual(expectedRows), "Font changes replaced or reordered result rows.");
                    Assert(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == selected, "Font changes lost the selected search result.");
                    Assert(list.TopItem.Index == topIndex, "Font changes lost the list scroll position.");
                    Assert(HistoryCount(form, "backHistory") == backDepth && HistoryCount(form, "forwardHistory") == forwardDepth,
                        "Font changes added, cleared or replaced navigation history.");
                    Assert(query.Text == "Target folder" && Field<ComboBox>(form, "scope").SelectedIndex == 1 && Field<TextBox>(form, "address").Text == addressText,
                        "Font changes lost the current search context.");
                    Assert(preview.Text == previewText, "Font changes reset preview contents: " + preview.Text);
                    Assert(FakeCalls(store) == 0, "Font changes unexpectedly contacted SVN.");
                };
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    Call(form, "ApplyUiFontSize", FractionalUiFontSize);
                    Application.DoEvents();
                    AssertScaledFonts(baseline, FractionalUiFontSize / DefaultUiFontSize);
                    Assert(navigation.ItemHeight >= navigation.Font.Height, "Large navigation font is clipped by its row height.");
                    Assert(list.GetItemRect(0).Height >= list.Font.Height, "Large list font is clipped by its row height.");
                    assertView();
                    Dictionary<Control, FontSnapshot> applied = SnapshotFonts(form);
                    int appliedTreeHeight = navigation.ItemHeight;
                    Call(form, "ApplyUiFontSize", FractionalUiFontSize);
                    AssertScaledFonts(applied, 1F);
                    Assert(navigation.ItemHeight == appliedTreeHeight, "Applying the same font size accumulated row scaling.");
                    Call(form, "ApplyUiFontSize", DefaultUiFontSize);
                    Application.DoEvents();
                    AssertScaledFonts(baseline, 1F);
                    Assert(navigation.ItemHeight == initialTreeHeight && Math.Abs(Field<Button>(form, "back").Height - initialBackHeight) <= 1,
                        "Returning to the default font did not restore control dimensions.");
                    assertView();
                }
                Call(form, "GoForward");
                Assert(Field<Label>(form, "pageTitle").Text == "Temporary font history view", "Font edits damaged the saved forward view.");
                Call(form, "GoBack");
                Assert(VisibleRows(list).SequenceEqual(expectedRows) && list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == selected,
                    "Navigation after font edits did not restore the prior rows and selection.");
            }
        }

        private static void MainFontSizeRestart()
        {
            AppStore store = NewStore("font-restart");
            using (MainForm form = OpenMain(store))
            {
                Call(form, "ApplyUiFontSize", FractionalUiFontSize);
                AssertNear(form.Font.SizeInPoints, FractionalUiFontSize, "Main window did not apply the selected size immediately.");
            }
            AppStore reloaded = new AppStore(store.DataDirectory);
            Assert(reloaded.Config.UiFontSize == FractionalUiFontSize, "Selected font size did not persist to application settings.");
            using (MainForm reopened = OpenMain(reloaded))
            {
                AssertNear(reopened.Font.SizeInPoints, FractionalUiFontSize, "Reopened main window did not restore the saved size.");
                AssertNear(Field<TreeView>(reopened, "navigation").Font.SizeInPoints, FractionalUiFontSize, "Reopened navigation did not restore the saved size.");
                AssertNear(Field<ListView>(reopened, "files").Font.SizeInPoints, FractionalUiFontSize, "Reopened list did not restore the saved size.");
                AssertNear(Field<TextBox>(reopened, "preview").Font.SizeInPoints, FractionalUiFontSize, "Reopened preview did not restore the saved size.");
            }
        }

        private static void FontSizeScreenshots()
        {
            string screenshotDirectory = Path.Combine(runDirectory, "font-screenshots");
            Directory.CreateDirectory(screenshotDirectory);
            List<string> layoutIssues = new List<string>();
            foreach (float size in new[] { DefaultUiFontSize, MaximumUiFontSize })
            {
                AppStore store = NewStore("font-endpoint");
                store.Config.UiFontSize = size;
                string suffix = size.ToString("0.#", CultureInfo.InvariantCulture);
                using (MainForm main = OpenMain(store))
                {
                    main.RenderDemo();
                    SaveFontScreenshot(main, size, Path.Combine(screenshotDirectory, "main-font-" + suffix + ".png"), layoutIssues);
                }
                Repository repository = new Repository { Id = "font-repository", Name = "示例文档仓库", Group = "示例资料/产品", Url = "https://192.0.2.23/svn/docs", Username = "reader" };
                using (RepositoryDialog dialog = new RepositoryDialog(store, repository, new[] { "示例资料/产品" }))
                {
                    OpenOffscreen(dialog);
                    SaveFontScreenshot(dialog, size, Path.Combine(screenshotDirectory, "repository-font-" + suffix + ".png"), layoutIssues);
                }
                Favorite favorite = new Favorite { Id = "font-favorite", RepositoryId = repository.Id, Name = "常用配置文档", Path = "设计/配置说明.md", Tags = "常用,参数", Note = "保存常用入口，便于后续核对。\r\n第二行备注用于检查大字号。" };
                using (FavoriteDialog dialog = new FavoriteDialog(favorite, size))
                {
                    OpenOffscreen(dialog);
                    SaveFontScreenshot(dialog, size, Path.Combine(screenshotDirectory, "favorite-font-" + suffix + ".png"), layoutIssues);
                }
                using (SettingsDialog dialog = new SettingsDialog(store))
                {
                    OpenOffscreen(dialog);
                    SaveFontScreenshot(dialog, size, Path.Combine(screenshotDirectory, "settings-font-" + suffix + ".png"), layoutIssues);
                }
            }
            Console.WriteLine("Font screenshots: " + screenshotDirectory);
            Assert(layoutIssues.Count == 0, String.Join(Environment.NewLine, layoutIssues));
        }

        private static void SaveFontScreenshot(Form form, float size, string path, List<string> layoutIssues)
        {
            AssertNear(form.Font.SizeInPoints, size, "Endpoint window did not use the requested font size.");
            UiTypography.Apply(form, size);
            Application.DoEvents();
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(path);
            }
            foreach (Control control in ControlTree(form))
            {
                if (!control.Visible || control.Parent == null || control.Parent is NumericUpDown) continue;
                const int EdgeTolerance = 3;
                ScrollableControl scrollParent = control.Parent as ScrollableControl;
                bool scrollCanvas = scrollParent != null && scrollParent.AutoScroll;
                if (!scrollCanvas && (control.Right > control.Parent.ClientSize.Width + EdgeTolerance || control.Bottom > control.Parent.ClientSize.Height + EdgeTolerance))
                    layoutIssues.Add("Control extends beyond its parent in " + Path.GetFileName(path) + ": " + control.GetType().Name + " " + control.Bounds
                        + " / parent=" + control.Parent.GetType().Name + " " + control.Parent.ClientSize + " / " + control.Text);
                Button button = control as Button;
                if (button == null) continue;
                Size textSize = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding);
                if (button.ClientSize.Height < textSize.Height || button.ClientSize.Width < textSize.Width)
                    layoutIssues.Add("Button text is clipped in " + Path.GetFileName(path) + ": " + button.Text);
            }
        }

        private static void NestedGroups()
        {
            AppStore store = NewStore("groups");
            AddRepository(store, "one", "一级", "团队/产品");
            AddRepository(store, "two", "二级", "团队/产品/归档");
            AddRepository(store, "three", "相邻", "团队/产品扩展");
            AddRepository(store, "four", "未分组", String.Empty);
            using (MainForm form = OpenMain(store))
            {
                TreeView tree = Field<TreeView>(form, "navigation");
                TreeNode root = tree.Nodes[1];
                TreeNode team = root.Nodes.Cast<TreeNode>().Single(n => n.Text == "团队");
                TreeNode product = team.Nodes.Cast<TreeNode>().Single(n => n.Text == "产品");
                Assert(product.Nodes.Cast<TreeNode>().Any(n => n.Text == "归档"), "Nested group node was not created.");
                Call(form, "ShowRepositories", "团队/产品");
                ListView list = Field<ListView>(form, "files");
                Assert(list.Items.Count == 2 && !list.Items.Cast<ListViewItem>().Any(i => i.Text == "相邻"), "Group filtering matched a sibling prefix.");
                Call(form, "ShowRepositories", new object[] { null });
                Assert(list.Items.Count == 4, "Root group does not show every repository.");
            }
        }

        private static Favorite AddFavorite(AppStore store, Repository repository)
        {
            Favorite favorite = new Favorite { Id = "favorite", RepositoryId = repository.Id, Path = "设计/manual.txt", Name = "常用手册", Tags = "回访,配置", Note = "发布前核对参数", IsDirectory = false };
            store.Config.Favorites.Add(favorite);
            return favorite;
        }

        private static void FavoriteAlias()
        {
            AppStore store = NewStore("favorite-alias");
            Repository repository = AddRepository(store, "repo", "文档库", "团队");
            Favorite saved = AddFavorite(store, repository);
            using (MainForm form = OpenMain(store))
            {
                Call(form, "ShowFavorites", new object[] { null });
                ListView list = Field<ListView>(form, "files");
                Assert(list.Items.Count == 1 && list.Items[0].Text == saved.Name, "Favorite alias is not shown.");
                SvnEntry entry = (SvnEntry)list.Items[0].Tag.GetType().GetField("Entry").GetValue(list.Items[0].Tag);
                Assert(entry.Name == "manual.txt" && entry.Path == saved.Path, "Editable favorite alias replaced the real filename/extension.");
            }
        }

        private static void FavoriteSearch()
        {
            AppStore store = NewStore("favorite-search");
            Repository repository = AddRepository(store, "repo", "文档库", "团队");
            AddFavorite(store, repository);
            store.Config.Favorites.Add(new Favorite { Id = "orphan", RepositoryId = "missing", Path = "manual.txt", Name = "不应出现" });
            using (MainForm form = OpenMain(store))
            {
                ListView list = Field<ListView>(form, "files");
                foreach (string term in new[] { "常用", "回访", "核对", "设计/", "MANUAL.TXT" })
                {
                    Call(form, "ShowFavorites", term);
                    Assert(list.Items.Count == 1, "Favorite query did not match: " + term);
                }
                Field<TextBox>(form, "search").Text = "核对";
                Pump((Task)Call(form, "SearchAsync"));
                Assert(list.Items.Count == 1, "Top search did not search favorite notes.");
                Field<TextBox>(form, "search").Text = "不存在";
                Pump((Task)Call(form, "SearchAsync"));
                Assert(list.Items.Count == 0, "Favorite query retained unrelated rows.");
            }
        }

        private static void FavoriteLatestPreview()
        {
            AppStore store = NewStore("favorite-preview");
            Repository repository = AddRepository(store, "repo", "文档库", "团队");
            Favorite saved = AddFavorite(store, repository);
            using (MainForm form = OpenMain(store))
            {
                Call(form, "ShowFavorites", new object[] { null });
                ListView list = Field<ListView>(form, "files");
                SetField(form, "suppressSelectionPreview", true);
                list.Items[0].Selected = true;
                SetField(form, "suppressSelectionPreview", false);
                Pump((Task)Call(form, "ShowSelectionAsync"));
                SvnEntry entry = (SvnEntry)list.Items[0].Tag.GetType().GetField("Entry").GetValue(list.Items[0].Tag);
                Assert(entry.Revision == "7" && entry.Size == Encoding.UTF8.GetByteCount(Payload), "Favorite still uses HEAD or unknown size.");
                Assert(Field<TextBox>(form, "preview").Text == Payload, "Favorite did not preview real file bytes.");
                Assert(Field<Label>(form, "detailTitle").Text == saved.Name, "Resolving metadata discarded the display alias.");
                Assert(Directory.GetFiles(store.CacheDirectory, "*.txt").Length == 1, "Favorite cache did not retain the real file extension.");
            }
        }

        private static void FilenameSearch()
        {
            AppStore store = NewStore("index-search");
            Repository repository = AddRepository(store, "repo", "文档库", "团队");
            store.SaveIndex(new IndexSnapshot
            {
                RepositoryId = repository.Id, Revision = "7", UpdatedUtc = DateTime.UtcNow,
                Entries = new List<SvnEntry>
                {
                    new SvnEntry { Name = "接口说明.txt", Path = "设计/接口说明.txt", Revision = "7", Size = 10, ModifiedUtc = DateTime.UtcNow },
                    new SvnEntry { Name = "接口说明.txt", Path = "设计扩展/接口说明.txt", Revision = "7", Size = 10, ModifiedUtc = DateTime.UtcNow },
                    new SvnEntry { Name = "接口清单.txt", Path = "设计/接口清单.txt", Revision = "7", Size = 10, ModifiedUtc = DateTime.UtcNow }
                }
            });
            using (MainForm form = OpenMain(store))
            {
                SetField(form, "currentRepository", repository);
                SetField(form, "currentPath", "设计");
                Field<ComboBox>(form, "scope").SelectedIndex = 2;
                Field<TextBox>(form, "search").Text = "接口 说明";
                Pump((Task)Call(form, "SearchAsync"));
                ListView list = Field<ListView>(form, "files");
                Assert(list.Items.Count == 1 && list.Items[0].SubItems[4].Text.EndsWith("/设计/接口说明.txt"), "Search did not combine terms or respect directory boundary.");
            }
        }

        private static void AddSortRepositories(AppStore store)
        {
            Repository third = AddRepository(store, "sort-c", "Charlie", "Alpha group");
            third.Url = "https://offline.invalid/b"; third.Username = "alpha";
            Repository first = AddRepository(store, "sort-a", "Alpha", "Charlie group");
            first.Url = "https://offline.invalid/c"; first.Username = "middle";
            Repository second = AddRepository(store, "sort-b", "Bravo", "Bravo group");
            second.Url = "https://offline.invalid/a"; second.Username = "zulu";
        }

        private static void AddSortFavorites(AppStore store, Repository repository)
        {
            store.Config.Favorites.Add(new Favorite { Id = "sort-fav-a", RepositoryId = repository.Id, Name = "Alpha reference", Path = "z/real-a.pdf", Tags = "zulu", Note = "bravo" });
            store.Config.Favorites.Add(new Favorite { Id = "sort-fav-b", RepositoryId = repository.Id, Name = "Bravo reference", Path = "a/real-b.txt", Tags = "alpha", Note = "charlie" });
            store.Config.Favorites.Add(new Favorite { Id = "sort-fav-c", RepositoryId = repository.Id, Name = "Zulu directory", Path = "m/real-c", Tags = "middle", Note = "alpha", IsDirectory = true });
        }

        private static List<SvnEntry> SortEntries()
        {
            const long Kibibyte = 1024L;
            const long Mebibyte = Kibibyte * Kibibyte;
            const long Gibibyte = Mebibyte * Kibibyte;
            return new List<SvnEntry>
            {
                new SvnEntry { Name = SortLargeName, Path = "z/" + SortLargeName, Size = 10L * Gibibyte, Revision = "7", ModifiedUtc = new DateTime(2026, 11, 2, 12, 30, 45, DateTimeKind.Utc) },
                new SvnEntry { Name = SortMediumName, Path = "a/" + SortMediumName, Size = 900L * Mebibyte, Revision = "7", ModifiedUtc = new DateTime(2026, 11, 2, 12, 30, 5, DateTimeKind.Utc) },
                new SvnEntry { Name = SortSmallName, Path = "y/" + SortSmallName, Size = 2L * Kibibyte, Revision = "7", ModifiedUtc = new DateTime(2025, 1, 2, 1, 1, 0, DateTimeKind.Utc) },
                new SvnEntry { Name = SortTinyName, Path = "b/" + SortTinyName, Size = 1000L, Revision = "7", ModifiedUtc = new DateTime(2024, 1, 2, 1, 1, 0, DateTimeKind.Utc) },
                new SvnEntry { Name = SortThreeGbName, Path = "x/" + SortThreeGbName, Size = 3L * Gibibyte, Revision = "7", ModifiedUtc = new DateTime(2027, 1, 2, 1, 1, 0, DateTimeKind.Utc) },
                new SvnEntry { Name = SortFolderName, Path = "c/" + SortFolderName, IsDirectory = true, Revision = "7", ModifiedUtc = new DateTime(2023, 1, 2, 1, 1, 0, DateTimeKind.Utc) }
            };
        }

        private static string[] ColumnTitles(ListView list)
        {
            return list.Columns.Cast<ColumnHeader>().Select(column => column.Text).ToArray();
        }

        private static void AssertSortState(MainForm form, string[] titles, int column, bool descending)
        {
            ListView list = Field<ListView>(form, "files");
            Assert(Field<int>(form, "sortColumn") == column && Field<bool>(form, "sortDescending") == descending, "Sort direction/column state is incorrect.");
            for (int index = 0; index < titles.Length; index++)
            {
                string expected = titles[index] + (index == column ? (descending ? " ▼" : " ▲") : String.Empty);
                Assert(list.Columns[index].Text == expected, "Sort arrow was lost or appeared on another column: " + index);
            }
        }

        private static void SelectSortRow(MainForm form, ListViewItem selected)
        {
            bool previous = Field<bool>(form, "suppressSelectionPreview");
            SetField(form, "suppressSelectionPreview", true);
            try
            {
                foreach (ListViewItem row in Field<ListView>(form, "files").Items) row.Selected = false;
                selected.Selected = true;
                selected.Focused = true;
            }
            finally { SetField(form, "suppressSelectionPreview", previous); }
        }

        private static void AssertSortSelection(MainForm form, object tag, string previewText, int calls, AppStore store)
        {
            ListView list = Field<ListView>(form, "files");
            Assert(list.SelectedItems.Count == 1 && Object.ReferenceEquals(list.SelectedItems[0].Tag, tag), "Sorting lost or replaced the selected row identity.");
            Assert(Field<TextBox>(form, "preview").Text == previewText, "Sorting triggered or reset the preview.");
            Assert(FakeCalls(store) == calls, "Sorting issued an SVN request.");
        }

        private static void AssertRepositoryOrder(ListView list, params string[] ids)
        {
            Assert(list.Items.Cast<ListViewItem>().Select(row => ((Repository)row.Tag).Id).SequenceEqual(ids), "Repository column order is incorrect.");
        }

        private static void AssertEntryOrder(ListView list, params string[] names)
        {
            Assert(list.Items.Cast<ListViewItem>().Select(row => ((SvnEntry)row.Tag.GetType().GetField("Entry").GetValue(row.Tag)).Name).SequenceEqual(names), "File/folder column order is incorrect.");
        }

        private static string FavoriteRowId(ListViewItem row)
        {
            return ((Favorite)row.Tag.GetType().GetField("Favorite").GetValue(row.Tag)).Id;
        }

        private static void AssertFavoriteOrder(ListView list, params string[] ids)
        {
            Assert(list.Items.Cast<ListViewItem>().Select(FavoriteRowId).SequenceEqual(ids), "Favorite column order is incorrect.");
        }

        private static void AssertTextColumnOrder(ListView list, int column, bool descending)
        {
            string[] actual = list.Items.Cast<ListViewItem>().Select(row => row.SubItems[column].Text).ToArray();
            IEnumerable<string> ordered = actual.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase);
            if (descending) ordered = ordered.Reverse();
            Assert(actual.SequenceEqual(ordered), "Text/type column is not sorted in the selected direction.");
        }

        private static void RepositoryColumnSorting()
        {
            AppStore store = NewStore("sort-repositories");
            AddSortRepositories(store);
            using (MainForm form = OpenMain(store))
            {
                ListView list = Field<ListView>(form, "files");
                string[] titles = ColumnTitles(list);
                SelectSortRow(form, list.Items.Cast<ListViewItem>().Single(row => ((Repository)row.Tag).Id == "sort-b"));
                object selected = list.SelectedItems[0].Tag;
                string previewText = Field<TextBox>(form, "preview").Text;
                string[][] expected = { new[] { "sort-a", "sort-b", "sort-c" }, new[] { "sort-c", "sort-b", "sort-a" }, new[] { "sort-b", "sort-c", "sort-a" }, new[] { "sort-c", "sort-a", "sort-b" } };
                for (int column = 0; column < titles.Length; column++)
                {
                    if (column == 0)
                        typeof(ListView).GetMethod("OnColumnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, new object[] { new ColumnClickEventArgs(column) });
                    else Call(form, "SortByColumn", column);
                    AssertRepositoryOrder(list, expected[column]);
                    AssertSortState(form, titles, column, false);
                    AssertSortSelection(form, selected, previewText, 0, store);
                    Call(form, "SortByColumn", column);
                    AssertRepositoryOrder(list, expected[column].Reverse().ToArray());
                    AssertSortState(form, titles, column, true);
                    AssertSortSelection(form, selected, previewText, 0, store);
                }
            }
        }

        private static void EntryColumnSorting()
        {
            AppStore store = NewStore("sort-entries");
            Repository repository = AddRepository(store, "sort-files", "Sort fixture", "Examples");
            store.SaveIndex(new IndexSnapshot { RepositoryId = repository.Id, Revision = "7", UpdatedUtc = DateTime.UtcNow, Entries = SortEntries() });
            using (MainForm form = OpenMain(store))
            {
                Pump((Task)Call(form, "NavigateAsync", repository, "sort-fixture"));
                ListView list = Field<ListView>(form, "files");
                Assert(list.Items.Count == SortEntries().Count, "Offline directory sort fixture did not load.");
                string[] titles = ColumnTitles(list);
                ListViewItem small = list.Items.Cast<ListViewItem>().Single(row => row.Text == SortSmallName);
                SelectSortRow(form, small);
                object selected = small.Tag;
                Field<TextBox>(form, "preview").Text = "Keep the existing sorting preview.";
                string previewText = Field<TextBox>(form, "preview").Text;
                int calls = FakeCalls(store);
                Call(form, "SortByColumn", 0);
                AssertEntryOrder(list, SortLargeName, SortMediumName, SortSmallName, SortTinyName, SortThreeGbName, SortFolderName);
                Call(form, "SortByColumn", 0);
                AssertEntryOrder(list, SortFolderName, SortThreeGbName, SortTinyName, SortSmallName, SortMediumName, SortLargeName);
                Call(form, "SortByColumn", 2);
                AssertEntryOrder(list, SortFolderName, SortTinyName, SortSmallName, SortMediumName, SortThreeGbName, SortLargeName);
                AssertSortState(form, titles, 2, false);
                Call(form, "SortByColumn", 2);
                AssertEntryOrder(list, SortLargeName, SortThreeGbName, SortMediumName, SortSmallName, SortTinyName, SortFolderName);
                Call(form, "SortByColumn", 3);
                AssertEntryOrder(list, SortFolderName, SortTinyName, SortSmallName, SortMediumName, SortLargeName, SortThreeGbName);
                Assert(list.Items.Cast<ListViewItem>().Single(row => row.Text == SortLargeName).SubItems[3].Text == list.Items.Cast<ListViewItem>().Single(row => row.Text == SortMediumName).SubItems[3].Text,
                    "Time fixture must have identical displayed minutes but different source seconds.");
                Call(form, "SortByColumn", 3);
                AssertEntryOrder(list, SortThreeGbName, SortLargeName, SortMediumName, SortSmallName, SortTinyName, SortFolderName);
                AssertSortSelection(form, selected, previewText, calls, store);
                Call(form, "SortByColumn", 1); AssertTextColumnOrder(list, 1, false);
                Call(form, "SortByColumn", 1); AssertTextColumnOrder(list, 1, true);
                Call(form, "SortByColumn", 3); Call(form, "SortByColumn", 3);
                Field<ComboBox>(form, "scope").SelectedIndex = 1;
                Field<TextBox>(form, "search").Text = "sort-";
                Pump((Task)Call(form, "SearchAsync"));
                AssertEntryOrder(list, SortThreeGbName, SortLargeName, SortMediumName, SortSmallName, SortTinyName, SortFolderName);
                AssertSortState(form, titles, 3, true);
                Assert(FakeCalls(store) == calls, "Indexed search or remembered ordering unexpectedly contacted SVN.");
                Call(form, "SortByColumn", 4);
                AssertEntryOrder(list, SortMediumName, SortTinyName, SortFolderName, SortThreeGbName, SortSmallName, SortLargeName);
                Call(form, "SortByColumn", 4);
                AssertEntryOrder(list, SortLargeName, SortSmallName, SortThreeGbName, SortFolderName, SortTinyName, SortMediumName);
                AssertSortState(form, titles, 4, true);
                Assert(FakeCalls(store) == calls, "Path sorting issued an SVN request.");
            }
        }

        private static void FavoriteColumnSorting()
        {
            AppStore store = NewStore("sort-favorites");
            AddSortRepositories(store);
            AddSortFavorites(store, store.Config.Repositories[0]);
            using (MainForm form = OpenMain(store))
            {
                string[] repositoryTitles = ColumnTitles(Field<ListView>(form, "files"));
                Call(form, "SortByColumn", 1);
                Call(form, "ShowFavorites", new object[] { null });
                ListView list = Field<ListView>(form, "files");
                string[] titles = ColumnTitles(list);
                SelectSortRow(form, list.Items.Cast<ListViewItem>().Single(row => FavoriteRowId(row) == "sort-fav-b"));
                object selected = list.SelectedItems[0].Tag;
                string previewText = Field<TextBox>(form, "preview").Text;
                int[] columns = { 0, 2, 3, 4 };
                string[][] orders = { new[] { "sort-fav-a", "sort-fav-b", "sort-fav-c" }, new[] { "sort-fav-b", "sort-fav-c", "sort-fav-a" }, new[] { "sort-fav-c", "sort-fav-a", "sort-fav-b" }, new[] { "sort-fav-b", "sort-fav-c", "sort-fav-a" } };
                for (int index = 0; index < columns.Length; index++)
                {
                    Call(form, "SortByColumn", columns[index]);
                    AssertFavoriteOrder(list, orders[index]);
                    AssertSortState(form, titles, columns[index], false);
                    Call(form, "SortByColumn", columns[index]);
                    AssertFavoriteOrder(list, orders[index].Reverse().ToArray());
                    AssertSortState(form, titles, columns[index], true);
                    AssertSortSelection(form, selected, previewText, 0, store);
                }
                Call(form, "SortByColumn", 1); AssertTextColumnOrder(list, 1, false);
                Call(form, "SortByColumn", 1); AssertTextColumnOrder(list, 1, true);
                string[] favoriteOrder = VisibleRows(list);
                Call(form, "ShowRepositories", new object[] { null });
                AssertSortState(form, repositoryTitles, 1, false);
                AssertRepositoryOrder(list, "sort-c", "sort-b", "sort-a");
                Call(form, "ShowFavorites", new object[] { null });
                AssertSortState(form, titles, 1, true);
                Assert(VisibleRows(list).SequenceEqual(favoriteOrder) && FakeCalls(store) == 0, "Changing schemas lost favorite ordering or contacted SVN.");
            }
        }

        private static void SortingHistory()
        {
            AppStore store = NewStore("sort-history");
            Repository repository = AddRepository(store, "sort-history", "Sort history", "Examples");
            AddSortFavorites(store, repository);
            store.SaveIndex(new IndexSnapshot { RepositoryId = repository.Id, Revision = "7", UpdatedUtc = DateTime.UtcNow, Entries = SortEntries() });
            using (MainForm form = OpenMain(store))
            {
                Field<ComboBox>(form, "scope").SelectedIndex = 1;
                Field<TextBox>(form, "search").Text = "sort-";
                Pump((Task)Call(form, "SearchAsync"));
                ListView list = Field<ListView>(form, "files");
                string[] searchTitles = ColumnTitles(list);
                Call(form, "SortByColumn", 3);
                SelectSortRow(form, list.Items.Cast<ListViewItem>().Single(row => row.Text == SortSmallName));
                string[] searchRows = VisibleRows(list);
                Call(form, "ShowFavorites", new object[] { null });
                string[] favoriteTitles = ColumnTitles(list);
                Call(form, "SortByColumn", 2); Call(form, "SortByColumn", 2);
                SelectSortRow(form, list.Items.Cast<ListViewItem>().Single(row => FavoriteRowId(row) == "sort-fav-b"));
                string[] favoriteRows = VisibleRows(list);
                Call(form, "GoBack");
                AssertSortState(form, searchTitles, 3, false);
                Assert(VisibleRows(list).SequenceEqual(searchRows) && list.SelectedItems.Count == 1 && list.SelectedItems[0].Text == SortSmallName,
                    "Back lost sorted search rows or selected identity.");
                Call(form, "GoForward");
                AssertSortState(form, favoriteTitles, 2, true);
                Assert(VisibleRows(list).SequenceEqual(favoriteRows) && list.SelectedItems.Count == 1 && FavoriteRowId(list.SelectedItems[0]) == "sort-fav-b",
                    "Forward lost sorted favorite rows or selected identity.");
                Assert(FakeCalls(store) == 0, "Sorting/history restoration issued an SVN request.");
            }
            AppStore unsortedStore = NewStore("sort-history-unsorted");
            AddSortRepositories(unsortedStore);
            AddSortFavorites(unsortedStore, unsortedStore.Config.Repositories[0]);
            using (MainForm form = OpenMain(unsortedStore))
            {
                ListView list = Field<ListView>(form, "files");
                string[] originalTitles = ColumnTitles(list);
                string[] originalRows = VisibleRows(list);
                SelectSortRow(form, list.Items.Cast<ListViewItem>().Single(row => ((Repository)row.Tag).Id == "sort-b"));
                Call(form, "ShowFavorites", new object[] { null });
                Call(form, "ShowRepositories", new object[] { null });
                Call(form, "SortByColumn", 0);
                Call(form, "GoBack"); Call(form, "GoBack");
                AssertSortState(form, originalTitles, -1, false);
                Assert(VisibleRows(list).SequenceEqual(originalRows) && ((Repository)list.SelectedItems[0].Tag).Id == "sort-b",
                    "A later remembered sort corrupted the originally unsorted history snapshot.");
                Call(form, "GoForward"); Call(form, "GoForward");
                AssertSortState(form, originalTitles, 0, false);
                AssertRepositoryOrder(list, "sort-a", "sort-b", "sort-c");
                Assert(FakeCalls(unsortedStore) == 0, "Unsorted history restoration issued an SVN request.");
            }
        }

        private static void NavigationRace()
        {
            AppStore store = NewStore("navigation-race");
            Repository repository = AddRepository(store, "repo", "文档库", "团队");
            using (MainForm form = OpenMain(store))
            {
                Task slow = (Task)Call(form, "NavigateAsync", repository, "slow");
                Task fast = (Task)Call(form, "NavigateAsync", repository, "fast");
                Pump(Task.WhenAll(slow, fast));
                ListView list = Field<ListView>(form, "files");
                Assert(list.Items.Count == 1 && list.Items[0].Text == "fast-result.txt", "Old navigation replaced the fast result.");
                Assert(Field<string>(form, "currentPath") == "fast", "Current directory was overwritten.");
                Assert(!Field<ProgressBar>(form, "progress").Visible, "Navigation left the busy indicator active.");
            }
        }

        private static void NavigationToFavorites()
        {
            AppStore store = NewStore("navigation-favorites");
            Repository repository = AddRepository(store, "repo", "文档库", "团队");
            AddFavorite(store, repository);
            using (MainForm form = OpenMain(store))
            {
                Task slow = (Task)Call(form, "NavigateAsync", repository, "slow");
                Call(form, "ShowFavorites", new object[] { null });
                Pump(slow);
                Assert(Field<bool>(form, "showingFavorites") && Field<Label>(form, "pageTitle").Text == "我的收藏", "Cancelled navigation replaced the favorites view.");
                Assert(Field<ListView>(form, "files").Items.Count == 1, "Favorites rows were lost.");
            }
        }

        private static int FakeSvn(string[] args)
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "fake-svn-call-" + Guid.NewGuid().ToString("N") + ".txt"), args[0]);
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args[0] == "cat") { Console.Write(Payload); return 0; }
            const string infoOperation = "info";
            const string submissionName = "manual.txt";
            if (args[0] == infoOperation && args[args.Length - 1].IndexOf(submissionName, StringComparison.Ordinal) >= 0)
            {
                Console.Write("<?xml version=\"1.0\"?><info><entry kind=\"file\" revision=\"7\" path=\"manual.txt\"><commit revision=\"7\"><author>offline</author><date>2026-11-02T02:30:00Z</date></commit></entry></info>");
                return 0;
            }
            if (args[0] == infoOperation) { Console.Write("<?xml version=\"1.0\"?><info><entry kind=\"dir\" revision=\"7\" path=\"docs\" /></info>"); return 0; }
            string url = args[args.Length - 1];
            if (url.IndexOf("/sort-fixture", StringComparison.Ordinal) >= 0)
            {
                Console.Write("<?xml version=\"1.0\"?><lists><list path=\"offline\">");
                foreach (SvnEntry entry in SortEntries())
                    Console.Write("<entry kind=\"" + (entry.IsDirectory ? "dir" : "file") + "\"><name>" + entry.Name + "</name><size>" + entry.Size.ToString(CultureInfo.InvariantCulture)
                        + "</size><commit revision=\"7\"><author>offline</author><date>" + entry.ModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "</date></commit></entry>");
                Console.Write("</list></lists>");
                return 0;
            }
            if (url.IndexOf("/failed", StringComparison.Ordinal) >= 0) { Console.Error.Write("svn: E170013: Offline fixture rejects this path."); return 1; }
            bool slow = url.IndexOf("/slow", StringComparison.Ordinal) >= 0;
            if (slow) Thread.Sleep(700);
            string name = slow ? "slow-result.txt" : url.IndexOf("/fast", StringComparison.Ordinal) >= 0 ? "fast-result.txt" : "manual.txt";
            Console.Write("<?xml version=\"1.0\"?><lists><list path=\"offline\"><entry kind=\"file\"><name>" + name + "</name><size>" + Encoding.UTF8.GetByteCount(Payload) + "</size><commit revision=\"7\"><author>offline</author><date>2026-11-02T02:30:00Z</date></commit></entry></list></lists>");
            return 0;
        }

        /// <summary>Checks file-only entry availability and post-commit metadata in sorted cached views.</summary>
        /// <returns>None.</returns>
        private static void SubmissionEntryAndRefresh()
        {
            AppStore store = NewStore("submission-entry");
            Repository repository = AddRepository(store, "submission-ui", "Submission fixture", "Examples");
            using (MainForm form = OpenMain(store))
            {
                Assert(!Field<Button>(form, "submitFile").Enabled && !Field<ToolStripMenuItem>(form, "submitFileMenuItem").Enabled,
                    "Submission was enabled without a selected file.");
                Pump((Task)Call(form, "NavigateAsync", repository, "sort-fixture"));
                ListView list = Field<ListView>(form, "files");
                SelectSortRow(form, list.Items.Cast<ListViewItem>().Single(row => row.Text == SortFolderName));
                Call(form, "UpdateActions");
                Assert(!Field<Button>(form, "submitFile").Enabled, "Submission was enabled for a directory.");
                ListViewItem selected = list.Items.Cast<ListViewItem>().Single(row => row.Text == SortSmallName);
                SelectSortRow(form, selected);
                Call(form, "UpdateActions");
                Assert(Field<Button>(form, "submitFile").Enabled && Field<ToolStripMenuItem>(form, "submitFileMenuItem").Enabled,
                    "A selected file did not enable both submission entry points.");
                using (CancellationTokenSource busy = new CancellationTokenSource())
                {
                    SetField(form, "operation", busy);
                    Call(form, "UpdateActions");
                    Assert(!Field<Button>(form, "submitFile").Enabled && !Field<ToolStripMenuItem>(form, "submitFileMenuItem").Enabled,
                        "Submission entry stayed available during another operation.");
                    SetField(form, "operation", null);
                }
                SetField(form, "demoMode", true);
                Call(form, "UpdateActions");
                Assert(!Field<Button>(form, "submitFile").Enabled, "Demo fixtures exposed a writable SVN action.");
                SetField(form, "demoMode", false);
                const int sizeColumn = 2;
                Call(form, "SortByColumn", sizeColumn);
                int calls = FakeCalls(store);
                const long updatedSize = 11L * 1024L * 1024L * 1024L;
                SvnEntry updated = new SvnEntry { Name = SortSmallName, Path = "sort-fixture/" + SortSmallName,
                    Revision = "8", Size = updatedSize, Author = "fixture", ModifiedUtc = DateTime.UtcNow };
                Call(form, "RefreshSubmittedFile", repository, updated.Path, updated);
                Assert(Object.ReferenceEquals(list.SelectedItems[0], selected) && list.Items[list.Items.Count - 1].Text == SortSmallName,
                    "Post-submit refresh lost selection or failed to restore the active size ordering.");
                object selectedTag = selected.Tag;
                Assert(((SvnEntry)selectedTag.GetType().GetField("Entry").GetValue(selectedTag)).Revision == updated.Revision,
                    "Post-submit row retained the old file revision.");
                Call(form, "ShowRepositories", "temporary submission history view");
                Call(form, "GoBack");
                Assert(list.Items[list.Items.Count - 1].Text == SortSmallName && list.SelectedItems[0].Text == SortSmallName,
                    "History restored stale post-submit metadata or lost the selected row.");
                Assert(FakeCalls(store) == calls, "Refreshing submitted metadata or history contacted SVN unnecessarily.");
            }
        }

        /// <summary>Pumps pending form work without starting a duplicate server read.</summary>
        /// <param name="dialog">The confirmation window under test.</param>
        /// <returns>None.</returns>
        private static void WaitSubmissionIdle(SubmitFileDialog dialog)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (Field<bool>(dialog, "_busy") && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(!Field<bool>(dialog, "_busy"), "Submission dialog initialization timed out.");
            Application.DoEvents();
        }

        /// <summary>Checks explicit content confirmation and input retention after a real local file access failure.</summary>
        /// <returns>None.</returns>
        private static void SubmissionDialogValidation()
        {
            AppStore store = NewStore("submission-dialog");
            Repository repository = AddRepository(store, "submission-confirm", "Confirmation fixture", "Examples");
            SvnService service = new SvnService(store, Application.ExecutablePath);
            const string relativePath = "设计/manual.txt";
            using (SubmitFileDialog dialog = new SubmitFileDialog(store, service, repository, relativePath, DefaultUiFontSize))
            {
                Assert(FakeCalls(store) == 0 && !Field<Button>(dialog, "_submit").Enabled, "Constructing the dialog contacted SVN or enabled premature submission.");
                OpenOffscreen(dialog);
                WaitSubmissionIdle(dialog);
                Assert(Field<SvnEntry>(dialog, "_latest") != null, "Confirmation did not obtain fresh server metadata.");
                TextBox target = Field<TextBox>(dialog, "_targetPath");
                Assert(target.ReadOnly && target.Text == relativePath + Environment.NewLine + SvnService.BuildUrl(repository, relativePath), "Target path is editable or does not show both the readable name and exact SVN URL.");
                string localFile = Path.Combine(store.DataDirectory, "different-local-name.txt");
                File.WriteAllText(localFile, "edited local content", new UTF8Encoding(false));
                Call(dialog, "SetLocalFile", localFile);
                Field<TextBox>(dialog, "_message").Text = "describe the document update";
                CheckBox acknowledgement = Field<CheckBox>(dialog, "_acknowledge");
                Assert(acknowledgement.Enabled && !Field<Button>(dialog, "_submit").Enabled, "Unknown local provenance did not require explicit confirmation.");
                Assert(Field<Label>(dialog, "_nameNotice").Text.Contains("manual.txt") && Field<Label>(dialog, "_nameNotice").Text.Contains(Path.GetFileName(localFile)),
                    "Different local basename did not explain the fixed remote target.");
                acknowledgement.Checked = true;
                Assert(Field<Button>(dialog, "_submit").Enabled, "A valid reviewed file and message did not enable submission.");
                Field<TextBox>(dialog, "_message").Text = "   ";
                Assert(!Field<Button>(dialog, "_submit").Enabled, "Whitespace-only commit message was accepted.");
                const string message = "keep this message after failure";
                Field<TextBox>(dialog, "_message").Text = message;
                using (FileStream locked = new FileStream(localFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Pump((Task)Call(dialog, "SubmitAsync"));
                Assert(Field<TextBox>(dialog, "_localFile").Text == localFile && Field<TextBox>(dialog, "_message").Text == message,
                    "Preparation failure discarded local file selection or commit message.");
                Assert(dialog.DialogResult != DialogResult.OK && !Field<Button>(dialog, "_submit").Enabled,
                    "Preparation failure reported success or allowed submission without refreshing the baseline.");
                Assert(service.GetPendingSubmission(repository, relativePath) == null, "Locked local source left a falsely unresolved server commit.");
                int callsAfterFailure = FakeCalls(store);
                Application.DoEvents();
                Assert(FakeCalls(store) == callsAfterFailure, "Preparation failure scheduled an automatic retry.");
                SubmissionAttempt pending = new SubmissionAttempt { Id = "offline-pending-attempt", Path = relativePath, BaseRevision = "7", LocalFile = localFile, Message = message };
                Call(dialog, "HandleResult", new SubmissionResult { Status = SubmissionStatus.Unknown, Attempt = pending, Message = "offline response lost" });
                Call(dialog, "UpdateAvailability");
                Assert(!Field<Button>(dialog, "_submit").Enabled && Field<Button>(dialog, "_verify").Enabled,
                    "Unverified commit did not block repeated submission and expose manual verification.");
                Assert(Field<TextBox>(dialog, "_localFile").Text == localFile && Field<TextBox>(dialog, "_message").Text == message
                    && Field<TextBox>(dialog, "_message").ReadOnly && !Field<Button>(dialog, "_chooseLocal").Enabled,
                    "Unverified commit lost its inputs or allowed them to be replaced before verification.");
                Assert(FakeCalls(store) == callsAfterFailure, "Unverified commit automatically retried or verified without a user action.");
            }
        }

        /// <summary>Renders confirmation at the supported font extremes and checks visible control bounds.</summary>
        /// <returns>None.</returns>
        private static void SubmissionDialogFontSizes()
        {
            List<string> layoutIssues = new List<string>();
            foreach (float size in new[] { UiFontSettings.MinimumSize, MaximumUiFontSize })
            {
                AppStore store = NewStore("submission-font");
                Repository repository = AddRepository(store, "submission-font", "Single-file submission fixture", "Examples");
                using (SubmitFileDialog dialog = new SubmitFileDialog(store, new SvnService(store, Application.ExecutablePath), repository, "设计/manual.txt", size))
                {
                    OpenOffscreen(dialog);
                    WaitSubmissionIdle(dialog);
                    string screenshot = Path.Combine(runDirectory, "submission-font-" + size.ToString("0.#", CultureInfo.InvariantCulture) + ".png");
                    SaveFontScreenshot(dialog, size, screenshot, layoutIssues);
                }
            }
            Assert(layoutIssues.Count == 0, String.Join(Environment.NewLine, layoutIssues));
        }

        private static void DataDirectoryShortcut()
        {
            string target = Path.Combine(runDirectory, "custom data 数据 " + Guid.NewGuid().ToString("N"));
            string shortcutPath = MainForm.CreateDataDirectoryShortcut(target, Application.ExecutablePath);
            Assert(File.Exists(shortcutPath) && Path.GetDirectoryName(shortcutPath) == target, "Shortcut was not created in selected directory.");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                string executable = (string)shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                string arguments = (string)shortcutType.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null);
                string workingDirectory = (string)shortcutType.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, shortcut, null);
                Assert(String.Equals(executable, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase), "Shortcut executable changed.");
                Assert(arguments == "--data-dir \"" + target + "\"", "Custom directory was not quoted correctly.");
                Assert(String.Equals(workingDirectory, target, StringComparison.OrdinalIgnoreCase), "Shortcut working directory changed.");
            }
            finally
            {
                if (shortcut != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        private static int FakeCalls(AppStore store)
        {
            return Directory.GetFiles(store.DataDirectory, "fake-svn-call-*.txt").Length;
        }

        private static int HistoryCount(MainForm form, string field)
        {
            return Field<System.Collections.ICollection>(form, field).Count;
        }

        private static string[] VisibleRows(ListView list)
        {
            return list.Items.Cast<ListViewItem>().Select(row => String.Join("\t", row.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(cell => cell.Text))).ToArray();
        }

        private static Repository PrepareHistorySearch(AppStore store)
        {
            Repository repository = AddRepository(store, "history", "History fixture", "Examples");
            List<SvnEntry> entries = new List<SvnEntry>();
            for (int index = 0; index < 100; index++) entries.Add(new SvnEntry
            {
                Name = "Target folder " + index.ToString("D3"), Path = "folder-" + index.ToString("D3"), IsDirectory = true,
                Revision = "7", ModifiedUtc = new DateTime(2026, 11, 2, 2, 30, 0, DateTimeKind.Utc)
            });
            store.SaveIndex(new IndexSnapshot { RepositoryId = repository.Id, Revision = "7", UpdatedUtc = DateTime.UtcNow, Entries = entries });
            return repository;
        }

        private static void HistorySearchRestore()
        {
            AppStore store = NewStore("history-search");
            PrepareHistorySearch(store);
            using (MainForm form = OpenMain(store))
            {
                ListView list = Field<ListView>(form, "files");
                TextBox query = Field<TextBox>(form, "search");
                ComboBox scopeBox = Field<ComboBox>(form, "scope");
                Assert(!Field<Button>(form, "back").Enabled && !Field<Button>(form, "forward").Enabled, "History was enabled on the initial view.");
                query.Text = "Target folder";
                scopeBox.SelectedIndex = 1;
                Pump((Task)Call(form, "SearchAsync"));
                Assert(list.Items.Count == 100 && FakeCalls(store) == 0, "Indexed search unexpectedly contacted SVN.");
                list.Columns[0].Width = 251;
                list.Columns[4].Width = 447;
                list.Items[65].Selected = true;
                list.Items[65].Focused = true;
                list.TopItem = list.Items[50];
                Application.DoEvents();
                int selected = list.SelectedIndices[0];
                int top = list.TopItem.Index;
                int[] widths = list.Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray();
                string[] expectedRows = VisibleRows(list);
                string expectedTitle = Field<Label>(form, "pageTitle").Text;
                string expectedAddress = Field<TextBox>(form, "address").Text;
                Pump((Task)Call(form, "ActivateSelectionAsync"));
                Assert(Field<string>(form, "currentPath") == "folder-065", "Selected search folder was not opened.");
                string[] directoryRows = VisibleRows(list);
                int callsAfterNavigation = FakeCalls(store);
                Call(form, "GoBack");
                Pump(Task.Delay(350));
                Assert(VisibleRows(list).SequenceEqual(expectedRows), "Back did not restore the exact search results and order.");
                Assert(query.Text == "Target folder" && scopeBox.SelectedIndex == 1, "Search query/scope was not restored.");
                Assert(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == selected && list.TopItem.Index == top, "Search selection/scroll position was not restored.");
                Assert(list.Columns.Cast<ColumnHeader>().Select(column => column.Width).SequenceEqual(widths), "Search column widths were not restored.");
                Assert(Field<Label>(form, "pageTitle").Text == expectedTitle && Field<TextBox>(form, "address").Text == expectedAddress, "Search context labels changed after Back.");
                Assert(FakeCalls(store) == callsAfterNavigation, "Back reissued SVN list/info/cat.");
                Call(form, "GoForward");
                Pump(Task.Delay(350));
                Assert(VisibleRows(list).SequenceEqual(directoryRows) && Field<string>(form, "currentPath") == "folder-065", "Forward did not restore cached directory rows.");
                Assert(FakeCalls(store) == callsAfterNavigation, "Forward reissued SVN list/info/cat.");
            }
        }

        private static void HistoryBranch()
        {
            AppStore store = NewStore("history-branch");
            Repository repository = AddRepository(store, "history", "History fixture", "Examples");
            using (MainForm form = OpenMain(store))
            {
                Pump((Task)Call(form, "NavigateAsync", repository, "first"));
                Pump((Task)Call(form, "NavigateAsync", repository, "second"));
                Call(form, "GoBack");
                Assert(HistoryCount(form, "forwardHistory") == 1 && Field<Button>(form, "forward").Enabled, "Back did not create a forward entry.");
                Pump((Task)Call(form, "NavigateAsync", repository, "branch"));
                Assert(HistoryCount(form, "forwardHistory") == 0 && !Field<Button>(form, "forward").Enabled, "New navigation retained the abandoned forward branch.");
                int calls = FakeCalls(store);
                Call(form, "GoForward");
                Assert(Field<string>(form, "currentPath") == "branch" && FakeCalls(store) == calls, "Unavailable Forward changed the current view.");
            }
        }

        private static void HistoryExecutedQuery()
        {
            AppStore store = NewStore("history-executed-query");
            PrepareHistorySearch(store);
            using (MainForm form = OpenMain(store))
            {
                TextBox query = Field<TextBox>(form, "search");
                Field<ComboBox>(form, "scope").SelectedIndex = 1;
                query.Text = "Target folder";
                Pump((Task)Call(form, "SearchAsync"));
                int previousDepth = HistoryCount(form, "backHistory");
                query.Text = "Target folder 06";
                Task executed = (Task)Call(form, "SearchAsync");
                query.Text = "Target folder";
                Pump(executed);
                Assert(Field<ListView>(form, "files").Items.Count == 11, "Second search used later unsubmitted input.");
                Assert(HistoryCount(form, "backHistory") == previousDepth + 1, "Executed second search was treated as a duplicate of unsubmitted input.");
                Call(form, "ShowRepositories", "Unrelated view");
                Call(form, "GoBack");
                Assert(query.Text == "Target folder 06" && Field<ListView>(form, "files").Items.Count == 11, "Back did not restore the executed second search.");
                Call(form, "GoBack");
                Assert(query.Text == "Target folder" && Field<ListView>(form, "files").Items.Count == 100, "The original search was lost from history.");
                Assert(FakeCalls(store) == 0, "Search history unexpectedly queried SVN.");
            }
        }

        private delegate bool WindowVisitor(IntPtr window, IntPtr context);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor callback, IntPtr context);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr parameter, IntPtr extra);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        /// <summary>Closes native message boxes owned by this test process without depending on off-screen activation.</summary>
        /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
        private sealed class TestMessageBoxCloser : IDisposable
        {
            private const string DialogClass = "#32770";
            private const uint CommandMessage = 0x0111;
            private const uint CloseMessage = 0x0010;
            private const int AcceptCommand = 1;
            private readonly WindowVisitor callback;
            private readonly ManualResetEvent stop = new ManualResetEvent(false);
            private readonly Thread worker;
            private readonly uint processId;
            private int seen;

            public TestMessageBoxCloser()
            {
                processId = GetCurrentProcessId();
                callback = CloseDialog;
                worker = new Thread(delegate()
                {
                    while (!stop.WaitOne(25)) EnumWindows(callback, IntPtr.Zero);
                });
                worker.IsBackground = true;
                worker.Start();
            }

            private bool CloseDialog(IntPtr window, IntPtr context)
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != processId) return true;
                StringBuilder className = new StringBuilder(64);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() == DialogClass)
                {
                    if (Interlocked.Increment(ref seen) == 1) Console.WriteLine("UI fixture: dismissing the test process error dialog.");
                    PostMessage(window, CommandMessage, new IntPtr(AcceptCommand), IntPtr.Zero);
                    PostMessage(window, CloseMessage, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
            }

            public void Dispose()
            {
                stop.Set();
                worker.Join();
                stop.Dispose();
                GC.KeepAlive(callback);
            }
        }

        private static void HistoryFailedAndCancelled()
        {
            AppStore store = NewStore("history-failure");
            Repository repository = AddRepository(store, "history", "History fixture", "Examples");
            using (MainForm form = OpenMain(store))
            {
                Pump((Task)Call(form, "NavigateAsync", repository, "first"));
                object committed = Field<object>(form, "currentView");
                int count = HistoryCount(form, "backHistory");
                using (TestMessageBoxCloser closer = new TestMessageBoxCloser())
                {
                    Pump((Task)Call(form, "NavigateAsync", repository, "failed"));
                }
                Assert(Field<Label>(form, "status").Text.IndexOf("操作未完成", StringComparison.Ordinal) >= 0, "Failure fixture did not report the operation error.");
                Assert(Object.ReferenceEquals(Field<object>(form, "currentView"), committed) && HistoryCount(form, "backHistory") == count, "Failed navigation entered history.");
                Assert(Field<string>(form, "currentPath") == "first", "Failed navigation changed the active directory.");
                Task cancelled = (Task)Call(form, "NavigateAsync", repository, "slow");
                Call(form, "CancelOperation");
                Pump(cancelled);
                Assert(Object.ReferenceEquals(Field<object>(form, "currentView"), committed) && HistoryCount(form, "backHistory") == count, "Cancelled navigation entered history.");
                Assert(Field<string>(form, "currentPath") == "first" && !Field<ProgressBar>(form, "progress").Visible, "Cancelled navigation changed the active directory or remained busy.");
            }
        }

        private static void HistoryLimit()
        {
            AppStore store = NewStore("history-limit");
            using (MainForm form = OpenMain(store))
            {
                Assert(!Field<Button>(form, "back").Enabled, "Initial Back button was enabled.");
                for (int index = 0; index < 45; index++) Call(form, "ShowRepositories", "Group " + index);
                Assert(HistoryCount(form, "backHistory") == 30, "Back history was not capped at thirty entries.");
                for (int index = 0; index < 30; index++) Call(form, "GoBack");
                Assert(HistoryCount(form, "backHistory") == 0 && !Field<Button>(form, "back").Enabled, "Back remained enabled at the retained-history boundary.");
                string earliest = Field<Label>(form, "pageTitle").Text;
                Call(form, "GoBack");
                Assert(Field<Label>(form, "pageTitle").Text == earliest, "Back at the boundary changed the view.");
                for (int index = 0; index < 30; index++) Call(form, "GoForward");
                Assert(Field<Label>(form, "pageTitle").Text == "Group 44" && !Field<Button>(form, "forward").Enabled, "Forward did not restore the latest retained view.");
                Assert(FakeCalls(store) == 0, "Repository history issued SVN requests.");
            }
        }

        private static void HistoryKeyboard()
        {
            AppStore store = NewStore("history-keyboard");
            using (MainForm form = OpenMain(store))
            {
                Call(form, "ShowRepositories", "First group");
                Call(form, "ShowRepositories", "Second group");
                MethodInfo keyDown = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
                KeyEventArgs backward = new KeyEventArgs(Keys.Alt | Keys.Left);
                keyDown.Invoke(form, new object[] { backward });
                Assert(backward.SuppressKeyPress && Field<Label>(form, "pageTitle").Text == "First group", "Alt+Left did not navigate Back.");
                KeyEventArgs forward = new KeyEventArgs(Keys.Alt | Keys.Right);
                keyDown.Invoke(form, new object[] { forward });
                Assert(forward.SuppressKeyPress && Field<Label>(form, "pageTitle").Text == "Second group", "Alt+Right did not navigate Forward.");
            }
        }
    }
}
