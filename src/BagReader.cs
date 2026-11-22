// ROS1 bag (v2.0) reader + sensor_msgs/PointCloud2 decoder.
// Strategy: read each chunk into memory and parse records sequentially (no per-record seeking).
// Supported: uncompressed chunks ("none"). bz2/lz4 chunks are reported as unsupported.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace L2PointAI
{
    public sealed class BagTopic
    {
        public int Conn;
        public string Topic = "";
        public string Type = "";
        public int Messages;
    }

    public sealed class BagInfo
    {
        public string Path = "";
        public readonly List<BagTopic> Topics = new List<BagTopic>();
        public int ChunkCount;
        public string Compression = "none";

        public int CloudMessages
        {
            get
            {
                int n = 0;
                foreach (BagTopic t in Topics) if (IsCloud(t.Type)) n += t.Messages;
                return n;
            }
        }

        public static bool IsCloud(string type)
        {
            return !string.IsNullOrEmpty(type) && type.EndsWith("PointCloud2", StringComparison.Ordinal);
        }

        public BagTopic FirstCloudTopic()
        {
            foreach (BagTopic t in Topics) if (IsCloud(t.Type)) return t;
            return null;
        }

        public BagTopic Find(string topic)
        {
            foreach (BagTopic t in Topics) if (t.Topic == topic) return t;
            return null;
        }
    }

    public sealed class BagReader : IDisposable
    {
        readonly FileStream _fs;
        readonly long _length;

        public BagInfo Info { get; private set; }

        public BagReader(string path)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            _length = _fs.Length;
            Info = new BagInfo { Path = path };

            byte[] magic = new byte[13];
            int got = _fs.Read(magic, 0, 13);
            string m = Encoding.ASCII.GetString(magic, 0, got);
            if (!m.StartsWith("#ROSBAG V2.0", StringComparison.Ordinal))
                throw new InvalidDataException(
                    "不是 ROS1 bag v2.0 文件（magic=" + m.Replace("\n", "\\n") +
                    "）。若是 ROS2 目录式 bag，请先转成 ROS1 bag。");
            _fs.Position = 13;
        }

        public void Dispose() { try { _fs.Dispose(); } catch { } }

        // ------------------------------------------------------------ field parsing
        struct Fields
        {
            public int Op, Conn, Size;
            public string Text;
            public bool IsCompression;
        }

        static Fields ParseFieldsFast(byte[] b, int start, int len)
        {
            Fields f = new Fields(); f.Op = -1; f.Conn = -1;
            int i = start, end = start + len;
            while (i + 4 <= end)
            {
                int n = BitConverter.ToInt32(b, i); i += 4;
                if (n <= 0 || i + n > end) break;
                int eq = -1;
                for (int k = i; k < i + n; k++) { if (b[k] == (byte)'=') { eq = k; break; } }
                if (eq > i)
                {
                    int klen = eq - i;
                    int vstart = eq + 1, vlen = i + n - eq - 1;
                    if (klen == 2 && b[i] == (byte)'o' && b[i + 1] == (byte)'p' && vlen >= 1) f.Op = b[vstart];
                    else if (klen == 4 && KeyIs(b, i, klen, "conn") && vlen >= 4) f.Conn = BitConverter.ToInt32(b, vstart);
                    else if (klen == 4 && KeyIs(b, i, klen, "size") && vlen >= 4) f.Size = BitConverter.ToInt32(b, vstart);
                    else if (klen == 5 && KeyIs(b, i, klen, "topic")) f.Text = Encoding.UTF8.GetString(b, vstart, vlen);
                    else if (klen == 11 && KeyIs(b, i, klen, "compression"))
                    {
                        f.Text = Encoding.ASCII.GetString(b, vstart, vlen);
                        f.IsCompression = true;
                    }
                }
                i += n;
            }
            return f;
        }

        static bool KeyIs(byte[] b, int start, int len, string key)
        {
            if (len != key.Length) return false;
            for (int i = 0; i < len; i++) if (b[start + i] != (byte)key[i]) return false;
            return true;
        }

        static Dictionary<string, string> ParseFieldsText(byte[] b, int start, int len)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = start, end = start + len;
            while (i + 4 <= end)
            {
                int n = BitConverter.ToInt32(b, i); i += 4;
                if (n <= 0 || i + n > end) break;
                int eq = -1;
                for (int k = i; k < i + n; k++) { if (b[k] == (byte)'=') { eq = k; break; } }
                if (eq > i)
                    map[Encoding.ASCII.GetString(b, i, eq - i)] = Encoding.UTF8.GetString(b, eq + 1, i + n - eq - 1);
                i += n;
            }
            return map;
        }

        // ------------------------------------------------------------ record walking
        /// <summary>Read the next top-level record header; leaves the stream at its data.</summary>
        bool ReadTopHeader(out byte[] header, out int dataLen)
        {
            header = null; dataLen = 0;
            long pos = _fs.Position;
            if (pos + 9 > _length) return false;
            byte[] l4 = new byte[4];
            if (_fs.Read(l4, 0, 4) != 4) return false;
            int hlen = BitConverter.ToInt32(l4, 0);
            if (hlen <= 0 || pos + 8 + hlen > _length) return false;
            header = new byte[hlen];
            if (_fs.Read(header, 0, hlen) != hlen) return false;
            if (_fs.Read(l4, 0, 4) != 4) return false;
            dataLen = BitConverter.ToInt32(l4, 0);
            if (dataLen < 0 || _fs.Position + dataLen > _length) return false;
            return true;
        }

        /// <summary>Iterate records inside a chunk buffer.</summary>
        struct MemRec
        {
            public int Op, Conn;
            public int HeaderStart, HeaderLen;
            public int DataStart, DataLen;
            public bool IsCompression;
            public string Text;
        }

        static bool NextMemRec(byte[] b, ref int pos, out MemRec r)
        {
            r = new MemRec();
            if (pos + 9 > b.Length) return false;
            int hlen = BitConverter.ToInt32(b, pos);
            if (hlen <= 0 || pos + 8 + hlen > b.Length) return false;
            int hstart = pos + 4;
            int dlenPos = hstart + hlen;
            int dlen = BitConverter.ToInt32(b, dlenPos);
            int dstart = dlenPos + 4;
            if (dlen < 0 || dstart + dlen > b.Length) return false;

            Fields f = ParseFieldsFast(b, hstart, hlen);
            r.Op = f.Op; r.Conn = f.Conn; r.Text = f.Text; r.IsCompression = f.IsCompression;
            r.HeaderStart = hstart; r.HeaderLen = hlen;
            r.DataStart = dstart; r.DataLen = dlen;
            pos = dstart + dlen;
            return true;
        }

        // ------------------------------------------------------------ connections
        void RegisterConnection(MemRec r, byte[] buf)
        {
            Dictionary<string, string> hf = ParseFieldsText(buf, r.HeaderStart, r.HeaderLen);
            int conn;
            if (!hf.ContainsKey("conn") || !int.TryParse(hf["conn"], out conn)) conn = r.Conn;
            string topic = hf.ContainsKey("topic") ? hf["topic"] : (r.Text ?? "");

            string type = "";
            if (r.DataLen > 0 && r.DataLen < (1 << 20))
            {
                Dictionary<string, string> cf = ParseFieldsText(buf, r.DataStart, r.DataLen);
                if (cf.ContainsKey("type")) type = cf["type"];
            }

            BagTopic exist = Info.Find(topic);
            if (exist != null) { exist.Type = type; exist.Conn = conn; return; }
            Info.Topics.Add(new BagTopic { Conn = conn, Topic = topic, Type = type });
        }

        void SetCompression(string comp)
        {
            if (string.IsNullOrEmpty(comp) || comp == "none") return;
            if (Info.Compression == "none") Info.Compression = comp;
            else if (Info.Compression.IndexOf(comp, StringComparison.Ordinal) < 0) Info.Compression += "," + comp;
        }

        // ------------------------------------------------------------ public API
        /// <summary>Scan the whole bag: topics, message counts, chunk count, compression.</summary>
        public BagInfo Scan()
        {
            foreach (BagTopic t in Info.Topics) t.Messages = 0;
            Info.ChunkCount = 0;
            Info.Compression = "none";
            _fs.Position = 13;

            byte[] header;
            int dataLen;
            while (ReadTopHeader(out header, out dataLen))
            {
                Fields f = ParseFieldsFast(header, 0, header.Length);
                if (f.Op == 0x05)
                {
                    Info.ChunkCount++;
                    if (f.IsCompression) SetCompression(f.Text);
                    byte[] chunk = new byte[dataLen];
                    if (_fs.Read(chunk, 0, dataLen) != dataLen) break;
                    ScanChunk(chunk);
                }
                else if (f.Op == 0x07)
                {
                    byte[] data = new byte[dataLen];
                    if (_fs.Read(data, 0, dataLen) != dataLen) break;
                    // connection at top level: header=header, data=data
                    MemRec r = new MemRec();
                    r.HeaderStart = 0; r.HeaderLen = header.Length;
                    r.DataStart = 0; r.DataLen = data.Length;
                    r.Conn = f.Conn; r.Text = f.Text;
                    byte[] combined = Concat(header, data);
                    // header length must be inside combined: rebuild offsets
                    MemRec c = r; c.DataStart = header.Length; c.DataLen = data.Length;
                    RegisterConnection(c, combined);
                }
                else
                {
                    _fs.Seek(dataLen, SeekOrigin.Current);
                }
            }
            return Info;
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Array.Copy(a, 0, r, 0, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        void ScanChunk(byte[] chunk)
        {
            int pos = 0;
            MemRec r;
            while (NextMemRec(chunk, ref pos, out r))
            {
                if (r.Op == 0x07) RegisterConnection(r, chunk);
                else if (r.Op == 0x02 && r.Conn >= 0)
                {
                    foreach (BagTopic t in Info.Topics)
                        if (t.Conn == r.Conn) { t.Messages++; break; }
                }
            }
        }

        /// <summary>Extract up to maxMessages PointCloud2 messages of one topic, merged into one cloud.</summary>
        public PointCloud Load(string topic, int startIndex, int maxMessages, Action<int, int> progress)
        {
            BagTopic target = Info.Find(topic);
            if (target == null) throw new InvalidOperationException("bag 里没有话题 " + topic);
            int wantConn = target.Conn;
            int total = Math.Max(0, target.Messages - startIndex);
            int seen = 0;

            List<float> xs = new List<float>(8192), ys = new List<float>(8192),
                        zs = new List<float>(8192), iv = new List<float>(8192);
            int got = 0;

            _fs.Position = 13;
            byte[] header;
            int dataLen;
            while (got < maxMessages && ReadTopHeader(out header, out dataLen))
            {
                Fields f = ParseFieldsFast(header, 0, header.Length);
                if (f.Op != 0x05)
                {
                    _fs.Seek(dataLen, SeekOrigin.Current);
                    continue;
                }
                byte[] chunk = new byte[dataLen];
                if (_fs.Read(chunk, 0, dataLen) != dataLen) break;

                int pos = 0;
                MemRec r;
                while (NextMemRec(chunk, ref pos, out r))
                {
                    if (r.Op != 0x02 || r.Conn != wantConn) continue;
                    if (seen++ < startIndex) continue;
                    float[] px, py, pz, pi;
                    if (PointCloud2Parser.TryParse(chunk, r.DataStart, r.DataLen, out px, out py, out pz, out pi))
                    {
                        xs.AddRange(px); ys.AddRange(py); zs.AddRange(pz); iv.AddRange(pi);
                        got++;
                        if (progress != null) progress(got, total);
                        if (got >= maxMessages) break;
                    }
                }
            }

            if (xs.Count == 0) throw new InvalidDataException("没有从 bag 里解析到点");
            return PointCloud.FromArrays(
                Path.GetFileName(Info.Path) + " · " + topic.TrimStart('/').Replace('/', '_') + " ×" + got,
                xs.ToArray(), ys.ToArray(), zs.ToArray(), iv.ToArray());
        }
    }

    /// <summary>sensor_msgs/PointCloud2 decoder (float32 xyz + intensity; LE/BE).</summary>
    public static class PointCloud2Parser
    {
        const byte FLOAT32 = 7, UINT16 = 4, UINT8 = 2, INT16 = 5;

        public static bool TryParse(byte[] raw, out float[] xs, out float[] ys, out float[] zs, out float[] iv)
        {
            return TryParse(raw, 0, raw == null ? 0 : raw.Length, out xs, out ys, out zs, out iv);
        }

        public static bool TryParse(byte[] buf, int baseOffset, int length, out float[] xs, out float[] ys, out float[] zs, out float[] iv)
        {
            xs = ys = zs = iv = null;
            try
            {
                if (buf == null || length < 40) return false;
                int o = baseOffset;
                int end = baseOffset + length;
                o += 4;                                  // seq
                o += 8;                                  // stamp
                int fl = BitConverter.ToInt32(buf, o); o += 4;
                o += fl;                                 // frame_id
                int height = BitConverter.ToInt32(buf, o); o += 4;
                int width = BitConverter.ToInt32(buf, o); o += 4;
                int nfields = BitConverter.ToInt32(buf, o); o += 4;

                int offX = -1, offY = -1, offZ = -1, offI = -1;
                byte typeI = 0;
                for (int k = 0; k < nfields; k++)
                {
                    int nl = BitConverter.ToInt32(buf, o); o += 4;
                    string name = Encoding.ASCII.GetString(buf, o, nl); o += nl;
                    int off = BitConverter.ToInt32(buf, o); o += 4;
                    byte dt = buf[o]; o += 1;
                    o += 4;                              // count
                    if (name == "x") offX = off;
                    else if (name == "y") offY = off;
                    else if (name == "z") offZ = off;
                    else if (name == "intensity" || name == "i" || name == "reflectivity") { offI = off; typeI = dt; }
                }
                if (offX < 0 || offY < 0 || offZ < 0) return false;

                bool bigEndian = buf[o] != 0; o += 1;
                int pointStep = BitConverter.ToInt32(buf, o); o += 4;
                o += 4;                                  // row_step
                int dataLen = BitConverter.ToInt32(buf, o); o += 4;
                if (pointStep <= 0 || o + dataLen > end) return false;

                int count = width * Math.Max(1, height);
                if (count <= 0) return false;

                xs = new float[count]; ys = new float[count]; zs = new float[count]; iv = new float[count];
                int n = 0;
                int limit = o + dataLen;
                for (int i = 0; i < count; i++)
                {
                    int b = o + i * pointStep;
                    if (b + pointStep > limit) break;
                    float x = ReadFloat(buf, b + offX, bigEndian);
                    float y = ReadFloat(buf, b + offY, bigEndian);
                    float z = ReadFloat(buf, b + offZ, bigEndian);
                    if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                        float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) continue;
                    if (x == 0f && y == 0f && z == 0f) continue;
                    float inten = 0f;
                    if (offI >= 0)
                    {
                        if (typeI == FLOAT32) inten = ReadFloat(buf, b + offI, bigEndian);
                        else if (typeI == UINT16) inten = ReadU16(buf, b + offI, bigEndian);
                        else if (typeI == UINT8) inten = buf[b + offI];
                        else if (typeI == INT16) inten = (short)ReadU16(buf, b + offI, bigEndian);
                    }
                    xs[n] = x; ys[n] = y; zs[n] = z; iv[n] = inten;
                    n++;
                }
                if (n == 0) return false;
                if (n < count)
                {
                    Array.Resize(ref xs, n); Array.Resize(ref ys, n);
                    Array.Resize(ref zs, n); Array.Resize(ref iv, n);
                }
                return true;
            }
            catch { return false; }
        }

        static float ReadFloat(byte[] b, int off, bool bigEndian)
        {
            if (!bigEndian) return BitConverter.ToSingle(b, off);
            byte[] t = new byte[] { b[off + 3], b[off + 2], b[off + 1], b[off] };
            return BitConverter.ToSingle(t, 0);
        }

        static ushort ReadU16(byte[] b, int off, bool bigEndian)
        {
            return bigEndian ? (ushort)((b[off] << 8) | b[off + 1]) : (ushort)(b[off] | (b[off + 1] << 8));
        }
    }
}
