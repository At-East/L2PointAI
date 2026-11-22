// Local plane fitting (PCA) used by the "point to plane" measurement.
using System;

namespace L2PointAI
{
    public static class PlaneFit
    {
        /// <summary>
        /// Fit a plane to the points around `index` within `radius` (capped at 600 samples).
        /// Returns false when there are too few neighbours or the fit is degenerate.
        /// </summary>
        public static bool Around(PointCloud pc, int index, double radius, out double cx, out double cy, out double cz,
                                  out double nx, out double ny, out double nz, out int used)
        {
            cx = pc.X[index]; cy = pc.Y[index]; cz = pc.Z[index];
            nx = 0; ny = 0; nz = 1;
            used = 0;
            if (pc == null || index < 0 || index >= pc.Count) return false;

            double r2 = radius * radius;
            double sx = 0, sy = 0, sz = 0;
            int n = 0;
            // first pass: centroid
            for (int i = 0; i < pc.Count; i++)
            {
                double dx = pc.X[i] - pc.X[index], dy = pc.Y[i] - pc.Y[index], dz = pc.Z[i] - pc.Z[index];
                if (dx * dx + dy * dy + dz * dz > r2) continue;
                sx += pc.X[i]; sy += pc.Y[i]; sz += pc.Z[i];
                if (++n >= 600) break;
            }
            if (n < 8) return false;
            cx = sx / n; cy = sy / n; cz = sz / n;

            // second pass: covariance
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            int m = 0;
            for (int i = 0; i < pc.Count; i++)
            {
                double dx = pc.X[i] - pc.X[index], dy = pc.Y[i] - pc.Y[index], dz = pc.Z[i] - pc.Z[index];
                if (dx * dx + dy * dy + dz * dz > r2) continue;
                double ax = pc.X[i] - cx, ay = pc.Y[i] - cy, az = pc.Z[i] - cz;
                xx += ax * ax; xy += ax * ay; xz += ax * az;
                yy += ay * ay; yz += ay * az; zz += az * az;
                if (++m >= 600) break;
            }
            if (m < 8) return false;

            double[,] a = { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            double[] eval = new double[3];
            double[,] evec = new double[3, 3];
            Jacobi(a, eval, evec);

            int minIdx = 0;
            if (eval[1] < eval[minIdx]) minIdx = 1;
            if (eval[2] < eval[minIdx]) minIdx = 2;

            nx = evec[0, minIdx]; ny = evec[1, minIdx]; nz = evec[2, minIdx];
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9) return false;
            nx /= len; ny /= len; nz /= len;
            used = n;
            return true;
        }

        /// <summary>Jacobi eigen decomposition for a symmetric 3x3 matrix.</summary>
        static void Jacobi(double[,] a, double[] eval, double[,] evec)
        {
            double[,] v = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) v[i, j] = (i == j) ? 1 : 0;

            for (int sweep = 0; sweep < 24; sweep++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-12) break;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-15) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1);
                        double s = t * c;
                        double app = a[p, p], aqq = a[q, q], apq = a[p, q];
                        a[p, p] = app - t * apq;
                        a[q, q] = aqq + t * apq;
                        a[p, q] = a[q, p] = 0;
                        for (int k = 0; k < 3; k++)
                        {
                            if (k != p && k != q)
                            {
                                double akp = a[k, p], akq = a[k, q];
                                a[k, p] = a[p, k] = c * akp - s * akq;
                                a[k, q] = a[q, k] = s * akp + c * akq;
                            }
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            for (int i = 0; i < 3; i++)
            {
                eval[i] = a[i, i];
                for (int j = 0; j < 3; j++) evec[i, j] = v[i, j];
            }
        }
    }
}
    public enum MeasureKind { Distance3D = 0, Horizontal = 1, PointToPlane = 2 }

    public sealed class MeasureResult
    {
        public int Mode;              // MeasureKind, or -1 for "first point picked"
        public double Distance;
        public double Dx, Dy, Dz;
        public double Ax, Ay, Az, Bx, By, Bz;
        public double Nx, Ny, Nz;     // plane normal (point-to-plane)
        public double PlanePoints;    // neighbours used by the plane fit
        public string Label = "";
    }