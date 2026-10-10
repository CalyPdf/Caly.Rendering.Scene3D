namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// Loops in a surface's parameters that cross themselves by a sliver: the points of an edge that lies just off its patch are
/// inverted to the patch's border, a little past a corner and back, and the loop folds over itself there. The triangulator refuses
/// such a loop; the sliver is cut out (the part of the loop between the two segments that cross, when it is the small one).
/// </summary>
internal static class PrcLoopRepair
{
    /// <summary>How many slivers one loop may have cut out, and how many points it may have for the search (each costs a pass over every pair of its segments).</summary>
    private const int MaxCuts = 64, MaxPoints = 1024;

    /// <summary>Whether two segments of the loop that do not follow one another cross or touch.</summary>
    public static bool HasCrossing(List<double> u, List<double> v) => FindCrossing(u, v, out _, out _);

    /// <summary>
    /// Cuts out every sliver; false (the loop as it was, or with the slivers cut so far) when a crossing's smaller part is more than
    /// <paramref name="maxCut"/> of the loop's area, or the loop is too long to search.
    /// </summary>
    public static bool TryUntangle(List<double> u, List<double> v, List<PrcVector> points, double maxCut)
    {
        if (u.Count > MaxPoints)
        {
            return false;
        }

        for (int cut = 0; cut <= MaxCuts; cut++)
        {
            if (!FindCrossing(u, v, out int i, out int j))
            {
                return true;
            }

            if (cut == MaxCuts)
            {
                return false;
            }

            // The segments i -> i + 1 and j -> j + 1 cross at X: one part is X and the points i + 1 .. j, the other X and j + 1 .. i
            // (round the end). Without X a part of two points would have no area.
            int n = u.Count;
            Crossing(u, v, i, j, out double xu, out double xv);
            double inner = Math.Abs(Area(u, v, i + 1, j - i, xu, xv)), outer = Math.Abs(Area(u, v, j + 1, n - (j - i), xu, xv));
            bool innerIsSmall = inner <= outer;
            double small = innerIsSmall ? inner : outer;
            int keep = innerIsSmall ? n - (j - i) : j - i;
            if (small > maxCut * (inner + outer) || keep < 3)
            {
                return false;
            }

            if (innerIsSmall)
            {
                Remove(u, v, points, i + 1, j - i);
            }
            else
            {
                // Only i + 1 .. j stay.
                Remove(u, v, points, j + 1, n - j - 1);
                Remove(u, v, points, 0, i + 1);
            }
        }

        return false;
    }

    private static void Remove(List<double> u, List<double> v, List<PrcVector> points, int start, int count)
    {
        u.RemoveRange(start, count);
        v.RemoveRange(start, count);
        points.RemoveRange(start, count);
    }

    /// <summary>Twice the signed area of the polygon of the point (xu, xv) and <paramref name="count"/> points from <paramref name="start"/>, round the end of the loop.</summary>
    private static double Area(List<double> u, List<double> v, int start, int count, double xu, double xv)
    {
        int n = u.Count;
        double sum = 0, pu = xu, pv = xv;
        for (int k = 0; k < count; k++)
        {
            int a = (start + k) % n;
            sum += (pu * v[a]) - (u[a] * pv);
            pu = u[a];
            pv = v[a];
        }

        return sum + ((pu * xv) - (xu * pv));
    }

    /// <summary>Where the segments i -> i + 1 and j -> j + 1 meet (the middle of their overlap's ends when they are in line).</summary>
    private static void Crossing(List<double> u, List<double> v, int i, int j, out double xu, out double xv)
    {
        int n = u.Count, i2 = (i + 1) % n, j2 = (j + 1) % n;
        double ru = u[i2] - u[i], rv = v[i2] - v[i], su = u[j2] - u[j], sv = v[j2] - v[j];
        double d = (ru * sv) - (rv * su);
        if (d == 0)
        {
            xu = (u[i2] + u[j]) / 2;
            xv = (v[i2] + v[j]) / 2;
            return;
        }

        double t = Math.Clamp((((u[j] - u[i]) * sv) - ((v[j] - v[i]) * su)) / d, 0, 1);
        xu = u[i] + (t * ru);
        xv = v[i] + (t * rv);
    }

    /// <summary>The first two segments (i &lt; j) that do not follow one another and cross or touch.</summary>
    private static bool FindCrossing(List<double> u, List<double> v, out int first, out int second)
    {
        int n = u.Count;
        for (int i = 0; i < n; i++)
        {
            int i2 = i + 1 < n ? i + 1 : 0;
            double minU = Math.Min(u[i], u[i2]), maxU = Math.Max(u[i], u[i2]), minV = Math.Min(v[i], v[i2]), maxV = Math.Max(v[i], v[i2]);
            for (int j = i + 2; j < n; j++)
            {
                int j2 = j + 1 < n ? j + 1 : 0;
                if (j2 == i || Math.Max(u[j], u[j2]) < minU || Math.Min(u[j], u[j2]) > maxU || Math.Max(v[j], v[j2]) < minV || Math.Min(v[j], v[j2]) > maxV)
                {
                    continue;
                }

                // Segments that meet at one point given twice (the triangulator takes it as one) do not cross there.
                if (Same(u, v, i, j) || Same(u, v, i, j2) || Same(u, v, i2, j) || Same(u, v, i2, j2))
                {
                    continue;
                }

                if (Cross(u[i], v[i], u[i2], v[i2], u[j], v[j], u[j2], v[j2]))
                {
                    first = i;
                    second = j;
                    return true;
                }
            }
        }

        first = second = -1;
        return false;
    }

    private static bool Same(List<double> u, List<double> v, int a, int b) => u[a] == u[b] && v[a] == v[b];

    private static bool Cross(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        double o1 = Orient(ax, ay, bx, by, cx, cy), o2 = Orient(ax, ay, bx, by, dx, dy);
        double o3 = Orient(cx, cy, dx, dy, ax, ay), o4 = Orient(cx, cy, dx, dy, bx, by);
        return o1 * o2 <= 0 && o3 * o4 <= 0 && !(o1 == 0 && o2 == 0);
    }

    private static double Orient(double ax, double ay, double bx, double by, double cx, double cy) => ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
}
