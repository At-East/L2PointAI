// Point cloud container: loading exported files (CSV / TXT / PLY), statistics, sampling.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace L2PointAI
{
    public sealed class PointCloud
    {
        public string Source = "";
        public float[] X = new float[0];
        public float[] Y = new float[0];
        public float[] Z = new float[0];
        public float[] Intensity = new float[0];
        public int Count;

        // segmentation / selection state (null = none)
        public byte[] Selected;
        public int[] Cluster;
        public int[] ClusterIds;      // not used yet, kept for compatibility


        public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        public double Cx, Cy, Cz;
        public double MeanRange, MinRange, MaxRange;

        public double SizeX { get { return MaxX - MinX; } }
        public double SizeY { get { return MaxY - MinY; } }
        public double SizeZ { get { return MaxZ - MinZ; } }

        public static PointCloud Empty()
        {
            return new PointCloud();
        }

        // ---------------------------------------------------------------- loading
        public static PointCloud Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ply") return LoadPly(path);
            using (StreamReader sr = new StreamReader(path, Encoding.UTF8, true))
            {
                string first = sr.ReadLine();
                if (first == null) throw new InvalidDataException("empty file");
                if (first.TrimStart().StartsWith("ply", StringComparison.OrdinalIgnoreCase))
                {
                    return LoadPly(path);
                }
                if (first.IndexOf(',') >= 0) return LoadCsv(path, first);
                return LoadTxt(path, first);
            }
        }

        sealed class Cols
        {
            public int X = 0, Y = 1, Z = 2, I = 3;
            public bool HasI = true;
        }

        static Cols ParseHeader(string header, char sep)
        {
            string[] parts = header.Split(sep);
            Cols c = new Cols();
            c.X = c.Y = c.Z = c.I = -1;
            for (int k = 0; k < parts.Length; k++)
            {
                string p = parts[k].Trim().Trim('"').ToLowerInvariant();
                if (p == "x" || p == "x_m" || p == "x(m)" || p == "x_meters" || p == "xm") c.X = k;
                else if (p == "y" || p == "y_m" || p == "y(m)" || p == "y_meters" || p == "ym") c.Y = k;
                else if (p == "z" || p == "z_m" || p == "z(m)" || p == "z_meters" || p == "zm") c.Z = k;
                else if (p == "intensity" || p == "i" || p == "reflectivity" || p == "reflect" || p == "intensities") c.I = k;
            }
            c.HasI = c.I >= 0;
            if (c.X < 0 || c.Y < 0 || c.Z < 0) return null;
            return c;
        }

        static bool LooksNumeric(string line, char sep)
        {
            string[] parts = line.Split(sep);
            if (parts.Length < 3) return false;
            double v;
            return double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        static PointCloud LoadCsv(string path, string firstLine)
        {
            char sep = firstLine.IndexOf(';') >= 0 && firstLine.IndexOf(',') < 0 ? ';' : ',';
            Cols cols = ParseHeader(firstLine, sep);
            List<float> xs = new List<float>(), ys = new List<float>(), zs = new List<float>(), ii = new List<float>();
            using (StreamReader sr = new StreamReader(path, Encoding.UTF8, true))
            {
                string line;
                bool headerConsumed = cols != null;
                if (!headerConsumed) cols = new Cols();
                while ((line = sr.ReadLine()) != null)
                {
                    if (!headerConsumed) { headerConsumed = true; continue; }
                    if (line.Length == 0) continue;
                    string[] p = line.Split(sep);
                    if (p.Length < 3) continue;
                    double x, y, z, inten = 0;
                    if (!TryNum(p, cols.X, out x) || !TryNum(p, cols.Y, out y) || !TryNum(p, cols.Z, out z)) continue;
                    if (cols.HasI) TryNum(p, cols.I, out inten);
                    xs.Add((float)x); ys.Add((float)y); zs.Add((float)z); ii.Add((float)inten);
                }
            }
            return Build(path, xs, ys, zs, ii);
        }

        static bool TryNum(string[] parts, int idx, out double v)
        {
            v = 0;
            if (idx < 0 || idx >= parts.Length) return false;
            return double.TryParse(parts[idx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        static PointCloud LoadTxt(string path, string firstLine)
        {
            List<float> xs = new List<float>(), ys = new List<float>(), zs = new List<float>(), ii = new List<float>();
            using (StreamReader sr = new StreamReader(path, Encoding.UTF8, true))
            {
                string line = firstLine;
                bool first = true;
                while (line != null)
                {
                    if (first && !LooksNumeric(line, ' '))
                    {
                        first = false;
                        line = sr.ReadLine();
                        continue;
                    }
                    first = false;
                    line = line.Trim();
                    if (line.Length == 0) { line = sr.ReadLine(); continue; }
                    string[] p = line.Split(new char[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    double x, y, z, inten = 0;
                    if (p.Length >= 3
                        && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                        && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                        && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    {
                        if (p.Length >= 4) double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out inten);
                        xs.Add((float)x); ys.Add((float)y); zs.Add((float)z); ii.Add((float)inten);
                    }
                    line = sr.ReadLine();
                }
            }
            return Build(path, xs, ys, zs, ii);
        }

        static PointCloud LoadPly(string path)
        {
            List<string> propNames = new List<string>();
            int vertexCount = 0;
            bool ascii = false;
            long dataStart = 0;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                StreamReader sr = new StreamReader(fs, Encoding.ASCII, false, 4096, true);
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    string t = line.Trim();
                    if (t.StartsWith("format", StringComparison.OrdinalIgnoreCase))
                        ascii = t.IndexOf("ascii", StringComparison.OrdinalIgnoreCase) >= 0;
                    else if (t.StartsWith("element vertex", StringComparison.OrdinalIgnoreCase))
                        vertexCount = int.Parse(t.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[2], CultureInfo.InvariantCulture);
                    else if (t.StartsWith("property", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] pp = t.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (pp.Length >= 3 && pp[1] != "list") propNames.Add(pp[pp.Length - 1].ToLowerInvariant());
                    }
                    else if (t == "end_header")
                    {
                        dataStart = fs.Position;
                        break;
                    }
                }
                fs.Position = dataStart;
                if (!ascii) throw new NotSupportedException("only ASCII PLY is supported");
                int ix = propNames.IndexOf("x"), iy = propNames.IndexOf("y"), iz = propNames.IndexOf("z");
                int ii = propNames.IndexOf("intensity");
                if (ii < 0) ii = propNames.IndexOf("scalar_intensity");
                List<float> xs = new List<float>(vertexCount), ys = new List<float>(vertexCount),
                             zs = new List<float>(vertexCount), iv = new List<float>(vertexCount);
                sr = new StreamReader(fs, Encoding.ASCII);
                string dl;
                while ((dl = sr.ReadLine()) != null && xs.Count < vertexCount)
                {
                    string[] p = dl.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 3) continue;
                    double x, y, z, inten = 0;
                    if (!double.TryParse(p[ix], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                    if (!double.TryParse(p[iy], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
                    if (!double.TryParse(p[iz], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                    if (ii >= 0 && ii < p.Length) double.TryParse(p[ii], NumberStyles.Float, CultureInfo.InvariantCulture, out inten);
                    xs.Add((float)x); ys.Add((float)y); zs.Add((float)z); iv.Add((float)inten);
                }
                return Build(path, xs, ys, zs, iv);
            }
        }

        static PointCloud Build(string path, List<float> xs, List<float> ys, List<float> zs, List<float> ii)
        {
            PointCloud pc = new PointCloud();
            pc.Source = path;
            pc.Count = xs.Count;
            pc.X = xs.ToArray();
            pc.Y = ys.ToArray();
            pc.Z = zs.ToArray();
            pc.Intensity = ii.ToArray();
            pc.ComputeStats();
            return pc;
        }

        public static PointCloud FromArrays(string source, float[] xs, float[] ys, float[] zs, float[] iv)
        {
            PointCloud pc = new PointCloud();
            pc.Source = source;
            pc.Count = xs.Length;
            pc.X = xs; pc.Y = ys; pc.Z = zs; pc.Intensity = iv;
            pc.ComputeStats();
            return pc;
        }

        // ---------------------------------------------------------------- stats
        public void ComputeStats()
        {
            if (Count == 0) return;
            MinX = MaxX = X[0]; MinY = MaxY = Y[0]; MinZ = MaxZ = Z[0];
            double sx = 0, sy = 0, sz = 0, sr = 0;
            double rmin = double.MaxValue, rmax = 0;
            for (int k = 0; k < Count; k++)
            {
                float x = X[k], y = Y[k], z = Z[k];
                if (x < MinX) MinX = x; if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y; if (y > MaxY) MaxY = y;
                if (z < MinZ) MinZ = z; if (z > MaxZ) MaxZ = z;
                sx += x; sy += y; sz += z;
                double r = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
                sr += r;
                if (r < rmin) rmin = r;
                if (r > rmax) rmax = r;
            }
            Cx = sx / Count; Cy = sy / Count; Cz = sz / Count;
            MeanRange = sr / Count;
            MinRange = rmin;
            MaxRange = rmax;
        }

        public PointCloud Sample(int maxPoints)
        {
            if (maxPoints <= 0 || Count <= maxPoints) return this;
            int step = (int)Math.Ceiling(Count / (double)maxPoints);
            int n = Count / step;
            float[] x = new float[n], y = new float[n], z = new float[n], i = new float[n];
            int w = 0;
            for (int k = 0; k < Count && w < n; k += step)
            {
                x[w] = X[k]; y[w] = Y[k]; z[w] = Z[k]; i[w] = Intensity[k];
                w++;
            }
            if (w < n)
            {
                Array.Resize(ref x, w); Array.Resize(ref y, w);
                Array.Resize(ref z, w); Array.Resize(ref i, w);
            }
            PointCloud pc = FromArrays(Source + " (sampled)", x, y, z, i);
            pc.Count = w;
            pc.ComputeStats();
            return pc;
        }

        public string StatsText()
        {
            if (Count == 0) return "（没有点数据）";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("点数: " + Count.ToString("N0", CultureInfo.InvariantCulture));
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "包围盒: X [{0:F3}, {1:F3}]  Y [{2:F3}, {3:F3}]  Z [{4:F3}, {5:F3}] (米){6}",
                MinX, MaxX, MinY, MaxY, MinZ, MaxZ, Environment.NewLine);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "尺寸(宽x深x高): {0:F3} x {1:F3} x {2:F3} 米{3}", SizeX, SizeY, SizeZ, Environment.NewLine);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "质心: ({0:F3}, {1:F3}, {2:F3}){3}", Cx, Cy, Cz, Environment.NewLine);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "到原点距离: 平均 {0:F3} 米, 最近 {1:F3} 米, 最远 {2:F3} 米{3}",
                MeanRange, MinRange, MaxRange, Environment.NewLine);
            int[] bins = new int[10];
            double h = Math.Max(SizeZ, 1e-6);
            for (int k = 0; k < Count; k++)
            {
                int b = (int)((Z[k] - MinZ) / h * 10);
                if (b < 0) b = 0; if (b > 9) b = 9;
                bins[b]++;
            }
            sb.Append("高度分布(自下而上 10 档, 每档约占 10%): ");
            for (int b = 0; b < 10; b++)
            {
                if (b > 0) sb.Append(", ");
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F1}%", bins[b] * 100.0 / Count);
            }
            sb.AppendLine();
            double voxel = 1.0; // 1m x 1m occupancy on XY
            HashSet<long> cells = new HashSet<long>();
            for (int k = 0; k < Count; k++)
            {
                long gx = (long)Math.Floor((X[k] - MinX) / voxel);
                long gy = (long)Math.Floor((Y[k] - MinY) / voxel);
                cells.Add(gx * 100000 + gy);
            }
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "XY 平面 1m 网格占用: {0} 格, 外接面积约 {1:F2} 平方米{2}",
                cells.Count, Math.Max(SizeX, 1e-3) * Math.Max(SizeY, 1e-3), Environment.NewLine);
            return sb.ToString();
        }


        // ---------------------------------------------------------------- selection / subsets
        public void ClearSelection() { Selected = null; }

        public int SelectedCount()
        {
            if (Selected == null) return 0;
            int n = 0;
            for (int i = 0; i < Count; i++) if (Selected[i] != 0) n++;
            return n;
        }

        public List<int> SelectedIndices(int limit)
        {
            List<int> list = new List<int>();
            if (Selected == null) return list;
            for (int i = 0; i < Count; i++)
            {
                if (Selected[i] == 0) continue;
                list.Add(i);
                if (limit > 0 && list.Count >= limit) break;
            }
            return list;
        }

        public PointCloud Subset(IList<int> indices, string name)
        {
            int n = indices.Count;
            float[] x = new float[n], y = new float[n], z = new float[n], i = new float[n];
            for (int k = 0; k < n; k++)
            {
                int idx = indices[k];
                x[k] = X[idx]; y[k] = Y[idx]; z[k] = Z[idx]; i[k] = Intensity[idx];
            }
            return FromArrays(name, x, y, z, i);
        }

        public PointCloud ClusterSubset(int clusterId, string name)
        {
            List<int> idx = new List<int>();
            if (Cluster != null)
                for (int i = 0; i < Count; i++) if (Cluster[i] == clusterId) idx.Add(i);
            return Subset(idx, name);
        }

        public void SelectCluster(int clusterId)
        {
            Selected = new byte[Count];
            if (Cluster == null) return;
            for (int i = 0; i < Count; i++) Selected[i] = (byte)(Cluster[i] == clusterId ? 1 : 0);
        }
        public void SaveCsv(string path)
        {
            using (StreamWriter sw = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("frame,seq,stamp_unix,x_m,y_m,z_m,intensity,time_s");
                double stamp = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                for (int k = 0; k < Count; k++)
                {
                    sw.Write(1); sw.Write(',');
                    sw.Write(0); sw.Write(',');
                    sw.Write(stamp.ToString("F6", CultureInfo.InvariantCulture)); sw.Write(',');
                    sw.Write(((double)X[k]).ToString("F4", CultureInfo.InvariantCulture)); sw.Write(',');
                    sw.Write(((double)Y[k]).ToString("F4", CultureInfo.InvariantCulture)); sw.Write(',');
                    sw.Write(((double)Z[k]).ToString("F4", CultureInfo.InvariantCulture)); sw.Write(',');
                    sw.Write(((int)Intensity[k])); sw.Write(',');
                    sw.WriteLine("0");
                }
            }
        }
    }
}
