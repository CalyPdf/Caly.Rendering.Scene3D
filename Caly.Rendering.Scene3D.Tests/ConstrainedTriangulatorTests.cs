using Caly.Rendering.Scene3D.Geometry;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>The triangulation of a polygon with holes and points inside it, in the plane.</summary>
public sealed class ConstrainedTriangulatorTests
{
    /// <summary>Triangulates; <c>loopStarts</c> has one more entry than there are loops: where the free points start.</summary>
    private static List<int> Run(double[] points, int[] loopStarts)
    {
        var triangles = new List<int>();
        Assert.True(new ConstrainedTriangulator().Triangulate(points, loopStarts, loopStarts.Length - 1, points.Length / 2, 1_000_000, triangles));
        return triangles;
    }

    private static double Area(double[] p, List<int> t)
    {
        double area = 0;
        for (int i = 0; i < t.Count; i += 3)
        {
            double ax = p[2 * t[i]], ay = p[(2 * t[i]) + 1], bx = p[2 * t[i + 1]], by = p[(2 * t[i + 1]) + 1], cx = p[2 * t[i + 2]], cy = p[(2 * t[i + 2]) + 1];
            double s = (((bx - ax) * (cy - ay)) - ((cx - ax) * (by - ay))) / 2;
            Assert.True(s > 0, "every triangle counter-clockwise and of positive area");
            area += s;
        }

        return area;
    }

    private static bool HasEdge(List<int> t, int a, int b)
    {
        for (int k = 0; k < t.Count; k += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                int u = t[k + e], v = t[k + ((e + 1) % 3)];
                if ((u == a && v == b) || (u == b && v == a))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void ASquareIsTwoTriangles()
    {
        double[] p = [0, 0, 1, 0, 1, 1, 0, 1];

        List<int> t = Run(p, [0, 4]);

        Assert.Equal(6, t.Count);
        Assert.Equal(1.0, Area(p, t), 12);
    }

    [Fact]
    public void ASquareWithASquareHoleCoversOnlyTheRing()
    {
        double[] p = [0, 0, 4, 0, 4, 4, 0, 4, 1, 1, 1, 3, 3, 3, 3, 1];

        List<int> t = Run(p, [0, 4, 8]);

        Assert.Equal(16.0 - 4.0, Area(p, t), 12);
        Assert.All(t, i => Assert.InRange(i, 0, 7));
    }

    [Fact]
    public void FreePointsInsideAreUsedAndNoneOutside()
    {
        double[] p = [0, 0, 2, 0, 2, 2, 0, 2, 1, 1, 0.5, 1.5];

        List<int> t = Run(p, [0, 4]);

        Assert.Contains(4, t);
        Assert.Contains(5, t);
        Assert.Equal(4.0, Area(p, t), 12);
    }

    [Fact]
    public void AFreePointOutsideTheLoopIsLeftOut()
    {
        double[] p = [0, 0, 2, 0, 2, 2, 0, 2, 5, 5, 1, 1];

        List<int> t = Run(p, [0, 4]);

        Assert.DoesNotContain(4, t);
        Assert.Contains(5, t);
        Assert.Equal(4.0, Area(p, t), 12);
    }

    [Fact]
    public void AFreePointInsideAHoleIsLeftOutToo()
    {
        double[] p = [0, 0, 4, 0, 4, 4, 0, 4, 1, 1, 1, 3, 3, 3, 3, 1, 2, 2];

        List<int> t = Run(p, [0, 4, 8]);

        Assert.DoesNotContain(8, t);
        Assert.Equal(12.0, Area(p, t), 12);
    }

    [Fact]
    public void EveryBoundarySegmentIsAnEdgeOfATriangleEvenWhenDelaunayWouldCrossIt()
    {
        // A long thin notch: the segment from (10,1) to (5,0.05) and the one from (5,0.05) to (0,1) are not Delaunay edges of these points.
        double[] p = [0, 0, 10, 0, 10, 1, 5, 0.05, 0, 1];

        List<int> t = Run(p, [0, 5]);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(HasEdge(t, i, (i + 1) % 5), $"segment {i}-{(i + 1) % 5}");
        }

        Assert.Equal(Math.Abs(ShoelaceArea(p, 5)), Area(p, t), 9);
    }

    private static double ShoelaceArea(double[] p, int count)
    {
        double a = 0;
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            a += (p[2 * i] * p[(2 * j) + 1]) - (p[2 * j] * p[(2 * i) + 1]);
        }

        return a / 2;
    }

    [Fact]
    public void ACollinearRunAndADuplicatePointAreHandled()
    {
        double[] p = [0, 0, 1, 0, 2, 0, 3, 0, 3, 1, 3, 1, 0, 1];

        List<int> t = Run(p, [0, 7]);

        Assert.Equal(3.0, Area(p, t), 12);
    }

    [Fact]
    public void ALoopThatCrossesItselfIsRefused()
    {
        double[] p = [0, 0, 1, 1, 1, 0, 0, 1];

        Assert.False(new ConstrainedTriangulator().Triangulate(p, [0, 4], 1, 4, 100_000, []));
    }

    [Fact]
    public void AHoleThatCrossesTheOuterLoopIsRefused()
    {
        double[] p = [0, 0, 4, 0, 4, 4, 0, 4, 3, 1, 5, 1, 5, 3, 3, 3];

        Assert.False(new ConstrainedTriangulator().Triangulate(p, [0, 4, 8], 2, 8, 100_000, []));
    }

    [Fact]
    public void ANonConvexPolygonIsTriangulatedInsideItselfOnly()
    {
        // An L: the corner (2,2) of the bounding box is not inside.
        double[] p = [0, 0, 2, 0, 2, 1, 1, 1, 1, 2, 0, 2];

        List<int> t = Run(p, [0, 6]);

        Assert.Equal(3.0, Area(p, t), 12);
    }

    [Fact]
    public void TheStepBoundStopsAWorkThatWouldGoOn()
    {
        double[] p = [0, 0, 1, 0, 1, 1, 0, 1, 0.5, 0.5];

        Assert.False(new ConstrainedTriangulator().Triangulate(p, [0, 4], 1, 5, 0, []));
    }

    [Fact]
    public void ReusingOneTriangulatorGivesTheSameResultsAsFreshOnes()
    {
        var reused = new ConstrainedTriangulator();
        double[] a = [0, 0, 1, 0, 1, 1, 0, 1, 0.5, 0.5];
        double[] b = [0, 0, 5, 0, 5, 5, 0, 5, 1, 1, 4, 1, 4, 4, 1, 4];
        var first = new List<int>();
        var second = new List<int>();
        var freshA = new List<int>();
        Assert.True(reused.Triangulate(a, [0, 4], 1, 5, 1_000_000, first));
        Assert.True(reused.Triangulate(b, [0, 4, 8], 2, 8, 1_000_000, second));
        Assert.True(new ConstrainedTriangulator().Triangulate(a, [0, 4], 1, 5, 1_000_000, freshA));

        Assert.Equal(freshA, first);
        Assert.Equal(25.0 - 9.0, Area(b, second), 12);
    }

    private static bool Inside(double[] p, int start, int end, double x, double y)
    {
        bool inside = false;
        for (int i = start, j = end - 1; i < end; j = i++)
        {
            double xi = p[2 * i], yi = p[(2 * i) + 1], xj = p[2 * j], yj = p[(2 * j) + 1];
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RandomStarsWithAHoleAndFreePointsAreTriangulatedExactly(int seed)
    {
        // A star-shaped outer loop of 60 vertices, a smaller star-shaped hole, 400 free points in the ring between: the triangles cover
        // the ring exactly, every segment of both loops is an edge, and every free point is used.
        var random = new Random(seed);
        const int outer = 60, hole = 20, free = 400;
        var p = new List<double>();
        void Star(int count, double inner, double outerRadius)
        {
            var angles = Enumerable.Range(0, count).Select(i => ((i + random.NextDouble() * 0.9) / count) * 2 * Math.PI).ToArray();
            foreach (double a in angles)
            {
                double r = inner + (random.NextDouble() * (outerRadius - inner));
                p.Add(r * Math.Cos(a));
                p.Add(r * Math.Sin(a));
            }
        }

        Star(outer, 6, 10);
        Star(hole, 1, 3);
        double[] pts = [.. p];
        var list = new List<double>(pts);
        int added = 0;
        while (added < free)
        {
            double x = (random.NextDouble() * 20) - 10, y = (random.NextDouble() * 20) - 10;
            if (Inside(pts, 0, outer, x, y) && !Inside(pts, outer, outer + hole, x, y))
            {
                list.Add(x);
                list.Add(y);
                added++;
            }
        }

        double[] all = [.. list];
        List<int> t = Run(all, [0, outer, outer + hole]);

        double expected = Math.Abs(ShoelaceArea(all, outer)) - Math.Abs(ShoelaceAreaOf(all, outer, hole));
        Assert.Equal(expected, Area(all, t), 7);
        for (int i = 0; i < outer; i++)
        {
            Assert.True(HasEdge(t, i, (i + 1) % outer), $"outer segment {i}");
        }

        for (int i = 0; i < hole; i++)
        {
            Assert.True(HasEdge(t, outer + i, outer + ((i + 1) % hole)), $"hole segment {i}");
        }

        var used = new HashSet<int>(t);
        for (int i = outer + hole; i < outer + hole + free; i++)
        {
            Assert.Contains(i, used);
        }
    }

    private static double ShoelaceAreaOf(double[] p, int start, int count)
    {
        double a = 0;
        for (int i = 0; i < count; i++)
        {
            int u = start + i, v = start + ((i + 1) % count);
            a += (p[2 * u] * p[(2 * v) + 1]) - (p[2 * v] * p[(2 * u) + 1]);
        }

        return a / 2;
    }

    [Fact]
    public void PointsOnCirclesAreDecidedWithoutAllocatingForEachTest()
    {
        // A disc with a hole, both circles of 2,000 points: every four neighbours on a circle are on one circle, which the double
        // filter cannot decide. Phase B: the exact tests ran in BigInteger, allocating for each (1.5 GB for one model).
        const int n = 2000;
        var p = new double[2 * (2 * n)];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            p[2 * i] = 10 * Math.Cos(a);
            p[(2 * i) + 1] = 10 * Math.Sin(a);
            p[2 * (n + i)] = 3 * Math.Cos(-a);
            p[(2 * (n + i)) + 1] = 3 * Math.Sin(-a);
        }

        var triangulator = new ConstrainedTriangulator();
        var t = new List<int>();
        Assert.True(triangulator.Triangulate(p, [0, n, 2 * n], 2, 2 * n, 100_000_000, t));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(triangulator.Triangulate(p, [0, n, 2 * n], 2, 2 * n, 100_000_000, t));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Math.PI * (100 - 9), Area(p, t), 0);
        Assert.True(allocated < 1_000_000, $"{allocated} bytes for {2 * n} points");
    }

    [Fact]
    public void ManyPointsStayFastAndDelaunay()
    {
        // The unit square, then a 200 x 200 grid of free points strictly inside it, jittered so that no four are cocircular.
        const int n = 200;
        var p = new double[2 * (4 + (n * n))];
        double[] square = [0, 0, 1, 0, 1, 1, 0, 1];
        square.CopyTo(p, 0);
        var random = new Random(5);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                int k = 4 + (i * n) + j;
                p[2 * k] = (i + 0.5 + ((random.NextDouble() - 0.5) * 0.2)) / n;
                p[(2 * k) + 1] = (j + 0.5 + ((random.NextDouble() - 0.5) * 0.2)) / n;
            }
        }

        var t = new List<int>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(new ConstrainedTriangulator().Triangulate(p, [0, 4], 1, p.Length / 2, 100_000_000, t));
        watch.Stop();
#if !DEBUG
        Assert.True(watch.ElapsedMilliseconds < 1500, $"{watch.ElapsedMilliseconds} ms");
#endif
        Assert.Equal(1.0, Area(p, t), 9);

        // Empty circumcircles: every 40th triangle against every point.
        for (int k = 0; k < t.Count; k += 3 * 40)
        {
            double ax = p[2 * t[k]], ay = p[(2 * t[k]) + 1], bx = p[2 * t[k + 1]], by = p[(2 * t[k + 1]) + 1], cx = p[2 * t[k + 2]], cy = p[(2 * t[k + 2]) + 1];
            for (int q = 0; q < p.Length / 2; q++)
            {
                if (q == t[k] || q == t[k + 1] || q == t[k + 2])
                {
                    continue;
                }

                double dx = p[2 * q], dy = p[(2 * q) + 1];
                double adx = ax - dx, ady = ay - dy, bdx = bx - dx, bdy = by - dy, cdx = cx - dx, cdy = cy - dy;
                double det = (((adx * adx) + (ady * ady)) * ((bdx * cdy) - (cdx * bdy)))
                           - (((bdx * bdx) + (bdy * bdy)) * ((adx * cdy) - (cdx * ady)))
                           + (((cdx * cdx) + (cdy * cdy)) * ((adx * bdy) - (bdx * ady)));
                Assert.True(det <= 1e-15, $"point {q} inside the circumcircle of triangle {k / 3}");
            }
        }
    }
}
