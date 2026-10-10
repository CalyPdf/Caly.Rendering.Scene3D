using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// A B-spline surface with its values rebuilt: the knots with their multiplicities expanded, the control points (index
/// <c>u * CountV + v</c>) and the weights when it is rational.
/// </summary>
internal sealed class PrcNurbs
{
    public required int DegreeU { get; init; }

    public required int DegreeV { get; init; }

    /// <summary>All the knots in u, a knot as many times as its multiplicity says (<c>CountU + DegreeU + 1</c> of them).</summary>
    public required double[] KnotsU { get; init; }

    public required double[] KnotsV { get; init; }

    public required int CountU { get; init; }

    public required int CountV { get; init; }

    public required PrcVector[] ControlPoints { get; init; }

    /// <summary>One weight for each control point, or null for a surface that is not rational.</summary>
    public double[]? Weights { get; init; }

    public bool ClosedU { get; init; }

    public bool ClosedV { get; init; }
}

internal sealed partial class PrcCompressedBrepReader
{
    private readonly List<PrcNurbs> _nurbs = [];

    /// <summary>Control points of a surface a hand-written test or a corrupt file could announce: the data bounds them first.</summary>
    private const long MaxControlPoints = 1L << 26;

    /// <summary>The number of bits a multiplicity takes: enough for the degree plus two (draft 7.9.20.6.1).</summary>
    private static int MultiplicityBits(int degree) => degree != 0 ? (int)Math.Ceiling(Math.Log(degree + 2) / Math.Log(2)) : 2;

    /// <summary>
    /// A compressed NURBS surface (<c>CompressedNurbs</c>): the degrees, how many knots each way and their multiplicities (a
    /// multiplicity that repeats the one before is not stored), whether it closes, the widths of the control point numbers, the
    /// control points, the knot vectors and, when rational, the weights. The number of control points each way is the sum of the
    /// multiplicities less the degree less one (issue #663).
    /// </summary>
    public PrcNurbs ReadCompressedNurbs(ref PrcBitReader r)
    {
        _ctx.Enter("CompressedNurbs", r.BitPosition);
        int degreeU = (int)r.ReadBits(5);
        int degreeV = (int)r.ReadBits(5);
        int storedU = (int)r.ReadBits(16);
        uint[] multiplicitiesU = ReadMultiplicities(ref r, storedU, MultiplicityBits(degreeU), "CompressedMultiplicitiesU");
        int storedV = (int)r.ReadBits(16);
        uint[] multiplicitiesV = ReadMultiplicities(ref r, storedV, MultiplicityBits(degreeV), "CompressedMultiplicitiesV");
        bool closedU = r.ReadBoolean();
        bool closedV = r.ReadBoolean();
        int bitsIsoMin = (int)r.ReadBits(20);
        int bitsRest = (int)r.ReadBits(20);

        int countU = ControlPointCount(multiplicitiesU, degreeU);
        int countV = ControlPointCount(multiplicitiesV, degreeV);
        // Each control point takes three bits at the least: nothing is allocated on the announced size alone.
        long points = (long)countU * countV;
        if (points > MaxControlPoints)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"A compressed PRC surface announces {countU} by {countV} control points.");
        }

        _ctx.Count(ref r, points, 2);
        PrcVector[] controlPoints = ReadControlPoints(ref r, countU, countV, bitsIsoMin, bitsRest);
        double[] knotsU = ReadKnotVector(ref r, multiplicitiesU, "CompressedKnotVectorU", "CompressedKnotsU");
        double[] knotsV = ReadKnotVector(ref r, multiplicitiesV, "CompressedKnotVectorV", "CompressedKnotsV");
        double[]? weights = r.ReadBoolean() ? ReadWeights(ref r, (int)points) : null;
        _ctx.Leave();
        _ctx.Charge(points * 24 + (knotsU.Length + knotsV.Length) * 8L + (weights is null ? 0 : points * 8));
        return new PrcNurbs
        {
            DegreeU = degreeU,
            DegreeV = degreeV,
            KnotsU = knotsU,
            KnotsV = knotsV,
            CountU = countU,
            CountV = countV,
            ControlPoints = controlPoints,
            Weights = weights,
            ClosedU = closedU,
            ClosedV = closedV,
        };
    }

    private static int ControlPointCount(uint[] multiplicities, int degree)
    {
        long sum = 0;
        foreach (uint m in multiplicities)
        {
            sum += m;
        }

        long count = sum - degree - 1;
        if (count < 1 || count > int.MaxValue)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC surface has {count} control points in a direction.");
        }

        return (int)count;
    }

    /// <summary>
    /// The multiplicities of a direction: for each knot a flag that says the multiplicity is the one before (not stored; the first
    /// always is), else the multiplicity itself.
    /// </summary>
    private uint[] ReadMultiplicities(ref PrcBitReader r, int count, int bits, string name)
    {
        if (count < 2)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC surface has {count} knots in a direction; it needs two at least.");
        }

        _ctx.Count(ref r, count, 1);
        var multiplicities = new uint[count];
        for (int i = 0; i < count; i++)
        {
            _ctx.Enter(name, r.BitPosition);
            // The flag says the multiplicity is not stored: it is the one before; for the first knot, 1 (as the reference reader has it).
            multiplicities[i] = r.ReadBoolean() ? (i > 0 ? multiplicities[i - 1] : 1) : r.ReadBits(bits);
            _ctx.Leave();
        }

        return multiplicities;
    }

    /// <summary>
    /// The control points: the corner, the rest of the first row (the v direction) and of the first column (u) as steps from the
    /// one before, then each other point as a correction to the fourth corner of the parallelogram that the three points before it
    /// make (draft 7.9.20.6.3.1), in the local frame of the two steps or as plain coordinates by its type (see the comment in the loop).
    /// </summary>
    private PrcVector[] ReadControlPoints(ref PrcBitReader r, int countU, int countV, int bitsIsoMin, int bitsRest)
    {
        double tolerance = Tolerance / 5;
        _ctx.Enter("CompressedControlPoints", r.BitPosition);
        PrcVector3D corner = PrcReaders.ReadVector3D(ref r, _ctx);
        var points = new PrcVector[countU * countV];
        points[0] = new PrcVector(corner.X, corner.Y, corner.Z);
        for (int j = 1; j < countV; j++)
        {
            _ctx.Mark("Point3DWithVariableBitNumber", r.BitPosition);
            points[j] = points[j - 1] + r.ReadPoint3DWithVariableBitNumber(bitsIsoMin + 1, tolerance);
        }

        for (int i = 1; i < countU; i++)
        {
            _ctx.Mark("Point3DWithVariableBitNumber", r.BitPosition);
            points[i * countV] = points[(i - 1) * countV] + r.ReadPoint3DWithVariableBitNumber(bitsIsoMin + 1, tolerance);
        }

        for (int i = 1; i < countU; i++)
        {
            for (int j = 1; j < countV; j++)
            {
                _ctx.Enter("InteriorCompressedControlPoints", r.BitPosition);
                uint type = r.ReadBits(2);
                double x = 0, y = 0, z = 0;
                int width = bitsRest + 1;
                switch (type)
                {
                    case 1:
                        z = r.ReadDoubleWithVariableBitNumber(width, tolerance);
                        break;
                    case 2:
                        x = r.ReadDoubleWithVariableBitNumber(width, tolerance);
                        y = r.ReadDoubleWithVariableBitNumber(width, tolerance);
                        break;
                    case 3:
                        x = r.ReadDoubleWithVariableBitNumber(width, tolerance);
                        y = r.ReadDoubleWithVariableBitNumber(width, tolerance);
                        z = r.ReadDoubleWithVariableBitNumber(width, tolerance);
                        break;
                }

                _ctx.Leave();

                // The fourth corner of the parallelogram of the three points before, plus the correction. A correction of type 1 (along
                // the normal only) or 2 (in the tangent plane only) is in the draft's local frame (7.9.20.6.3.1): U the step along u at
                // v − 1, N = U × V, axes U/|U|, N × U, N; one of type 3 is in plain coordinates (the type the draft says a frame of no
                // length falls back to). Read so, 981 faces of the corpus are left out; all plain, 1,784; all in the frame, 2,356.
                PrcVector origin = points[((i - 1) * countV) + j - 1];
                PrcVector u = points[(i * countV) + j - 1] - origin, v = points[((i - 1) * countV) + j] - origin;
                PrcVector predicted = origin + u + v;
                points[(i * countV) + j] = type switch
                {
                    0 => predicted,
                    3 => predicted + new PrcVector(x, y, z),
                    _ => predicted + Local(u, v, x, y, z),
                };
            }
        }

        _ctx.Leave();
        return points;
    }

    /// <summary>A correction in the local frame of the two steps before a control point (as plain coordinates when the frame has no length).</summary>
    internal static PrcVector Local(PrcVector u, PrcVector v, double x, double y, double z)
    {
        PrcVector normal = u.Cross(v);
        double lu = Math.Sqrt(u.LengthSquared()), ln = Math.Sqrt(normal.LengthSquared());
        if (!(lu > 1e-12) || !(ln > 1e-12))
        {
            return new PrcVector(x, y, z);
        }

        PrcVector e1 = u / lu, e3 = normal / ln, e2 = e3.Cross(e1);
        return (e1 * x) + (e2 * y) + (e3 * z);
    }

    /// <summary>
    /// The knots of a direction (<c>CompressedKnotVector</c>): uniform (nothing stored); or a form, a width and the values — the
    /// first and last intervals when two are stored (pseudo-uniform), else the interior knots, each as a step from the one before
    /// (the draft, 7.9.20.6.4.1). The first and last distinct knots are 0 and 1. Returns the knots with their multiplicities.
    /// </summary>
    private double[] ReadKnotVector(ref PrcBitReader r, uint[] multiplicities, string vectorName, string knotsName)
    {
        int distinct = multiplicities.Length;
        double[] values;
        _ctx.Enter(vectorName, r.BitPosition);
        if (r.ReadBoolean())
        {
            values = new double[distinct];
            for (int k = 0; k < distinct; k++)
            {
                values[k] = (double)k / (distinct - 1);
            }
        }
        else
        {
            _ctx.Enter(knotsName, r.BitPosition);
            bool interiorForm = r.ReadBoolean();
            bool pseudoUniform = !interiorForm && r.ReadBoolean();
            int bits = (int)r.ReadBits(6);
            double knotTolerance = 1.0 / Math.Pow(2, bits - 1);
            int stored = pseudoUniform ? 2 : distinct - 2;
            _ctx.Count(ref r, stored, 1);
            var stepValues = new double[stored];
            for (int k = 0; k < stored; k++)
            {
                _ctx.Enter("CompressedKnot", r.BitPosition);
                stepValues[k] = bits > 30 ? r.ReadDouble() : r.ReadDoubleWithVariableBitNumber(bits + 1, knotTolerance);
                _ctx.Leave();
            }

            _ctx.Leave();
            values = pseudoUniform ? PseudoUniform(stepValues[0], stepValues[1], distinct) : Cumulative(stepValues, distinct);
        }

        _ctx.Leave();
        long total = 0;
        foreach (uint m in multiplicities)
        {
            total += m;
        }

        var knots = new double[total];
        int at = 0;
        for (int k = 0; k < distinct; k++)
        {
            for (uint m = 0; m < multiplicities[k]; m++)
            {
                knots[at++] = values[k];
            }
        }

        return knots;
    }

    private static double[] Cumulative(double[] steps, int distinct)
    {
        var values = new double[distinct];
        for (int k = 1; k < distinct - 1; k++)
        {
            values[k] = values[k - 1] + steps[k - 1];
        }

        values[distinct - 1] = 1;
        return values;
    }

    private static double[] PseudoUniform(double first, double last, int distinct)
    {
        var values = new double[distinct];
        values[distinct - 1] = 1;
        if (distinct >= 3)
        {
            values[1] = first;
            values[distinct - 2] = 1 - last;
        }

        // The knots between the second and the last but one are evenly spaced.
        for (int k = 2; k < distinct - 2; k++)
        {
            values[k] = first + ((k - 1) * (1 - last - first) / (distinct - 3));
        }

        return values;
    }

    /// <summary>The weights (<c>CompressedWeights</c>): a width; plain doubles above 30, else a tolerance and numbers of that width.</summary>
    private double[] ReadWeights(ref PrcBitReader r, int count)
    {
        _ctx.Enter("CompressedWeights", r.BitPosition);
        int bits = (int)r.ReadBits(6);
        var weights = new double[count];
        if (bits > 30)
        {
            for (int i = 0; i < count; i++)
            {
                weights[i] = r.ReadDouble();
            }
        }
        else
        {
            double tolerance = r.ReadDouble();
            for (int i = 0; i < count; i++)
            {
                weights[i] = r.ReadDoubleWithVariableBitNumber(bits + 1, tolerance);
            }
        }

        _ctx.Leave();
        return weights;
    }

    // ---- the faces that carry one

    /// <summary>
    /// An iso NURBS face (<c>PRC_HCG_IsoNurbs</c>): its own record, not the common boundary of the iso faces. Every side that is not
    /// referred to is a new curve, stored or deduced (and numbered either way).
    /// </summary>
    private void ReadIsoNurbsFace(ref PrcBitReader r, ref FaceParts parts)
    {
        parts.OrientationWithShell = r.ReadBoolean();
        bool orientationLoop = r.ReadBoolean();
        _surfaceData.Add(r.ReadBoolean() ? 1 : 0);
        _surfaceData.Add(r.ReadBoolean() ? 1 : 0);
        _surfaceData.Add(r.ReadBoolean() ? 1 : 0);
        parts.Surface = _nurbs.Count;
        _nurbs.Add(ReadCompressedNurbs(ref r));

        // Four sides: each a curve stored before (an index), a circle, a line deduced from the loop vertices (no data), or one of
        // the natural boundaries of the surface (issues #582, #666).
        var sides = new int[4];
        var referenced = new bool[4];
        for (int side = 0; side < 4; side++)
        {
            _ctx.Enter("IsoNurbsTrimCurve", r.BitPosition);
            if (r.ReadBoolean())
            {
                referenced[side] = true;
                sides[side] = ResolveCurve(r.ReadBits(_referenceBits));
            }
            else
            {
                _ctx.Enter("IsoNurbsTrimCrv", r.BitPosition);
                if (r.ReadBoolean())
                {
                    sides[side] = AddImplicitCurve(PrcCurveKind.IsoBoundary, side);
                }
                else if (r.ReadBoolean())
                {
                    sides[side] = ReadIsoSplineCircle(ref r);
                }
                else
                {
                    sides[side] = AddImplicitCurve(PrcCurveKind.Implicit, side);
                }

                _ctx.Leave();
            }

            _ctx.Leave();
        }

        // The vertex between two neighbouring sides is stored only when neither of the two was stored before: vertex 3 between
        // sides 3 and 0, vertex 0 between 0 and 1, vertex 1 between 1 and 2, vertex 2 between 2 and 3.
        ReadOnlySpan<int> between = [3, 0, 0, 1, 1, 2, 2, 3];
        for (int v = 0; v < 4; v++)
        {
            bool stored = !referenced[between[2 * v]] && !referenced[between[(2 * v) + 1]];
            _faceVertices.Add(stored ? ReadVertex(ref r) : -1);
        }

        parts.VertexCount = 4;
        int firstCoedge = _coedges.Count;
        _coedges.AddRange(sides);
        _loops.Add(new PrcBrepLoop(firstCoedge, 4, orientationLoop));
        parts.Trimmed = true;
        parts.LoopCount = 1;
    }
}
