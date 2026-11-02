using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LightSvnBrowser
{
    internal static class Program
    {
        private const string DataDirectoryArgument = "--data-dir";
        private const string SmokeArgument = "--smoke-test";
        private const string DataEnvironmentVariable = "LIGHTSVNBROWSER_DATA_DIR";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string dataDirectory = null;
            try
            {
                string requestedDirectory = Environment.GetEnvironmentVariable(DataEnvironmentVariable);
                string smokeOutput = null;
                for (int index = 0; index < args.Length; index++)
                {
                    if (args[index] == DataDirectoryArgument && index + 1 < args.Length)
                        requestedDirectory = args[++index];
                    else if (args[index] == SmokeArgument && index + 1 < args.Length)
                        smokeOutput = args[++index];
                    else
                        throw new ArgumentException("未知启动参数：" + args[index] + "\n可用参数：--data-dir <数据目录>");
                }
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (!String.IsNullOrWhiteSpace(requestedDirectory)) requestedDirectory = Environment.ExpandEnvironmentVariables(requestedDirectory);
                dataDirectory = String.IsNullOrWhiteSpace(requestedDirectory)
                    ? Path.Combine(baseDirectory, "data")
                    : Path.GetFullPath(Path.IsPathRooted(requestedDirectory)
                        ? requestedDirectory
                        : Path.Combine(baseDirectory, requestedDirectory));
                Directory.CreateDirectory(dataDirectory);
                string tempDirectory = Path.Combine(dataDirectory, "temp");
                Directory.CreateDirectory(tempDirectory);
                Environment.SetEnvironmentVariable("TEMP", tempDirectory, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("TMP", tempDirectory, EnvironmentVariableTarget.Process);

                string mutexName;
                using (SHA256 hash = SHA256.Create())
                    mutexName = "Local\\LightSvnBrowser." + BitConverter.ToString(hash.ComputeHash(
                        Encoding.UTF8.GetBytes(Environment.UserName + "|" + dataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()))).Replace("-", "");
                using (Mutex mutex = new Mutex(false, mutexName))
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        MessageBox.Show("这个数据目录已经有一个窗口在使用。请切换到已打开的窗口。", "Light SVN Browser", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    try
                    {
                        AppStore store = new AppStore(dataDirectory);
                        store.Log("application.start", "version=" + Application.ProductVersion + "; data=" + dataDirectory);
                        if (String.IsNullOrWhiteSpace(smokeOutput))
                        {
                            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs error)
                            {
                                store.Log("ui.error", error.Exception.ToString());
                                MessageBox.Show("操作未完成：" + error.Exception.Message + "\n请检查数据目录或查看 logs 中的诊断日志。", "Light SVN Browser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            };
                        }
                        string executable = NativeRuntime.Resolve(dataDirectory, store.Config.SvnExecutable);
                        SvnService service = new SvnService(store, executable);
                        MainForm form = new MainForm(store, service);
                        if (!String.IsNullOrWhiteSpace(smokeOutput))
                        {
                            form.StartPosition = FormStartPosition.Manual;
                            form.Location = new Point(-32000, -32000);
                            form.Shown += delegate
                            {
                                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                                timer.Interval = 400;
                                timer.Tick += delegate
                                {
                                    timer.Stop();
                                    try
                                    {
                                        form.RenderDemo();
                                        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                                        {
                                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                                            bitmap.Save(Path.GetFullPath(smokeOutput), System.Drawing.Imaging.ImageFormat.Png);
                                        }
                                        store.Log("ui.smoke", "render succeeded");
                                    }
                                    catch (Exception error) { store.Log("ui.smoke.error", error.ToString()); Environment.ExitCode = 1; }
                                    finally { timer.Dispose(); form.Close(); }
                                };
                                timer.Start();
                            };
                        }
                        Application.Run(form);
                        store.Log("application.stop", "normal exit");
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                Environment.ExitCode = 1;
                if (Array.IndexOf(args, SmokeArgument) >= 0)
                {
                    if (dataDirectory != null)
                        File.WriteAllText(Path.Combine(dataDirectory, "smoke-error.txt"), error.ToString(), Encoding.UTF8);
                    return;
                }
                MessageBox.Show("启动失败：" + error.Message + "\n\n请检查指定数据目录的写入权限。程序不会改用 C 盘保存数据。", "Light SVN Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
