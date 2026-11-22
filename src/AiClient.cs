// AI client: local Ollama (/api/chat) and OpenAI-compatible (/chat/completions), both streaming.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace L2PointAI
{
    public enum AiBackend { Ollama = 0, OpenAi = 1 }

    /// <summary>Ollama reasoning control: Off = think:false, Auto = send no flag, On = think:true.</summary>
    public enum ThinkMode { Off = 0, Auto = 1, On = 2 }

    public sealed class AiRequest
    {
        public AiBackend Backend = AiBackend.Ollama;
        public string BaseUrl = "http://127.0.0.1:11434";
        public string ApiKey = "";
        public string Model = "";
        public string SystemPrompt = "";
        public string UserPrompt = "";
        public List<string> ImagesB64 = new List<string>();
        public double Temperature = 0.3;
        public int MaxTokens = 2048;

        /// <summary>Ollama only: whether to let a reasoning model emit "thinking".</summary>
        public ThinkMode Thinking = ThinkMode.Off;

        /// <summary>Optional: receives reasoning ("thinking") deltas; not part of the answer.</summary>
        public Action<string> OnThinkingDelta;
    }

    public sealed class AiClient
    {
        HttpWebRequest _req;
        volatile bool _cancelled;

        public void Cancel()
        {
            _cancelled = true;
            try { if (_req != null) _req.Abort(); } catch { }
        }

        static AiClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
                ServicePointManager.Expect100Continue = false;
            }
            catch { }
        }

        public static string NormalizeOllamaUrl(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) url = "http://127.0.0.1:11434";
            if (!url.StartsWith("http")) url = "http://" + url;
            return url.TrimEnd('/');
        }

        public static string NormalizeApiBase(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) url = "https://api.openai.com/v1";
            if (!url.StartsWith("http")) url = "https://" + url;
            url = url.TrimEnd('/');
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - "/chat/completions".Length);
            return url;
        }

        // ------------------------------------------------------------ model list
        public static List<string> ListOllamaModels(string baseUrl, out string error)
        {
            error = "";
            List<string> names = new List<string>();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(NormalizeOllamaUrl(baseUrl) + "/api/tags");
                req.Method = "GET";
                req.Timeout = 8000;
                req.Proxy = null;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    Dictionary<string, object> map = Json.Obj(Json.Parse(sr.ReadToEnd()));
                    foreach (object m in Json.Arr(map.ContainsKey("models") ? map["models"] : null))
                    {
                        Dictionary<string, object> mm = Json.Obj(m);
                        string name = Json.S(mm.ContainsKey("name") ? mm["name"] : null, "");
                        if (name.Length > 0) names.Add(name);
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return names;
        }

        // ------------------------------------------------------------ chat
        public void Start(AiRequest r, Action<string> onDelta, Action<string> onDone, Action<string> onError)
        {
            _cancelled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                StringBuilder all = new StringBuilder();
                try
                {
                    bool ollama = r.Backend == AiBackend.Ollama;
                    string url = ollama
                        ? NormalizeOllamaUrl(r.BaseUrl) + "/api/chat"
                        : NormalizeApiBase(r.BaseUrl) + "/chat/completions";
                    string body = ollama ? BuildOllamaBody(r, r.Thinking) : BuildOpenAiBody(r);

                    try
                    {
                        StreamChat(url, body, r, ollama, all, onDelta);
                    }
                    catch (WebException we)
                    {
                        // Some Ollama builds reject the "think" field: retry once without it.
                        if (ollama && r.Thinking != ThinkMode.Auto && IsThinkRejection(we))
                        {
                            all.Length = 0;
                            body = BuildOllamaBody(r, ThinkMode.Auto);
                            StreamChat(url, body, r, ollama, all, onDelta);
                        }
                        else throw;
                    }
                    if (!_cancelled) onDone(all.ToString());
                }
                catch (Exception ex)
                {
                    if (!_cancelled) onError(DescribeError(ex, r.Backend));
                }
            });
        }

        static bool IsThinkRejection(WebException we)
        {
            try
            {
                if (we.Response == null) return false;
                HttpWebResponse hr = we.Response as HttpWebResponse;
                if (hr != null && hr.StatusCode != HttpStatusCode.BadRequest) return false;
                using (Stream s = hr.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    return text.IndexOf("think", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        void StreamChat(string url, string body, AiRequest r, bool ollama, StringBuilder all, Action<string> onDelta)
        {
            _req = (HttpWebRequest)WebRequest.Create(url);
            _req.Method = "POST";
            _req.ContentType = "application/json";
            _req.Accept = ollama ? "application/x-ndjson" : "text/event-stream";
            _req.Timeout = 600000;
            _req.ReadWriteTimeout = 600000;
            _req.Proxy = null;
            _req.KeepAlive = true;
            _req.ServicePoint.Expect100Continue = false;
            if (!ollama && !string.IsNullOrEmpty(r.ApiKey))
                _req.Headers["Authorization"] = "Bearer " + r.ApiKey;

            byte[] data = Encoding.UTF8.GetBytes(body);
            _req.ContentLength = data.Length;
            using (Stream rs = _req.GetRequestStream()) rs.Write(data, 0, data.Length);

            using (HttpWebResponse resp = (HttpWebResponse)_req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (_cancelled) return;
                    if (line.Length == 0) continue;
                    if (ollama)
                    {
                        string content, thinking;
                        ParseOllamaLine(line, out content, out thinking);
                        if (!string.IsNullOrEmpty(thinking) && r.OnThinkingDelta != null) r.OnThinkingDelta(thinking);
                        if (!string.IsNullOrEmpty(content))
                        {
                            all.Append(content);
                            onDelta(content);
                        }
                    }
                    else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        string payload = line.Substring(5).Trim();
                        if (payload == "[DONE]") break;
                        if (payload.Length == 0) continue;
                        string delta = ParseSseLine(payload);
                        if (!string.IsNullOrEmpty(delta))
                        {
                            all.Append(delta);
                            onDelta(delta);
                        }
                    }
                }
            }
        }

        static string DescribeError(Exception ex, AiBackend backend)
        {
            WebException we = ex as WebException;
            if (we != null)
            {
                string body = "";
                try
                {
                    if (we.Response != null)
                    {
                        using (Stream s = we.Response.GetResponseStream())
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            body = sr.ReadToEnd();
                    }
                }
                catch { }
                if (body.Length > 800) body = body.Substring(0, 800) + "...";
                string hint = backend == AiBackend.Ollama
                    ? "\n提示：确认 Ollama 已启动（ollama serve）、模型名正确、且该模型支持图像输入。"
                    : "\n提示：检查 BaseURL 是否含 /v1、API Key 是否有效、模型是否支持图像输入。";
                return string.Format("{0}{1}\n{2}", we.Message, hint, body);
            }
            return ex.Message;
        }

        static void ParseOllamaLine(string line, out string content, out string thinking)
        {
            content = "";
            thinking = "";
            try
            {
                Dictionary<string, object> root = Json.Obj(Json.Parse(line));
                if (root.ContainsKey("error"))
                {
                    content = "\n[Ollama 错误] " + Json.S(root["error"], "");
                    return;
                }
                Dictionary<string, object> msg = Json.Obj(root.ContainsKey("message") ? root["message"] : null);
                content = Json.S(msg.ContainsKey("content") ? msg["content"] : null, "");
                thinking = Json.S(msg.ContainsKey("thinking") ? msg["thinking"] : null, "");
            }
            catch { }
        }

        static string ParseSseLine(string payload)
        {
            try
            {
                Dictionary<string, object> root = Json.Obj(Json.Parse(payload));
                if (root.ContainsKey("error"))
                {
                    Dictionary<string, object> err = Json.Obj(root["error"]);
                    return "\n[API 错误] " + Json.S(err.ContainsKey("message") ? err["message"] : root["error"], "");
                }
                List<object> choices = Json.Arr(root.ContainsKey("choices") ? root["choices"] : null);
                if (choices.Count == 0) return "";
                Dictionary<string, object> ch = Json.Obj(choices[0]);
                Dictionary<string, object> delta = Json.Obj(ch.ContainsKey("delta") ? ch["delta"] : null);
                return Json.S(delta.ContainsKey("content") ? delta["content"] : null, "");
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------ payload
        static string BuildOllamaBody(AiRequest r)
        {
            return BuildOllamaBody(r, r.Thinking);
        }

        static string BuildOllamaBody(AiRequest r, ThinkMode mode)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = r.Model;
            body["stream"] = true;
            if (mode == ThinkMode.Off) body["think"] = false;
            else if (mode == ThinkMode.On) body["think"] = true;

            List<object> msgs = new List<object>();
            if (!string.IsNullOrEmpty(r.SystemPrompt)) msgs.Add(Msg("system", r.SystemPrompt));
            Dictionary<string, object> user = new Dictionary<string, object>();
            user["role"] = "user";
            user["content"] = r.UserPrompt;
            if (r.ImagesB64.Count > 0) user["images"] = new List<string>(r.ImagesB64);
            msgs.Add(user);
            body["messages"] = msgs;

            Dictionary<string, object> opt = new Dictionary<string, object>();
            opt["temperature"] = r.Temperature;
            opt["num_predict"] = r.MaxTokens;
            body["options"] = opt;
            return Json.Stringify(body);
        }

        static string BuildOpenAiBody(AiRequest r)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = r.Model;
            body["stream"] = true;
            body["temperature"] = r.Temperature;
            body["max_tokens"] = r.MaxTokens;

            List<object> msgs = new List<object>();
            if (!string.IsNullOrEmpty(r.SystemPrompt)) msgs.Add(Msg("system", r.SystemPrompt));

            Dictionary<string, object> user = new Dictionary<string, object>();
            user["role"] = "user";
            if (r.ImagesB64.Count == 0)
            {
                user["content"] = r.UserPrompt;
            }
            else
            {
                List<object> parts = new List<object>();
                Dictionary<string, object> text = new Dictionary<string, object>();
                text["type"] = "text";
                text["text"] = r.UserPrompt;
                parts.Add(text);
                foreach (string b64 in r.ImagesB64)
                {
                    Dictionary<string, object> img = new Dictionary<string, object>();
                    img["type"] = "image_url";
                    Dictionary<string, object> u = new Dictionary<string, object>();
                    u["url"] = "data:image/png;base64," + b64;
                    img["image_url"] = u;
                    parts.Add(img);
                }
                user["content"] = parts;
            }
            msgs.Add(user);
            body["messages"] = msgs;
            return Json.Stringify(body);
        }

        static Dictionary<string, object> Msg(string role, string content)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["role"] = role;
            m["content"] = content;
            return m;
        }
    }
}
