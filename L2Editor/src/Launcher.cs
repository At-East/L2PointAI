using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace L2EditorLauncher
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string baseDir = Path.GetDirectoryName(Application.ExecutablePath);
            string page = Path.Combine(baseDir, "www", "index.html");

            if (!File.Exists(page))
            {
                MessageBox.Show("缺少 www\\index.html，请保持目录完整。", "点云编辑器");
                return;
            }

            string url = new Uri(page).AbsoluteUri;
            string profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "L2PointCloudEditor", "profile");

            string browser = FindBrowser();
            if (browser != null)
            {
                string arguments =
                    "--app=\"" + url + "\" " +
                    "--window-size=1520,940 " +
                    "--user-data-dir=\"" + profile + "\" " +
                    "--allow-file-access-from-files " +
                    "--no-first-run --no-default-browser-check";

                try
                {
                    Process.Start(new ProcessStartInfo(browser, arguments) { UseShellExecute = false });
                    return;
                }
                catch (Exception) { }
            }

            // 找不到 Chromium 内核浏览器时，退回系统默认浏览器
            try { Process.Start(url); }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开编辑器：\n" + ex.Message, "点云编辑器");
            }
        }

        static string FindBrowser()
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] candidates = new string[]
            {
                Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                Combine(local, @"Google\Chrome\Application\chrome.exe"),
                Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Combine(pf86, @"Google\Chrome\Application\chrome.exe")
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] != null && File.Exists(candidates[i])) return candidates[i];
            }
            return null;
        }

        static string Combine(string root, string rel)
        {
            if (string.IsNullOrEmpty(root)) return null;
            return Path.Combine(root, rel);
        }
    }
}
