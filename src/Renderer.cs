// Software 3D renderer (z-buffer) for point clouds - fast, no GPU/dependency.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace L2PointAI
{
    public enum ColorMode { Height = 0, Intensity = 1, Range = 2, Mono = 3 }

    public sealed class Camera
    {
        public double Yaw = 0.65;    // radians
        public double Pitch = 1.13;  // radians, measured from vertical (pi/2 = horizon, 0 = top view)
        public double Dist = 8.0;
        public double Tx, Ty, Tz;    // look-at target
    }

    public sealed class RenderOptions
    {
        public ColorMode Mode = ColorMode.Height;
        public int PointSize = 2;
        public Color Background = Color.FromArgb(16, 18, 22);
        public bool ShowGrid = true;
        public bool ShowAxes = true;
        public Color GridColor = Color.FromArgb(60, 90, 110);
        public int MaxPoints = 400000;

        /// <summary>When set (6 numbers: ax,ay,az,bx,by,bz), draws a measurement line.</summary>
        public double[] Measure;
        /// <summary>Dim points that are not selected (needs pc.Selected).</summary>
        public bool DimUnselected = true;
        /// <summary>Text drawn next to the measurement line (empty = auto 3D distance).</summary>
        public string MeasureLabel;
    }

    public static class CloudRenderer
    {
        const double FovDeg = 55.0;

        static readonly int[] Palette = BuildPalette();

        static int[] BuildPalette()
        {
            double[,] stops =
            {
                { 0.00, 48, 18, 59 },
                { 0.15, 70, 107, 227 },
                { 0.35, 35, 190, 190 },
                { 0.55, 100, 220, 90 },
                { 0.75, 250, 200, 80 },
                { 1.00, 240, 80, 50 }
            };
            int[] pal = new int[256];
            for (int k = 0; k < 256; k++)
            {
                double t = k / 255.0;
                int a = 0;
                while (a < 4 && stops[a + 1, 0] < t) a++;
                double span = stops[a + 1, 0] - stops[a, 0];
                double f = span <= 1e-9 ? 0 : (t - stops[a, 0]) / span;
                int r = (int)(stops[a, 1] + (stops[a + 1, 1] - stops[a, 1]) * f);
                int g = (int)(stops[a, 2] + (stops[a + 1, 2] - stops[a, 2]) * f);
                int b = (int)(stops[a, 3] + (stops[a + 1, 3] - stops[a, 3]) * f);
                pal[k] = (255 << 24) | (r << 16) | (g << 8) | b;
            }
            return pal;
        }

        public static void Fit(PointCloud pc, Camera cam)
        {
            cam.Tx = pc.Cx; cam.Ty = pc.Cy; cam.Tz = pc.Cz;
            double dx = pc.SizeX, dy = pc.SizeY, dz = pc.SizeZ;
            double radius = 0.5 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (radius < 0.05) radius = 0.05;
            cam.Dist = radius / Math.Tan(FovDeg * Math.PI / 360.0) * 1.2;
            if (cam.Dist < 0.2) cam.Dist = 0.2;
        }

        public static Bitmap Render(PointCloud pc, Camera cam, int w, int h, RenderOptions opt, out int drawn)
        {
            drawn = 0;
            if (w < 16) w = 16;
            if (h < 16) h = 16;
            int[] pix = new int[w * h];
            int bg = opt.Background.ToArgb();
            for (int k = 0; k < pix.Length; k++) pix[k] = bg;

            if (pc == null || pc.Count == 0)
            {
                Bitmap empty = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics ge = Graphics.FromImage(empty)) ge.Clear(opt.Background);
                return empty;
            }

            PointCloud src = (pc.Selected != null) ? pc : pc.Sample(opt.MaxPoints);
            float[] xs = src.X, ys = src.Y, zs = src.Z, iv = src.Intensity;
            int n = src.Count;

            double f = 0.5 * h / Math.Tan(FovDeg * Math.PI / 360.0);
            double cy = Math.Cos(cam.Yaw), sy = Math.Sin(cam.Yaw);
            double cp = Math.Cos(cam.Pitch), sp = Math.Sin(cam.Pitch);
            double tx = cam.Tx, ty = cam.Ty, tz = cam.Tz, dist = cam.Dist;
            double halfW = w * 0.5, halfH = h * 0.5;
            double nearClip = Math.Max(0.02, dist * 0.001);

            float[] zbuf = new float[w * h];
            for (int k = 0; k < zbuf.Length; k++) zbuf[k] = float.MaxValue;

            double minZ = src.MinZ, sizeZ = Math.Max(src.SizeZ, 1e-9);
            double minR = src.MinRange, sizeR = Math.Max(src.MaxRange - src.MinRange, 1e-9);
            int size = Math.Max(1, Math.Min(6, opt.PointSize));
            int half = size / 2;

            int written = 0;
            for (int k = 0; k < n; k++)
            {
                double dx = xs[k] - tx, dy = ys[k] - ty, dz = zs[k] - tz;
                double x1 = dx * cy + dy * sy;
                double y1 = -dx * sy + dy * cy;
                double y2 = y1 * cp - dz * sp;
                double z2 = y1 * sp + dz * cp;
                double depth = dist - z2;
                if (depth < nearClip) continue;

                double sx = halfW + f * x1 / depth;
                double sy2 = halfH - f * y2 / depth;
                int ix = (int)(sx + 0.5);
                int iy = (int)(sy2 + 0.5);
                if (ix < -size || iy < -size || ix >= w + size || iy >= h + size) continue;

                double t;
                switch (opt.Mode)
                {
                    case ColorMode.Intensity:
                        t = iv[k] / 255.0; break;
                    case ColorMode.Range:
                        t = (Math.Sqrt((double)xs[k] * xs[k] + (double)ys[k] * ys[k] + (double)zs[k] * zs[k]) - minR) / sizeR; break;
                    case ColorMode.Mono:
                        t = 0.72; break;
                    default:
                        t = (zs[k] - minZ) / sizeZ; break;
                }
                if (t < 0) t = 0; if (t > 1) t = 1;
                int color = Palette[(int)(t * 255)];
                if (src.Selected != null && src.Selected[k] == 0)
                {
                    int cc = color & 0x00FFFFFF;
                    int rr = (int)(((cc >> 16) & 0xFF) * 0.22);
                    int gg = (int)(((cc >> 8) & 0xFF) * 0.22);
                    int bb = (int)((cc & 0xFF) * 0.22);
                    color = (255 << 24) | (rr << 16) | (gg << 8) | bb;
                }
                float fd = (float)depth;
                written++;
                for (int oy = -half; oy <= size - 1 - half; oy++)
                {
                    int py = iy + oy;
                    if (py < 0 || py >= h) continue;
                    int row = py * w;
                    for (int ox = -half; ox <= size - 1 - half; ox++)
                    {
                        int px = ix + ox;
                        if (px < 0 || px >= w) continue;
                        int idx = row + px;
                        if (fd < zbuf[idx])
                        {
                            zbuf[idx] = fd;
                            pix[idx] = color;
                        }
                    }
                }
            }
            drawn = written;

            // background + ground grid first, then blend points on top (zbuf mask)
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics gb = Graphics.FromImage(bmp))
            {
                gb.Clear(opt.Background);
                if (opt.ShowGrid) DrawGroundGrid(gb, pc, cam, w, h, opt);
            }

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int[] existing = new int[pix.Length];
                Marshal.Copy(bd.Scan0, existing, 0, existing.Length);
                for (int k = 0; k < existing.Length; k++)
                {
                    if (zbuf[k] != float.MaxValue) existing[k] = pix[k];
                }
                Marshal.Copy(existing, 0, bd.Scan0, existing.Length);
            }
            finally { bmp.UnlockBits(bd); }

            DrawOverlay(bmp, pc, cam, src, w, h, opt);
            return bmp;
        }

        static void DrawOverlay(Bitmap bmp, PointCloud pc, Camera cam, PointCloud src, int w, int h, RenderOptions opt)
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (pc != null && opt.ShowAxes) DrawAxes(g, pc, cam, w, h);
                if (pc != null && opt.Measure != null && opt.Measure.Length == 6) DrawMeasure(g, pc, cam, w, h, opt.Measure, opt.MeasureLabel);
                if (pc != null)
                {
                    double elev = 90.0 - cam.Pitch * 180 / Math.PI;
                    string cap = string.Format("{0}   点数 {1:N0}   方位 {2:F0}° / 仰角 {3:F0}°",
                        System.IO.Path.GetFileName(pc.Source), pc.Count, cam.Yaw * 180 / Math.PI, elev);
                    using (Font font = new Font("Consolas", 9f))
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(200, 220, 235)))
                    {
                        g.DrawString(cap, font, br, 8, 6);
                    }
                }
            }
        }

        static bool Project(PointCloud pc, Camera cam, double x, double y, double z, int w, int h, out double sx, out double sy)
        {
            sx = sy = 0;
            double f = 0.5 * h / Math.Tan(FovDeg * Math.PI / 360.0);
            double cy = Math.Cos(cam.Yaw), sy0 = Math.Sin(cam.Yaw);
            double cp = Math.Cos(cam.Pitch), sp = Math.Sin(cam.Pitch);
            double dx = x - cam.Tx, dy = y - cam.Ty, dz = z - cam.Tz;
            double x1 = dx * cy + dy * sy0;
            double y1 = -dx * sy0 + dy * cy;
            double y2 = y1 * cp - dz * sp;
            double z2 = y1 * sp + dz * cp;
            double depth = cam.Dist - z2;
            if (depth < 0.02) return false;
            sx = w * 0.5 + f * x1 / depth;
            sy = h * 0.5 - f * y2 / depth;
            return true;
        }

        static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double step = 1;
            if (norm > 5) step = 10; else if (norm > 2) step = 5; else if (norm > 1) step = 2;
            return step * mag;
        }

        static void DrawGroundGrid(Graphics g, PointCloud pc, Camera cam, int w, int h, RenderOptions opt)
        {
            double span = Math.Max(pc.SizeX, pc.SizeY);
            if (span <= 0) return;
            double step = NiceStep(span / 10.0);
            double x0 = Math.Floor(pc.MinX / step) * step, x1 = Math.Ceiling(pc.MaxX / step) * step;
            double y0 = Math.Floor(pc.MinY / step) * step, y1 = Math.Ceiling(pc.MaxY / step) * step;
            if ((x1 - x0) / step > 400 || (y1 - y0) / step > 400) return;
            using (Pen pen = new Pen(Color.FromArgb(70, opt.GridColor), 1f))
            {
                double z = pc.MinZ;
                double sxa, sya, sxb, syb;
                for (double x = x0; x <= x1 + 1e-9; x += step)
                {
                    if (Project(pc, cam, x, y0, z, w, h, out sxa, out sya) && Project(pc, cam, x, y1, z, w, h, out sxb, out syb))
                        g.DrawLine(pen, (float)sxa, (float)sya, (float)sxb, (float)syb);
                }
                for (double y = y0; y <= y1 + 1e-9; y += step)
                {
                    if (Project(pc, cam, x0, y, z, w, h, out sxa, out sya) && Project(pc, cam, x1, y, z, w, h, out sxb, out syb))
                        g.DrawLine(pen, (float)sxa, (float)sya, (float)sxb, (float)syb);
                }
            }
        }

        static void DrawAxes(Graphics g, PointCloud pc, Camera cam, int w, int h)
        {
            double len = Math.Max(Math.Max(pc.SizeX, pc.SizeY), pc.SizeZ) * 0.25;
            if (len <= 0) len = 0.5;
            double ox = pc.MinX, oy = pc.MinY, oz = pc.MinZ;
            double ax, ay, bx, by;
            using (Pen px = new Pen(Color.FromArgb(230, 80, 80), 2f))
            using (Pen py = new Pen(Color.FromArgb(80, 220, 120), 2f))
            using (Pen pz = new Pen(Color.FromArgb(90, 150, 250), 2f))
            {
                if (Project(pc, cam, ox, oy, oz, w, h, out ax, out ay))
                {
                    if (Project(pc, cam, ox + len, oy, oz, w, h, out bx, out by)) g.DrawLine(px, (float)ax, (float)ay, (float)bx, (float)by);
                    if (Project(pc, cam, ox, oy + len, oz, w, h, out bx, out by)) g.DrawLine(py, (float)ax, (float)ay, (float)bx, (float)by);
                    if (Project(pc, cam, ox, oy, oz + len, w, h, out bx, out by)) g.DrawLine(pz, (float)ax, (float)ay, (float)bx, (float)by);
                }
            }
        }


        static void DrawMeasure(Graphics g, PointCloud pc, Camera cam, int w, int h, double[] m, string optLabel)
        {
            double ax, ay, bx, by;
            if (!Project(pc, cam, m[0], m[1], m[2], w, h, out ax, out ay)) return;
            if (!Project(pc, cam, m[3], m[4], m[5], w, h, out bx, out by)) return;
            using (Pen pen = new Pen(Color.FromArgb(255, 210, 90), 2f))
            {
                g.DrawLine(pen, (float)ax, (float)ay, (float)bx, (float)by);
                using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 210, 90)))
                {
                    g.FillEllipse(br, (float)ax - 4, (float)ay - 4, 8, 8);
                    g.FillEllipse(br, (float)bx - 4, (float)by - 4, 8, 8);
                }
            }
            double dx = m[3] - m[0], dy = m[4] - m[1], dz = m[5] - m[2];
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            string text = string.IsNullOrEmpty(optLabel)
                ? string.Format(CultureInfo.InvariantCulture, "{0:F3} m  (ΔX {1:F3}  ΔY {2:F3}  ΔZ {3:F3})", dist, dx, dy, dz)
                : optLabel;
            using (Font font = new Font("Consolas", 9f))
            using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 225, 150)))
            {
                float tx = (float)((ax + bx) / 2) + 8;
                float ty = (float)((ay + by) / 2) - 10;
                g.DrawString(text, font, br, tx, ty);
            }
        }
        public static void SavePng(Bitmap bmp, string path)
        {
            bmp.Save(path, ImageFormat.Png);
        }

        // three standard views for the AI request
        public static Dictionary<string, Bitmap> RenderThreeViews(PointCloud pc, RenderOptions opt, int w, int h)
        {
            Dictionary<string, Bitmap> d = new Dictionary<string, Bitmap>();
            // pitch is measured from the vertical: 90° = horizon, 0° = looking straight down
            double[,] views = { { 0.68, 1.13 }, { 0.0, 0.03 }, { 0.0, 1.47 } };
            string[] names = { "iso", "top", "front" };
            for (int k = 0; k < 3; k++)
            {
                Camera c = new Camera();
                c.Yaw = views[k, 0];
                c.Pitch = views[k, 1];
                Fit(pc, c);
                int drawn;
                d[names[k]] = Render(pc, c, w, h, opt, out drawn);
            }
            return d;
        }
    }
}
