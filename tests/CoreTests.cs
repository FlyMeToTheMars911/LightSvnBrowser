using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LightSvnBrowser;

public static class TestRunner
{
    private static int _assertions;
    private static string _testRoot;
    private static string _svn;
    private static string _bundledSvn;

    public static int Main(string[] args)
    {
        if (args.Length > 0 && new[] { "list", "cat", "info" }.Contains(args[0])) return FakeSvn(args);
        if (args.Length > 0 && args[0] == "--benchmark") return RunBenchmarks(args);
        try
        {
            string requestedRoot = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "work", "core-tests");
            _testRoot = Path.Combine(Path.GetFullPath(requestedRoot), "fixture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_testRoot);
            _svn = args.Length > 1 ? args[1] : @"C:\Program Files\TortoiseSVN\bin\svn.exe";
            _bundledSvn = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
            TestConfiguration();
            TestUrls();
            TestNativeRepository().GetAwaiter().GetResult();
            if (!String.IsNullOrEmpty(_bundledSvn)) TestLoopbackAuthentication().GetAwaiter().GetResult();
            TestProcessIsolation().GetAwaiter().GetResult();
            TestCertificateCompatibility().GetAwaiter().GetResult();
            TestIndexMemoryCache();
            TestFilenameSearch().GetAwaiter().GetResult();
            TestCleanup();
            Console.WriteLine("PASS: " + _assertions + " assertions. Local fixtures only. " + _testRoot);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", null);
            Environment.SetEnvironmentVariable("LIGHTSVN_TEST_PASSWORD", null);
        }
    }

    private static void TestConfiguration()
    {
        string root = Path.Combine(_testRoot, "settings");
        AppStore store = new AppStore(root);
        Assert(store.Config.UiFontSize == UiFontSettings.DefaultSize, "new configuration defaults to nine point UI font");
        const float preferredFontSize = 12.5F;
        store.Config.UiFontSize = preferredFontSize;
        const string password = "Test only 密码 \" @ & 1092";
        string encrypted = store.Protect(password);
        Assert(encrypted.StartsWith("dpapi:"), "DPAPI prefix");
        Assert(encrypted.IndexOf(password, StringComparison.Ordinal) < 0, "protected value not plaintext");
        Assert(store.Unprotect(encrypted) == password, "DPAPI round trip");
        Assert(store.Unprotect(String.Empty) == String.Empty, "empty password");
        Expect<InvalidDataException>(delegate { store.Unprotect("invalid-password-format"); }, "bad secret fails clearly");
        Repository repository = new Repository { Id = "test", Name = "测试仓库", Url = "https://example.invalid/svn/docs", Username = "fixture-user", ProtectedPassword = encrypted };
        Assert(!repository.AllowIpCertificateNameMismatch, "certificate name exception disabled by default");
        repository.AllowIpCertificateNameMismatch = true;
        store.Config.Repositories.Add(repository);
        store.Config.Favorites.Add(new Favorite { Id = "fav", RepositoryId = repository.Id, Name = "设计", Path = "方案 @ old/说明.md", Tags = "技术,常用", Note = "测试备注" });
        store.Save();
        store.Config.Favorites[0].Note = "第二次保存";
        store.Save();
        string configPath = Path.Combine(root, "settings.json");
        Assert(File.Exists(configPath + ".bak"), "atomic save retains previous config");
        Assert(!File.ReadAllText(configPath).Contains(password), "settings never contain plaintext secret");
        AppStore reloaded = new AppStore(root);
        Assert(reloaded.Config.Favorites[0].Note == "第二次保存", "JSON Unicode round trip");
        Assert(reloaded.Unprotect(reloaded.Config.Repositories[0].ProtectedPassword) == password, "persisted DPAPI secret");
        Assert(reloaded.Config.Repositories[0].AllowIpCertificateNameMismatch, "certificate name exception JSON round trip");
        Assert(reloaded.Config.UiFontSize == preferredFontSize, "fractional UI font size persists after reload");
        string before = File.ReadAllText(configPath);
        float[] invalidFontSizes = { Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity, UiFontSettings.MinimumSize - 0.5F, UiFontSettings.MaximumSize + 0.5F };
        foreach (float invalid in invalidFontSizes)
        {
            store.Config.UiFontSize = invalid;
            Expect<InvalidDataException>(delegate { store.Save(); }, "non-finite or out-of-range UI font rejected");
            Assert(File.ReadAllText(configPath) == before, "invalid UI font save preserves existing settings file");
        }
        foreach (float endpoint in new[] { UiFontSettings.MinimumSize, UiFontSettings.MaximumSize })
        {
            store.Config.UiFontSize = endpoint;
            store.Save();
            Assert(new AppStore(root).Config.UiFontSize == endpoint, "UI font range endpoint is accepted");
        }
        store.Config.UiFontSize = preferredFontSize;
        store.Save();
        before = File.ReadAllText(configPath);
        File.WriteAllText(configPath, "{ broken json");
        Expect<InvalidDataException>(delegate { new AppStore(root); }, "corrupt settings fails without wipe");
        Assert(File.ReadAllText(configPath) == "{ broken json", "corrupt settings preserved");
        File.WriteAllText(configPath, before);
        Assert(File.ReadAllText(Path.Combine(store.SvnConfigDirectory, "config")).Contains("store-auth-creds = no"), "dedicated auth storage disabled");
        string legacyRoot = Path.Combine(_testRoot, "legacy-settings");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "{\"Repositories\":[{\"Id\":\"legacy\",\"Name\":\"Legacy\",\"Url\":\"https://example.invalid/svn\"}],\"Favorites\":[]}");
        AppStore legacy = new AppStore(legacyRoot);
        Assert(!legacy.Config.Repositories[0].AllowIpCertificateNameMismatch, "legacy configuration without certificate flag remains disabled");
        Assert(legacy.Config.UiFontSize == UiFontSettings.DefaultSize, "legacy JSON without UI font uses nine point default");
        string invalidFontRoot = Path.Combine(_testRoot, "invalid-font-settings");
        Directory.CreateDirectory(invalidFontRoot);
        string invalidFontJson = "{\"Repositories\":[],\"Favorites\":[],\"UiFontSize\":17}";
        string invalidFontPath = Path.Combine(invalidFontRoot, "settings.json");
        File.WriteAllText(invalidFontPath, invalidFontJson);
        Expect<InvalidDataException>(delegate { new AppStore(invalidFontRoot); }, "invalid font in JSON rejected on load");
        Assert(File.ReadAllText(invalidFontPath) == invalidFontJson, "invalid font JSON remains intact for repair");
    }

    private static void TestUrls()
    {
        Repository repository = new Repository { Id = "url", Url = "https://example.invalid/svn/资料 空格/" };
        string url = SvnService.BuildUrl(repository, "目录 @ new/说明 100%.txt");
        Assert(url.Contains("%20") && url.Contains("%40") && url.Contains("100%25.txt"), "URL escaping of spaces Unicode @ and percent");
        Assert(SvnService.BuildUrl(repository, String.Empty).EndsWith("%E7%A9%BA%E6%A0%BC"), "root URL normalized");
        Expect<ArgumentException>(delegate { SvnService.BuildUrl(repository, "../other.txt"); }, "path traversal rejected");
        Expect<ArgumentException>(delegate { SvnService.BuildUrl(repository, "/absolute.txt"); }, "absolute relative path rejected");
        Expect<ArgumentException>(delegate { SvnService.BuildUrl(repository, "x/./other.txt"); }, "dot segment rejected");
        Expect<ArgumentException>(delegate { SvnService.BuildUrl(new Repository { Url = "https://user:secret@example.invalid/svn" }, "x"); }, "URL credentials rejected");
        Expect<ArgumentException>(delegate { SvnService.BuildUrl(new Repository { Url = "https://example.invalid/svn?x=1" }, "x"); }, "query URL rejected");
        Expect<ArgumentException>(delegate { SvnService.BuildUrl(new Repository { Url = "svn+ssh://example.invalid/svn" }, "x"); }, "SSH protocol rejected even outside UI");
    }

    private static async Task TestNativeRepository()
    {
        string root = Path.Combine(_testRoot, "真实 SVN fixture");
        string seed = Path.Combine(root, "seed");
        string repositoryPath = Path.Combine(root, "repo @ 文档");
        Directory.CreateDirectory(Path.Combine(seed, "目录 @ test"));
        const string textName = "文档 空格@name.txt";
        const string content = "本文只存在SVN文件中。METADATA_MUST_NOT_CONTAIN_THIS_BODY\r\n第二行\n";
        File.WriteAllText(Path.Combine(seed, textName), content, new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(seed, "empty@.md"), new byte[0]);
        byte[] binary = Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(seed, "目录 @ test", "binary @ 数据.bin"), binary);
        string admin = Path.Combine(Path.GetDirectoryName(_svn), "svnadmin.exe");
        RunLocal(admin, "create", repositoryPath);
        string repositoryUrl = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        RunLocal(_svn, "import", seed, repositoryUrl, "-m", "local fixture import", "--non-interactive", "--no-auth-cache");
        AppStore store = new AppStore(Path.Combine(root, "app data"));
        Repository repository = new Repository { Id = "native-local", Name = "本地测试", Url = repositoryUrl };
        store.Config.Repositories.Add(repository);
        store.Save();
        SvnService service = new SvnService(store, _svn);
        List<SvnEntry> listing = await service.ListAsync(repository, String.Empty, false, CancellationToken.None);
        Assert(listing.Count == 3, "native shallow listing count");
        Assert(listing[0].IsDirectory, "directories sort before files");
        SvnEntry text = listing.Single(e => e.Name == textName);
        Assert(text.Path == textName && text.Revision == "1", "native Unicode @ path and revision parsed");
        List<SvnEntry> child = await service.ListAsync(repository, "目录 @ test", false, CancellationToken.None);
        Assert(child.Count == 1 && child[0].Path == "目录 @ test/binary @ 数据.bin", "nested path remains repository relative");
        IndexSnapshot snapshot = await service.BuildIndexAsync(repository, CancellationToken.None);
        Assert(snapshot.Entries.Count == 4 && snapshot.Revision == "1", "fixed-revision recursive index");
        Assert(store.LoadIndex(repository.Id).Entries.Count == 4, "index JSON round trip");
        string indexText = File.ReadAllText(Directory.GetFiles(store.IndexDirectory, "*.json").Single());
        Assert(!indexText.Contains("METADATA_MUST_NOT_CONTAIN_THIS_BODY"), "file index contains metadata only");
        Assert(Directory.GetFiles(store.CacheDirectory).Length == 0, "index creates no document cache");
        string preview = await service.FetchAsync(repository, text, CancellationToken.None);
        Assert(File.ReadAllText(preview, Encoding.UTF8) == content, "native text preview bytes exact");
        Assert((File.GetAttributes(preview) & FileAttributes.ReadOnly) != 0, "preview cache is read only");
        string previewAgain = await service.FetchAsync(repository, text, CancellationToken.None);
        Assert(preview == previewAgain, "same revision reuses cache");
        string binaryPreview = await service.FetchAsync(repository, child[0], CancellationToken.None);
        Assert(File.ReadAllBytes(binaryPreview).SequenceEqual(binary), "binary cat not decoded as text");
        string emptyPreview = await service.FetchAsync(repository, listing.Single(e => e.Name == "empty@.md"), CancellationToken.None);
        Assert(new FileInfo(emptyPreview).Length == 0, "empty file supported");
        string saved = Path.Combine(root, "用户正式保存 @ 文件.bin");
        File.WriteAllText(saved, "existing file must be atomically replaced");
        await service.DownloadAsync(repository, child[0], saved, CancellationToken.None);
        Assert(File.ReadAllBytes(saved).SequenceEqual(binary), "download atomically replaces exact binary");
        Assert((File.GetAttributes(saved) & FileAttributes.ReadOnly) == 0, "download is editable");
        string newText = Path.Combine(root, "new-text.txt");
        File.WriteAllText(newText, "new revision text", new UTF8Encoding(false));
        string mucc = Path.Combine(Path.GetDirectoryName(_svn), "svnmucc.exe");
        RunLocal(mucc, "put", newText, SvnService.BuildUrl(repository, text.Path), "-m", "local fixture update", "--non-interactive", "--no-auth-cache");
        string oldRevisionPreview = await service.FetchAsync(repository, text, CancellationToken.None);
        Assert(File.ReadAllText(oldRevisionPreview, Encoding.UTF8) == content, "old revision cache remains stable after repository changes");
        List<SvnEntry> fresh = await service.ListAsync(repository, String.Empty, false, CancellationToken.None);
        SvnEntry latestText = fresh.Single(e => e.Name == textName);
        Assert(latestText.Revision == "2", "refresh sees new revision");
        string updatedPreview = await service.FetchAsync(repository, latestText, CancellationToken.None);
        Assert(updatedPreview != preview && File.ReadAllText(updatedPreview) == "new revision text", "changed revision uses different cache");
        string copyWithoutCache = Path.Combine(root, "native-cold-data");
        AppStore coldStore = new AppStore(copyWithoutCache);
        string oldFromSvn = await new SvnService(coldStore, _svn).FetchAsync(repository, text, CancellationToken.None);
        Assert(File.ReadAllText(oldFromSvn, Encoding.UTF8) == content, "cat uses explicit historical revision without old cache");
        if (!String.IsNullOrEmpty(_bundledSvn))
        {
            AppStore bundledStore = new AppStore(Path.Combine(root, "bundled-client-data"));
            SvnService bundledService = new SvnService(bundledStore, _bundledSvn);
            List<SvnEntry> bundledListing = await bundledService.ListAsync(repository, String.Empty, false, CancellationToken.None);
            Assert(bundledListing.Count == listing.Count && bundledListing.Any(e => e.Name == textName), "bundled CLI lists Unicode and @ fixture");
            List<SvnEntry> bundledChildren = await bundledService.ListAsync(repository, "目录 @ test", false, CancellationToken.None);
            string bundledBinary = await bundledService.FetchAsync(repository, bundledChildren.Single(), CancellationToken.None);
            Assert(HashBytes(File.ReadAllBytes(bundledBinary)) == HashBytes(binary), "bundled CLI binary cat SHA256 exact");
            string bundledText = await bundledService.FetchAsync(repository, bundledListing.Single(e => e.Name == textName), CancellationToken.None);
            Assert(File.ReadAllText(bundledText) == "new revision text", "bundled CLI fetches current text revision");
        }
        store.DeleteIndex(repository.Id);
        Assert(store.LoadIndex(repository.Id) == null, "index delete");
        Assert(!Directory.EnumerateDirectories(root, ".svn", SearchOption.AllDirectories).Any(), "application never creates a working copy");
    }

    private static async Task TestLoopbackAuthentication()
    {
        string root = Path.Combine(_testRoot, "loopback-auth");
        string seed = Path.Combine(root, "seed");
        string repositoryPath = Path.Combine(root, "repository");
        Directory.CreateDirectory(seed);
        byte[] binary = Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(seed, "auth-proof.bin"), binary);
        string toolDirectory = Path.GetDirectoryName(_svn);
        RunLocal(Path.Combine(toolDirectory, "svnadmin.exe"), "create", repositoryPath);
        RunLocal(_svn, "import", seed, new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri, "-m", "local auth fixture", "--non-interactive", "--no-auth-cache");
        const string password = "local-fixture-readonly-948217";
        string configuration = Path.Combine(repositoryPath, "conf");
        File.WriteAllText(Path.Combine(configuration, "svnserve.conf"), "[general]\r\nanon-access = none\r\nauth-access = read\r\npassword-db = passwd\r\nrealm = LightSvnBrowser-local-test\r\n[sasl]\r\nuse-sasl = false\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(configuration, "passwd"), "[users]\r\nfixture-user = " + password + "\r\n", new UTF8Encoding(false));
        int port;
        TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        try { port = ((IPEndPoint)reservation.LocalEndpoint).Port; }
        finally { reservation.Stop(); }
        string[] arguments = { "--daemon", "--foreground", "--read-only", "--listen-host", "127.0.0.1", "--listen-port", port.ToString(), "--root", root };
        ProcessStartInfo start = new ProcessStartInfo(Path.Combine(toolDirectory, "svnserve.exe"), String.Join(" ", arguments.Select(Quote)))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
        };
        start.EnvironmentVariables["TEMP"] = root;
        start.EnvironmentVariables["TMP"] = root;
        using (Process server = new Process { StartInfo = start })
        {
            Task<string> stdout = null;
            Task<string> stderr = null;
            try
            {
                Assert(server.Start(), "loopback read-only svnserve starts");
                stdout = server.StandardOutput.ReadToEndAsync();
                stderr = server.StandardError.ReadToEndAsync();
                Stopwatch readiness = Stopwatch.StartNew();
                bool listening = false;
                while (readiness.Elapsed < TimeSpan.FromSeconds(5))
                {
                    if (server.HasExited) throw new Exception("Local svnserve exited before authentication test: " + await stderr);
                    listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port && e.Address.Equals(IPAddress.Loopback));
                    if (listening) break;
                    await Task.Delay(50);
                }
                Assert(listening, "fixture server listens on loopback only");
                AppStore store = new AppStore(Path.Combine(root, "app-data"));
                Repository repository = new Repository
                {
                    Id = "loopback-auth", Name = "localhost read-only authentication fixture",
                    Url = "svn://127.0.0.1:" + port + "/repository", Username = "fixture-user", ProtectedPassword = store.Protect(password)
                };
                SvnService client = new SvnService(store, _bundledSvn);
                List<SvnEntry> listing = await client.ListAsync(repository, String.Empty, false, CancellationToken.None);
                Assert(listing.Count == 1 && listing[0].Name == "auth-proof.bin", "bundled CLI authenticates via stdin with real svnserve CRAM");
                string fetched = await client.FetchAsync(repository, listing[0], CancellationToken.None);
                Assert(HashBytes(File.ReadAllBytes(fetched)) == HashBytes(binary), "authenticated bundled binary SHA256 exact");
                Assert(!Directory.EnumerateFiles(store.SvnConfigDirectory, "*", SearchOption.AllDirectories).Any(p => p.IndexOf("svn.simple", StringComparison.OrdinalIgnoreCase) >= 0), "authenticated request writes no credential cache");
                string logs = String.Join("", Directory.GetFiles(store.LogDirectory).Select(File.ReadAllText));
                Assert(!logs.Contains(password), "real authenticated requests do not log password");
            }
            finally
            {
                try { if (!server.HasExited) server.Kill(); } catch (InvalidOperationException) { }
                try { server.WaitForExit(5000); } catch (InvalidOperationException) { }
                if (stdout != null && stderr != null) Task.WaitAll(stdout, stderr);
            }
            Assert(server.HasExited, "loopback fixture server terminates");
        }
    }

    private static string HashBytes(byte[] bytes)
    {
        using (SHA256 algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(bytes));
    }

    private static async Task TestProcessIsolation()
    {
        string root = Path.Combine(_testRoot, "isolated process");
        AppStore store = new AppStore(root);
        const string password = "only-stdin-测试-9347";
        Repository repository = new Repository { Id = "mock", Name = "Mock", Url = "https://example.invalid/svn/never-contacted", Username = "fixture-user", ProtectedPassword = store.Protect(password) };
        string executable = Assembly.GetExecutingAssembly().Location;
        SvnService service = new SvnService(store, executable);
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_PASSWORD", password);
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "ok");
        List<SvnEntry> entries = await service.ListAsync(repository, String.Empty, false, CancellationToken.None);
        Assert(entries.Single().Name == "mock.txt", "fake native executable protocol");
        string proof = File.ReadAllText(Path.Combine(root, "fake-proof.txt"));
        Assert(proof.Contains("passwordInArguments=False") && proof.Contains("stdin=True"), "password delivered only through stdin");
        Assert(proof.Contains("config=True") && proof.Contains("temp=True") && proof.Contains("noAuthCache=True"), "SVN temp config auth isolated under data directory");
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "missing-date");
        List<SvnEntry> missingDate = await service.ListAsync(repository, String.Empty, false, CancellationToken.None);
        Assert(missingDate[0].ModifiedUtc == DateTime.MinValue && missingDate[0].ModifiedUtc.Kind == DateTimeKind.Utc, "missing date is UTC minimum");
        store.SaveIndex(new IndexSnapshot { RepositoryId = repository.Id, UpdatedUtc = DateTime.UtcNow, Entries = missingDate });
        Assert(store.LoadIndex(repository.Id).Entries[0].ModifiedUtc.Kind == DateTimeKind.Utc, "missing date serializes without timezone underflow");
        string allLogs = String.Join("", Directory.GetFiles(store.LogDirectory).Select(File.ReadAllText));
        Assert(!allLogs.Contains(password), "logs contain no password");
        int before = CountAttempts(root);
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "fail");
        try
        {
            await service.ListAsync(repository, String.Empty, false, CancellationToken.None);
            throw new Exception("Expected native authentication failure");
        }
        catch (IOException ex)
        {
            Assert(!ex.Message.Contains(password), "native stderr secret redacted");
        }
        Assert(CountAttempts(root) == before + 1, "failure invokes exactly once without retries");
        allLogs = String.Join("", Directory.GetFiles(store.LogDirectory).Select(File.ReadAllText));
        Assert(!allLogs.Contains(password), "failure logs redact native stderr password");
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "dtd");
        bool dtdRejected = false;
        try { await service.ListAsync(repository, String.Empty, false, CancellationToken.None); }
        catch (System.Xml.XmlException) { dtdRejected = true; }
        Assert(dtdRejected, "external XML entity prohibited");
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "slow");
        Stopwatch timer = Stopwatch.StartNew();
        using (CancellationTokenSource cancellation = new CancellationTokenSource(250))
        {
            bool canceled = false;
            try { await service.ListAsync(repository, String.Empty, false, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Assert(canceled && timer.Elapsed < TimeSpan.FromSeconds(5), "cancellation kills child process promptly");
        }
        Assert(Directory.GetFiles(store.CacheDirectory, "*.part").Length == 0, "failure does not leave partial cache");
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "oversize");
        store.Config.CacheLimitMb = 1;
        bool rejected = false;
        try { await service.FetchAsync(repository, new SvnEntry { Name = "mock.txt", Path = "mock.txt", Size = 1, Revision = "1" }, CancellationToken.None); }
        catch (IOException) { rejected = true; }
        Assert(rejected && Directory.GetFiles(store.CacheDirectory).Length == 0, "stream size bound cleans partial preview");
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "partial");
        string destination = Path.Combine(root, "keep-original.txt");
        File.WriteAllText(destination, "original");
        bool failed = false;
        try { await service.DownloadAsync(repository, new SvnEntry { Name = "mock.txt", Path = "mock.txt", Size = 64, Revision = "1" }, destination, CancellationToken.None); }
        catch (IOException) { failed = true; }
        Assert(failed && File.ReadAllText(destination) == "original", "failed download preserves existing user file");
        Assert(Directory.GetFiles(store.CacheDirectory, "*.part").Length == 0, "failed download removes staging file");
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", null);
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_PASSWORD", null);
    }

    private static void TestCleanup()
    {
        AppStore store = new AppStore(Path.Combine(_testRoot, "cleanup"));
        store.Config.CacheLimitMb = 1;
        store.Config.CacheDays = 2;
        string expired = Path.Combine(store.CacheDirectory, "expired.txt");
        File.WriteAllText(expired, "old");
        File.SetLastAccessTimeUtc(expired, DateTime.UtcNow.AddDays(-4));
        File.SetAttributes(expired, FileAttributes.ReadOnly);
        string older = Path.Combine(store.CacheDirectory, "older.bin");
        string recent = Path.Combine(store.CacheDirectory, "recent.bin");
        File.WriteAllBytes(older, new byte[700000]);
        File.WriteAllBytes(recent, new byte[700000]);
        File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(recent, DateTime.UtcNow);
        string activePartial = Path.Combine(store.CacheDirectory, "active.part");
        File.WriteAllBytes(activePartial, new byte[1]);
        store.CleanupCache();
        Assert(!File.Exists(expired), "TTL removes read-only cache");
        Assert(!File.Exists(older) && File.Exists(recent), "capacity cleanup retains most recent file");
        Assert(File.Exists(activePartial), "cleanup preserves active staging file");
    }

    private static void TestIndexMemoryCache()
    {
        AppStore store = new AppStore(Path.Combine(_testRoot, "index-memory-cache"));
        IndexSnapshot first = MakeSnapshot("first", 10);
        store.SaveIndex(first);
        Assert(Object.ReferenceEquals(first, store.LoadIndex("first")), "SaveIndex populates memory snapshot cache");
        Assert(Object.ReferenceEquals(store.LoadIndex("first"), store.LoadIndex("first")), "unchanged file loads return cached reference");
        string path = Path.Combine(store.IndexDirectory, AppStore.Hash("first") + ".json");
        IndexSnapshot replacement = MakeSnapshot("first", 12);
        WriteSnapshot(path, replacement);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        IndexSnapshot changed = store.LoadIndex("first");
        Assert(changed.Entries.Count == 12 && !Object.ReferenceEquals(changed, first), "external length and timestamp change invalidates memory cache");
        replacement.Entries[0].Name = "Document 1111111.md";
        WriteSnapshot(path, replacement);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
        Assert(store.LoadIndex("first").Entries[0].Name == "Document 1111111.md", "timestamp-only index change invalidates cache at equal length");
        DateTime unchangedStamp = File.GetLastWriteTimeUtc(path);
        replacement.Entries.Add(new SvnEntry { Name = "extra.md", Path = "documents/extra.md", Revision = "1", ModifiedUtc = DateTime.UtcNow });
        WriteSnapshot(path, replacement);
        File.SetLastWriteTimeUtc(path, unchangedStamp);
        Assert(store.LoadIndex("first").Entries.Count == 13, "length-only index change invalidates cache at equal timestamp");
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        Assert(store.GetIndexUpdatedUtc("first") == stamp, "fast index timestamp uses file metadata");
        File.WriteAllText(path, "broken json");
        Assert(store.GetIndexUpdatedUtc("first").HasValue, "fast timestamp does not deserialize JSON");
        Expect<InvalidDataException>(delegate { store.LoadIndex("first"); }, "invalid file never returns a stale cached snapshot");
        WriteSnapshot(path, replacement);
        Assert(store.LoadIndex("first").Entries.Count == 13, "invalid file is not cached after repair");
        store.SaveIndex(first);
        Assert(Object.ReferenceEquals(first, store.LoadIndex("first")), "SaveIndex replaces cached snapshot");
        IndexSnapshot second = MakeSnapshot("second", 1);
        IndexSnapshot third = MakeSnapshot("third", 1);
        store.SaveIndex(second);
        store.LoadIndex("first");
        store.SaveIndex(third);
        Assert(Object.ReferenceEquals(first, store.LoadIndex("first")), "two-snapshot LRU retains recently accessed index");
        Assert(!Object.ReferenceEquals(second, store.LoadIndex("second")), "two-snapshot LRU evicts least recently used index");
        store.DeleteIndex("first");
        Assert(store.LoadIndex("first") == null && !store.GetIndexUpdatedUtc("first").HasValue, "DeleteIndex invalidates cache and metadata");
        AppStore concurrent = new AppStore(store.DataDirectory);
        Task<IndexSnapshot>[] reads = Enumerable.Range(0, 6).Select(i => Task.Run(delegate { return concurrent.LoadIndex("second"); })).ToArray();
        Task.WaitAll(reads);
        Assert(reads.All(task => Object.ReferenceEquals(task.Result, reads[0].Result)), "concurrent first loads deserialize once and share snapshot");
    }

    private static async Task TestFilenameSearch()
    {
        AppStore store = new AppStore(Path.Combine(_testRoot, "filename-search"));
        SvnService service = new SvnService(store, Assembly.GetExecutingAssembly().Location);
        FilenameSearchEngine engine = new FilenameSearchEngine(store, service);
        Repository alpha = new Repository { Id = "alpha", Name = "Alpha", Url = "https://example.invalid/alpha" };
        Repository beta = new Repository { Id = "beta", Name = "Beta", Url = "https://example.invalid/beta" };
        List<Repository> repositories = new List<Repository> { beta, alpha };
        Random random = new Random(94817);
        foreach (Repository repository in repositories)
        {
            List<SvnEntry> entries = Enumerable.Range(0, 250).Select(i => new SvnEntry
            {
                Name = "Alpha Plan " + (i % 50).ToString("D2") + ".md", Path = (i % 3 == 0 ? "docs2/" : "docs/") + i.ToString("D4") + ".md",
                Revision = "1", IsDirectory = i % 17 == 0, ModifiedUtc = DateTime.UtcNow
            }).OrderBy(entry => random.Next()).ToList();
            entries.Add(new SvnEntry { Name = "Unrelated.md", Path = "docs/Alpha Plan only in path.md", Revision = "1", ModifiedUtc = DateTime.UtcNow });
            store.SaveIndex(new IndexSnapshot { RepositoryId = repository.Id, UpdatedUtc = DateTime.UtcNow.AddHours(repository == alpha ? -2 : -1), Entries = entries });
        }
        int callerThread = Thread.CurrentThread.ManagedThreadId;
        int workerThread = callerThread;
        using (ManualResetEvent progressEntered = new ManualResetEvent(false))
        using (ManualResetEvent releaseProgress = new ManualResetEvent(false))
        {
            Task<FilenameSearchResult> background = engine.SearchAsync(repositories, "alpha PLAN", "docs", 7, delegate(string message)
            {
                workerThread = Thread.CurrentThread.ManagedThreadId;
                progressEntered.Set();
                releaseProgress.WaitOne(3000);
            }, CancellationToken.None);
            Assert(progressEntered.WaitOne(3000) && !background.IsCompleted, "search returns task while background progress is running");
            releaseProgress.Set();
            FilenameSearchResult result = await background;
            Assert(workerThread != callerThread, "loading and filtering run off caller UI thread");
            List<FilenameSearchMatch> expected = repositories.SelectMany(repository => store.LoadIndex(repository.Id).Entries
                .Where(entry => entry.Name.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) >= 0 && entry.Name.IndexOf("PLAN", StringComparison.OrdinalIgnoreCase) >= 0 && entry.Path.StartsWith("docs/", StringComparison.Ordinal))
                .Select(entry => new FilenameSearchMatch { Repository = repository, Entry = entry }))
                .OrderByDescending(match => match.Entry.IsDirectory).ThenBy(match => match.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(match => match.Repository.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(match => match.Entry.Path, StringComparer.Ordinal)
                .ThenBy(match => match.Repository.Id, StringComparer.Ordinal).ToList();
            Assert(result.TotalCount == expected.Count && result.Matches.Count == 7, "limited result counts all matches without returning all objects");
            Assert(result.Matches.Select(match => match.Repository.Id + ":" + match.Entry.Path).SequenceEqual(expected.Take(7).Select(match => match.Repository.Id + ":" + match.Entry.Path)), "bounded heap matches full global folder/name sort");
            Assert(result.Matches.All(match => match.Entry.Path.StartsWith("docs/", StringComparison.Ordinal)), "scope matches docs descendants but excludes docs2");
            Assert(result.OldestIndexUpdatedUtc == store.LoadIndex(alpha.Id).UpdatedUtc, "oldest index timestamp includes all selected repositories");
            Assert(result.ElapsedMilliseconds >= 0, "search reports elapsed milliseconds");
        }
        FilenameSearchResult noMatch = await engine.SearchAsync(repositories, "only in path", String.Empty, 10, null, CancellationToken.None);
        Assert(noMatch.TotalCount == 0 && noMatch.Matches.Count == 0, "query searches filename not path body");
        FilenameSearchResult limitOne = await engine.SearchAsync(repositories, "alpha alpha plan", "docs/", 1, null, CancellationToken.None);
        Assert(limitOne.TotalCount > 1 && limitOne.Matches.Count == 1 && limitOne.Matches[0].Entry.IsDirectory, "single-result heap duplicate terms trailing scope slash");
        Expect<ArgumentException>(delegate { engine.SearchAsync(repositories, "   ", String.Empty, 1, null, CancellationToken.None); }, "empty search rejected");
        Expect<ArgumentOutOfRangeException>(delegate { engine.SearchAsync(repositories, "x", String.Empty, 0, null, CancellationToken.None); }, "invalid search limit rejected");
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            bool canceled = false;
            try { await engine.SearchAsync(repositories, "alpha", String.Empty, 5, delegate(string message) { if (message.StartsWith("正在搜索", StringComparison.Ordinal)) cancellation.Cancel(); }, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Assert(canceled, "cancellation observed before filtering cached snapshot");
        }
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool canceled = false;
            try { await engine.SearchAsync(repositories, "alpha", String.Empty, 5, null, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Assert(canceled, "pre-canceled search performs no work");
        }
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "ok");
        Repository missing = new Repository { Id = "missing", Name = "Missing", Url = "https://example.invalid/fake-only" };
        FilenameSearchResult built = await engine.SearchAsync(new List<Repository> { missing }, "mock", String.Empty, 5, null, CancellationToken.None);
        int attempts = CountAttempts(store.DataDirectory);
        Assert(built.TotalCount == 1 && attempts == 2, "missing index builds exactly one info/list sequence with fake native client");
        await engine.SearchAsync(new List<Repository> { missing }, "mock", String.Empty, 5, null, CancellationToken.None);
        Assert(CountAttempts(store.DataDirectory) == attempts, "existing index search performs no SVN request");
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", null);
    }

    private static IndexSnapshot MakeSnapshot(string repositoryId, int count)
    {
        return new IndexSnapshot
        {
            RepositoryId = repositoryId, Revision = "1", UpdatedUtc = DateTime.UtcNow,
            Entries = Enumerable.Range(0, count).Select(i => new SvnEntry { Name = "Document " + i.ToString("D7") + ".md", Path = "documents/" + i.ToString("D7") + ".md", Revision = "1", Author = "fixture", IsDirectory = i % 17 == 0, ModifiedUtc = DateTime.UtcNow }).ToList()
        };
    }

    private static void WriteSnapshot(string path, IndexSnapshot snapshot)
    {
        using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) new DataContractJsonSerializer(typeof(IndexSnapshot)).WriteObject(stream, snapshot);
    }

    private static IndexSnapshot ReadSnapshot(string path)
    {
        using (FileStream stream = File.OpenRead(path)) return (IndexSnapshot)new DataContractJsonSerializer(typeof(IndexSnapshot)).ReadObject(stream);
    }

    private static int RunBenchmarks(string[] args)
    {
        try
        {
            string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Environment.CurrentDirectory, "work", "search-benchmark");
            Directory.CreateDirectory(output);
            List<string> rows = new List<string> { "dataset,entries,json_bytes,old_parse_linq_ms,new_cold_ms,new_warm_median_ms,total_matches,returned_matches", "# cold means application snapshot cache only; OS file cache is not cleared; metadata only; no SVN requests" };
            foreach (int size in new[] { 100000, 200000 }) BenchmarkOne(output, "synthetic-" + size, MakeSnapshot("synthetic-" + size, size), "document", rows);
            if (args.Length > 2)
            {
                int number = 0;
                foreach (string path in Directory.GetFiles(Path.GetFullPath(args[2]), "*.json").OrderBy(path => path, StringComparer.Ordinal))
                {
                    IndexSnapshot snapshot = ReadSnapshot(path);
                    BenchmarkOne(output, "real-local-" + (++number), snapshot, ".", rows);
                }
            }
            string report = Path.Combine(output, "benchmark.csv");
            File.WriteAllLines(report, rows, new UTF8Encoding(false));
            foreach (string row in rows) Console.WriteLine(row);
            Console.WriteLine("Benchmark report: " + report);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("Benchmark failed: " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }

    private static void BenchmarkOne(string output, string label, IndexSnapshot source, string query, List<string> rows)
    {
        const int limit = 500;
        AppStore writer = new AppStore(Path.Combine(output, label));
        source.RepositoryId = label;
        writer.SaveIndex(source);
        string path = Path.Combine(writer.IndexDirectory, AppStore.Hash(label) + ".json");
        long bytes = new FileInfo(path).Length;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Stopwatch oldTimer = Stopwatch.StartNew();
        int oldTotal = OldSearch(path, query, limit);
        oldTimer.Stop();
        AppStore reader = new AppStore(writer.DataDirectory);
        Repository repository = new Repository { Id = label, Name = label, Url = "https://example.invalid/local-benchmark-only" };
        FilenameSearchEngine engine = new FilenameSearchEngine(reader, new SvnService(reader, Assembly.GetExecutingAssembly().Location));
        FilenameSearchResult cold = engine.SearchAsync(new List<Repository> { repository }, query, String.Empty, limit, null, CancellationToken.None).GetAwaiter().GetResult();
        long[] warm = new long[3];
        FilenameSearchResult result = cold;
        for (int i = 0; i < warm.Length; i++)
        {
            result = engine.SearchAsync(new List<Repository> { repository }, query, String.Empty, limit, null, CancellationToken.None).GetAwaiter().GetResult();
            warm[i] = result.ElapsedMilliseconds;
        }
        Array.Sort(warm);
        Assert(oldTotal == cold.TotalCount && result.TotalCount == oldTotal, "benchmark count consistency " + label);
        rows.Add(String.Join(",", new[] { label, source.Entries.Count.ToString(), bytes.ToString(), oldTimer.ElapsedMilliseconds.ToString(), cold.ElapsedMilliseconds.ToString(), warm[1].ToString(), result.TotalCount.ToString(), result.Matches.Count.ToString() }));
    }

    private static int OldSearch(string path, string query, int limit)
    {
        IndexSnapshot snapshot = ReadSnapshot(path);
        string[] terms = query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        List<FilenameSearchMatch> matches = snapshot.Entries.Where(entry => terms.All(term => entry.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            .Select(entry => new FilenameSearchMatch { Entry = entry }).ToList();
        List<FilenameSearchMatch> displayed = matches.Take(limit).OrderByDescending(match => match.Entry.IsDirectory).ThenBy(match => match.Entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        GC.KeepAlive(displayed);
        return matches.Count;
    }

    private static async Task TestCertificateCompatibility()
    {
        string root = Path.Combine(_testRoot, "certificate-option-proof");
        AppStore store = new AppStore(root);
        SvnService service = new SvnService(store, Assembly.GetExecutingAssembly().Location);
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", "ok");
        Tuple<string, bool, bool>[] cases =
        {
            Tuple.Create("https://192.0.2.17:8443/private-documents", false, false),
            Tuple.Create("https://192.0.2.17:8443/private-documents", true, true),
            Tuple.Create("https://example.invalid/svn", true, false),
            Tuple.Create("https://192.0.2.17.example.invalid/svn", true, false),
            Tuple.Create("http://192.0.2.17/svn", true, false),
            Tuple.Create("svn://192.0.2.17/repository", true, false),
            Tuple.Create(new Uri(Path.Combine(root, "local-repository") + Path.DirectorySeparatorChar).AbsoluteUri, true, false),
            Tuple.Create("https://[2001:db8::17]:8443/private-documents", true, true)
        };
        foreach (Tuple<string, bool, bool> item in cases)
        {
            Repository repository = new Repository
            {
                Id = "certificate-fixture", Name = "PRIVATE_CERTIFICATE_TEST_NAME", Url = item.Item1,
                AllowIpCertificateNameMismatch = item.Item2
            };
            await service.ListAsync(repository, String.Empty, false, CancellationToken.None);
            string proof = File.ReadAllText(Path.Combine(root, "fake-proof.txt"));
            string expectedFailures = item.Item3 ? "cn-mismatch" : "none";
            int expectedOptions = item.Item3 ? 1 : 0;
            Assert(proof.Contains("trustFailures=" + expectedFailures + ";"), "certificate failure allowlist: " + item.Item1 + " enabled=" + item.Item2);
            Assert(proof.Contains("trustOptions=" + expectedOptions + ";") && proof.Contains("legacyTrust=False;"), "no broad or duplicate certificate trust option: " + item.Item1);
        }
        string[] events = Directory.GetFiles(store.LogDirectory).SelectMany(File.ReadAllLines).Where(line => line.Contains("svn_certificate_name_exception")).ToArray();
        Assert(events.Length == 2, "certificate exception logged only for opted-in HTTPS IP repositories");
        Assert(events.All(line => line.Contains("certificate-fixture") && !line.Contains("private-documents") && !line.Contains("PRIVATE_CERTIFICATE_TEST_NAME")), "certificate logs omit repository names and URL paths");
        Assert(!Directory.EnumerateFiles(store.SvnConfigDirectory, "*", SearchOption.AllDirectories).Any(path => path.IndexOf("svn.ssl.server", StringComparison.OrdinalIgnoreCase) >= 0), "certificate compatibility does not persist global trust");
        Environment.SetEnvironmentVariable("LIGHTSVN_TEST_MODE", null);
    }

    private static int FakeSvn(string[] args)
    {
        string root = Environment.CurrentDirectory;
        File.AppendAllText(Path.Combine(root, "fake-attempts.txt"), "attempt\n");
        string password = Environment.GetEnvironmentVariable("LIGHTSVN_TEST_PASSWORD") ?? String.Empty;
        string input = Console.In.ReadLine();
        int configAt = Array.IndexOf(args, "--config-dir");
        bool config = configAt >= 0 && Path.GetFullPath(args[configAt + 1]).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        int trustAt = Array.IndexOf(args, "--trust-server-cert-failures");
        string trustFailures = trustAt >= 0 && trustAt + 1 < args.Length ? args[trustAt + 1] : "none";
        string proof = "passwordInArguments=" + args.Any(a => a.Contains(password)) + ";stdin=" + (input == password) + ";config=" + config
            + ";temp=" + (Environment.GetEnvironmentVariable("TEMP") == Path.Combine(root, "temp")) + ";noAuthCache=" + args.Contains("--no-auth-cache")
            + ";trustFailures=" + trustFailures + ";trustOptions=" + args.Count(a => a.StartsWith("--trust-server-cert", StringComparison.Ordinal)) + ";legacyTrust=" + args.Contains("--trust-server-cert") + ";";
        File.WriteAllText(Path.Combine(root, "fake-proof.txt"), proof);
        string mode = Environment.GetEnvironmentVariable("LIGHTSVN_TEST_MODE");
        if (mode == "slow") { Thread.Sleep(30000); return 0; }
        if (mode == "fail") { Console.Error.WriteLine("SVN fake auth failure " + password); return 7; }
        if (mode == "dtd") { Console.Write("<!DOCTYPE lists [<!ENTITY data SYSTEM 'file:///C:/Windows/win.ini'>]><lists><list>&data;</list></lists>"); return 0; }
        if (mode == "oversize")
        {
            using (Stream output = Console.OpenStandardOutput())
            {
                byte[] chunk = new byte[65536];
                for (int i = 0; i < 48; i++) output.Write(chunk, 0, chunk.Length);
            }
            return 0;
        }
        if (mode == "partial") { Console.Write("partial"); return 0; }
        if (args[0] == "info") { Console.Write("<?xml version=\"1.0\"?><info><entry kind=\"dir\" revision=\"1\" path=\"mock\"><url>https://example.invalid/mock</url></entry></info>"); return 0; }
        string date = mode == "missing-date" ? String.Empty : "<date>2026-01-01T00:00:00Z</date>";
        Console.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><lists><list path=\"https://example.invalid/\"><entry kind=\"file\"><name>mock.txt</name><size>64</size><commit revision=\"1\"><author>local-test</author>" + date + "</commit></entry></list></lists>");
        return 0;
    }

    private static int CountAttempts(string root) { return File.ReadAllLines(Path.Combine(root, "fake-attempts.txt")).Length; }

    private static void RunLocal(string executable, params string[] arguments)
    {
        List<string> fixtureArguments = arguments.ToList();
        string name = Path.GetFileNameWithoutExtension(executable);
        if (name == "svn" || name == "svnmucc")
        {
            string config = Path.Combine(_testRoot, "fixture-svn-config");
            Directory.CreateDirectory(config);
            fixtureArguments.Add("--config-dir");
            fixtureArguments.Add(config);
        }
        ProcessStartInfo start = new ProcessStartInfo(executable, String.Join(" ", fixtureArguments.Select(Quote)))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = _testRoot
        };
        start.EnvironmentVariables["TEMP"] = _testRoot;
        start.EnvironmentVariables["TMP"] = _testRoot;
        using (Process process = Process.Start(start))
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException("Local fixture command timed out"); }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0) throw new Exception("Local fixture command failed: " + executable + "\n" + stderr.Result + stdout.Result);
        }
    }

    private static string Quote(string text)
    {
        StringBuilder result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in text)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes); slashes = 0; result.Append(character);
        }
        result.Append('\\', slashes * 2); result.Append('"');
        return result.ToString();
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("Assertion failed: " + message);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        bool caught = false;
        try { action(); } catch (T) { caught = true; }
        Assert(caught, message);
    }
}
