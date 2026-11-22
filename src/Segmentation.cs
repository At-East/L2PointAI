// Point-cloud segmentation: voxel connected components (fast, no dependencies) + per-cluster metrics.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace L2PointAI
{
    public sealed class ClusterInfo
    {
        public int Id;
        public int Points;
        public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        public double Cx, Cy, Cz;
        public double MeanRange, MinRange, MaxRange;

        public double SizeX { get { return MaxX - MinX; } }
        public double SizeY { get { return MaxY - MinY; } }
        public double SizeZ { get { return MaxZ - MinZ; } }

        public string Short()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "#{0}  {1} 点  {2:F2}×{3:F2}×{4:F2} m  中心({5:F2},{6:F2},{7:F2})  最近{8:F2} m",
                Id, Points, SizeX, SizeY, SizeZ, Cx, Cy, Cz, MinRange);
        }
    }

    public static class CloudSegmenter
    {
        /// <summary>
        /// Voxel connected-component segmentation: points in the same or 26-neighbour voxel join one cluster.
        /// Returns clusters sorted by point count (desc) and writes per-point cluster ids (-1 = noise/small).
        /// </summary>
        public static List<ClusterInfo> Segment(PointCloud pc, double voxelSize, int minPoints, out int[] ids)
        {
            ids = new int[pc.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = -1;
            List<ClusterInfo> result = new List<ClusterInfo>();
            if (pc == null || pc.Count == 0) return result;

            if (voxelSize <= 0.001) voxelSize = 0.001;

            // ---- 1. bucket points into voxels
            Dictionary<long, int> voxelIndex = new Dictionary<long, int>(pc.Count / 4 + 16);
            List<int> voxelFirst = new List<int>(1024);   // first point of each voxel
            List<int> pointVoxel = new List<int>(pc.Count);
            List<long> voxelKey = new List<long>(1024);

            long nx = 1L << 21;   // wrap space into 21 bits per axis
            double inv = 1.0 / voxelSize;
            for (int i = 0; i < pc.Count; i++)
            {
                long gx = (long)Math.Floor(pc.X[i] * inv) + 1;
                long gy = (long)Math.Floor(pc.Y[i] * inv) + 1;
                long gz = (long)Math.Floor(pc.Z[i] * inv) + 1;
                long key = ((gx & (nx - 1)) << 42) | ((gy & (nx - 1)) << 21) | (gz & (nx - 1));
                int vi;
                if (!voxelIndex.TryGetValue(key, out vi))
                {
                    vi = voxelFirst.Count;
                    voxelIndex[key] = vi;
                    voxelFirst.Add(i);
                    voxelKey.Add(key);
                }
                pointVoxel.Add(vi);
            }

            // ---- 2. BFS over voxel adjacency (26-neighbourhood)
            int nv = voxelFirst.Count;
            int[] comp = new int[nv];
            for (int i = 0; i < nv; i++) comp[i] = -1;
            int compCount = 0;
            Queue<int> queue = new Queue<int>(1024);
            int[] vx = new int[nv], vy = new int[nv], vz = new int[nv];
            for (int i = 0; i < nv; i++)
            {
                long k = voxelKey[i];
                vx[i] = (int)((k >> 42) & (nx - 1));
                vy[i] = (int)((k >> 21) & (nx - 1));
                vz[i] = (int)(k & (nx - 1));
            }

            for (int s = 0; s < nv; s++)
            {
                if (comp[s] >= 0) continue;
                int c = compCount++;
                comp[s] = c;
                queue.Clear();
                queue.Enqueue(s);
                while (queue.Count > 0)
                {
                    int v = queue.Dequeue();
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                if (dx == 0 && dy == 0 && dz == 0) continue;
                                long key = (((long)((vx[v] + dx) & (nx - 1))) << 42)
                                         | (((long)((vy[v] + dy) & (nx - 1))) << 21)
                                         | (long)((vz[v] + dz) & (nx - 1));
                                int w;
                                if (!voxelIndex.TryGetValue(key, out w)) continue;
                                if (comp[w] >= 0) continue;
                                comp[w] = c;
                                queue.Enqueue(w);
                            }
                }
            }

            // ---- 3. per-point ids, count, filter
            int[] perCompPoints = new int[compCount];
            for (int i = 0; i < pc.Count; i++)
            {
                int c = comp[pointVoxel[i]];
                ids[i] = c;
                perCompPoints[c]++;
            }

            List<int> keep = new List<int>();
            for (int c = 0; c < compCount; c++) if (perCompPoints[c] >= minPoints) keep.Add(c);

            // ---- 4. metrics
            for (int n = 0; n < keep.Count; n++)
            {
                int c = keep[n];
                ClusterInfo info = new ClusterInfo { Id = n };
                info.MinX = info.MinY = info.MinZ = float.MaxValue;
                info.MaxX = info.MaxY = info.MaxZ = float.MinValue;
                info.MinRange = double.MaxValue;
                double sx = 0, sy = 0, sz = 0, sr = 0;
                int cnt = 0;
                for (int i = 0; i < pc.Count; i++)
                {
                    if (ids[i] != c) continue;
                    float x = pc.X[i], y = pc.Y[i], z = pc.Z[i];
                    if (x < info.MinX) info.MinX = x; if (x > info.MaxX) info.MaxX = x;
                    if (y < info.MinY) info.MinY = y; if (y > info.MaxY) info.MaxY = y;
                    if (z < info.MinZ) info.MinZ = z; if (z > info.MaxZ) info.MaxZ = z;
                    sx += x; sy += y; sz += z;
                    double r = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
                    sr += r;
                    if (r < info.MinRange) info.MinRange = r;
                    if (r > info.MaxRange) info.MaxRange = r;
                    cnt++;
                }
                if (cnt == 0) continue;
                info.Points = cnt;
                info.Cx = sx / cnt; info.Cy = sy / cnt; info.Cz = sz / cnt;
                info.MeanRange = sr / cnt;
                result.Add(info);
            }

            // renumber: ids become 0..n-1 over kept clusters, others -1
            Dictionary<int, int> remap = new Dictionary<int, int>();
            for (int n = 0; n < keep.Count; n++) remap[keep[n]] = n;
            for (int i = 0; i < ids.Length; i++)
            {
                int nid;
                if (ids[i] >= 0 && remap.TryGetValue(ids[i], out nid)) ids[i] = nid;
                else ids[i] = -1;
            }

            result.Sort(delegate (ClusterInfo a, ClusterInfo b) { return b.Points.CompareTo(a.Points); });
            // re-sort also renumbers ids to match list order
            Dictionary<int, int> finalMap = new Dictionary<int, int>();
            for (int n = 0; n < result.Count; n++) finalMap[result[n].Id] = n;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] >= 0) ids[i] = finalMap[ids[i]];
            for (int n = 0; n < result.Count; n++) result[n].Id = n;

            return result;
        }

        /// <summary>Metrics for an arbitrary subset of points (selection / cluster / measurement ROI).</summary>
        public static ClusterInfo Describe(PointCloud pc, IList<int> indices, int id)
        {
            ClusterInfo info = new ClusterInfo { Id = id, Points = indices.Count };
            info.MinX = info.MinY = info.MinZ = float.MaxValue;
            info.MaxX = info.MaxY = info.MaxZ = float.MinValue;
            info.MinRange = double.MaxValue;
            double sx = 0, sy = 0, sz = 0, sr = 0;
            foreach (int i in indices)
            {
                float x = pc.X[i], y = pc.Y[i], z = pc.Z[i];
                if (x < info.MinX) info.MinX = x; if (x > info.MaxX) info.MaxX = x;
                if (y < info.MinY) info.MinY = y; if (y > info.MaxY) info.MaxY = y;
                if (z < info.MinZ) info.MinZ = z; if (z > info.MaxZ) info.MaxZ = z;
                sx += x; sy += y; sz += z;
                double r = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
                sr += r;
                if (r < info.MinRange) info.MinRange = r;
                if (r > info.MaxRange) info.MaxRange = r;
            }
            if (indices.Count == 0) { info.Points = 0; return info; }
            info.Cx = sx / indices.Count; info.Cy = sy / indices.Count; info.Cz = sz / indices.Count;
            info.MeanRange = sr / indices.Count;
            return info;
        }
    }
}
