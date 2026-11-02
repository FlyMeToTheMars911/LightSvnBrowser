using System;
using System.Collections.Generic;
using System.Drawing;
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
            Run("nested groups contain descendants without matching sibling prefixes", NestedGroups);
            Run("favorite aliases preserve real file names", FavoriteAlias);
            Run("favorite preview resolves latest metadata and keeps alias", FavoriteLatestPreview);
            Run("favorite search matches names, tags, notes and paths", FavoriteSearch);
            Run("filename search uses stored index and directory boundary", FilenameSearch);
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
                TableLayoutPanel fields = dialog.Controls.OfType<TableLayoutPanel>().First();
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
            if (args[0] == "info") { Console.Write("<?xml version=\"1.0\"?><info><entry kind=\"dir\" revision=\"7\" path=\"docs\" /></info>"); return 0; }
            string url = args[args.Length - 1];
            if (url.IndexOf("/failed", StringComparison.Ordinal) >= 0) { Console.Error.Write("svn: E170013: Offline fixture rejects this path."); return 1; }
            bool slow = url.IndexOf("/slow", StringComparison.Ordinal) >= 0;
            if (slow) Thread.Sleep(700);
            string name = slow ? "slow-result.txt" : url.IndexOf("/fast", StringComparison.Ordinal) >= 0 ? "fast-result.txt" : "manual.txt";
            Console.Write("<?xml version=\"1.0\"?><lists><list path=\"offline\"><entry kind=\"file\"><name>" + name + "</name><size>" + Encoding.UTF8.GetByteCount(Payload) + "</size><commit revision=\"7\"><author>offline</author><date>2026-11-02T02:30:00Z</date></commit></entry></list></lists>");
            return 0;
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
