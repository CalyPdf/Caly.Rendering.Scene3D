using Caly.Rendering.Scene3D.Prc;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>A vertex of a compressed body as written: a new point, or the index of one already stored.</summary>
internal readonly record struct BrepVertex(PrcVector? Point, int Index)
{
    public static BrepVertex New(double x, double y, double z) => new(new PrcVector(x, y, z), -1);

    public static BrepVertex Ref(int index) => new(null, index);
}

/// <summary>
/// Writes the parts of a compressed body (ISO 14739-1 draft 7.9.20, as PDF Association issues #582, #666, #696, #705, #706
/// and prc.json have it), for tests: the counterpart of the compressed body reader, written from the format and not from the reader.
/// Points are written at a hundredth of the body's tolerance, curves of Hermite form at the tolerance itself.
/// </summary>
internal sealed class PrcBrepWriter(double tolerance, int referenceBits)
{
    public PrcBitWriter Bits { get; } = new();

    public double Tolerance { get; } = tolerance;

    public int ReferenceBits { get; } = referenceBits;

    public double PointTolerance => Tolerance / 100;

    public PrcBrepWriter Vertex(BrepVertex vertex)
    {
        if (vertex.Point is { } p)
        {
            Bits.Bit(true).CompressedPoint(p.X, p.Y, p.Z, PointTolerance);
        }
        else
        {
            Bits.Bit(false).Bits((uint)vertex.Index, ReferenceBits);
        }

        return this;
    }

    public PrcBrepWriter Point(PrcVector p)
    {
        Bits.CompressedPoint(p.X, p.Y, p.Z, PointTolerance);
        return this;
    }

    public PrcBrepWriter StartEnd(BrepVertex start, BrepVertex end) => Vertex(start).Vertex(end);

    /// <summary>A curve written in place of a <c>RefOrCompressedCurve</c> that is stored now: a flag, then the curve.</summary>
    public PrcBrepWriter NewCurve(Action<PrcBrepWriter> curve)
    {
        Bits.Bit(true);
        curve(this);
        return this;
    }

    /// <summary>A <c>RefOrCompressedCurve</c> that refers to a curve stored before.</summary>
    public PrcBrepWriter CurveRef(int index)
    {
        Bits.Bit(false).Bits((uint)index, ReferenceBits);
        return this;
    }

    public PrcBrepWriter Line(BrepVertex start, BrepVertex end)
    {
        Bits.CompressedEntityType(true, 0);
        return StartEnd(start, end);
    }

    /// <summary>A circle that is a whole circle (<paramref name="isoSpline"/>: the trim of an iso NURBS face: no type, no ends).</summary>
    public PrcBrepWriter FullCircle(BrepVertex start, BrepVertex end, PrcVector centre, PrcVector normal, bool isoSpline = false)
    {
        if (!isoSpline)
        {
            Bits.CompressedEntityType(true, 1);
        }

        Bits.Bit(true).Bit(true);
        if (!isoSpline)
        {
            StartEnd(start, end);
        }

        return Point(centre).Point(normal);
    }

    /// <summary>A circle of half a turn, given by a point in the middle of the arc.</summary>
    public PrcBrepWriter HalfCircle(BrepVertex start, BrepVertex end, PrcVector middle, bool isoSpline = false)
    {
        if (!isoSpline)
        {
            Bits.CompressedEntityType(true, 1);
        }

        Bits.Bit(true).Bit(false);
        if (!isoSpline)
        {
            StartEnd(start, end);
        }

        return Point(middle);
    }

    /// <summary>Any other arc: its ends, its centre, and whether its angle passes a half turn.</summary>
    public PrcBrepWriter Arc(BrepVertex start, BrepVertex end, PrcVector centre, bool overHalfTurn, bool isoSpline = false)
    {
        if (!isoSpline)
        {
            Bits.CompressedEntityType(true, 1);
        }

        Bits.Bit(false);
        if (!isoSpline)
        {
            StartEnd(start, end);
        }

        Point(centre);
        Bits.Bit(overHalfTurn);
        return this;
    }

    /// <summary>
    /// A Hermite curve: <c>points</c> are the inner points of the curve (two fewer than tangents) as positions; each is written as its
    /// difference from the point before (the first from the start), at half the body's tolerance, in the width its size needs, or as
    /// plain vectors when <paramref name="wide"/>.
    /// </summary>
    public PrcBrepWriter Hermite(BrepVertex start, BrepVertex end, PrcVector[] points, PrcVector[] tangents, bool wide = false)
    {
        Bits.CompressedEntityType(true, 2);
        StartEnd(start, end);
        int numberPoints = tangents.Length;
        if (points.Length != numberPoints - 2)
        {
            throw new ArgumentException("A Hermite curve has two fewer inner points than tangents.");
        }

        int numberBits = Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)numberPoints));
        Bits.Bits((uint)numberBits, 4).Bits((uint)numberPoints, numberBits);
        var differences = new PrcVector[points.Length];
        PrcVector before = start.Point ?? default;
        for (int i = 0; i < points.Length; i++)
        {
            differences[i] = points[i] - before;
            before = points[i];
        }

        VectorArray(differences, wide);
        VectorArray(tangents, wide);
        return this;
    }

    private void VectorArray(PrcVector[] vectors, bool wide)
    {
        double tolerance = Tolerance / 2;
        uint largest = 0;
        foreach (PrcVector v in vectors)
        {
            largest = Math.Max(largest, (uint)Math.Round(Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) / tolerance));
        }

        int width = wide ? 31 : (largest == 0 ? 1 : 32 - System.Numerics.BitOperations.LeadingZeroCount(largest) + 1);
        Bits.Bits((uint)width, 6);
        foreach (PrcVector v in vectors)
        {
            if (width > 30)
            {
                Bits.Double(v.X).Double(v.Y).Double(v.Z);
            }
            else
            {
                Bits.DoubleWithVariableBitNumber(v.X, width, tolerance).DoubleWithVariableBitNumber(v.Y, width, tolerance).DoubleWithVariableBitNumber(v.Z, width, tolerance);
            }
        }
    }

    /// <summary>A composite curve: its ends, its dimension, whether it closes, and its members (each a <c>RefOrCompressedCurve</c>).</summary>
    public PrcBrepWriter Composite(BrepVertex start, BrepVertex end, bool closed, params Action<PrcBrepWriter>[] members)
    {
        Bits.CompressedEntityType(true, 13);
        StartEnd(start, end);
        Bits.Unsigned(3).Bit(closed).Unsigned((uint)members.Length);
        foreach (Action<PrcBrepWriter> member in members)
        {
            member(this);
        }

        return this;
    }

    // ---- faces, shells, bodies

    /// <summary>Faces written so far (shells count theirs): the number of <c>BaseTopology</c> a body ends with.</summary>
    public int FaceCount { get; private set; }

    /// <summary>
    /// An iso face (<paramref name="kind"/> 2 – 6): the kind's own values before the face, the orientations, the first two
    /// trim curves, the third and fourth as an index or left to be deduced (null), and the vertex they share when both are.
    /// </summary>
    public PrcBrepWriter IsoFace(byte kind, Action<PrcBrepWriter>? before, bool orientationWithShell, bool orientationLoop,
        Action<PrcBrepWriter> first, Action<PrcBrepWriter> second, int? third, int? fourth, BrepVertex? common)
    {
        Bits.CompressedEntityType(false, kind);
        before?.Invoke(this);
        Bits.Bit(orientationWithShell).Bit(orientationLoop);
        first(this);
        second(this);
        foreach (int? index in new[] { third, fourth })
        {
            if (index is null)
            {
                Bits.Bit(true);
            }
            else
            {
                Bits.Bit(false).Bits((uint)index.Value, ReferenceBits);
            }
        }

        if (third is null && fourth is null)
        {
            Vertex(common ?? throw new ArgumentException("Two curves to deduce need the vertex they share."));
        }

        return this;
    }

    /// <summary>The normal of a plane: x, y and whether z is positive.</summary>
    public static Action<PrcBrepWriter> PlaneNormal(double x, double y, bool positiveZ) => w => w.Bits.Double(x).Double(y).Bit(positiveZ);

    /// <summary>A loop of an ana face: how it runs with the surface, and its curves (each a new curve or a reference).</summary>
    public sealed record AnaLoop(bool Orientation, params Action<PrcBrepWriter>[] Curves);

    /// <summary>
    /// An ana face (<paramref name="kind"/> 8 – 14): the kind's own values before the face, the orientation, the loops (null:
    /// the surface is not trimmed), a point on the torus when the face needs one, and the kind's own values after.
    /// </summary>
    public PrcBrepWriter AnaFace(byte kind, Action<PrcBrepWriter>? before, bool orientationWithShell, IReadOnlyList<AnaLoop>? loops, PrcVector? pointOnTorus, Action<PrcBrepWriter>? after)
    {
        Bits.CompressedEntityType(false, kind);
        before?.Invoke(this);
        Bits.Bit(orientationWithShell).Bit(loops is not null);
        if (loops is not null)
        {
            for (int i = 0; i < loops.Count; i++)
            {
                Bits.Bit(loops[i].Orientation);
                foreach (Action<PrcBrepWriter> curve in loops[i].Curves)
                {
                    curve(this);
                }

                // A sentinel ends the loop: "not stored", then the type new loop (0) or end loop (1).
                Bits.Bit(true).CompressedEntityType(false, (byte)(i == loops.Count - 1 ? 1 : 0));
            }
        }

        if (pointOnTorus is { } p)
        {
            Point(p);
        }

        after?.Invoke(this);
        return this;
    }

    /// <summary>A shell: its faces in the order they belong in (each written by its action, with whether it is an iso face).</summary>
    public PrcBrepWriter Shell(params (Action<PrcBrepWriter> Face, bool Iso)[] faces)
    {
        Bits.Bit(faces.Length == 1);
        if (faces.Length != 1)
        {
            Bits.NumberOfBitsThenUnsigned((uint)faces.Length);
        }

        // All iso faces are stored first, then the others; the flags say which kind comes at each place of the shell.
        foreach ((Action<PrcBrepWriter> face, bool iso) in faces)
        {
            if (iso)
            {
                face(this);
            }
        }

        foreach ((Action<PrcBrepWriter> face, bool iso) in faces)
        {
            if (!iso)
            {
                face(this);
            }
        }

        foreach ((_, bool iso) in faces)
        {
            Bits.Bit(iso);
        }

        FaceCount += faces.Length;
        return this;
    }

    /// <summary>
    /// The start of a body up to its shells (type, an empty content body, tolerance, widths, counts, one connex or several);
    /// the caller writes the shells, then <see cref="EndBody"/>.
    /// </summary>
    public PrcBrepWriter BodyStart(uint isoVertices, uint isoEdges, bool singleConnex)
    {
        Bits.Unsigned(PrcType.PRC_TYPE_TOPO_BrepDataCompress).Bit(false).Bits(0, 8);                    // no base; no bounding box
        Bits.Double(Tolerance).NumberOfBitsThenUnsigned((uint)ReferenceBits);
        Bits.Bits(isoVertices, ReferenceBits).Bits(isoEdges, ReferenceBits).Bit(singleConnex);
        return this;
    }

    /// <summary>The end of a body: one <c>BaseTopology</c> (not there) for each face written.</summary>
    public PrcBitWriter EndBody()
    {
        for (int i = 0; i < FaceCount; i++)
        {
            Bits.Bit(false);
        }

        return Bits;
    }

    /// <summary>The surface of a generic ana face on a cylinder: no placement of its own, the given radius.</summary>
    public static Action<PrcBrepWriter> GenericCylinder(double radius) => w =>
    {
        w.Bits.Unsigned(PrcType.PRC_TYPE_SURF_Cylinder).Bit(false).Unsigned(0);                       // no base geometry; not extended
        w.Bits.Bit(false);                                                                          // no transformation
        w.Bits.Bit(false).Double(0).Double(0).Double(1).Double(1).Double(1).Double(0).Double(0).Double(0);   // no swap; domain; coefficients
        w.Bits.Double(radius);
    };

    // ---- compressed NURBS surfaces

    /// <summary>The stored form of a knot vector: uniform (nothing stored), pseudo-uniform (the two end intervals) or the interior values.</summary>
    public sealed record KnotSpec(string Form, int Bits, double[] Values)
    {
        public static KnotSpec Uniform { get; } = new("uniform", 0, []);

        /// <summary>All the interior knots (as the differences from the one before, per the draft).</summary>
        public static KnotSpec Interior(int bits, params double[] values) => new("interior", bits, values);

        /// <summary>Only the first and last intervals.</summary>
        public static KnotSpec Pseudo(int bits, double first, double last) => new("pseudo", bits, [first, last]);
    }

    /// <summary>One stored interior control point: its type (0 – 3) and the coordinates that type stores.</summary>
    public sealed record InteriorPoint(int Type, double X = 0, double Y = 0, double Z = 0);

    /// <summary>What a compressed NURBS surface stores (ISO 14739-1 draft 7.9.20.6; prc.json).</summary>
    public sealed record NurbsSpec(
        int DegreeU, int DegreeV,
        int[] MultiplicitiesU, int[] MultiplicitiesV,
        PrcVector P00, PrcVector[] StepsV, PrcVector[] StepsU, InteriorPoint[] Interior,
        KnotSpec KnotsU, KnotSpec KnotsV,
        int BitsIsoMin = 10, int BitsRest = 10, bool ClosedU = false, bool ClosedV = false,
        int WeightBits = 0, double WeightTolerance = 0.25, double[]? Weights = null);

    private static int MultiplicityBits(int degree) => degree != 0 ? (int)Math.Ceiling(Math.Log(degree + 2) / Math.Log(2)) : 2;

    private void Multiplicities(int degree, int[] multiplicities)
    {
        int bits = MultiplicityBits(degree);
        Bits.Bits((uint)multiplicities.Length, 16);
        for (int i = 0; i < multiplicities.Length; i++)
        {
            // A multiplicity that repeats the one before is not stored; the first always is.
            bool repeated = i > 0 && multiplicities[i] == multiplicities[i - 1];
            Bits.Bit(repeated);
            if (!repeated)
            {
                Bits.Bits((uint)multiplicities[i], bits);
            }
        }
    }

    private void KnotVector(KnotSpec knots)
    {
        if (knots.Form == "uniform")
        {
            Bits.Bit(true);
            return;
        }

        Bits.Bit(false);
        if (knots.Form == "interior")
        {
            Bits.Bit(true);
        }
        else
        {
            Bits.Bit(false).Bit(true);
        }

        Bits.Bits((uint)knots.Bits, 6);
        double tolerance = 1.0 / Math.Pow(2, knots.Bits - 1);
        foreach (double value in knots.Values)
        {
            if (knots.Bits > 30)
            {
                Bits.Double(value);
            }
            else
            {
                Bits.DoubleWithVariableBitNumber(value, knots.Bits + 1, tolerance);
            }
        }
    }

    /// <summary>A compressed NURBS surface: degrees, multiplicities, flags, widths, control points, knot vectors, weights.</summary>
    public PrcBrepWriter CompressedNurbs(NurbsSpec n)
    {
        double nurbsTolerance = Tolerance / 5;
        Bits.Bits((uint)n.DegreeU, 5).Bits((uint)n.DegreeV, 5);
        Multiplicities(n.DegreeU, n.MultiplicitiesU);
        Multiplicities(n.DegreeV, n.MultiplicitiesV);
        Bits.Bit(n.ClosedU).Bit(n.ClosedV).Bits((uint)n.BitsIsoMin, 20).Bits((uint)n.BitsRest, 20);
        Bits.Double(n.P00.X).Double(n.P00.Y).Double(n.P00.Z);
        foreach (PrcVector step in n.StepsV)
        {
            Bits.DoubleWithVariableBitNumber(step.X, n.BitsIsoMin + 1, nurbsTolerance).DoubleWithVariableBitNumber(step.Y, n.BitsIsoMin + 1, nurbsTolerance).DoubleWithVariableBitNumber(step.Z, n.BitsIsoMin + 1, nurbsTolerance);
        }

        foreach (PrcVector step in n.StepsU)
        {
            Bits.DoubleWithVariableBitNumber(step.X, n.BitsIsoMin + 1, nurbsTolerance).DoubleWithVariableBitNumber(step.Y, n.BitsIsoMin + 1, nurbsTolerance).DoubleWithVariableBitNumber(step.Z, n.BitsIsoMin + 1, nurbsTolerance);
        }

        foreach (InteriorPoint p in n.Interior)
        {
            Bits.Bits((uint)p.Type, 2);
            int width = n.BitsRest + 1;
            switch (p.Type)
            {
                case 1:
                    Bits.DoubleWithVariableBitNumber(p.Z, width, nurbsTolerance);
                    break;
                case 2:
                    Bits.DoubleWithVariableBitNumber(p.X, width, nurbsTolerance).DoubleWithVariableBitNumber(p.Y, width, nurbsTolerance);
                    break;
                case 3:
                    Bits.DoubleWithVariableBitNumber(p.X, width, nurbsTolerance).DoubleWithVariableBitNumber(p.Y, width, nurbsTolerance).DoubleWithVariableBitNumber(p.Z, width, nurbsTolerance);
                    break;
            }
        }

        KnotVector(n.KnotsU);
        KnotVector(n.KnotsV);
        Bits.Bit(n.Weights is not null);
        if (n.Weights is { } weights)
        {
            Bits.Bits((uint)n.WeightBits, 6);
            if (n.WeightBits > 30)
            {
                foreach (double w in weights)
                {
                    Bits.Double(w);
                }
            }
            else
            {
                Bits.Double(n.WeightTolerance);
                foreach (double w in weights)
                {
                    Bits.DoubleWithVariableBitNumber(w, n.WeightBits + 1, n.WeightTolerance);
                }
            }
        }

        return this;
    }

    /// <summary>One of the four trim curves of an iso NURBS face.</summary>
    public abstract record IsoTrim
    {
        public sealed record Referenced(int Index) : IsoTrim;

        public sealed record IsoBoundary : IsoTrim;

        public sealed record ImplicitLine : IsoTrim;

        public sealed record Circle(PrcVector Centre, PrcVector Normal) : IsoTrim;
    }

    /// <summary>
    /// An iso NURBS face: the orientations, the three booleans of the sense array, the surface, the four trim curves and the loop
    /// vertices that are needed (<c>vertices</c>: loop vertex 3, 0, 1, 2 in the order the file has them; null where not written).
    /// </summary>
    public PrcBrepWriter IsoNurbsFace(bool orientationWithShell, bool orientationLoop, (bool, bool, bool) sense, NurbsSpec surface, IsoTrim[] trims, BrepVertex?[] vertices)
    {
        Bits.CompressedEntityType(false, 7).Bit(orientationWithShell).Bit(orientationLoop).Bit(sense.Item1).Bit(sense.Item2).Bit(sense.Item3);
        CompressedNurbs(surface);
        foreach (IsoTrim trim in trims)
        {
            switch (trim)
            {
                case IsoTrim.Referenced r:
                    Bits.Bit(true).Bits((uint)r.Index, ReferenceBits);
                    break;
                case IsoTrim.IsoBoundary:
                    Bits.Bit(false).Bit(true);
                    break;
                case IsoTrim.ImplicitLine:
                    Bits.Bit(false).Bit(false).Bit(false);
                    break;
                case IsoTrim.Circle c:
                    Bits.Bit(false).Bit(false).Bit(true);
                    FullCircle(default, default, c.Centre, c.Normal, isoSpline: true);
                    break;
            }
        }

        foreach (BrepVertex? vertex in vertices)
        {
            if (vertex is { } v)
            {
                Vertex(v);
            }
        }

        return this;
    }
}
