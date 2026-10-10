using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>What a curve of a compressed body is, as far as the surfaces need to know: a straight line, an arc of a circle, or neither.</summary>
internal static class PrcBrepCurves
{
    /// <summary>An arc of a circle: <c>p(φ) = c + r(φ) (cos φ U + sin φ V)</c> for φ from 0 to the signed sweep, r from the start's radius to the end's.</summary>
    public readonly record struct Arc(PrcVector Centre, PrcVector U, PrcVector V, double StartRadius, double EndRadius, double Sweep);


    /// <summary>The circle or arc a curve is, from what is stored of it (R6 of the decoding note).</summary>
    public static bool TryGetArc(PrcTopoBrepDataCompress brep, PrcBrepCurve curve, PrcVector start, PrcVector end, out Arc arc)
    {
        var flags = (PrcBrepCurveFlags)curve.Flags;
        ReadOnlySpan<double> data = brep.CurveData.AsSpan(curve.DataStart, curve.DataCount);
        arc = default;
        if ((flags & PrcBrepCurveFlags.Particular) != 0 && (flags & PrcBrepCurveFlags.Full) != 0)
        {
            // A whole circle: centre and normal; it starts and ends at the vertex.
            var centre = new PrcVector(data[0], data[1], data[2]);
            var normal = new PrcVector(data[3], data[4], data[5]);
            double length = Math.Sqrt(normal.LengthSquared());
            if (!(length > 1e-12))
            {
                return false;
            }

            normal /= length;
            PrcVector radial = start - centre;
            radial -= normal * radial.Dot(normal);
            double r = Math.Sqrt(radial.LengthSquared());
            if (!(r > 0))
            {
                return false;
            }

            PrcVector u = radial / r;
            arc = new Arc(centre, u, normal.Cross(u), r, r, 2 * Math.PI);
            return true;
        }

        if ((flags & PrcBrepCurveFlags.Particular) != 0)
        {
            // Half a circle: the middle of the arc is stored, the centre is halfway between the ends. A few in a hundred are not half
            // circles (their middle point is a fifth nearer or further than the radius): the circle through the ends and that point.
            var middle = new PrcVector(data[0], data[1], data[2]);
            PrcVector centre = (start + end) / 2;
            double r = Math.Sqrt((end - start).LengthSquared()) / 2;
            if (!(r > 0))
            {
                return false;
            }

            double off = Math.Abs(Math.Sqrt((middle - centre).LengthSquared()) - r);
            if (off > Math.Max(1e-3 * r, 10 * brep.Tolerance))
            {
                return TryFitArc(start, middle, end, out arc);
            }

            PrcVector u = (start - centre) / r;
            PrcVector toMiddle = middle - centre;
            toMiddle -= u * toMiddle.Dot(u);
            double m = Math.Sqrt(toMiddle.LengthSquared());
            if (!(m > 0))
            {
                return false;
            }

            arc = new Arc(centre, u, toMiddle / m, r, r, Math.PI);
            return true;
        }

        {
            // Any other arc: its ends and its centre; the plane is the one they are in, the way round is the short one unless the
            // angle passes a half turn.
            var centre = new PrcVector(data[0], data[1], data[2]);
            PrcVector a = start - centre, b = end - centre;
            double ra = Math.Sqrt(a.LengthSquared()), rb = Math.Sqrt(b.LengthSquared());
            if (!(ra > 0) || !(rb > 0))
            {
                return false;
            }

            PrcVector u = a / ra, e = b / rb;
            PrcVector n = u.Cross(e);
            double sin = Math.Sqrt(n.LengthSquared());
            if (sin < 1e-12)
            {
                return false;
            }

            double theta = Math.Atan2(sin, u.Dot(e));
            PrcVector v = (n / sin).Cross(u);
            double sweep = (flags & PrcBrepCurveFlags.OverHalfTurn) != 0 ? -((2 * Math.PI) - theta) : theta;
            arc = new Arc(centre, u, v, ra, rb, sweep);
            return true;
        }
    }


    /// <summary>
    /// The circle through three points that are in this order along an arc (not in line): its centre, the way round from the first
    /// to the last through the second, and its radius.
    /// </summary>
    public static bool TryFitArc(PrcVector a, PrcVector b, PrcVector c, out Arc arc)
    {
        arc = default;
        PrcVector ab = b - a, ac = c - a;
        PrcVector n = ab.Cross(ac);
        double n2 = n.LengthSquared();
        if (!(n2 > 1e-300))
        {
            return false;
        }

        // The centre: a + (|ac|² (ab × ac) × ab + |ab|² ac × (ab × ac)) / (2 |ab × ac|²).
        PrcVector centre = a + (((n.Cross(ab) * ac.LengthSquared()) + (ac.Cross(n) * ab.LengthSquared())) / (2 * n2));
        double r = Math.Sqrt((a - centre).LengthSquared());
        if (!(r > 0))
        {
            return false;
        }

        PrcVector u = (a - centre) / r;
        PrcVector unit = n / Math.Sqrt(n2);
        PrcVector v = unit.Cross(u);
        PrcVector toC = c - centre;
        double sweep = Math.Atan2(toC.Dot(v), toC.Dot(u));
        if (sweep < 0)
        {
            sweep += 2 * Math.PI;
        }

        arc = new Arc(centre, u, v, r, r, sweep);
        return true;
    }

    /// <summary>
    /// What a curve is: a line between two points, an arc, or neither. A curve with ends of its own is read from what is stored of
    /// it; one that was deduced by a face (or is a spline) from its samples.
    /// </summary>
    public static bool TryDescribe(PrcTopoBrepDataCompress brep, PrcEdgeSampler edges, int curveId, out bool isLine, out PrcVector start, out PrcVector end, out Arc arc)
    {
        PrcBrepCurve curve = brep.Curves[curveId];
        isLine = false;
        arc = default;
        start = default;
        end = default;
        if (curve.Kind == PrcCurveKind.Line && curve.Start >= 0 && curve.End >= 0)
        {
            isLine = true;
            start = brep.Vertices[curve.Start];
            end = brep.Vertices[curve.End];
            return true;
        }

        if (curve.Kind == PrcCurveKind.Circle && curve.Start >= 0 && curve.End >= 0)
        {
            start = brep.Vertices[curve.Start];
            end = brep.Vertices[curve.End];
            return TryGetArc(brep, curve, start, end, out arc);
        }

        if (curve.Kind is PrcCurveKind.Implicit or PrcCurveKind.IsoBoundary && !edges.IsResolved(curveId))
        {
            return false;
        }

        ReadOnlySpan<PrcVector> samples = edges.Samples(curveId);
        if (samples.Length < 2)
        {
            return false;
        }

        start = samples[0];
        end = samples[^1];
        if (edges.TryGetArc(curveId, out arc))
        {
            return true;            // a deduced side that is an arc of its face's surface, however few its points
        }

        if (samples.Length == 2)
        {
            isLine = true;
            return true;
        }

        PrcVector chordVector = end - start;
        double length = Math.Sqrt(chordVector.LengthSquared());
        double worst = 0;
        foreach (PrcVector s in samples)
        {
            worst = Math.Max(worst, length > 0 ? Math.Sqrt(chordVector.Cross(s - start).LengthSquared()) / length : Math.Sqrt((s - start).LengthSquared()));
        }

        if (worst <= 1e-9 * Math.Max(length, 1e-12))
        {
            isLine = true;
            return true;
        }

        return TryFitArc(start, samples[samples.Length / 2], end, out arc);
    }
}