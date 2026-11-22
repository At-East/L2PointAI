// Settings (JSON + DPAPI-protected API key), prompt templates and local history.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace L2PointAI
{
    public sealed class PromptTemplate
    {
        public string Name = "";
        public string Text = "";
    }

    public sealed class AppSettings
    {
        public AiBackend Backend = AiBackend.Ollama;
        public string OllamaUrl = "http://127.0.0.1:11434";
        public string OllamaModel = "";
        public string ApiBase = "https://api.openai.com/v1";
        public string ApiModel = "gpt-4o-mini";
        public string ApiKeyProtected = "";
        public double Temperature = 0.3;
        public int MaxTokens = 2048;
        /// <summary>0=off, 1=auto, 2=on (see L2PointAI.ThinkMode).</summary>
        public int ThinkMode = 0;
        /// <summary>Segmentation: voxel size in meters.</summary>
        public double SegVoxel = 0.2;
        /// <summary>Segmentation: minimum points per cluster.</summary>
        public int SegMinPoints = 30;
        /// <summary>Box selection: only front-most (visible) points.</summary>
        public bool DepthTest = true;
        /// <summary>0=3D, 1=horizontal, 2=point-to-plane.</summary>
        public int MeasureKind = 0;
        public string Prompt = "";
        public int ColorMode = 0;
        public int PointSize = 2;
        public int Background = 0;
        public bool ShowGrid = true;
        public bool ShowAxes = true;
        public int CapturePort = 6201;
        public int ViewSize = 900;
        public int MaxViewPoints = 250000;

        static AppSettings _current;
        static string _dataDir;

        /// <summary>
        /// Writable data folder. Portable mode (next to the exe) when that folder is writable,
        /// otherwise %LOCALAPPDATA%\L2PointAI so the app also works from Program Files.
        /// </summary>
        public static string Dir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                if (CanWrite(exeDir))
                {
                    _dataDir = exeDir;
                }
                else
                {
                    // installed in a read-only place (e.g. Program Files): keep data per-user
                    try
                    {
                        string local = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "L2PointAI");
                        Directory.CreateDirectory(local);
                        if (CanWrite(local)) { _dataDir = local; return _dataDir; }
                    }
                    catch { }
                    _dataDir = exeDir; // last resort: stay usable even if nothing is writable
                }
                return _dataDir;
            }
        }

        public static bool IsPortable
        {
            get { return string.Equals(Dir, AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase); }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".l2write.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string SettingsPath { get { return Path.Combine(Dir, "L2PointAI.settings.json"); } }
        public static string PromptsPath { get { return Path.Combine(Dir, "prompts.json"); } }
        public static string HistoryDir { get { return Path.Combine(Dir, "history"); } }
        public static string ExportsDir { get { return Path.Combine(Dir, "exports"); } }

        public static AppSettings Load()
        {
            if (_current != null) return _current;
            AppSettings s = new AppSettings();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    Dictionary<string, object> m = Json.Obj(Json.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8)));
                    s.Backend = (AiBackend)(int)Json.D(m.ContainsKey("backend") ? m["backend"] : null, 0);
                    s.OllamaUrl = Json.S(m.ContainsKey("ollamaUrl") ? m["ollamaUrl"] : null, s.OllamaUrl);
                    s.OllamaModel = Json.S(m.ContainsKey("ollamaModel") ? m["ollamaModel"] : null, s.OllamaModel);
                    s.ApiBase = Json.S(m.ContainsKey("apiBase") ? m["apiBase"] : null, s.ApiBase);
                    s.ApiModel = Json.S(m.ContainsKey("apiModel") ? m["apiModel"] : null, s.ApiModel);
                    s.ApiKeyProtected = Json.S(m.ContainsKey("apiKey") ? m["apiKey"] : null, "");
                    s.Temperature = Json.D(m.ContainsKey("temperature") ? m["temperature"] : null, s.Temperature);
                    s.MaxTokens = (int)Json.D(m.ContainsKey("maxTokens") ? m["maxTokens"] : null, s.MaxTokens);
                    s.SegVoxel = Json.D(m.ContainsKey("segVoxel") ? m["segVoxel"] : null, 0.2);
                    s.SegMinPoints = (int)Json.D(m.ContainsKey("segMinPoints") ? m["segMinPoints"] : null, 30);
                    s.DepthTest = Json.B(m.ContainsKey("depthTest") ? m["depthTest"] : null, true);
                    s.MeasureKind = (int)Json.D(m.ContainsKey("measureKind") ? m["measureKind"] : null, 0);
                    if (m.ContainsKey("thinkMode"))
                        s.ThinkMode = (int)Json.D(m["thinkMode"], 0);
                    else if (m.ContainsKey("disableThinking"))
                        s.ThinkMode = Json.B(m["disableThinking"], true) ? 0 : 2;
                    s.Prompt = Json.S(m.ContainsKey("prompt") ? m["prompt"] : null, s.Prompt);
                    s.ColorMode = (int)Json.D(m.ContainsKey("colorMode") ? m["colorMode"] : null, 0);
                    s.PointSize = (int)Json.D(m.ContainsKey("pointSize") ? m["pointSize"] : null, 2);
                    s.Background = (int)Json.D(m.ContainsKey("background") ? m["background"] : null, 0);
                    s.ShowGrid = Json.B(m.ContainsKey("showGrid") ? m["showGrid"] : null, true);
                    s.ShowAxes = Json.B(m.ContainsKey("showAxes") ? m["showAxes"] : null, true);
                    s.CapturePort = (int)Json.D(m.ContainsKey("capturePort") ? m["capturePort"] : null, 6201);
                    s.ViewSize = (int)Json.D(m.ContainsKey("viewSize") ? m["viewSize"] : null, 900);
                    s.MaxViewPoints = (int)Json.D(m.ContainsKey("maxViewPoints") ? m["maxViewPoints"] : null, 250000);
                }
            }
            catch { }
            if (string.IsNullOrEmpty(s.Prompt)) s.Prompt = PromptTemplates.DefaultText();
            _current = s;
            return s;
        }

        public void Save()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["backend"] = (int)Backend;
            m["ollamaUrl"] = OllamaUrl;
            m["ollamaModel"] = OllamaModel;
            m["apiBase"] = ApiBase;
            m["apiModel"] = ApiModel;
            m["apiKey"] = ApiKeyProtected;
            m["temperature"] = Temperature;
            m["maxTokens"] = MaxTokens;
            m["thinkMode"] = ThinkMode;
            m["segVoxel"] = SegVoxel;
            m["segMinPoints"] = SegMinPoints;
            m["depthTest"] = DepthTest;
            m["measureKind"] = MeasureKind;
            m["prompt"] = Prompt;
            m["colorMode"] = ColorMode;
            m["pointSize"] = PointSize;
            m["background"] = Background;
            m["showGrid"] = ShowGrid;
            m["showAxes"] = ShowAxes;
            m["capturePort"] = CapturePort;
            m["viewSize"] = ViewSize;
            m["maxViewPoints"] = MaxViewPoints;
            try { File.WriteAllText(SettingsPath, Json.Stringify(m), new UTF8Encoding(false)); } catch { }
        }

        // -------------------------------------------------------------- API key
        public string GetApiKey()
        {
            if (string.IsNullOrEmpty(ApiKeyProtected)) return "";
            try
            {
                if (ApiKeyProtected.StartsWith("plain:", StringComparison.Ordinal))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(ApiKeyProtected.Substring(6)));
                byte[] enc = Convert.FromBase64String(ApiKeyProtected);
                byte[] raw = ProtectedData.Unprotect(enc, Encoding.UTF8.GetBytes("L2PointAI"), DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return ""; }
        }

        public void SetApiKey(string key)
        {
            if (string.IsNullOrEmpty(key)) { ApiKeyProtected = ""; return; }
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(key);
                byte[] enc = ProtectedData.Protect(raw, Encoding.UTF8.GetBytes("L2PointAI"), DataProtectionScope.CurrentUser);
                ApiKeyProtected = Convert.ToBase64String(enc);
            }
            catch
            {
                ApiKeyProtected = "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
            }
        }

        public bool HasApiKey { get { return !string.IsNullOrEmpty(GetApiKey()); } }
    }

    public static class PromptTemplates
    {
        public static string DefaultText()
        {
            return
"你是经验丰富的 3D 点云分析与机器人感知工程师。下面是从 Unitree 4D LiDAR L2 导出的点云统计与三视图（等轴测 ISO / 俯视 TOP / 正视 FRONT，坐标系：原点在雷达底部中心，+X 指向出线口反方向，+Y 为 +X 逆时针 90°，+Z 垂直向上，单位米）。\n\n" +
"请完成：\n" +
"1) 描述场景：能看出哪些物体/结构（墙、地面、箱子、人、通道、障碍等），给出它们的相对位置与大致尺寸（用统计里的数值推算）。\n" +
"2) 质量评估：点数、覆盖范围、是否有明显空洞/遮挡/噪声/地面倾斜。\n" +
"3) 若用于机器人导航或避障，指出风险区域（狭窄处、低矮障碍、悬空物）。\n" +
"4) 给出 2-4 条可执行建议。\n\n" +
"输出要求：先用中文自然语言写结论（分小标题、不要空话），然后在最后附一个 ```json 代码块，字段为：\n" +
"{\"summary\": \"一句话总结\", \"objects\": [{\"name\": \"\", \"position\": \"\", \"size_m\": \"\", \"confidence\": 0.0}], \"defects\": [{\"type\": \"\", \"where\": \"\", \"severity\": \"low|medium|high\"}], \"metrics\": {\"point_count\": 0, \"extent_m\": \"\"}, \"confidence\": 0.0, \"suggestions\": [\"\"]}\n" +
"不要编造看不到的细节；不确定就写“无法判断”。";
        }

        public static List<PromptTemplate> Load()
        {
            List<PromptTemplate> list = new List<PromptTemplate>();
            try
            {
                if (File.Exists(AppSettings.PromptsPath))
                {
                    List<object> arr = Json.Arr(Json.Parse(File.ReadAllText(AppSettings.PromptsPath, Encoding.UTF8)));
                    foreach (object o in arr)
                    {
                        Dictionary<string, object> m = Json.Obj(o);
                        PromptTemplate t = new PromptTemplate();
                        t.Name = Json.S(m.ContainsKey("name") ? m["name"] : null, "未命名");
                        t.Text = Json.S(m.ContainsKey("text") ? m["text"] : null, "");
                        if (t.Text.Length > 0) list.Add(t);
                    }
                }
            }
            catch { }
            if (list.Count == 0)
            {
                list.Add(new PromptTemplate { Name = "通用场景分析（默认）", Text = DefaultText() });
                list.Add(new PromptTemplate
                {
                    Name = "工业巡检 / 缺件异物",
                    Text =
"你是工业巡检专家。这是仓库/车间区域的三维点云三视图与统计。\n" +
"请检查：是否有缺件、异物、堆叠不稳、货物越界、通道被占用、地面杂物等异常；对每个疑似异常给出位置（用米，参考统计里的包围盒范围）与严重程度。\n" +
"最后附 ```json 代码块：{\"summary\":\"\",\"objects\":[],\"defects\":[{\"type\":\"\",\"where\":\"\",\"severity\":\"\"}],\"metrics\":{},\"confidence\":0.0,\"suggestions\":[]}。看不到就说无法判断。"
                });
                list.Add(new PromptTemplate
                {
                    Name = "建图 / 标定质量",
                    Text =
"你是 SLAM 与标定工程师。请评估这份 L2 点云的建图质量：点云密度是否均匀、是否有运动畸变、地面是否水平、有无重影/拖尾、范围与遮挡是否合理。\n" +
"给出定量的判断依据（用统计中的点数、包围盒、高度分布）和改进建议。\n" +
"最后附 ```json 代码块：{\"summary\":\"\",\"objects\":[],\"defects\":[],\"metrics\":{},\"confidence\":0.0,\"suggestions\":[]}。"
                });
                list.Add(new PromptTemplate
                {
                    Name = "目标定位（给坐标）",
                    Text =
"你是机器人抓取/导航规划器。请在点云中识别主要物体，并用雷达坐标系（原点在底座中心，+X 与出线口相反，+Y 逆时针 90°，+Z 向上，单位米）给出每个物体的中心坐标估计与尺寸。\n" +
"不确定时给出区间。最后附 ```json 代码块：{\"summary\":\"\",\"objects\":[{\"name\":\"\",\"position\":\"(x, y, z)\",\"size_m\":\"\",\"confidence\":0.0}],\"defects\":[],\"metrics\":{},\"confidence\":0.0,\"suggestions\":[]}。"
                });
                Save(list);
            }
            return list;
        }

        public static void Save(List<PromptTemplate> list)
        {
            List<object> arr = new List<object>();
            foreach (PromptTemplate t in list)
            {
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["name"] = t.Name;
                m["text"] = t.Text;
                arr.Add(m);
            }
            try { File.WriteAllText(AppSettings.PromptsPath, Json.Stringify(arr), new UTF8Encoding(false)); } catch { }
        }
    }

    public sealed class HistoryEntry
    {
        public string Dir = "";
        public DateTime Time;
        public string Source = "";
        public string Model = "";
        public string Backend = "";
        public string Summary = "";
        public override string ToString()
        {
            string s = Summary.Length > 60 ? Summary.Substring(0, 60) + "…" : Summary;
            return string.Format("{0:MM-dd HH:mm}  {1}  {2}", Time, Model, s);
        }
    }

    public static class HistoryStore
    {
        /// <summary>Folder of the most recent saved record (used for extra artefacts such as thinking.txt).</summary>
        public static string CurrentDir = "";

        public static string Save(string sourceFile, string model, string backend, string reportMd,
                                  string rawText, int points, Dictionary<string, Bitmap> views)
        {
            Directory.CreateDirectory(AppSettings.HistoryDir);
            string dir = Path.Combine(AppSettings.HistoryDir, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            int suffix = 1;
            while (Directory.Exists(dir))
                dir = Path.Combine(AppSettings.HistoryDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + (suffix++));
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "report.md"), reportMd, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "raw.txt"), rawText, new UTF8Encoding(false));

            string summary = FirstLine(rawText);
            Dictionary<string, object> meta = new Dictionary<string, object>();
            meta["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            meta["source"] = sourceFile;
            meta["model"] = model;
            meta["backend"] = backend;
            meta["points"] = points;
            meta["summary"] = summary;
            File.WriteAllText(Path.Combine(dir, "meta.json"), Json.Stringify(meta), new UTF8Encoding(false));
            CurrentDir = dir;

            if (views != null)
            {
                string vd = Path.Combine(dir, "views");
                Directory.CreateDirectory(vd);
                foreach (KeyValuePair<string, Bitmap> kv in views)
                {
                    try { kv.Value.Save(Path.Combine(vd, kv.Key + ".png"), ImageFormat.Png); } catch { }
                }
            }
            return dir;
        }

        static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim().TrimStart('#', '*', '-', ' ');
                if (t.Length > 8) return t;
            }
            return text.Trim();
        }

        public static List<HistoryEntry> List(int max)
        {
            List<HistoryEntry> list = new List<HistoryEntry>();
            try
            {
                if (!Directory.Exists(AppSettings.HistoryDir)) return list;
                foreach (string d in Directory.GetDirectories(AppSettings.HistoryDir))
                {
                    HistoryEntry e = new HistoryEntry();
                    e.Dir = d;
                    e.Time = Directory.GetCreationTime(d);
                    string metaPath = Path.Combine(d, "meta.json");
                    if (File.Exists(metaPath))
                    {
                        try
                        {
                            Dictionary<string, object> m = Json.Obj(Json.Parse(File.ReadAllText(metaPath, Encoding.UTF8)));
                            e.Source = Json.S(m.ContainsKey("source") ? m["source"] : null, "");
                            e.Model = Json.S(m.ContainsKey("model") ? m["model"] : null, "");
                            e.Backend = Json.S(m.ContainsKey("backend") ? m["backend"] : null, "");
                            e.Summary = Json.S(m.ContainsKey("summary") ? m["summary"] : null, "");
                        }
                        catch { }
                    }
                    list.Add(e);
                }
                list.Sort(delegate (HistoryEntry a, HistoryEntry b) { return b.Time.CompareTo(a.Time); });
                if (list.Count > max) list = list.GetRange(0, max);
            }
            catch { }
            return list;
        }
    }
}
