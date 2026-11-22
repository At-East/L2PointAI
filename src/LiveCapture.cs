// Live UDP capture from the L2: continuous refresh buffer + on-demand snapshot.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace L2PointAI
{
    public static class L2Protocol
    {
        public const int PacketSize = 1044;

        public static bool Parse(byte[] b, out float[] xs, out float[] ys, out float[] zs, out float[] iv)
        {
            xs = ys = zs = iv = null;
            if (b == null || b.Length < 132) return false;
            if (b[0] != 0x55 || b[1] != 0xAA || b[2] != 0x05 || b[3] != 0x0A) return false;
            int type = BitConverter.ToInt32(b, 4);
            int size = BitConverter.ToInt32(b, 8);
            if (type != 102) return false;
            if (size != b.Length) return false;
            if (b[b.Length - 2] != 0x00 || b[b.Length - 1] != 0xFF) return false;

            float aAxis = BitConverter.ToSingle(b, 64);
            float bAxis = BitConverter.ToSingle(b, 68);
            float thetaBias = BitConverter.ToSingle(b, 72);
            float alphaBias = BitConverter.ToSingle(b, 76);
            float beta = BitConverter.ToSingle(b, 80);
            float xi = BitConverter.ToSingle(b, 84);
            float rangeBias = BitConverter.ToSingle(b, 88);
            float rangeScale = BitConverter.ToSingle(b, 92);

            float hAngleStart = BitConverter.ToSingle(b, 96);
            float hAngleStep = BitConverter.ToSingle(b, 100);
            float rangeMin = BitConverter.ToSingle(b, 104 + 4);
            float rangeMax = BitConverter.ToSingle(b, 112);
            float angleMin = BitConverter.ToSingle(b, 116);
            float angleIncrement = BitConverter.ToSingle(b, 120);

            int pointNum = BitConverter.ToInt32(b, 128);
            if (pointNum < 0 || pointNum > 300) return false;

            double sinB = Math.Sin(beta), cosB = Math.Cos(beta);
            double sinX = Math.Sin(xi), cosX = Math.Cos(xi);

            List<float> lx = new List<float>(pointNum), ly = new List<float>(pointNum),
                        lz = new List<float>(pointNum), li = new List<float>(pointNum);

            double alpha = angleMin + alphaBias;
            double theta = hAngleStart + thetaBias;
            for (int j = 0; j < pointNum; j++)
            {
                int rmm = BitConverter.ToUInt16(b, 132 + j * 2);
                float inten = b[732 + j];
                if (rmm >= 1)
                {
                    double r = rangeScale * ((double)rmm + rangeBias);
                    if (r >= rangeMin && r <= rangeMax)
                    {
                        double sa = Math.Sin(alpha), ca = Math.Cos(alpha);
                        double st = Math.Sin(theta), ct = Math.Cos(theta);
                        double A = (-cosB * sinX + sinB * cosX * sa) * r + bAxis;
                        double Bv = ca * cosX * r;
                        double C = (sinB * sinX + cosB * cosX * sa) * r;
                        lx.Add((float)(ct * A - st * Bv));
                        ly.Add((float)(st * A + ct * Bv));
                        lz.Add((float)(C + aAxis));
                        li.Add(inten);
                    }
                }
                alpha += angleIncrement;
                theta += hAngleStep;
            }

            xs = lx.ToArray(); ys = ly.ToArray(); zs = lz.ToArray(); iv = li.ToArray();
            return true;
        }
    }

    public sealed class LiveCapture : IDisposable
    {
        readonly object _lock = new object();
        readonly List<float> _x = new List<float>();
        readonly List<float> _y = new List<float>();
        readonly List<float> _z = new List<float>();
        readonly List<float> _i = new List<float>();

        Thread _thread;
        volatile bool _run;
        UdpClient _udp;

        public int Port = 6201;
        public volatile int PacketCount;
        public volatile int PointCount;
        public volatile int BadPackets;
        public string LastError = "";
        public bool Running { get { return _run; } }
        public const int MaxBufferedPoints = 600000;

        public bool Start(int port)
        {
            if (_run) return true;
            Port = port;
            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.ReceiveBufferSize = 8 * 1024 * 1024;
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch (Exception ex)
            {
                LastError = "UDP " + port + " 绑定失败：" + ex.Message + "（先关掉 Unilidar 2 或占用该端口的程序）";
                try { if (_udp != null) _udp.Close(); } catch { }
                _udp = null;
                return false;
            }
            _run = true;
            PacketCount = 0; PointCount = 0; BadPackets = 0; LastError = "";
            lock (_lock) { _x.Clear(); _y.Clear(); _z.Clear(); _i.Clear(); }
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "L2Capture";
            _thread.Start();
            return true;
        }

        void Loop()
        {
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            while (_run)
            {
                try
                {
                    byte[] data = _udp.Receive(ref ep);
                    float[] xs, ys, zs, iv;
                    if (!L2Protocol.Parse(data, out xs, out ys, out zs, out iv))
                    {
                        BadPackets++;
                        continue;
                    }
                    PacketCount++;
                    PointCount += xs.Length;
                    lock (_lock)
                    {
                        _x.AddRange(xs); _y.AddRange(ys); _z.AddRange(zs); _i.AddRange(iv);
                        if (_x.Count > MaxBufferedPoints)
                        {
                            int drop = _x.Count - MaxBufferedPoints / 2;
                            _x.RemoveRange(0, drop);
                            _y.RemoveRange(0, drop);
                            _z.RemoveRange(0, drop);
                            _i.RemoveRange(0, drop);
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (!_run) break;
                    LastError = ex.Message;
                    Thread.Sleep(50);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Thread.Sleep(50);
                }
            }
        }

        public void Stop()
        {
            _run = false;
            try { if (_udp != null) _udp.Close(); } catch { }
            try { if (_thread != null) _thread.Join(500); } catch { }
            _udp = null;
        }

        public void Dispose() { Stop(); }

        public PointCloud Snapshot(int maxPoints)
        {
            float[] xs, ys, zs, iv;
            lock (_lock)
            {
                int n = _x.Count;
                int start = 0, step = 1;
                if (maxPoints > 0 && n > maxPoints)
                {
                    step = Math.Max(1, n / maxPoints);
                    n = n / step;
                }
                xs = new float[n]; ys = new float[n]; zs = new float[n]; iv = new float[n];
                int w = 0;
                for (int k = start; k < _x.Count && w < n; k += step)
                {
                    xs[w] = _x[k]; ys[w] = _y[k]; zs[w] = _z[k]; iv[w] = _i[k];
                    w++;
                }
            }
            return PointCloud.FromArrays("L2 live capture", xs, ys, zs, iv);
        }

        public int BufferedPoints
        {
            get { lock (_lock) return _x.Count; }
        }
    }
}
