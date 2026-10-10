using System.Numerics;

namespace Caly.Rendering.Scene3D.Geometry;

/// <summary>
/// Triangulates a polygon with holes, with points inside it, in the plane: a Delaunay triangulation of all the points in which
/// every segment of the loops is an edge, with the triangles outside the outer loop and inside the holes left out.
/// <para>
/// The points are put in by incremental insertion with the flips that keep the triangulation Delaunay; each segment of a loop that
/// is not an edge then is made one by flipping the edges that cross it (Sloan's method); the triangles are told apart by how many
/// segments of a loop one crosses to reach them from outside. The tests of orientation and of the circle are exact: a filter in
/// double precision decides what it can, the rest is decided in integers.
/// </para>
/// <para>
/// It is reusable: its buffers are kept between calls. It knows nothing of what the points stand for.
/// </para>
/// </summary>
internal sealed class ConstrainedTriangulator
{
    /// <summary>
    /// The points are put on a grid of this many steps across the unit square (2^24, sixty billionths of the extent), and the corners
    /// of the triangle that holds them all on it too, <see cref="Far"/> squares off: every exact test is then in integers that fit in
    /// 128 bits (differences under 2^31, lifts and products of two under 2^62, the circle test under 2^124), without allocating.
    /// </summary>
    private const double Grid = 16777216;

    /// <summary>How far the corners of the triangle that holds everything lie, in units of the points' extent (far enough for nothing:
    /// what lies outside the loops is carved away, and <see cref="Legalize"/> makes what is inside Delaunay whatever the corners did).</summary>
    private const double Far = 32;

    private double[] _x = [];
    private double[] _y = [];
    private long[] _ix = [];
    private long[] _iy = [];
    private readonly Dictionary<(long, long), int> _seen = [];
    private readonly List<(ulong Key, int Index)> _order = [];
    private readonly Stack<int> _stack = new();
    private readonly Stack<(int, int)> _pairs = new();
    private readonly Queue<(int, int)> _queue = new();
    private readonly List<(int, int)> _crossing = [];
    private int[] _depth = [];
    private int[] _deque = [];
    private int[] _canonical = [];
    private int[] _vertexTriangle = [];
    private int[] _tv = [];          // vertices of triangle t: 3t, 3t+1, 3t+2 (counter-clockwise)
    private int[] _tn = [];          // neighbour across the edge from vertex k to vertex k+1 of triangle t, or -1
    private bool[] _tc = [];         // whether that edge is a segment of a loop
    private int _triangleCount;
    private int _pointCount;         // the points of the caller, plus three of the super-triangle
    private int _steps;
    private int _maxSteps;
    private int _lastTriangle;
    private bool _exhausted;

    /// <summary>The corners of the triangle that holds all the points are numbered from here (<c>_pointCount</c>).</summary>
    private int Super => _pointCount;

    /// <param name="points">x, y pairs. The loops come first, each as consecutive points that close on the first; then the free points.</param>
    /// <param name="loopStarts">
    /// The index of the first point of each loop, then one more entry: where the free points start. The first loop is the outer
    /// one; the others are holes (their direction does not matter).
    /// </param>
    /// <param name="loopCount">The number of loops.</param>
    /// <param name="pointCount">The number of points in <paramref name="points"/>.</param>
    /// <param name="maxSteps">A bound on the work (insertions, flips, walks); running out returns false.</param>
    /// <param name="triangles">Receives the triangles as index triples into <paramref name="points"/>, counter-clockwise; it is cleared first.</param>
    /// <returns>False when the loops cross each other or themselves, or the work exceeded <paramref name="maxSteps"/>.</returns>
    public bool Triangulate(ReadOnlySpan<double> points, ReadOnlySpan<int> loopStarts, int loopCount, int pointCount, int maxSteps, List<int> triangles)
    {
        triangles.Clear();
        if (loopCount < 1 || loopStarts.Length < loopCount + 1 || pointCount < 3 || points.Length < 2 * pointCount)
        {
            return false;
        }

        _maxSteps = maxSteps;
        _steps = 0;
        _exhausted = false;
        Load(points, pointCount);
        if (!Insert())
        {
            return false;
        }

        for (int l = 0; l < loopCount; l++)
        {
            int start = loopStarts[l], end = loopStarts[l + 1];
            if (start < 0 || end > pointCount || end - start < 3)
            {
                return false;
            }

            for (int i = start; i < end; i++)
            {
                int a = _canonical[i], b = _canonical[i + 1 < end ? i + 1 : start];
                if (!InsertSegment(a, b))
                {
                    return false;
                }
            }
        }

        if (!Legalize())
        {
            return false;
        }

        Carve(triangles);
        return !_exhausted;
    }

    private bool Step()
    {
        if (++_steps > _maxSteps)
        {
            _exhausted = true;
            return false;
        }

        return true;
    }

    // ---- set-up

    /// <summary>
    /// Copies the points, scaled into the unit square (so that the bounds below mean the same for any size), finds the points that
    /// coincide, and makes the triangle that holds them all.
    /// </summary>
    private void Load(ReadOnlySpan<double> points, int pointCount)
    {
        _pointCount = pointCount;
        int capacity = pointCount + 3;
        if (_x.Length < capacity)
        {
            _x = new double[capacity];
            _y = new double[capacity];
            _ix = new long[capacity];
            _iy = new long[capacity];
            _canonical = new int[capacity];
            _vertexTriangle = new int[capacity];
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < pointCount; i++)
        {
            double x = points[2 * i], y = points[(2 * i) + 1];
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        // Into the unit square, on the grid: every coordinate is then an integer number of steps, which the exact tests use, and the
        // double filter sees the same values.
        double extent = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-300);
        for (int i = 0; i < pointCount; i++)
        {
            double x = Math.Round((points[2 * i] - minX) / extent * Grid), y = Math.Round((points[(2 * i) + 1] - minY) / extent * Grid);
            _ix[i] = double.IsFinite(x) ? (long)Math.Clamp(x, 0, Grid) : 0;
            _iy[i] = double.IsFinite(y) ? (long)Math.Clamp(y, 0, Grid) : 0;
            _x[i] = _ix[i] / Grid;
            _y[i] = _iy[i] / Grid;
            _canonical[i] = i;
            _vertexTriangle[i] = -1;
        }

        // Points on the same step of the grid are one point.
        _seen.Clear();
        _seen.EnsureCapacity(pointCount);
        for (int i = 0; i < pointCount; i++)
        {
            (long, long) key = (_ix[i], _iy[i]);
            if (_seen.TryGetValue(key, out int first))
            {
                _canonical[i] = first;
            }
            else
            {
                _seen.Add(key, i);
            }
        }

        // The triangle that holds all the points, its corners on the grid.
        (double X, double Y)[] corners = [(-Far, -Far), (Far + 1, -Far), (0.5, Far)];
        for (int s = 0; s < 3; s++)
        {
            _ix[Super + s] = (long)(corners[s].X * Grid);
            _iy[Super + s] = (long)(corners[s].Y * Grid);
            _x[Super + s] = _ix[Super + s] / Grid;
            _y[Super + s] = _iy[Super + s] / Grid;
            _canonical[Super + s] = Super + s;
            _vertexTriangle[Super + s] = -1;
        }

        int triangleCapacity = (2 * pointCount) + 16;
        if (_tv.Length < 3 * triangleCapacity)
        {
            _tv = new int[3 * triangleCapacity];
            _tn = new int[3 * triangleCapacity];
            _tc = new bool[3 * triangleCapacity];
        }

        _triangleCount = 0;
        NewTriangle(Super, Super + 1, Super + 2, -1, -1, -1);
        _lastTriangle = 0;
    }

    private int NewTriangle(int a, int b, int c, int nab, int nbc, int nca)
    {
        int t = _triangleCount++;
        if (3 * (t + 1) > _tv.Length)
        {
            Array.Resize(ref _tv, _tv.Length * 2);
            Array.Resize(ref _tn, _tn.Length * 2);
            Array.Resize(ref _tc, _tc.Length * 2);
        }

        Set(t, a, b, c, nab, nbc, nca, false, false, false);
        return t;
    }

    private void Set(int t, int a, int b, int c, int nab, int nbc, int nca, bool cab, bool cbc, bool cca)
    {
        _tv[3 * t] = a;
        _tv[(3 * t) + 1] = b;
        _tv[(3 * t) + 2] = c;
        _tn[3 * t] = nab;
        _tn[(3 * t) + 1] = nbc;
        _tn[(3 * t) + 2] = nca;
        _tc[3 * t] = cab;
        _tc[(3 * t) + 1] = cbc;
        _tc[(3 * t) + 2] = cca;
        _vertexTriangle[a] = t;
        _vertexTriangle[b] = t;
        _vertexTriangle[c] = t;
    }

    // ---- predicates

    /// <summary>The sign of twice the area of the triangle a, b, c: positive when it is counter-clockwise.</summary>
    private int Orient(int a, int b, int c)
    {
        double ax = _x[a], ay = _y[a];
        double acx = ax - _x[c], acy = ay - _y[c], bcx = _x[b] - _x[c], bcy = _y[b] - _y[c];
        double left = acx * bcy, right = acy * bcx;
        double det = left - right;
        double bound = 3.3306690738754716e-16 * (Math.Abs(left) + Math.Abs(right));
        if (det > bound)
        {
            return 1;
        }

        if (-det > bound)
        {
            return -1;
        }

        return ExactOrient(a, b, c);
    }

    /// <summary>Positive when d is inside the circle through the counter-clockwise triangle a, b, c; negative outside; zero on it.</summary>
    private int InCircle(int a, int b, int c, int d)
    {
        double adx = _x[a] - _x[d], ady = _y[a] - _y[d];
        double bdx = _x[b] - _x[d], bdy = _y[b] - _y[d];
        double cdx = _x[c] - _x[d], cdy = _y[c] - _y[d];
        double bdxcdy = bdx * cdy, cdxbdy = cdx * bdy, alift = (adx * adx) + (ady * ady);
        double cdxady = cdx * ady, adxcdy = adx * cdy, blift = (bdx * bdx) + (bdy * bdy);
        double adxbdy = adx * bdy, bdxady = bdx * ady, clift = (cdx * cdx) + (cdy * cdy);
        double det = (alift * (bdxcdy - cdxbdy)) + (blift * (cdxady - adxcdy)) + (clift * (adxbdy - bdxady));
        double permanent = ((Math.Abs(bdxcdy) + Math.Abs(cdxbdy)) * alift) + ((Math.Abs(cdxady) + Math.Abs(adxcdy)) * blift) + ((Math.Abs(adxbdy) + Math.Abs(bdxady)) * clift);
        double bound = 1.1102230246251577e-15 * permanent;
        if (det > bound)
        {
            return 1;
        }

        if (-det > bound)
        {
            return -1;
        }

        return ExactInCircle(a, b, c, d);
    }

    private int ExactOrient(int a, int b, int c)
    {
        long acx = _ix[a] - _ix[c], acy = _iy[a] - _iy[c], bcx = _ix[b] - _ix[c], bcy = _iy[b] - _iy[c];
        return Math.Sign((acx * bcy) - (acy * bcx));
    }

    private int ExactInCircle(int a, int b, int c, int d) => GridInCircle(a, b, c, d);

    /// <summary>The circle test on the grid, in 128-bit integers (see <see cref="Grid"/> for the bounds).</summary>
    private int GridInCircle(int a, int b, int c, int d)
    {
        long adx = _ix[a] - _ix[d], ady = _iy[a] - _iy[d], bdx = _ix[b] - _ix[d], bdy = _iy[b] - _iy[d], cdx = _ix[c] - _ix[d], cdy = _iy[c] - _iy[d];
        Int128 alift = ((Int128)adx * adx) + ((Int128)ady * ady), blift = ((Int128)bdx * bdx) + ((Int128)bdy * bdy), clift = ((Int128)cdx * cdx) + ((Int128)cdy * cdy);
        Int128 det = (alift * (((Int128)bdx * cdy) - ((Int128)cdx * bdy))) + (blift * (((Int128)cdx * ady) - ((Int128)adx * cdy))) + (clift * (((Int128)adx * bdy) - ((Int128)bdx * ady)));
        return det == 0 ? 0 : det > 0 ? 1 : -1;
    }

    // ---- incremental insertion

    private bool Insert()
    {
        // The points in an order that keeps neighbours together (a Morton curve), so that each is found by a short walk from the last.
        int n = _pointCount;
        List<(ulong Key, int Index)> order = _order;
        order.Clear();
        for (int i = 0; i < n; i++)
        {
            if (_canonical[i] == i)
            {
                order.Add((Morton((uint)Math.Clamp(_x[i] * 65535, 0, 65535), (uint)Math.Clamp(_y[i] * 65535, 0, 65535)), i));
            }
        }

        order.Sort((p, q) => p.Key.CompareTo(q.Key));
        foreach ((_, int index) in order)
        {
            if (!Step() || !InsertPoint(index))
            {
                return false;
            }
        }

        return true;
    }

    private static ulong Morton(uint x, uint y)
    {
        static ulong Spread(uint v)
        {
            ulong w = v;
            w = (w | (w << 16)) & 0x0000FFFF0000FFFFUL;
            w = (w | (w << 8)) & 0x00FF00FF00FF00FFUL;
            w = (w | (w << 4)) & 0x0F0F0F0F0F0F0F0FUL;
            w = (w | (w << 2)) & 0x3333333333333333UL;
            w = (w | (w << 1)) & 0x5555555555555555UL;
            return w;
        }

        return Spread(x) | (Spread(y) << 1);
    }

    /// <summary>The triangle that holds the point, by a walk from the last one found. Returns where it lies on it: -1 inside, 0 – 2 on that edge, 3 – 5 on that vertex (minus 3).</summary>
    private bool Locate(int p, out int triangle, out int where)
    {
        int t = _lastTriangle;
        int limit = (3 * _triangleCount) + 16;
        for (int walked = 0; walked < limit; walked++)
        {
            int a = _tv[3 * t], b = _tv[(3 * t) + 1], c = _tv[(3 * t) + 2];
            int o0 = Orient(a, b, p), o1 = Orient(b, c, p), o2 = Orient(c, a, p);
            int next = -1;
            if (o0 < 0)
            {
                next = _tn[3 * t];
            }
            else if (o1 < 0)
            {
                next = _tn[(3 * t) + 1];
            }
            else if (o2 < 0)
            {
                next = _tn[(3 * t) + 2];
            }
            else
            {
                triangle = t;
                if (o0 == 0 && o1 == 0)
                {
                    where = 4;      // the vertex b
                }
                else if (o1 == 0 && o2 == 0)
                {
                    where = 5;      // c
                }
                else if (o2 == 0 && o0 == 0)
                {
                    where = 3;      // a
                }
                else
                {
                    where = o0 == 0 ? 0 : o1 == 0 ? 1 : o2 == 0 ? 2 : -1;
                }

                _lastTriangle = t;
                return true;
            }

            if (next < 0)
            {
                break;
            }

            t = next;
        }

        triangle = -1;
        where = -1;
        return false;
    }

    private bool InsertPoint(int p)
    {
        if (!Locate(p, out int t, out int where))
        {
            return false;
        }

        if (where >= 3)
        {
            // The point is at a vertex already: it is the same point (the merging above did not catch it).
            _canonical[p] = _tv[(3 * t) + (where - 3)];
            return true;
        }

        int a = _tv[3 * t], b = _tv[(3 * t) + 1], c = _tv[(3 * t) + 2];
        if (where < 0)
        {
            // Inside: three triangles around the point.
            int nab = _tn[3 * t], nbc = _tn[(3 * t) + 1], nca = _tn[(3 * t) + 2];
            bool cab = _tc[3 * t], cbc = _tc[(3 * t) + 1], cca = _tc[(3 * t) + 2];
            int t1 = NewTriangle(b, c, p, nbc, -1, -1);
            int t2 = NewTriangle(c, a, p, nca, -1, -1);
            Set(t, a, b, p, nab, t1, t2, cab, false, false);
            Set(t1, b, c, p, nbc, t2, t, cbc, false, false);
            Set(t2, c, a, p, nca, t, t1, cca, false, false);
            Relink(nbc, c, b, t1);
            Relink(nca, a, c, t2);
            _vertexTriangle[p] = t;
            return LegalizeAround(p, [t, t1, t2]);
        }

        // On an edge: the two triangles that share it are each cut in two.
        int e = where;
        int v0 = _tv[(3 * t) + e], v1 = _tv[(3 * t) + ((e + 1) % 3)], v2 = _tv[(3 * t) + ((e + 2) % 3)];
        int across = _tn[(3 * t) + e];
        int outer1 = _tn[(3 * t) + ((e + 1) % 3)], outer2 = _tn[(3 * t) + ((e + 2) % 3)];
        bool segment = _tc[(3 * t) + e], seg1 = _tc[(3 * t) + ((e + 1) % 3)], seg2 = _tc[(3 * t) + ((e + 2) % 3)];
        if (across < 0)
        {
            return false;
        }

        int j = SlotOf(across, v1, v0);
        int d = _tv[(3 * across) + ((j + 2) % 3)];
        int outerU1 = _tn[(3 * across) + ((j + 1) % 3)], outerU2 = _tn[(3 * across) + ((j + 2) % 3)];
        bool segU1 = _tc[(3 * across) + ((j + 1) % 3)], segU2 = _tc[(3 * across) + ((j + 2) % 3)];

        // t = (v0, v1, v2) becomes (v0, p, v2) and (p, v1, v2); across = (v1, v0, d) becomes (v1, p, d) and (p, v0, d).
        int tNew = NewTriangle(p, v1, v2, -1, -1, -1);
        int uNew = NewTriangle(p, v0, d, -1, -1, -1);
        Set(t, v0, p, v2, uNew, tNew, outer2, segment, false, seg2);
        Set(tNew, p, v1, v2, across, outer1, t, segment, seg1, false);
        Set(across, v1, p, d, tNew, uNew, outerU2, segment, false, segU2);
        Set(uNew, p, v0, d, t, outerU1, across, segment, segU1, false);
        Relink(outer1, v2, v1, tNew);
        Relink(outerU1, d, v0, uNew);
        _vertexTriangle[p] = t;
        return LegalizeAround(p, [t, tNew, across, uNew]);
    }

    /// <summary>The slot (0 – 2) of the edge of triangle t that runs from vertex a to vertex b.</summary>
    private int SlotOf(int t, int a, int b)
    {
        for (int k = 0; k < 3; k++)
        {
            if (_tv[(3 * t) + k] == a && _tv[(3 * t) + ((k + 1) % 3)] == b)
            {
                return k;
            }
        }

        throw new InvalidOperationException("The triangulation lost a link.");
    }

    /// <summary>Points the edge of triangle t that runs from a to b at a new neighbour.</summary>
    private void Relink(int t, int a, int b, int neighbour)
    {
        if (t >= 0)
        {
            _tn[(3 * t) + SlotOf(t, a, b)] = neighbour;
        }
    }

    /// <summary>
    /// Flips the edges opposite a point just put in, and the edges that this makes wrong in turn, until the triangulation is Delaunay
    /// again (none of the edges is a loop's segment yet).
    /// </summary>
    private bool LegalizeAround(int p, ReadOnlySpan<int> triangles)
    {
        Stack<int> stack = _stack;
        stack.Clear();
        foreach (int t in triangles)
        {
            stack.Push(t);
        }

        while (stack.Count > 0)
        {
            int t = stack.Pop();
            int k = Array.IndexOf(_tv, p, 3 * t, 3) - (3 * t);
            if (k < 0)
            {
                continue;
            }

            // The edge opposite p runs from vertex k+1 to vertex k+2.
            int slot = (k + 1) % 3;
            int u = _tn[(3 * t) + slot];
            if (u < 0 || _tc[(3 * t) + slot])
            {
                continue;
            }

            int a = _tv[(3 * t) + slot], b = _tv[(3 * t) + ((slot + 1) % 3)];
            int j = SlotOf(u, b, a);
            int d = _tv[(3 * u) + ((j + 2) % 3)];
            if (InCircle(a, b, p, d) > 0)
            {
                if (!Step())
                {
                    return false;
                }

                Flip(t, slot);
                stack.Push(t);
                stack.Push(u);
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces the edge in slot i of triangle t, from a to b, by the one between the vertices opposite it: t = (a, b, c) and its
    /// neighbour u = (b, a, d) become (c, a, d) and (d, b, c).
    /// </summary>
    private void Flip(int t, int i)
    {
        int a = _tv[(3 * t) + i], b = _tv[(3 * t) + ((i + 1) % 3)], c = _tv[(3 * t) + ((i + 2) % 3)];
        int u = _tn[(3 * t) + i];
        int j = SlotOf(u, b, a);
        int d = _tv[(3 * u) + ((j + 2) % 3)];
        int nBc = _tn[(3 * t) + ((i + 1) % 3)], nCa = _tn[(3 * t) + ((i + 2) % 3)];
        int nAd = _tn[(3 * u) + ((j + 1) % 3)], nDb = _tn[(3 * u) + ((j + 2) % 3)];
        bool cBc = _tc[(3 * t) + ((i + 1) % 3)], cCa = _tc[(3 * t) + ((i + 2) % 3)];
        bool cAd = _tc[(3 * u) + ((j + 1) % 3)], cDb = _tc[(3 * u) + ((j + 2) % 3)];
        Set(t, c, a, d, nCa, nAd, u, cCa, cAd, false);
        Set(u, d, b, c, nDb, nBc, t, cDb, cBc, false);
        Relink(nAd, d, a, t);
        Relink(nBc, c, b, u);
    }

    // ---- the segments of the loops

    /// <summary>Whether the edge from a to b is in the triangulation, and if so in which triangle and slot.</summary>
    private bool FindEdge(int a, int b, out int triangle, out int slot)
    {
        int start = _vertexTriangle[a];
        int t = start;
        if (t < 0)
        {
            triangle = -1;
            slot = -1;
            return false;
        }

        // Round the vertex one way, and if the fan is open (at the super-triangle's rim) the other way too.
        for (int direction = 0; direction < 2; direction++)
        {
            t = start;
            for (int guard = 0; guard < _triangleCount + 3; guard++)
            {
                int k = IndexOfVertex(t, a);
                if (_tv[(3 * t) + ((k + 1) % 3)] == b)
                {
                    triangle = t;
                    slot = k;
                    return true;
                }

                if (_tv[(3 * t) + ((k + 2) % 3)] == b)
                {
                    int s = (k + 2) % 3;
                    int u = _tn[(3 * t) + s];
                    if (u >= 0)
                    {
                        triangle = u;
                        slot = SlotOf(u, a, b);
                        return true;
                    }
                }

                // Next triangle round a: across the edge that leaves a (counter-clockwise) or the one that arrives.
                int next = direction == 0 ? _tn[(3 * t) + k] : _tn[(3 * t) + ((k + 2) % 3)];
                if (next < 0 || next == start)
                {
                    break;
                }

                t = next;
            }
        }

        triangle = -1;
        slot = -1;
        return false;
    }

    private int IndexOfVertex(int t, int v)
    {
        return _tv[3 * t] == v ? 0 : _tv[(3 * t) + 1] == v ? 1 : 2;
    }

    private void MarkSegment(int t, int slot)
    {
        _tc[(3 * t) + slot] = true;
        int u = _tn[(3 * t) + slot];
        if (u >= 0)
        {
            _tc[(3 * u) + SlotOf(u, _tv[(3 * t) + ((slot + 1) % 3)], _tv[(3 * t) + slot])] = true;
        }
    }

    /// <summary>
    /// Makes the segment from a to b an edge, by flipping the edges it crosses, and marks it. A vertex that lies on the segment
    /// splits it in two. False when the segment crosses another segment of a loop.
    /// </summary>
    private bool InsertSegment(int a, int b)
    {
        if (a == b)
        {
            return true;
        }

        if (!Step())
        {
            return false;
        }

        if (FindEdge(a, b, out int tri, out int slot))
        {
            MarkSegment(tri, slot);
            return true;
        }

        // The edges the segment crosses, from a to b.
        List<(int, int)> crossing = _crossing;
        crossing.Clear();
        int vertexOnSegment = -1;
        if (!Collect(a, b, crossing, ref vertexOnSegment))
        {
            return false;
        }

        if (vertexOnSegment >= 0)
        {
            return InsertSegment(a, vertexOnSegment) && InsertSegment(vertexOnSegment, b);
        }

        // Sloan: flip a crossing edge whose two triangles make a convex quadrilateral; one that is not waits for its turn again.
        Queue<(int, int)> queue = _queue;
        queue.Clear();
        foreach ((int, int) edge in crossing)
        {
            queue.Enqueue(edge);
        }

        int crossed = crossing.Count;
        int rounds = 0;
        while (queue.Count > 0)
        {
            if (!Step() || ++rounds > 8 * (crossed + 4) * (crossed + 4) + 64)
            {
                return false;
            }

            (int p, int q) = queue.Dequeue();
            if (!FindEdge(p, q, out int t, out int i))
            {
                // Flipped away already by a flip that made it into something else: nothing crosses here any more.
                continue;
            }

            int u = _tn[(3 * t) + i];
            int c = _tv[(3 * t) + ((i + 2) % 3)];
            int j = SlotOf(u, q, p);
            int d = _tv[(3 * u) + ((j + 2) % 3)];
            bool convex = Orient(c, d, p) != 0 && Orient(c, d, q) != 0 && Orient(c, d, p) != Orient(c, d, q) && Orient(p, q, c) != Orient(p, q, d);
            if (!convex)
            {
                queue.Enqueue((p, q));
                continue;
            }

            Flip(t, i);
            if ((c == a && d == b) || (c == b && d == a))
            {
                continue;
            }

            if (CrossesStrictly(a, b, c, d))
            {
                queue.Enqueue((c, d));
            }
        }

        if (!FindEdge(a, b, out tri, out slot))
        {
            return false;
        }

        MarkSegment(tri, slot);
        return true;
    }

    private bool CrossesStrictly(int a, int b, int c, int d)
    {
        int oc = Orient(a, b, c), od = Orient(a, b, d);
        if (oc == 0 || od == 0 || oc == od)
        {
            return false;
        }

        int oa = Orient(c, d, a), ob = Orient(c, d, b);
        return oa != 0 && ob != 0 && oa != ob;
    }

    /// <summary>
    /// The edges that the segment from a to b crosses, walking along it. When a vertex lies on the segment, it is returned in
    /// <paramref name="vertexOnSegment"/>. False when it crosses a segment of a loop.
    /// </summary>
    private bool Collect(int a, int b, List<(int, int)> crossing, ref int vertexOnSegment)
    {
        // The triangle round a that the segment leaves through.
        int start = _vertexTriangle[a];
        int t = -1;
        int slot = -1;
        int t0 = start;
        for (int direction = 0; direction < 2 && t < 0; direction++)
        {
            int current = start;
            for (int guard = 0; guard < _triangleCount + 3; guard++)
            {
                int k = IndexOfVertex(current, a);
                int x = _tv[(3 * current) + ((k + 1) % 3)], y = _tv[(3 * current) + ((k + 2) % 3)];
                int ox = Orient(a, x, b), oy = Orient(a, y, b);
                if (ox == 0 && Between(a, x, b))
                {
                    vertexOnSegment = x;
                    return true;
                }

                if (oy == 0 && Between(a, y, b))
                {
                    vertexOnSegment = y;
                    return true;
                }

                if (ox > 0 && oy < 0)
                {
                    t = current;
                    slot = (k + 1) % 3;
                    break;
                }

                int next = direction == 0 ? _tn[(3 * current) + k] : _tn[(3 * current) + ((k + 2) % 3)];
                if (next < 0 || next == t0)
                {
                    break;
                }

                current = next;
            }
        }

        if (t < 0)
        {
            return false;
        }

        while (true)
        {
            if (!Step())
            {
                return false;
            }

            int p = _tv[(3 * t) + slot], q = _tv[(3 * t) + ((slot + 1) % 3)];
            if (_tc[(3 * t) + slot])
            {
                return false;
            }

            crossing.Add((p, q));
            int u = _tn[(3 * t) + slot];
            if (u < 0)
            {
                return false;
            }

            int j = SlotOf(u, q, p);
            int w = _tv[(3 * u) + ((j + 2) % 3)];
            if (w == b)
            {
                return true;
            }

            int ow = Orient(a, b, w);
            if (ow == 0)
            {
                vertexOnSegment = w;
                crossing.Clear();
                return true;
            }

            // p is on the left of a→b or q is; the segment leaves u through the edge between w and the endpoint on the other side.
            int op = Orient(a, b, p);
            if (ow > 0 == op > 0)
            {
                // w is on p's side: the next edge is (w, q), which is slot j+1 of u (from d... see below).
                t = u;
                slot = SlotOf(u, w, q);
            }
            else
            {
                t = u;
                slot = SlotOf(u, p, w);
            }
        }
    }

    /// <summary>Whether c lies strictly between a and b, which are in line with it.</summary>
    private bool Between(int a, int c, int b)
    {
        double abx = _x[b] - _x[a], aby = _y[b] - _y[a], acx = _x[c] - _x[a], acy = _y[c] - _y[a];
        double dot = (abx * acx) + (aby * acy);
        return dot > 0 && dot < (abx * abx) + (aby * aby);
    }

    // ---- the end

    /// <summary>Flips every edge that is not a segment and not Delaunay, until none is: what flipping to make the segments left behind.</summary>
    private bool Legalize()
    {
        Stack<(int, int)> stack = _pairs;
        stack.Clear();
        for (int t = 0; t < _triangleCount; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                if (_tn[(3 * t) + k] > t && !_tc[(3 * t) + k])
                {
                    stack.Push((t, k));
                }
            }
        }

        while (stack.Count > 0)
        {
            (int t, int k) = stack.Pop();
            int u = _tn[(3 * t) + k];
            if (u < 0 || _tc[(3 * t) + k])
            {
                continue;
            }

            int a = _tv[(3 * t) + k], b = _tv[(3 * t) + ((k + 1) % 3)], c = _tv[(3 * t) + ((k + 2) % 3)];
            int j = SlotOf(u, b, a);
            int d = _tv[(3 * u) + ((j + 2) % 3)];
            if (InCircle(a, b, c, d) > 0)
            {
                // Only a quadrilateral that is strictly convex can be flipped.
                if (Orient(c, a, d) <= 0 || Orient(d, b, c) <= 0)
                {
                    continue;
                }

                if (!Step())
                {
                    return false;
                }

                Flip(t, k);
                for (int s = 0; s < 3; s++)
                {
                    if (_tn[(3 * t) + s] >= 0 && !_tc[(3 * t) + s])
                    {
                        stack.Push((t, s));
                    }

                    if (_tn[(3 * u) + s] >= 0 && !_tc[(3 * u) + s])
                    {
                        stack.Push((u, s));
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The triangles inside: those reached from outside across an odd number of segments (the outer loop, not a hole), without the
    /// ones that have a corner of the surrounding triangle.
    /// </summary>
    private void Carve(List<int> triangles)
    {
        if (_depth.Length < _triangleCount)
        {
            _depth = new int[_triangleCount];
        }

        Span<int> depth = _depth.AsSpan(0, _triangleCount);
        depth.Fill(int.MaxValue);

        // A double-ended queue in a ring (a triangle can be in it more than once, at most once for each of its three neighbours).
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, (4 * _triangleCount) + 4));
        if (_deque.Length < capacity)
        {
            _deque = new int[capacity];
        }

        int mask = _deque.Length - 1, head = 0, count = 0;
        void AddFirst(int value)
        {
            head = (head - 1) & mask;
            _deque[head] = value;
            count++;
        }

        void AddLast(int value)
        {
            _deque[(head + count) & mask] = value;
            count++;
        }

        for (int t = 0; t < _triangleCount; t++)
        {
            if (_tv[3 * t] >= Super || _tv[(3 * t) + 1] >= Super || _tv[(3 * t) + 2] >= Super)
            {
                depth[t] = 0;
                AddLast(t);
            }
        }

        // Crossing a segment costs one, anything else nothing: the triangles are reached in the order of their depth.
        while (count > 0)
        {
            int t = _deque[head];
            head = (head + 1) & mask;
            count--;
            for (int k = 0; k < 3; k++)
            {
                int u = _tn[(3 * t) + k];
                if (u < 0)
                {
                    continue;
                }

                int cost = _tc[(3 * t) + k] ? 1 : 0;
                if (depth[t] + cost < depth[u])
                {
                    depth[u] = depth[t] + cost;
                    if (count >= _deque.Length)
                    {
                        // Cannot happen with the capacity above (each triangle is added at most once per neighbour); safe if it did.
                        int[] grown = new int[_deque.Length * 2];
                        for (int i = 0; i < count; i++)
                        {
                            grown[i] = _deque[(head + i) & mask];
                        }

                        _deque = grown;
                        mask = _deque.Length - 1;
                        head = 0;
                    }

                    if (cost == 0)
                    {
                        AddFirst(u);
                    }
                    else
                    {
                        AddLast(u);
                    }
                }
            }
        }

        for (int t = 0; t < _triangleCount; t++)
        {
            if (depth[t] != int.MaxValue && (depth[t] & 1) == 1)
            {
                triangles.Add(_tv[3 * t]);
                triangles.Add(_tv[(3 * t) + 1]);
                triangles.Add(_tv[(3 * t) + 2]);
            }
        }
    }
}
