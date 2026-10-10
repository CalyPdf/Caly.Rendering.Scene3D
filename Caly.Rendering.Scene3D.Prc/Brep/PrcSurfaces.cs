using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

internal enum PrcSurfaceKind : byte
{
    Plane,
    Cylinder,
    Cone,
    Sphere,
    Torus,
    Nurbs,
}

/// <summary>
/// A surface of a compressed face with a parameterisation chosen here: the format stores none for compressed faces (draft 7.9.20.5.1).
/// The analytic kinds sit in a frame (an origin and three perpendicular unit axes; the Z axis of a cylinder may lean over: the surface\n/// that an iso cylinder face names is a circle swept along a line, and the line need not be square to the circle's plane):
/// <list type="bullet">
/// <item>plane — <c>O + u X + v Y</c>;</item>
/// <item>cylinder, radius r — <c>O + r (cos u X + sin u Y) + v Z</c>;</item>
/// <item>cone, apex O, <c>tan</c> of the half angle t — <c>O + v Z + v t (cos u X + sin u Y)</c>;</item>
/// <item>sphere, radius r — <c>O + r (cos v (cos u X + sin u Y) + sin v Z)</c>;</item>
/// <item>torus, radii R and r — <c>O + (R + r cos v) (cos u X + sin u Y) + r sin v Z</c>.</item>
/// </list>
/// The normal is the natural one (<c>∂u × ∂v</c>); the face's own orientation turns it round.
/// </summary>
internal readonly struct PrcSurface
{
    private readonly Matrix4x4d _frame;
    private readonly double _r1;
    private readonly double _r2;
    private readonly PrcNurbs? _nurbs;

    /// <summary>A B-spline surface (its own parameters, its derivatives from the spline).</summary>
    public PrcSurface(PrcNurbs nurbs)
    {
        Kind = PrcSurfaceKind.Nurbs;
        _frame = Matrix4x4d.Identity;
        _r1 = 0;
        _r2 = 0;
        _nurbs = nurbs;
    }

    /// <summary>An analytic surface: <paramref name="r1"/> is the radius (the major radius of a torus, the tangent of a cone's half angle), <paramref name="r2"/> the minor radius of a torus.</summary>
    public PrcSurface(PrcSurfaceKind kind, Matrix4x4d frame, double r1, double r2)
    {
        Kind = kind;
        _frame = frame;
        _r1 = r1;
        _r2 = r2;
        _nurbs = null;
    }

    public PrcSurfaceKind Kind { get; }

    /// <summary>The spline of a <see cref="PrcSurfaceKind.Nurbs"/> surface.</summary>
    public PrcNurbs? Nurbs => _nurbs;

    /// <summary>Whether the surface comes back on itself when <c>u</c> grows by <see cref="PeriodU"/>.</summary>
    public bool PeriodicU => Kind is PrcSurfaceKind.Cylinder or PrcSurfaceKind.Cone or PrcSurfaceKind.Sphere or PrcSurfaceKind.Torus || (Kind == PrcSurfaceKind.Nurbs && _nurbs!.ClosedU);

    /// <summary>Whether it does the same in <c>v</c> (a torus).</summary>
    public bool PeriodicV => Kind == PrcSurfaceKind.Torus || (Kind == PrcSurfaceKind.Nurbs && _nurbs!.ClosedV);

    public double PeriodU => Kind == PrcSurfaceKind.Nurbs ? PrcNurbsSurface.MaxU(_nurbs!) - PrcNurbsSurface.MinU(_nurbs!) : 2 * Math.PI;

    public double PeriodV => Kind == PrcSurfaceKind.Nurbs ? PrcNurbsSurface.MaxV(_nurbs!) - PrcNurbsSurface.MinV(_nurbs!) : 2 * Math.PI;

    public Matrix4x4d Frame => _frame;

    public double Radius => _r1;

    public double SecondRadius => _r2;

    public PrcVector Evaluate(double u, double v, out PrcVector du, out PrcVector dv)
    {
        PrcVector x = _frame.X, y = _frame.Y, z = _frame.Z, o = _frame.Origin;
        double c = Math.Cos(u), s = Math.Sin(u);
        PrcVector radial = (x * c) + (y * s), tangent = (y * c) - (x * s);
        switch (Kind)
        {
            case PrcSurfaceKind.Plane:
                du = x;
                dv = y;
                return o + (x * u) + (y * v);
            case PrcSurfaceKind.Cylinder:
                du = tangent * _r1;
                dv = z;
                return o + (radial * _r1) + (z * v);
            case PrcSurfaceKind.Cone:
                du = tangent * (v * _r1);
                dv = z + (radial * _r1);
                return o + (z * v) + (radial * (v * _r1));
            case PrcSurfaceKind.Sphere:
            {
                double cv = Math.Cos(v), sv = Math.Sin(v);
                du = tangent * (_r1 * cv);
                dv = ((radial * -sv) + (z * cv)) * _r1;
                return o + (((radial * cv) + (z * sv)) * _r1);
            }

            case PrcSurfaceKind.Torus:
            {
                double cv = Math.Cos(v), sv = Math.Sin(v);
                double rho = _r1 + (_r2 * cv);
                du = tangent * rho;
                dv = ((radial * -sv) + (z * cv)) * _r2;
                return o + (radial * rho) + (z * (_r2 * sv));
            }

            default:
                return PrcNurbsSurface.Evaluate(_nurbs!, u, v, out du, out dv);
        }
    }

    /// <summary>The unit normal at a parameter.</summary>
    public PrcVector Normal(double u, double v)
    {
        if (Kind == PrcSurfaceKind.Nurbs)
        {
            return NurbsNormal(u, v);
        }

        PrcVector x = _frame.X, y = _frame.Y, z = _frame.Z;
        double c = Math.Cos(u), s = Math.Sin(u);
        PrcVector radial = (x * c) + (y * s);
        PrcVector n = Kind switch
        {
            PrcSurfaceKind.Plane => z,
            PrcSurfaceKind.Cylinder => (((y * c) - (x * s)).Cross(z)),
            PrcSurfaceKind.Cone => (radial - (z * _r1)) * (v < 0 ? -1 : 1),     // ∂u × ∂v: away from the axis on either nappe
            PrcSurfaceKind.Sphere or PrcSurfaceKind.Torus => (radial * Math.Cos(v)) + (z * Math.Sin(v)),
            _ => z,
        };
        double length = Math.Sqrt(n.LengthSquared());
        return length > 0 ? n / length : z;
    }

    /// <summary>The normal of a spline: the product of its derivatives, or, where they vanish (a pole), that a little way off.</summary>
    private PrcVector NurbsNormal(double u, double v)
    {
        PrcNurbs n = _nurbs!;
        PrcNurbsSurface.Evaluate(n, u, v, out PrcVector du, out PrcVector dv);
        PrcVector normal = du.Cross(dv);
        double length = Math.Sqrt(normal.LengthSquared());
        if (length > 1e-14 * (1 + Math.Sqrt(du.LengthSquared()) * Math.Sqrt(dv.LengthSquared())))
        {
            return normal / length;
        }

        double eu = (PrcNurbsSurface.MaxU(n) - PrcNurbsSurface.MinU(n)) * 1e-4, ev = (PrcNurbsSurface.MaxV(n) - PrcNurbsSurface.MinV(n)) * 1e-4;
        PrcNurbsSurface.Evaluate(n, u + eu, v + ev, out du, out dv);
        normal = du.Cross(dv);
        length = Math.Sqrt(normal.LengthSquared());
        return length > 0 ? normal / length : new PrcVector(0, 0, 1);
    }

    /// <summary>
    /// The parameters of the point of the surface nearest to a point (exact for a point on it): angles are taken as near as
    /// possible to the hints, so that a curve followed point by point does not jump a whole turn.
    /// </summary>
    public bool TryInvert(PrcVector p, double hintU, double hintV, out double u, out double v) => TryInvert(p, hintU, hintV, double.PositiveInfinity, out u, out v);

    /// <summary>As <see cref="TryInvert(PrcVector, double, double, out double, out double)"/>; a spline's nearest point may be further than <paramref name="maxDistance"/> from the point, which is then refused.</summary>
    public bool TryInvert(PrcVector p, double hintU, double hintV, double maxDistance, out double u, out double v)
    {
        if (Kind == PrcSurfaceKind.Nurbs)
        {
            return PrcNurbsSurface.TryInvert(_nurbs!, p, hintU, hintV, maxDistance, out u, out v);
        }

        PrcVector d = p - _frame.Origin;
        double qx = d.Dot(_frame.X), qy = d.Dot(_frame.Y), qz = d.Dot(_frame.Z);
        u = 0;
        v = 0;
        switch (Kind)
        {
            case PrcSurfaceKind.Plane:
                u = qx;
                v = qy;
                return true;
            case PrcSurfaceKind.Cylinder:
            {
                // The frame's Z may lean over the plane of X and Y: take the part along it out first.
                PrcVector plane = _frame.X.Cross(_frame.Y);
                double lean = _frame.Z.Dot(plane);
                if (!(Math.Abs(lean) > 1e-12))
                {
                    return false;
                }

                v = d.Dot(plane) / lean;
                PrcVector across = d - (_frame.Z * v);
                qx = across.Dot(_frame.X);
                qy = across.Dot(_frame.Y);
                u = Near(Math.Atan2(qy, qx), hintU, qx, qy);
                return true;
            }
            case PrcSurfaceKind.Cone:
                // Below the apex the radius v · tan is negative: the point's angle is the other side of the axis.
                v = qz;
                u = v * _r1 < 0 ? Near(Math.Atan2(-qy, -qx), hintU, qx, qy) : Near(Math.Atan2(qy, qx), hintU, qx, qy);
                return true;
            case PrcSurfaceKind.Sphere:
            {
                double rho = Math.Sqrt((qx * qx) + (qy * qy));
                u = Near(Math.Atan2(qy, qx), hintU, qx, qy);
                v = Math.Atan2(qz, rho);
                return !double.IsNaN(u + v);
            }

            case PrcSurfaceKind.Torus:
            {
                double rho = Math.Sqrt((qx * qx) + (qy * qy));
                u = Near(Math.Atan2(qy, qx), hintU, qx, qy);
                v = Math.Atan2(qz, rho - _r1);
                v = hintV + WrapToPi(v - hintV);
                return !double.IsNaN(u + v);
            }

            default:
                return false;
        }
    }

    /// <summary>The angle <paramref name="angle"/> turned by whole turns to be nearest the hint; the hint itself on the axis, where no angle is defined.</summary>
    private static double Near(double angle, double hint, double x, double y)
    {
        if (x == 0 && y == 0)
        {
            return hint;
        }

        return hint + WrapToPi(angle - hint);
    }

    private static double WrapToPi(double angle)
    {
        angle %= 2 * Math.PI;
        if (angle > Math.PI)
        {
            angle -= 2 * Math.PI;
        }
        else if (angle < -Math.PI)
        {
            angle += 2 * Math.PI;
        }

        return angle;
    }

    /// <summary>
    /// The largest steps in <c>u</c> and <c>v</c> that keep chords within <paramref name="chord"/> of the surface and normals within
    /// the angle cap; infinite along a direction in which the surface is straight. <paramref name="vMin"/> and <paramref name="vMax"/> are the extent in <c>v</c> that will be cut (it sizes a cone's circles).
    /// </summary>
    public (double Du, double Dv) Steps(double chord, double vMin, double vMax)
    {
        switch (Kind)
        {
            case PrcSurfaceKind.Cylinder:
                return (AngularStep(chord, _r1), double.PositiveInfinity);
            case PrcSurfaceKind.Cone:
                return (AngularStep(chord, Math.Max(Math.Abs(vMin), Math.Abs(vMax)) * _r1), double.PositiveInfinity);
            case PrcSurfaceKind.Sphere:
            {
                double step = AngularStep(chord, _r1);
                return (step, step);
            }

            case PrcSurfaceKind.Torus:
                return (AngularStep(chord, _r1 + _r2), AngularStep(chord, _r2));
            default:
                return (double.PositiveInfinity, double.PositiveInfinity);
        }
    }

    /// <summary>The angle whose chord on a circle of the radius is within the tolerance of it, at most the cap.</summary>
    public static double AngularStep(double chord, double radius)
    {
        if (!(radius > 0) || chord >= radius)
        {
            return PrcBrepTolerance.MaxTurnRadians;
        }

        return Math.Min(2 * Math.Acos(1 - (chord / radius)), PrcBrepTolerance.MaxTurnRadians);
    }
}

/// <summary>Rebuilds the surface of a compressed face from what the face stores and the curves that bound it (draft 7.9.20.5; decoding note R5, R10).</summary>
internal static class PrcSurfaces
{
    /// <summary>
    /// The surface of a face. False when what it stores and the curves around it do not make the surface it names (a cylinder whose
    /// curves are two lines), or of a kind that is not read.
    /// </summary>
    public static bool TryBuild(PrcTopoBrepDataCompress brep, int face, PrcEdgeSampler edges, out PrcSurface surface)
    {
        surface = default;
        PrcBrepFace f = brep.Faces[face];
        ReadOnlySpan<double> data = brep.SurfaceData.AsSpan(f.SurfaceDataStart, f.SurfaceDataCount);
        switch (f.Kind)
        {
            case PrcFaceKind.IsoPlane:
            case PrcFaceKind.AnaPlane:
                return TryPlane(brep, f, edges, data, out surface);
            case PrcFaceKind.IsoCylinder:
                return TryIsoCylinder(brep, f, edges, out surface);
            case PrcFaceKind.IsoCone:
                return TryIsoCone(brep, f, edges, out surface);
            case PrcFaceKind.IsoSphere:
                return TryIsoSphere(brep, f, edges, out surface);
            case PrcFaceKind.IsoTorus:
                return TryIsoTorus(brep, f, edges, data, out surface);
            case PrcFaceKind.AnaCylinder:
                return TryAnaCylinder(brep, f, edges, data, out surface);
            case PrcFaceKind.AnaSphere:
                return TryAnaSphere(brep, f, edges, data, out surface);
            case PrcFaceKind.AnaTorus:
                return TryAnaTorus(data, out surface);
            case PrcFaceKind.AnaCone:
                return TryAnaCone(brep, f, edges, data, out surface);
            case PrcFaceKind.AnaGenericFace:
                return TryGeneric(brep, f, out surface);
            case PrcFaceKind.IsoNurbs:
            case PrcFaceKind.AnaNurbs:
                if (f.Surface < 0 || f.Surface >= brep.Nurbs.Length)
                {
                    return false;
                }

                surface = new PrcSurface(brep.Nurbs[f.Surface]);
                return true;
            default:
                return false;
        }
    }

    private static PrcVector Point(ReadOnlySpan<double> data, int at) => new(data[at], data[at + 1], data[at + 2]);

    /// <summary>The unit axes of a frame whose third axis is <paramref name="z"/>, the first as near to <paramref name="xHint"/> as is perpendicular to it.</summary>
    internal static Matrix4x4d FrameAt(PrcVector origin, PrcVector z, PrcVector xHint)
    {
        PrcVector zu = z / Math.Sqrt(z.LengthSquared());
        PrcVector x = xHint - (zu * xHint.Dot(zu));
        double length = Math.Sqrt(x.LengthSquared());
        if (!(length > 1e-12))
        {
            // Any perpendicular: the axis of smallest component crossed with z.
            PrcVector other = Math.Abs(zu.X) < 0.6 ? new PrcVector(1, 0, 0) : new PrcVector(0, 1, 0);
            x = other - (zu * other.Dot(zu));
            length = Math.Sqrt(x.LengthSquared());
        }

        x /= length;
        return new Matrix4x4d(origin, x, zu.Cross(x), zu);
    }

    /// <summary>The points of the loops of a face: the vertices its curves start and end at, and the first and last samples of the deduced ones.</summary>
    private static List<PrcVector> LoopPoints(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges)
    {
        var points = new List<PrcVector>();
        if (f.LoopCount == 0)
        {
            return points;
        }

        // Every loop's: the first of a cone's may be the apex alone (a vertex loop) and say nothing of the radius.
        for (int l = 0; l < f.LoopCount; l++)
        {
            PrcBrepLoop loop = brep.Loops[f.FirstLoop + l];
            foreach (int curveId in brep.Coedges.AsSpan(loop.FirstCoedge, loop.CoedgeCount))
            {
                PrcBrepCurve curve = brep.Curves[curveId];
                if (curve.Start >= 0 && curve.End >= 0)
                {
                    points.Add(brep.Vertices[curve.Start]);
                    points.Add(brep.Vertices[curve.End]);
                }
                else if (edges.IsResolved(curveId))
                {
                    ReadOnlySpan<PrcVector> samples = edges.Samples(curveId);
                    points.Add(samples[0]);
                    points.Add(samples[^1]);
                }
            }
        }

        return points;
    }

    private static bool Sides(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, out CurveFacts first, out CurveFacts second)
    {
        first = default;
        second = default;
        if (f.LoopCount == 0)
        {
            return false;
        }

        PrcBrepLoop loop = brep.Loops[f.FirstLoop];
        if (loop.CoedgeCount < 2)
        {
            return false;
        }

        ReadOnlySpan<int> curves = brep.Coedges.AsSpan(loop.FirstCoedge, loop.CoedgeCount);
        return Facts(brep, edges, curves[0], out first) && Facts(brep, edges, curves[1], out second);
    }

    private readonly struct CurveFacts
    {
        public bool IsLine { get; init; }

        public PrcVector Start { get; init; }

        public PrcVector End { get; init; }

        public PrcBrepCurves.Arc Arc { get; init; }
    }

    private static bool Facts(PrcTopoBrepDataCompress brep, PrcEdgeSampler edges, int curve, out CurveFacts facts)
    {
        bool ok = PrcBrepCurves.TryDescribe(brep, edges, curve, out bool isLine, out PrcVector start, out PrcVector end, out PrcBrepCurves.Arc arc);
        facts = new CurveFacts { IsLine = isLine, Start = start, End = end, Arc = arc };
        return ok;
    }

    private static PrcVector ArcNormal(in PrcBrepCurves.Arc arc)
    {
        PrcVector n = arc.U.Cross(arc.V);
        return n / Math.Sqrt(n.LengthSquared());
    }

    // ---- planes

    private static bool TryPlane(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, ReadOnlySpan<double> data, out PrcSurface surface)
    {
        surface = default;
        double nz = Math.Sqrt(Math.Max(0, 1 - (data[0] * data[0]) - (data[1] * data[1])));
        if (data[2] == 0)
        {
            nz = -nz;
        }

        // The plane passes through the first vertex of the loop (draft 7.9.20.5.3).
        List<PrcVector> points = LoopPoints(brep, f, edges);
        if (points.Count == 0)
        {
            return false;
        }

        var normal = new PrcVector(data[0], data[1], nz);
        surface = new PrcSurface(PrcSurfaceKind.Plane, FrameAt(points[0], normal, new PrcVector(1, 0, 0)), 0, 0);
        return true;
    }

    // ---- iso faces: the surface follows from the first two curves

    private static bool TryIsoCylinder(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, out PrcSurface surface)
    {
        surface = default;
        if (!Sides(brep, f, edges, out CurveFacts a, out CurveFacts b) || !LineAndArc(a, b, out CurveFacts line, out PrcBrepCurves.Arc arc))
        {
            return false;
        }

        double radius = (arc.StartRadius + arc.EndRadius) / 2;
        // The circle swept along the line: the cylinder is oblique when the line is not square to the circle's plane.
        PrcVector direction = line.End - line.Start;
        double length = Math.Sqrt(direction.LengthSquared());
        if (!(length > 0))
        {
            return false;
        }

        direction /= length;
        PrcVector normal = ArcNormal(arc);
        if (!(Math.Abs(direction.Dot(normal)) > 1e-6))
        {
            return false;
        }

        PrcVector zAxis = direction.Dot(normal) > 0 ? direction : -direction;
        surface = new PrcSurface(PrcSurfaceKind.Cylinder, new Matrix4x4d(arc.Centre, arc.U, arc.V, zAxis), radius, 0);
        return true;
    }

    private static bool LineAndArc(CurveFacts a, CurveFacts b, out CurveFacts line, out PrcBrepCurves.Arc arc)
    {
        line = default;
        arc = default;
        if (a.IsLine && !b.IsLine)
        {
            line = a;
            arc = b.Arc;
            return true;
        }

        if (b.IsLine && !a.IsLine)
        {
            line = b;
            arc = a.Arc;
            return true;
        }

        return false;
    }

    private static bool TryIsoCone(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, out PrcSurface surface)
    {
        surface = default;
        if (!Sides(brep, f, edges, out CurveFacts a, out CurveFacts b) || !LineAndArc(a, b, out CurveFacts line, out PrcBrepCurves.Arc arc))
        {
            return false;
        }

        // The apex is where the generator meets the axis (the line through the circle's centre, perpendicular to its plane).
        PrcVector n = ArcNormal(arc);
        PrcVector d = line.End - line.Start, w = line.Start - arc.Centre;
        double dd = d.Dot(d), dn = d.Dot(n), ddw = d.Dot(w), nw = n.Dot(w);
        double denominator = (dd * 1) - (dn * dn);
        if (!(Math.Abs(denominator) > 1e-12 * dd))
        {
            return false;
        }

        double s = ((dn * nw) - (1 * ddw)) / denominator;
        double t = ((dd * nw) - (dn * ddw)) / denominator;
        PrcVector onLine = line.Start + (d * s), onAxis = arc.Centre + (n * t);
        PrcVector apex = (onLine + onAxis) / 2;
        double reach = Math.Sqrt(dd) + arc.StartRadius;
        if (Math.Sqrt((onLine - onAxis).LengthSquared()) > 1e-3 * reach)
        {
            return false;
        }

        double height = (arc.Centre - apex).Dot(n);
        if (!(Math.Abs(height) > 1e-12 * reach))
        {
            return false;
        }

        PrcVector z = height > 0 ? n : -n;
        double tan = ((arc.StartRadius + arc.EndRadius) / 2) / Math.Abs(height);
        surface = new PrcSurface(PrcSurfaceKind.Cone, FrameAt(apex, z, arc.U), tan, 0);
        return true;
    }

    private static bool TryIsoSphere(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, out PrcSurface surface)
    {
        surface = default;
        if (!Sides(brep, f, edges, out CurveFacts a, out CurveFacts b) || a.IsLine || b.IsLine)
        {
            return false;
        }

        // Two circles of the sphere: a meridian (its plane holds the polar axis) and a parallel (perpendicular to it). The line
        // through a circle's centre and perpendicular to its plane meets the other's: that is the centre of the sphere.
        PrcVector n1 = ArcNormal(a.Arc), n2 = ArcNormal(b.Arc);
        PrcVector w = a.Arc.Centre - b.Arc.Centre;
        double b12 = n1.Dot(n2), d1 = n1.Dot(w), d2 = n2.Dot(w);
        double denominator = 1 - (b12 * b12);
        PrcVector centre;
        if (Math.Abs(denominator) > 1e-9)
        {
            double s = ((b12 * d2) - d1) / denominator;
            double t = (d2 - (b12 * d1)) / denominator;
            centre = ((a.Arc.Centre + (n1 * s)) + (b.Arc.Centre + (n2 * t))) / 2;
        }
        else if (Math.Sqrt(w.LengthSquared()) < 1e-9 * Math.Max(a.Arc.StartRadius, 1e-12))
        {
            centre = a.Arc.Centre;
        }
        else
        {
            return false;
        }

        double radius = Math.Sqrt((a.Start - centre).LengthSquared());
        if (!(radius > 0))
        {
            return false;
        }

        // The polar axis is the normal of the parallel, the smaller circle.
        PrcVector axis = a.Arc.StartRadius <= b.Arc.StartRadius ? n1 : n2;
        PrcVector xHint = a.Arc.StartRadius <= b.Arc.StartRadius ? a.Arc.U : b.Arc.U;
        surface = new PrcSurface(PrcSurfaceKind.Sphere, FrameAt(centre, axis, xHint), radius, 0);
        return true;
    }

    private static bool TryIsoTorus(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, ReadOnlySpan<double> data, out PrcSurface surface)
    {
        surface = default;
        if (!Sides(brep, f, edges, out CurveFacts a, out CurveFacts b) || a.IsLine || b.IsLine)
        {
            return false;
        }

        // The first circle is the one about the torus's axis (a parallel) when the face says so, the other is a meridian. A parallel is not
        // the core circle of the tube: it is wherever the face meets the surface next to it. The axis is its centre and its normal; the
        // tube's radius is the meridian's; the core circle's radius is how far the meridian's centre is from the axis.
        CurveFacts parallel = data[0] != 0 ? a : b, meridian = data[0] != 0 ? b : a;
        PrcVector axis = ArcNormal(parallel.Arc);
        PrcVector toCore = meridian.Arc.Centre - parallel.Arc.Centre;
        double along = toCore.Dot(axis);
        PrcVector radial = toCore - (axis * along);
        double rMajor = Math.Sqrt(radial.LengthSquared()), rMinor = (meridian.Arc.StartRadius + meridian.Arc.EndRadius) / 2;
        PrcVector xHint = rMajor > 1e-12 ? radial / rMajor : parallel.Arc.U;
        surface = new PrcSurface(PrcSurfaceKind.Torus, FrameAt(parallel.Arc.Centre + (axis * along), axis, xHint), rMajor, rMinor);
        return true;
    }

    // ---- ana faces: the surface is stored, the radius comes from the loop's vertices

    private static bool TryAnaCylinder(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, ReadOnlySpan<double> data, out PrcSurface surface)
    {
        surface = default;
        PrcVector point = Point(data, 0), direction = Point(data, 3);
        double length = Math.Sqrt(direction.LengthSquared());
        List<PrcVector> points = LoopPoints(brep, f, edges);
        if (!(length > 0) || points.Count == 0)
        {
            return false;
        }

        PrcVector axis = direction / length;
        double sum = 0;
        foreach (PrcVector p in points)
        {
            PrcVector d = p - point;
            d -= axis * d.Dot(axis);
            sum += Math.Sqrt(d.LengthSquared());
        }

        double radius = sum / points.Count;
        if (!(radius > 0))
        {
            return false;
        }

        surface = new PrcSurface(PrcSurfaceKind.Cylinder, FrameAt(point, axis, points[0] - point), radius, 0);
        return true;
    }

    private static bool TryAnaSphere(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, ReadOnlySpan<double> data, out PrcSurface surface)
    {
        surface = default;
        PrcVector centre = Point(data, 0);
        List<PrcVector> points = LoopPoints(brep, f, edges);
        if (points.Count == 0)
        {
            return false;
        }

        double radius = Math.Sqrt((points[0] - centre).LengthSquared());
        if (!(radius > 0))
        {
            return false;
        }

        surface = new PrcSurface(PrcSurfaceKind.Sphere, FrameAt(centre, new PrcVector(0, 0, 1), new PrcVector(1, 0, 0)), radius, 0);
        return true;
    }

    private static bool TryAnaTorus(ReadOnlySpan<double> data, out PrcSurface surface)
    {
        surface = default;
        PrcVector centre = Point(data, 0), xAxis = Point(data, 3), yAxis = Point(data, 6);
        double major = Math.Sqrt(xAxis.LengthSquared()), minor = Math.Sqrt(yAxis.LengthSquared());
        PrcVector z = xAxis.Cross(yAxis);
        if (!(major > 0) || !(minor > 0) || !(z.LengthSquared() > 1e-24))
        {
            return false;
        }

        surface = new PrcSurface(PrcSurfaceKind.Torus, FrameAt(centre, z, xAxis), major, minor);
        return true;
    }

    private static bool TryAnaCone(PrcTopoBrepDataCompress brep, PrcBrepFace f, PrcEdgeSampler edges, ReadOnlySpan<double> data, out PrcSurface surface)
    {
        surface = default;
        PrcVector axisPoint = Point(data, 0), apex = Point(data, 3);
        PrcVector direction = axisPoint - apex;
        double length = Math.Sqrt(direction.LengthSquared());
        List<PrcVector> points = LoopPoints(brep, f, edges);
        if (!(length > 0) || points.Count == 0)
        {
            return false;
        }

        // The half angle is that of the loop vertex that is furthest from the axis.
        PrcVector axis = direction / length;
        double best = -1, height = 0;
        PrcVector furthest = default;
        foreach (PrcVector p in points)
        {
            PrcVector d = p - apex;
            double along = d.Dot(axis);
            double away = Math.Sqrt(Math.Max(0, d.LengthSquared() - (along * along)));
            if (away > best)
            {
                best = away;
                height = along;
                furthest = p;
            }
        }

        if (!(best > 0) || !(Math.Abs(height) > 1e-12 * Math.Max(best, 1e-12)))
        {
            return false;
        }

        if (height < 0)
        {
            axis = -axis;
        }

        surface = new PrcSurface(PrcSurfaceKind.Cone, FrameAt(apex, axis, furthest - apex), best / Math.Abs(height), 0);
        return true;
    }

    private static bool TryGeneric(PrcTopoBrepDataCompress brep, PrcBrepFace f, out PrcSurface surface)
    {
        surface = default;
        if (f.Surface < 0 || f.Surface >= brep.GenericSurfaces.Length)
        {
            return false;
        }

        PrcGenericSurface g = brep.GenericSurfaces[f.Surface];
        double scale = Math.Sqrt(g.Frame.X.LengthSquared());
        Matrix4x4d frame = FrameAt(g.Frame.Origin, g.Frame.Z, g.Frame.X);
        switch (g.Kind)
        {
            case PrcGenericSurfaceKind.Plane:
                surface = new PrcSurface(PrcSurfaceKind.Plane, frame, 0, 0);
                return true;
            case PrcGenericSurfaceKind.Cylinder:
                surface = new PrcSurface(PrcSurfaceKind.Cylinder, frame, g.Radius * scale, 0);
                return g.Radius > 0;
            case PrcGenericSurfaceKind.Sphere:
                surface = new PrcSurface(PrcSurfaceKind.Sphere, frame, g.Radius * scale, 0);
                return g.Radius > 0;
            case PrcGenericSurfaceKind.Torus:
                surface = new PrcSurface(PrcSurfaceKind.Torus, frame, g.Radius * scale, g.Radius2 * scale);
                return g.Radius > 0 && g.Radius2 > 0;
            case PrcGenericSurfaceKind.Cone:
            {
                // The radius at the frame's origin is the stored one and grows with z at the tangent of the semi-angle: the apex is where it is nothing.
                double tan = Math.Tan(g.Radius2);
                if (!(Math.Abs(tan) > 1e-12))
                {
                    surface = new PrcSurface(PrcSurfaceKind.Cylinder, frame, g.Radius * scale, 0);
                    return g.Radius > 0;
                }

                PrcVector apex = frame.Origin - (frame.Z * (g.Radius * scale / tan));
                Matrix4x4d coneFrame = tan > 0 ? frame with { Origin = apex } : new Matrix4x4d(apex, frame.X, -frame.Y, -frame.Z);
                surface = new PrcSurface(PrcSurfaceKind.Cone, coneFrame, Math.Abs(tan), 0);
                return true;
            }

            default:
                return false;
        }
    }
}
