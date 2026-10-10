using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// The points of the curves of a compressed body, to a chord tolerance and the angle cap of <see cref="PrcBrepTolerance"/>, each
/// curve sampled once and the same samples given to every face that uses it: neighbouring faces meet on the same points.
/// <para>
/// A curve is sampled from its start vertex to its end vertex, both its own, exactly. A line is its two ends; an arc takes
/// <c>ceil(sweep / min(2·acos(1 − ε/r), 15°))</c> segments; a Hermite curve is cut into cubic Bézier pieces (the draft's
/// control points, 7.9.20.9.4) and each is halved until it is flat to ε and turns less than 15°; a composite curve is its members
/// one after the other, each turned the way that continues the one before.
/// </para>
/// </summary>
internal sealed class PrcEdgeSampler(PrcTopoBrepDataCompress brep, double chord)
{
    private readonly PrcVector[]?[] _samples = new PrcVector[brep.Curves.Length][];
    private readonly bool[] _underway = new bool[brep.Curves.Length];
    private PrcEdgeSampler? _parent;

    /// <summary>
    /// The same curves sampled <paramref name="factor"/> times finer (for one face the triangulator refused: two curves that touch
    /// can cross between their samples). The curves a face deduces are the ones this sampler was given.
    /// </summary>
    public PrcEdgeSampler Finer(double factor) => new(brep, chord / factor) { _parent = this };
    private int _depth;

    /// <summary>How many composites may be inside one another, and how many points a composite may gather (the rest of the file's bits cannot be trusted to stop it).</summary>
    private const int MaxCompositeDepth = 64, MaxCompositeSamples = 4 * PrcBrepTolerance.MaxSamplesPerEdge;

    /// <summary>Whether any edge needed more segments than the cap allows.</summary>
    public bool Capped { get; private set; }

    /// <summary>How many curves have been sampled (not asked for again).</summary>
    public int CurvesSampled { get; private set; }

    /// <summary>The samples of a curve from its start to its end, both included. The same array contents for every caller.</summary>
    /// <exception cref="InvalidOperationException">The curve has no ends of its own (a deduced side, or the trim of an iso NURBS face).</exception>
    public ReadOnlySpan<PrcVector> Samples(int curve)
    {
        return _samples[curve] ??= Compute(curve);
    }

    /// <summary>
    /// Gives a curve its samples: the sides an iso face deduces have none of their own; the face that deduces them computes them,
    /// once, and every face that refers to them then gets these.
    /// </summary>
    public void Register(int curve, PrcVector[] samples)
    {
        _samples[curve] = samples;
    }

    /// <summary>
    /// Says that a side a face deduced is an arc of a circle (a line of its surface's parameters along a turn): its samples may be two
    /// points (a short arc) and say nothing of the circle a face built from that side needs.
    /// </summary>
    public void Describe(int curve, PrcBrepCurves.Arc arc)
    {
        (_arcs ??= [])[curve] = arc;
    }

    /// <summary>The arc a deduced side was said to be by <see cref="Describe"/>, here or in the sampler this one is finer than.</summary>
    public bool TryGetArc(int curve, out PrcBrepCurves.Arc arc)
    {
        if (_arcs is not null && _arcs.TryGetValue(curve, out arc))
        {
            return true;
        }

        if (_parent is not null)
        {
            return _parent.TryGetArc(curve, out arc);
        }

        arc = default;
        return false;
    }

    private Dictionary<int, PrcBrepCurves.Arc>? _arcs;

    /// <summary>Whether the curve has samples already (computed, or given by <see cref="Register"/>).</summary>
    public bool IsResolved(int curve)
    {
        // A side with no ends of its own that the sampler this one is finer than has: it is taken from there.
        return _samples[curve] is not null || (_parent is not null && WithoutEnds(brep.Curves[curve]) && _parent.IsResolved(curve));
    }

    private static bool WithoutEnds(PrcBrepCurve curve) => curve.Kind is PrcCurveKind.Implicit or PrcCurveKind.IsoBoundary || curve.Start < 0 || curve.End < 0;

    private PrcVector[] Compute(int id)
    {
        PrcBrepCurve curve = brep.Curves[id];
        if (_parent is not null && WithoutEnds(curve) && _parent.IsResolved(id))
        {
            return _parent.Samples(id).ToArray();
        }

        if (WithoutEnds(curve))
        {
            throw new InvalidOperationException("A curve without ends of its own is sampled by the face that knows them.");
        }

        if (_underway[id])
        {
            throw new InvalidOperationException("A composite curve refers to itself.");
        }

        if (_depth >= MaxCompositeDepth)
        {
            throw new InvalidOperationException("Composite curves are nested too deep.");
        }

        _underway[id] = true;
        _depth++;
        CurvesSampled++;
        try
        {
            PrcVector start = brep.Vertices[curve.Start], end = brep.Vertices[curve.End];
            return curve.Kind switch
            {
                PrcCurveKind.Line => [start, end],
                PrcCurveKind.Circle => SampleCircle(curve, start, end),
                PrcCurveKind.HermiteSpline => SampleHermite(curve, start, end),
                PrcCurveKind.Composite => SampleComposite(curve, start),
                PrcCurveKind.Conic => SampleConic(curve, start, end),
                PrcCurveKind.NurbsCurve => SampleNurbs(curve, start, end),
                PrcCurveKind.Polyline => SamplePolyline(curve, start, end),
                _ => [start, end],
            };
        }
        finally
        {
            _underway[id] = false;
            _depth--;
        }
    }

    // ---- circles and arcs

    private PrcVector[] SampleCircle(PrcBrepCurve curve, PrcVector start, PrcVector end)
    {
        if (!PrcBrepCurves.TryGetArc(brep, curve, start, end, out PrcBrepCurves.Arc arc))
        {
            return [start, end];
        }

        double radius = Math.Max(arc.StartRadius, arc.EndRadius);
        double step = chord >= 2 * radius ? PrcBrepTolerance.MaxTurnRadians : Math.Min(2 * Math.Acos(Math.Clamp(1 - (chord / radius), -1, 1)), PrcBrepTolerance.MaxTurnRadians);
        int segments = (int)Math.Min(Math.Ceiling(Math.Abs(arc.Sweep) / Math.Max(step, 1e-12)), double.MaxValue);
        segments = Math.Max(segments, 1);
        if (segments > PrcBrepTolerance.MaxSamplesPerEdge)
        {
            segments = PrcBrepTolerance.MaxSamplesPerEdge;
            Capped = true;
        }

        var samples = new PrcVector[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            double fraction = (double)i / segments;
            double phi = arc.Sweep * fraction;
            double r = arc.StartRadius + ((arc.EndRadius - arc.StartRadius) * fraction);
            samples[i] = arc.Centre + (((arc.U * Math.Cos(phi)) + (arc.V * Math.Sin(phi))) * r);
        }

        // Exactly the vertices: the faces on either side of the edge have them too.
        samples[0] = start;
        samples[segments] = end;
        return samples;
    }

    // ---- Hermite curves

    private PrcVector[] SampleHermite(PrcBrepCurve curve, PrcVector start, PrcVector end)
    {
        ReadOnlySpan<double> data = brep.CurveData.AsSpan(curve.DataStart, curve.DataCount);
        int n = (int)data[0];
        if (n < 2 || data.Length < 1 + (3 * ((n - 2) + n)))
        {
            return [start, end];
        }

        // The points on the curve: the start, the n - 2 stored ones, the end; a tangent at each. Each stored point is stored as its difference
        // from the one before (draft 7.9.20.9.4: P3i = P3(i-1) + the stored values).
        var q = new PrcVector[n];
        var t = new PrcVector[n];
        q[0] = start;
        q[n - 1] = end;
        for (int i = 1; i < n - 1; i++)
        {
            q[i] = q[i - 1] + new PrcVector(data[1 + (3 * (i - 1))], data[2 + (3 * (i - 1))], data[3 + (3 * (i - 1))]);
        }

        int tangentsAt = 1 + (3 * (n - 2));
        for (int i = 0; i < n; i++)
        {
            t[i] = new PrcVector(data[tangentsAt + (3 * i)], data[tangentsAt + (3 * i) + 1], data[tangentsAt + (3 * i) + 2]);
        }

        var samples = new List<PrcVector> { q[0] };
        for (int i = 1; i < n; i++)
        {
            double length = Math.Sqrt((q[i] - q[i - 1]).LengthSquared());
            if (!(length > 0))
            {
                continue;
            }

            // The Bézier piece between two points: the control points lie a third of the chord along the tangents (chord-length knots, draft
            // 7.9.20.9.4). Only the direction of a stored tangent is used: in every Hermite curve of the corpus its length is the chord to the
            // next point over three, which is the same thing said again (ExploreHermite).
            PrcVector b1 = q[i - 1] + (Unit(t[i - 1], q[i] - q[i - 1]) * (length / 3)), b2 = q[i] - (Unit(t[i], q[i] - q[i - 1]) * (length / 3));
            Subdivide(q[i - 1], b1, b2, q[i], 0, samples);
            samples[^1] = q[i];
        }

        samples[0] = start;
        samples[^1] = end;
        return [.. samples];
    }

    /// <summary>The direction of a tangent as a unit vector; that of the chord when the tangent is nothing.</summary>
    private static PrcVector Unit(PrcVector tangent, PrcVector chord)
    {
        double length = Math.Sqrt(tangent.LengthSquared());
        if (length > 1e-300)
        {
            return tangent / length;
        }

        double c = Math.Sqrt(chord.LengthSquared());
        return c > 0 ? chord / c : default;
    }

    /// <summary>
    /// Adds the points of a Bézier piece after its first: it is split in half until its control points are within the chord tolerance
    /// of the line between its ends and its end tangents are within the angle cap.
    /// </summary>
    private void Subdivide(PrcVector p0, PrcVector p1, PrcVector p2, PrcVector p3, int depth, List<PrcVector> samples)
    {
        if (depth >= 12 || samples.Count >= PrcBrepTolerance.MaxSamplesPerEdge || (Flat(p0, p1, p2, p3) && Straight(p0, p1, p2, p3)))
        {
            if (samples.Count >= PrcBrepTolerance.MaxSamplesPerEdge)
            {
                Capped = true;
            }

            samples.Add(p3);
            return;
        }

        PrcVector p01 = (p0 + p1) / 2, p12 = (p1 + p2) / 2, p23 = (p2 + p3) / 2;
        PrcVector p012 = (p01 + p12) / 2, p123 = (p12 + p23) / 2;
        PrcVector mid = (p012 + p123) / 2;
        Subdivide(p0, p01, p012, mid, depth + 1, samples);
        Subdivide(mid, p123, p23, p3, depth + 1, samples);
    }

    private bool Flat(PrcVector p0, PrcVector p1, PrcVector p2, PrcVector p3)
    {
        return Deviation(p0, p3, p1) <= chord && Deviation(p0, p3, p2) <= chord;
    }

    /// <summary>The distance of a point from the line through a and b (from a when they are the same).</summary>
    private static double Deviation(PrcVector a, PrcVector b, PrcVector p)
    {
        PrcVector ab = b - a;
        double lengthSquared = ab.LengthSquared();
        if (!(lengthSquared > 0))
        {
            return Math.Sqrt((p - a).LengthSquared());
        }

        PrcVector ap = p - a;
        return Math.Sqrt(ab.Cross(ap).LengthSquared() / lengthSquared);
    }

    /// <summary>Whether the two ends of the piece point within the angle cap of each other.</summary>
    private static bool Straight(PrcVector p0, PrcVector p1, PrcVector p2, PrcVector p3)
    {
        PrcVector d0 = p1 - p0, d1 = p3 - p2;
        double l0 = Math.Sqrt(d0.LengthSquared()), l1 = Math.Sqrt(d1.LengthSquared());
        if (!(l0 > 1e-300) || !(l1 > 1e-300))
        {
            return true;
        }

        return d0.Dot(d1) / (l0 * l1) >= Math.Cos(PrcBrepTolerance.MaxTurnRadians);
    }

    // ---- the curves of uncompressed bodies

    /// <summary>An arc of an ellipse: as many equal steps of the angle as the larger half-axis needs for the chord and the angle cap.</summary>
    private PrcVector[] SampleConic(PrcBrepCurve curve, PrcVector start, PrcVector end)
    {
        ReadOnlySpan<double> d = brep.CurveData.AsSpan(curve.DataStart, curve.DataCount);
        if (d.Length < 11)
        {
            return [start, end];
        }

        PrcVector centre = new(d[0], d[1], d[2]), a = new(d[3], d[4], d[5]), b = new(d[6], d[7], d[8]);
        double from = d[9], sweep = d[10] - d[9];
        double radius = Math.Max(Math.Sqrt(a.LengthSquared()), Math.Sqrt(b.LengthSquared()));
        if (!double.IsFinite(sweep) || !double.IsFinite(radius) || !(radius > 0))
        {
            return [start, end];
        }

        double step = chord >= 2 * radius ? PrcBrepTolerance.MaxTurnRadians : Math.Min(2 * Math.Acos(Math.Clamp(1 - (chord / radius), -1, 1)), PrcBrepTolerance.MaxTurnRadians);
        double wanted = Math.Ceiling(Math.Abs(sweep) / Math.Max(step, 1e-12));
        int segments = wanted > 1 ? (int)Math.Min(wanted, PrcBrepTolerance.MaxSamplesPerEdge) : 1;
        if (wanted > PrcBrepTolerance.MaxSamplesPerEdge)
        {
            Capped = true;
        }

        var samples = new PrcVector[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            double phi = from + (sweep * i / segments);
            samples[i] = centre + (a * Math.Cos(phi)) + (b * Math.Sin(phi));
        }

        samples[0] = start;
        samples[segments] = end;
        return samples;
    }

    /// <summary>
    /// A part of a B-spline curve: the knots inside it, then each piece halved until the middle of a chord is within the tolerance of
    /// the curve and its ends turn less than the cap.
    /// </summary>
    private PrcVector[] SampleNurbs(PrcBrepCurve curve, PrcVector start, PrcVector end)
    {
        ReadOnlySpan<double> d = brep.CurveData.AsSpan(curve.DataStart, curve.DataCount);
        if (d.Length < 3 || !(d[0] >= 0) || d[0] >= brep.CurveNurbs.Length)
        {
            return [start, end];
        }

        IPrcParametricCurve n = brep.CurveNurbs[(int)d[0]];
        double t0 = d[1], t1 = d[2];
        if (!double.IsFinite(t0) || !double.IsFinite(t1) || t0 == t1)
        {
            return [start, end];
        }

        var breaks = new List<double> { t0 };
        double lo = Math.Min(t0, t1), hi = Math.Max(t0, t1);
        foreach (double k in n.Knots)
        {
            if (k > lo && k < hi && (breaks.Count == 0 || k != breaks[^1]))
            {
                breaks.Add(k);
            }
        }

        if (t1 < t0)
        {
            breaks.Sort();
            breaks.Reverse();
        }

        breaks.Add(t1);
        var samples = new List<PrcVector> { n.Evaluate(breaks[0]) };
        for (int i = 1; i < breaks.Count && samples.Count < PrcBrepTolerance.MaxSamplesPerEdge; i++)
        {
            if (breaks[i] == breaks[i - 1])
            {
                continue;
            }

            Bisect(n, breaks[i - 1], samples[^1], breaks[i], n.Evaluate(breaks[i]), 0, samples);
        }

        if (samples.Count >= PrcBrepTolerance.MaxSamplesPerEdge)
        {
            Capped = true;
        }

        samples[0] = start;
        if (samples.Count == 1)
        {
            samples.Add(end);
        }

        samples[^1] = end;
        return [.. samples];
    }

    /// <summary>Adds the points of a piece of a B-spline curve after its first, halving it while its middle is off the chord or it turns too much.</summary>
    private void Bisect(IPrcParametricCurve n, double ta, PrcVector a, double tb, PrcVector b, int depth, List<PrcVector> samples)
    {
        double tm = (ta + tb) / 2;
        PrcVector m = n.Evaluate(tm);
        bool flat = Deviation(a, b, m) <= chord && Straight(a, m, m, b);
        if (depth >= 2 && (flat || depth >= 14 || samples.Count >= PrcBrepTolerance.MaxSamplesPerEdge))
        {
            samples.Add(b);
            return;
        }

        Bisect(n, ta, a, tm, m, depth + 1, samples);
        Bisect(n, tm, m, tb, b, depth + 1, samples);
    }

    private PrcVector[] SamplePolyline(PrcBrepCurve curve, PrcVector start, PrcVector end)
    {
        ReadOnlySpan<double> d = brep.CurveData.AsSpan(curve.DataStart, curve.DataCount);
        int count = d.Length / 3;
        if (count < 2)
        {
            return [start, end];
        }

        var samples = new PrcVector[Math.Min(count, PrcBrepTolerance.MaxSamplesPerEdge)];
        for (int i = 0; i < samples.Length; i++)
        {
            int at = i == samples.Length - 1 ? count - 1 : i;
            samples[i] = new PrcVector(d[3 * at], d[(3 * at) + 1], d[(3 * at) + 2]);
        }

        samples[0] = start;
        samples[^1] = end;
        return samples;
    }

    // ---- composite curves

    private PrcVector[] SampleComposite(PrcBrepCurve curve, PrcVector start)
    {
        var samples = new List<PrcVector>();
        PrcVector current = start;
        foreach (int member in brep.CompositeMembers.AsSpan(curve.DataStart, curve.DataCount))
        {
            ReadOnlySpan<PrcVector> part = Samples(member);
            bool forward = (part[0] - current).LengthSquared() <= (part[^1] - current).LengthSquared();
            if (samples.Count == 0)
            {
                samples.Add(current);
            }

            if (samples.Count + part.Length > MaxCompositeSamples)
            {
                throw new InvalidOperationException("A composite curve has more points than a curve may.");
            }

            for (int i = 1; i < part.Length; i++)
            {
                samples.Add(forward ? part[i] : part[part.Length - 1 - i]);
            }

            current = samples[^1];
        }

        if (samples.Count == 0)
        {
            samples.Add(start);
        }

        samples[0] = start;
        samples[^1] = brep.Vertices[curve.End];
        return [.. samples];
    }
}
