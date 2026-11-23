// Entry point + headless self-test / API test / UI test modes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace L2PointAI
{
    internal static class Program
    {
        static readonly object _logLock = new object();
        static bool _silentErrors;

        /// <summary>Crash / startup log folder (always per-user writable).</summary>
        public static string LogDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "L2PointAI", "logs");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        public static void Log(string tag, string message)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}: {2}{3}",
                DateTime.Now, tag, message, Environment.NewLine);
            lock (_logLock)
            {
                try { File.AppendAllText(Path.Combine(LogDir, "startup.log"), line, Encoding.UTF8); } catch { }
                try
                {
                    string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
                    if (File.Exists(beside) || IsWritable(AppDomain.CurrentDomain.BaseDirectory))
                        File.AppendAllText(beside, line, Encoding.UTF8);
                }
                catch { }
            }
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".w.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Log("UnhandledException", ex != null ? ex.ToString() : Convert.ToString(e.ExceptionObject));
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Log("ThreadException", e.Exception.ToString());
                if (_silentErrors) return;
                MessageBox.Show(
                    "程序发生未处理的界面异常，已写入日志：\n" + Path.Combine(LogDir, "startup.log") +
                    "\n\n" + e.Exception.GetType().Name + ": " + e.Exception.Message +
                    "\n\n" + TopFrames(e.Exception),
                    "L2PointAI 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Log("start", "args=[" + string.Join(" ", args) + "] exe=" + Application.ExecutablePath);

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                if (args.Length > 0)
                {
                    string cmd = args[0].ToLowerInvariant();
                    if (cmd == "--selftest") return SelfTest(args);
                    if (cmd == "--apitest") return ApiTest(args);
                    if (cmd == "--uitest") return UiTest();
                    if (cmd == "--shot") return ShotTest(args);
                    if (cmd == "--capture") return CaptureTest(args);
                    if (cmd == "--bag") return BagTest(args);
                    if (cmd == "--bagshot") return BagShot(args);
                    if (cmd == "--streamtest") return StreamTest(args);
                    if (cmd == "--view") return ViewTest(args);
                    if (cmd == "--seg") return SegTest(args);
                    if (cmd == "--plane") return PlaneTest(args);
                    if (cmd == "--diag") { _silentErrors = true; return DiagTest(args); }
                    if (cmd == "--crashtest") { _silentErrors = true; throw new InvalidOperationException("intentional crash test"); }
                }

                Log("start", "creating MainForm");
                MainForm form = new MainForm();
                Log("start", "MainForm created, entering message loop");
                Application.Run(form);
                Log("exit", "message loop finished");
                return 0;
            }
            catch (Exception ex)
            {
                Log("fatal", ex.ToString());
                if (_silentErrors) { Console.Error.WriteLine(ex.ToString()); return 1; }
                MessageBox.Show(
                    "启动失败：\n" + ex.Message + "\n\n完整信息已写入：\n" + Path.Combine(LogDir, "startup.log"),
                    "L2PointAI 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        /// <summary>Head-less environment diagnostics: writes diag.txt and prints it.</summary>
        static int DiagTest(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("时间        : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Exe         : " + Application.ExecutablePath);
            sb.AppendLine("工作目录    : " + Environment.CurrentDirectory);
            sb.AppendLine("OS          : " + Environment.OSVersion + " 64bit=" + Environment.Is64BitProcess);
            sb.AppendLine("CLR         : " + Environment.Version);
            sb.AppendLine("用户        : " + Environment.UserName);
            sb.AppendLine("屏幕        : " + (System.Windows.Forms.Screen.PrimaryScreen == null ? "none" :
                                                    System.Windows.Forms.Screen.PrimaryScreen.Bounds.ToString()));
            sb.AppendLine(".NET 4.8    : " + (Type.GetType("System.AppContext") != null));
            try { sb.AppendLine("数据目录    : " + AppSettings.Dir + "  portable=" + AppSettings.IsPortable); }
            catch (Exception ex) { sb.AppendLine("数据目录    : 失败 " + ex.Message); }
            try
            {
                AppSettings cfg = AppSettings.Load();
                sb.AppendLine("配置读取    : OK  backend=" + cfg.Backend + "  model=" + cfg.OllamaModel);
                cfg.Save();
                sb.AppendLine("配置写入    : OK");
            }
            catch (Exception ex) { sb.AppendLine("配置        : 失败 " + ex.Message); }
            try
            {
                List<PromptTemplate> t = PromptTemplates.Load();
                sb.AppendLine("提示词模板  : OK x" + t.Count);
            }
            catch (Exception ex) { sb.AppendLine("提示词模板  : 失败 " + ex.Message); }
            try
            {
                PointCloud pc = SyntheticRoom();
                sb.AppendLine("点云生成    : OK " + pc.Count + " 点");
                RenderOptions o = new RenderOptions();
                Dictionary<string, Bitmap> v = CloudRenderer.RenderThreeViews(pc, o, 640, 480);
                sb.AppendLine("三视图渲染  : OK x" + v.Count);
            }
            catch (Exception ex) { sb.AppendLine("渲染        : 失败 " + ex); }
            try
            {
                Log("diag", "creating MainForm");
                MainForm f = new MainForm();
                f.CreateControl();
                sb.AppendLine("主窗体构造  : OK");
                f.Dispose();
            }
            catch (Exception ex) { sb.AppendLine("主窗体构造  : 失败 " + ex); }

            string text = sb.ToString();
            Console.WriteLine(text);
            try { File.WriteAllText(Path.Combine(LogDir, "diag.txt"), text, Encoding.UTF8); } catch { }
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag.txt"), text, Encoding.UTF8); } catch { }
            return 0;
        }

        static int UiTest()
        {            MainForm f = new MainForm();
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1500;
            t.Tick += delegate { t.Stop(); f.Close(); };
            t.Start();
            Application.Run(f);
            return 0;
        }

        static int ShotTest(string[] args)
        {
            string outPng = args.Length > 1
                ? args[1]
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot.png");
            MainForm f = new MainForm();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            if (args.Length > 2)
            {
                if (args[2].ToLowerInvariant() == "api") f.DebugSelectBackend(false);
                else if (args[2].ToLowerInvariant() == "ollama") f.DebugSelectBackend(true);
            }
            if (args.Length > 3) f.DebugLoadCloud(args[3]);
            if (args.Length > 6)
            {
                float sc;
                if (float.TryParse(args[6], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out sc) && sc > 1.0f)
                {
                    f.Scale(new SizeF(sc, sc));
                }
            }
            f.Show();
            if (args.Length > 8)
            {
                int w2, h2;
                if (int.TryParse(args[7], out w2) && int.TryParse(args[8], out h2) && w2 > 400 && h2 > 300)
                    f.Size = new Size(w2, h2);
            }
            if (args.Length > 4)
            {
                int tab;
                if (int.TryParse(args[4], out tab)) f.DebugSelectTab(tab);
            }
            if (args.Length > 5 && args[5] == "seg")
            {
                f.DebugSegmentNow();
                f.DebugSelectTab(4);
                Application.DoEvents();
                try
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_segtest");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "prompt.txt"), f.DebugBuildPrompt(), Encoding.UTF8);
                }
                catch { }
            }
            Application.DoEvents();
            System.Threading.Thread.Sleep(400);
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    try { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(outPng)); } catch { }
                    File.WriteAllBytes(outPng, ms.ToArray());
                }
            }
            f.Close();
            return 0;
        }

        // ------------------------------------------------------------ selftest
        static int SelfTest(string[] args)
        {
            string src = args.Length > 1 && args[1] != "-" ? args[1] : null;
            string outDir = args.Length > 2 ? args[2] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_selftest");
            Directory.CreateDirectory(outDir);
            StringBuilder log = new StringBuilder();
            bool ok = true;

            // 1) protocol parser
            byte[] pkt = BuildSyntheticPacket(1500, 8);
            float[] xs, ys, zs, iv;
            bool okProto = L2Protocol.Parse(pkt, out xs, out ys, out zs, out iv) && xs.Length == 8;
            log.AppendLine("protocol parse: " + (okProto ? "OK" : "FAIL") + "  points=" + (xs == null ? 0 : xs.Length));
            ok &= okProto;

            // 2) cloud (given file or synthetic room)
            PointCloud pc;
            if (src != null && File.Exists(src)) pc = PointCloud.Load(src);
            else pc = SyntheticRoom();
            log.AppendLine("cloud: " + pc.Count + " points, size " + pc.SizeX.ToString("F2") + " x " +
                           pc.SizeY.ToString("F2") + " x " + pc.SizeZ.ToString("F2") + " m");
            ok &= pc.Count > 100;

            // 3) csv roundtrip
            string csv = Path.Combine(outDir, "roundtrip.csv");
            pc.SaveCsv(csv);
            PointCloud back = PointCloud.Load(csv);
            bool okCsv = Math.Abs(back.Count - pc.Count) <= 1 && back.Count > 100;
            log.AppendLine("csv roundtrip: " + (okCsv ? "OK" : "FAIL") + " (" + back.Count + " points)");
            ok &= okCsv;

            // 4) three views
            RenderOptions opt = new RenderOptions();
            int w = 800, h = 600;
            Dictionary<string, Bitmap> views = CloudRenderer.RenderThreeViews(pc, opt, w, h);
            foreach (KeyValuePair<string, Bitmap> kv in views)
            {
                string p = Path.Combine(outDir, "view_" + kv.Key + ".png");
                kv.Value.Save(p, ImageFormat.Png);
                log.AppendLine("view " + kv.Key + ": " + new FileInfo(p).Length + " bytes");
            }
            ok &= views.Count == 3 && new FileInfo(Path.Combine(outDir, "view_iso.png")).Length > 1000;

            // 5) stats + report skeleton
            File.WriteAllText(Path.Combine(outDir, "stats.txt"), pc.StatsText(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"), log.ToString(), new UTF8Encoding(false));

            // 6) settings roundtrip (no key)
            AppSettings cfg = AppSettings.Load();
            cfg.Save();
            log.AppendLine("settings saved: " + File.Exists(AppSettings.SettingsPath));
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"), log.ToString(), new UTF8Encoding(false));

            Console.WriteLine(log.ToString());
            return ok ? 0 : 1;
        }

        static PointCloud SyntheticRoom()
        {
            List<float> x = new List<float>(), y = new List<float>(), z = new List<float>(), iv = new List<float>();
            float sx = 6.0f, sy = 5.0f, hz = 2.6f;
            for (float px = 0; px <= sx; px += 0.05f)
                for (float py = 0; py <= sy; py += 0.05f)
                {
                    x.Add(px); y.Add(py); z.Add(0); iv.Add(120 + 40 * (float)Math.Sin(px));
                }
            for (float pz = 0; pz <= hz; pz += 0.05f)
            {
                for (float px = 0; px <= sx; px += 0.05f)
                {
                    x.Add(px); y.Add(0); z.Add(pz); iv.Add(200);
                    x.Add(px); y.Add(sy); z.Add(pz); iv.Add(200);
                }
                for (float py = 0; py <= sy; py += 0.05f)
                {
                    x.Add(0); y.Add(py); z.Add(pz); iv.Add(180);
                    x.Add(sx); y.Add(py); z.Add(pz); iv.Add(180);
                }
            }
            // a box on the floor
            float bx = 1.2f, by = 1.1f, bz = 0.85f;
            for (float px = 0; px <= bx; px += 0.03f)
                for (float py = 0; py <= by; py += 0.03f)
                {
                    x.Add(3.0f + px); y.Add(2.0f + py); z.Add(bz); iv.Add(240);
                }
            for (float pz = 0; pz <= bz; pz += 0.03f)
                for (float px = 0; px <= bx; px += 0.03f)
                {
                    x.Add(3.0f + px); y.Add(2.0f); z.Add(pz); iv.Add(230);
                    x.Add(3.0f + px); y.Add(2.0f + by); z.Add(pz); iv.Add(230);
                }
            return PointCloud.FromArrays("synthetic room 6x5x2.6", x.ToArray(), y.ToArray(), z.ToArray(), iv.ToArray());
        }

        static byte[] BuildSyntheticPacket(int distMm, int n)
        {
            byte[] b = new byte[1044];
            b[0] = 0x55; b[1] = 0xAA; b[2] = 0x05; b[3] = 0x0A;
            Array.Copy(BitConverter.GetBytes(102), 0, b, 4, 4);
            Array.Copy(BitConverter.GetBytes(1044), 0, b, 8, 4);
            // DataInfo at 12, inside state at 28 (left zero)
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 64, 4);      // a_axis_dist
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 68, 4);      // b_axis_dist
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 72, 4);      // theta bias
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 76, 4);      // alpha bias
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 80, 4);      // beta
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 84, 4);      // xi
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 88, 4);      // range bias
            Array.Copy(BitConverter.GetBytes(0.001f), 0, b, 92, 4);  // range scale mm -> m
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 96, 4);      // h angle start
            Array.Copy(BitConverter.GetBytes(0.003f), 0, b, 100, 4); // h angle step
            Array.Copy(BitConverter.GetBytes(0.1f), 0, b, 104, 4);   // scan period
            Array.Copy(BitConverter.GetBytes(0.05f), 0, b, 108, 4);  // range min
            Array.Copy(BitConverter.GetBytes(30f), 0, b, 112, 4);    // range max
            Array.Copy(BitConverter.GetBytes(0f), 0, b, 116, 4);     // angle min
            Array.Copy(BitConverter.GetBytes(0.0174533f), 0, b, 120, 4); // angle increment (1 deg)
            Array.Copy(BitConverter.GetBytes(0.00001f), 0, b, 124, 4);// time increment
            Array.Copy(BitConverter.GetBytes(n), 0, b, 128, 4);
            for (int k = 0; k < n; k++)
            {
                Array.Copy(BitConverter.GetBytes((ushort)distMm), 0, b, 132 + k * 2, 2);
                b[732 + k] = 42;
            }
            b[1042] = 0x00; b[1043] = 0xFF;
            return b;
        }

        // ------------------------------------------------------------ bagexit

        /// <summary>Head-less bag check: --bag &lt;path&gt; [frames] [topic]</summary>
        static int BagTest(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: --bag <file.bag> [frames] [topic]"); return 2; }
            string path = args[1];
            int frames = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1;
            string topic = args.Length > 3 ? args[3] : null;
            StringBuilder sb = new StringBuilder();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (BagReader r = new BagReader(path))
                {
                    BagInfo info = r.Scan();
                    sb.AppendLine("bag        : " + path);
                    sb.AppendLine("chunks     : " + info.ChunkCount + "  compression=" + info.Compression);
                    sb.AppendLine("topics     :");
                    foreach (BagTopic t in info.Topics)
                        sb.AppendLine(string.Format("  {0,-22} {1,-28} msgs={2}", t.Topic, t.Type, t.Messages));
                    sb.AppendLine("scan time  : " + sw.ElapsedMilliseconds + " ms");

                    if (topic == null)
                    {
                        BagTopic first = info.FirstCloudTopic();
                        if (first == null) { sb.AppendLine("没有 PointCloud2 话题"); WriteBagLog(sb); return 1; }
                        topic = first.Topic;
                    }
                    sw.Restart();
                    PointCloud pc = r.Load(topic, 0, frames, null);
                    sb.AppendLine(string.Format("loaded     : topic={0} frames={1} points={2} in {3} ms",
                        topic, frames, pc.Count, sw.ElapsedMilliseconds));
                    sb.AppendLine(pc.StatsText());

                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_bagtest");
                    Directory.CreateDirectory(dir);
                    pc.SaveCsv(Path.Combine(dir, "cloud.csv"));
                    RenderOptions opt = new RenderOptions();
                    Dictionary<string, Bitmap> views = CloudRenderer.RenderThreeViews(pc, opt, 800, 600);
                    foreach (KeyValuePair<string, Bitmap> kv in views)
                        kv.Value.Save(Path.Combine(dir, "view_" + kv.Key + ".png"), ImageFormat.Png);
                    sb.AppendLine("output     : " + dir);
                }
                WriteBagLog(sb);
                Console.WriteLine(sb.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex);
                WriteBagLog(sb);
                Console.WriteLine(sb.ToString());
                return 1;
            }
        }



        /// <summary>Head-less segmentation test: --seg &lt;cloud|bag&gt; [voxel] [minPts]</summary>

        static int PlaneTest(string[] args)
        {
            if (args.Length < 2) return 2;
            PointCloud pc = PointCloud.Load(args[1]);
            double rad = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 0.4;
            double cx, cy, cz, nx, ny, nz;
            int used;
            bool ok = PlaneFit.Around(pc, pc.Count / 2, rad, out cx, out cy, out cz, out nx, out ny, out nz, out used);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("cloud: " + pc.Count + " points, radius " + rad);
            if (!ok) sb.AppendLine("plane fit FAILED");
            else
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "plane: point ({0:F3},{1:F3},{2:F3})  normal ({3:F4},{4:F4},{5:F4})  neighbours {6}",
                    cx, cy, cz, nx, ny, nz, used));
                // residual check: distance of a few points from the plane
                double worst = 0;
                for (int i = 0; i < pc.Count; i += Math.Max(1, pc.Count / 200))
                {
                    double d = Math.Abs((pc.X[i] - cx) * nx + (pc.Y[i] - cy) * ny + (pc.Z[i] - cz) * nz);
                    if (d > worst) worst = d;
                }
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "max |distance| sampled: {0:F4} m", worst));
            }
            WriteBagLog(sb);
            Console.WriteLine(sb.ToString());
            return ok ? 0 : 1;
        }
        static int SegTest(string[] args)
        {
            if (args.Length < 2) return 2;
            string path = args[1];
            double voxel = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 0.2;
            int minPts = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 30;
            int frames = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 1;
            StringBuilder sb = new StringBuilder();
            try
            {
                PointCloud pc;
                if (path.EndsWith(".bag", StringComparison.OrdinalIgnoreCase))
                {
                    using (BagReader r = new BagReader(path))
                    {
                        BagInfo info = r.Scan();
                        BagTopic t0 = info.FirstCloudTopic();
                        if (t0 == null) { sb.AppendLine("no PointCloud2 topic"); WriteBagLog(sb); return 1; }
                        pc = r.Load(t0.Topic, 0, frames, null);
                    }
                }
                else pc = PointCloud.Load(path);

                sb.AppendLine("cloud: " + pc.Count + " points");
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                int[] ids;
                List<ClusterInfo> list = CloudSegmenter.Segment(pc, voxel, minPts, out ids);
                sw.Stop();
                sb.AppendLine(string.Format("segmentation: {0} clusters, voxel {1} m, minPts {2}, {3} ms",
                    list.Count, voxel, minPts, sw.ElapsedMilliseconds));
                int shown = 0;
                foreach (ClusterInfo c in list)
                {
                    sb.AppendLine("  " + c.Short());
                    if (++shown >= 12) break;
                }
                int noise = 0;
                for (int i = 0; i < ids.Length; i++) if (ids[i] < 0) noise++;
                sb.AppendLine("noise/small points: " + noise + " (" + (100.0 * noise / pc.Count).ToString("F1") + "%)");

                // highlight the biggest cluster and render one view
                if (list.Count > 0)
                {
                    pc.Cluster = ids;
                    pc.SelectCluster(0);
                    RenderOptions opt = new RenderOptions();
                    Camera cam = new Camera();
                    CloudRenderer.Fit(pc, cam);
                    int drawn;
                    Bitmap bmp = CloudRenderer.Render(pc, cam, 800, 600, opt, out drawn);
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_segtest");
                    Directory.CreateDirectory(dir);
                    bmp.Save(Path.Combine(dir, "cluster0.png"), ImageFormat.Png);
                    sb.AppendLine("highlight render: " + Path.Combine(dir, "cluster0.png"));
                }
                WriteBagLog(sb);
                Console.WriteLine(sb.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex);
                WriteBagLog(sb);
                return 1;
            }
        }

        /// <summary>--streamtest &lt;chunks&gt; &lt;chunkLen&gt; : stress the streaming UI update path.</summary>

        /// <summary>--view &lt;cloud&gt; &lt;out.png&gt; &lt;yawDeg&gt; &lt;pitchDeg&gt; [tx ty tz dist] [w h]</summary>
        static int ViewTest(string[] args)
        {
            if (args.Length < 5) return 2;
            PointCloud pc = PointCloud.Load(args[1]);
            Camera cam = new Camera();
            cam.Yaw = double.Parse(args[3], CultureInfo.InvariantCulture) * Math.PI / 180.0;
            cam.Pitch = double.Parse(args[4], CultureInfo.InvariantCulture) * Math.PI / 180.0;
            CloudRenderer.Fit(pc, cam);
            if (args.Length > 8)
            {
                cam.Tx = double.Parse(args[5], CultureInfo.InvariantCulture);
                cam.Ty = double.Parse(args[6], CultureInfo.InvariantCulture);
                cam.Tz = double.Parse(args[7], CultureInfo.InvariantCulture);
                cam.Dist = double.Parse(args[8], CultureInfo.InvariantCulture);
            }
            int w = 1600, h = 1000;
            if (args.Length > 10)
            {
                w = int.Parse(args[9], CultureInfo.InvariantCulture);
                h = int.Parse(args[10], CultureInfo.InvariantCulture);
            }
            RenderOptions opt = new RenderOptions();
            opt.ShowGrid = false;
            opt.ShowAxes = true;
            opt.PointSize = 4;
            opt.MaxPoints = 2000000;
            int drawn;
            Bitmap bmp = CloudRenderer.Render(pc, cam, w, h, opt, out drawn);
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                File.WriteAllBytes(args[2], ms.ToArray());
            }
            Console.WriteLine("rendered " + drawn + " points -> " + args[2]);
            return 0;
        }
        static int StreamTest(string[] args)
        {
            int chunks = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 300;
            int len = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 2000;
            MainForm f = new MainForm();
            f.Show();
            Application.DoEvents();
            StringBuilder err = new StringBuilder();
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                err.AppendLine(e.Exception.ToString());
            };
            f.DebugFakeStream(chunks, len);
            DateTime t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < 20)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(10);
                if (err.Length > 0) break;
            }
            f.Close();
            string result = err.Length == 0
                ? "streamtest PASSED (" + chunks + " x " + len + " chars)"
                : "streamtest FAILED:\n" + err;
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "streamtest.txt"), result, Encoding.UTF8); } catch { }
            Console.WriteLine(result);
            return err.Length == 0 ? 0 : 1;
        }
        static int BagShot(string[] args)
        {
            if (args.Length < 3) return 2;
            using (BagImportForm f = new BagImportForm(args[1]))
            {
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        File.WriteAllBytes(args[2], ms.ToArray());
                    }
                }
                f.Close();
            }
            return 0;
        }

        static string TopFrames(Exception ex)
        {
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(ex, true);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < st.FrameCount && i < 4; i++)
                {
                    System.Diagnostics.StackFrame fr = st.GetFrame(i);
                    if (fr == null) continue;
                    System.Reflection.MethodBase mb = fr.GetMethod();
                    if (mb == null || mb.DeclaringType == null) continue;
                    sb.AppendLine(string.Format("at {0}.{1}  行 {2}",
                        mb.DeclaringType.Name, mb.Name, fr.GetFileLineNumber()));
                }
                return sb.ToString();
            }
            catch { return ""; }
        }
        static void WriteBagLog(StringBuilder sb)
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bag_test.txt"), sb.ToString(), Encoding.UTF8); } catch { }
        }
        // ------------------------------------------------------------ apitest
        static int ApiTest(string[] args)
        {
            if (args.Length < 5)
            {
                Console.WriteLine("usage: --apitest <ollama|openai> <baseUrl> <model> <outFile> [prompt]");
                return 2;
            }
            AiRequest r = new AiRequest();
            r.Backend = args[1].ToLowerInvariant() == "ollama" ? AiBackend.Ollama : AiBackend.OpenAi;
            r.BaseUrl = args[2];
            r.Model = args[3];
            string outFile = args[4];
            r.ApiKey = Environment.GetEnvironmentVariable("L2_API_KEY");
            r.UserPrompt = args.Length > 5 ? args[5] : "请只回复两个字：收到。";
            r.SystemPrompt = "你是测试助手。";
            r.Temperature = 0.2;
            r.MaxTokens = 64;
            if (args.Length > 6)
            {
                string mode = args[6].ToLowerInvariant();
                if (mode == "on") r.Thinking = ThinkMode.On;
                else if (mode == "auto") r.Thinking = ThinkMode.Auto;
                else r.Thinking = ThinkMode.Off;
                if (r.Thinking != ThinkMode.Off) r.MaxTokens = 1500;
            }

            StringBuilder sb = new StringBuilder();
            StringBuilder think = new StringBuilder();
            r.OnThinkingDelta = delegate (string th) { lock (think) think.Append(th); };
            string err = "";
            ManualResetEvent done = new ManualResetEvent(false);
            AiClient c = new AiClient();
            c.Start(r,
                delegate (string d) { lock (sb) sb.Append(d); },
                delegate (string full) { lock (sb) sb.Append("\n[DONE]"); done.Set(); },
                delegate (string e) { err = e; done.Set(); });
            if (!done.WaitOne(90000)) err = "timeout";
            string text = sb.ToString();
            string thinkingText;
            lock (think) thinkingText = think.ToString();
            File.WriteAllText(outFile,
                "text:\n" + text + "\n\nthinking (" + thinkingText.Length + " chars):\n" +
                (thinkingText.Length > 400 ? thinkingText.Substring(0, 400) + "\n...(truncated)" : thinkingText) +
                "\n\nerror:\n" + err, new UTF8Encoding(false));
            Console.WriteLine("text length=" + text.Length + " thinking=" + thinkingText.Length + " error=" + err);
            return err.Length == 0 && text.Length > 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ capture
        static int CaptureTest(string[] args)
        {
            // --capture <seconds> <port> <outCsv>
            double sec = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 3;
            int port = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 6201;
            string outCsv = args.Length > 3 ? args[3] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_capture.csv");
            using (LiveCapture cap = new LiveCapture())
            {
                if (!cap.Start(port))
                {
                    Console.WriteLine("start failed: " + cap.LastError);
                    return 2;
                }
                DateTime t0 = DateTime.Now;
                while ((DateTime.Now - t0).TotalSeconds < sec)
                {
                    Thread.Sleep(100);
                    if (cap.BadPackets > 50 && cap.PacketCount == 0) break;
                }
                PointCloud pc = cap.Snapshot(0);
                cap.Stop();
                pc.SaveCsv(outCsv);
                Console.WriteLine("packets=" + cap.PacketCount + " points=" + pc.Count + " -> " + outCsv);
                return pc.Count > 0 ? 0 : 1;
            }
        }
    }
}
