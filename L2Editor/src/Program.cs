using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace L2PointCloudEditor
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            try { File.AppendAllText(Path.Combine(dir, "boot.log"), DateTime.Now.ToString("HH:mm:ss") + " start args=" + string.Join(" ", args) + "\r\n"); }
            catch (Exception) { }

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { File.AppendAllText(Path.Combine(dir, "boot.log"), DateTime.Now.ToString("HH:mm:ss") + " CRASH " + e.ExceptionObject + "\r\n"); }
                catch (Exception) { }
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    class MainForm : Form
    {
        private WebView2 web;
        private string baseDir;
        private string[] args;

        public MainForm(string[] a)
        {
            args = a;
            baseDir = Path.GetDirectoryName(Application.ExecutablePath);

            Text = "L2 点云脱轨模拟器 · C70E 敞车";
            Width = 1520;
            Height = 960;
            MinimumSize = new Size(1100, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(7, 11, 17);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            Controls.Add(web);

            Load += delegate { Start(); };
        }

        private bool HasArg(string name)
        {
            return Array.IndexOf(args, name) >= 0;
        }

        private void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(baseDir, "boot.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
            }
            catch (Exception) { }
        }

        private async void Start()
        {
            string www = Path.Combine(baseDir, "www");
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "L2PointCloudEditor");

            Log("start www=" + www);
            if (!Directory.Exists(www))
            {
                Log("missing www");
                MessageBox.Show("缺少 www 目录：" + www, "启动失败");
                Close();
                return;
            }

            try
            {
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, dataDir, null);
                Log("env ok");
                await web.EnsureCoreWebView2Async(env);
                Log("core ok");
                CoreWebView2 core = web.CoreWebView2;

                CoreWebView2Settings s = core.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.IsStatusBarEnabled = false;
                s.AreDevToolsEnabled = HasArg("--devtools");
                s.IsZoomControlEnabled = false;

                core.SetVirtualHostNameToFolderMapping(
                    "l2editor.local", www, CoreWebView2HostResourceAccessKind.Allow);
                Log("mapping ok");

                core.NewWindowRequested += delegate(object o, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                    try { System.Diagnostics.Process.Start(e.Uri); }
                    catch (Exception) { }
                };

                core.NavigationCompleted += OnNav;

                string url = "https://l2editor.local/index.html";
                if (HasArg("--selftest")) url = url + "?view=iso&preset=derail";
                core.Navigate(url);
                Log("navigate " + url);
            }
            catch (Exception ex)
            {
                Log("EX " + ex);
                MessageBox.Show("WebView2 初始化失败：\n\n" + ex.Message +
                    "\n\n请确认已安装 Microsoft Edge WebView2 运行时。", "启动失败");
                Close();
            }
        }

        private async void OnNav(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!HasArg("--selftest")) return;

            string log = Path.Combine(baseDir, "selftest.log");
            if (!e.IsSuccess)
            {
                File.WriteAllText(log, "navigation failed: " + e.WebErrorStatus);
                Close();
                return;
            }

            await Task.Delay(4500);

            string json = "{}";
            try
            {
                json = await web.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify({n:(window.RAIL_CLOUD||{}).n,title:document.title," +
                    "counts:['cWagon','cRail','cGround','cOther'].map(function(i){return document.getElementById(i).textContent;})," +
                    "state:document.getElementById('roState').textContent," +
                    "fps:document.getElementById('hudFps').textContent})");
            }
            catch (Exception ex)
            {
                json = "{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
            }
            File.WriteAllText(log, json);

            try
            {
                using (FileStream fs = File.Create(Path.Combine(baseDir, "selftest.png")))
                {
                    await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                }
            }
            catch (Exception) { }

            Close();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
            }
            catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
