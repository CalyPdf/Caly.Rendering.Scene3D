using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>A curve of an uncompressed body's edge, followed by its parameter between knots (the knots say where it may bend).</summary>
internal interface IPrcParametricCurve
{
    double[] Knots { get; }

    double Min { get; }

    double Max { get; }

    PrcVector Evaluate(double t);
}

/// <summary>
/// The edge of an uncompressed body that has no curve in space, only a curve in the parameters of a face's surface (the 2D
/// B-spline of a coedge, draft 7.9.9): the surface at the points of that curve.
/// </summary>
internal sealed class PrcCurveOnSurface(PrcNurbsCurve uv, PrcNativeSurface surface) : IPrcParametricCurve
{
    public double[] Knots => uv.Knots;

    public double Min => uv.Min;

    public double Max => uv.Max;

    public PrcVector Evaluate(double t)
    {
        PrcVector p = uv.Evaluate(t);
        return surface.At(p.X, p.Y);
    }
}

/// <summary>
/// A surface of an uncompressed body with the parameterisation the format gives it (draft 7.11): the stored parameters turned
/// into the surface's own by its UVParameterization (<c>coeff_a · p + coeff_b</c>, u and v swapped when it says so), then the
/// canonical surface placed by its transformation. Only for the 2D curves of coedges: faces are cut on <see cref="PrcSurface"/>.
/// </summary>
internal sealed class PrcNativeSurface
{
    private readonly PrcSurfaceKind _kind;
    private readonly Matrix4x4d _frame;
    private readonly double _r1, _r2;
    private readonly double _ua, _ub, _va, _vb;
    private readonly bool _swap;
    private readonly PrcNurbs? _nurbs;

    private PrcNativeSurface(PrcSurfaceKind kind, Matrix4x4d frame, double r1, double r2, PrcUVParameterization? p, PrcNurbs? nurbs)
    {
        _kind = kind;
        _frame = frame;
        _r1 = r1;
        _r2 = r2;
        _nurbs = nurbs;
        (_ua, _ub, _va, _vb, _swap) = p is null ? (1, 0, 1, 0, false) : (p.UParamCoeffA, p.UParamCoeffB, p.VParamCoeffA, p.VParamCoeffB, p.SwapUv);
        Domain = p?.SufaceDomain;
    }

    /// <summary>The part of the surface its parameterisation keeps (a face that has neither loops nor a domain of its own is that part).</summary>
    public PrcDomain? Domain { get; }

    private PrcNativeSurface(Matrix4x4d frame, double ua, double ub, double va, double vb)
    {
        _kind = PrcSurfaceKind.Plane;
        _frame = frame;
        (_ua, _ub, _va, _vb, _swap) = (ua, ub, va, vb, false);
    }

    /// <summary>The surface a face names, for the kinds whose 2D curves are followed; null for any other.</summary>
    public static PrcNativeSurface? From(PrcSurf? surface, PrcNurbs? nurbs)
    {
        if (nurbs is not null)
        {
            return new PrcNativeSurface(PrcSurfaceKind.Nurbs, Matrix4x4d.Identity, 0, 0, null, nurbs);
        }

        return surface?.Id switch
        {
            PrcSurfPlane p => new PrcNativeSurface(PrcBrepMath.Frame(p.Transform), p.UParameterCoeffA, p.UParameterCoeffB, p.VParameterCoeffA, p.VParameterCoeffB),
            PrcSurfCylinder c => new PrcNativeSurface(PrcSurfaceKind.Cylinder, PrcBrepMath.Frame(c.Transform.Transformation), c.Radius, 0, c.Parameterization, null),
            PrcSurfCone c => new PrcNativeSurface(PrcSurfaceKind.Cone, PrcBrepMath.Frame(c.Transform.Transformation), c.Radius, Math.Tan(c.SemiAngle), c.Parameterization, null),
            PrcSurfSphere s => new PrcNativeSurface(PrcSurfaceKind.Sphere, PrcBrepMath.Frame(s.Transform.Transformation), s.Radius, 0, s.Parameterization, null),
            PrcSurfTorus t => new PrcNativeSurface(PrcSurfaceKind.Torus, PrcBrepMath.Frame(t.Transform.Transformation), t.MajorRadius, t.MinorRadius, t.Parameterization, null),
            _ => null,
        };
    }

    /// <summary>The point at stored parameters (u, v).</summary>
    public PrcVector At(double u, double v)
    {
        if (_kind == PrcSurfaceKind.Nurbs)
        {
            return PrcNurbsSurface.Evaluate(_nurbs!, u, v, out _, out _);
        }

        double iu = _swap ? (_va * v) + _vb : (_ua * u) + _ub, iv = _swap ? (_ua * u) + _ub : (_va * v) + _vb;
        PrcVector local = _kind switch
        {
            PrcSurfaceKind.Plane => new PrcVector(iu, iv, 0),
            PrcSurfaceKind.Cylinder => new PrcVector(_r1 * Math.Cos(iu), _r1 * Math.Sin(iu), iv),
            PrcSurfaceKind.Cone => Ring(iu, _r1 + (iv * _r2), iv),
            PrcSurfaceKind.Sphere => new PrcVector(_r1 * Math.Cos(iv) * Math.Cos(iu), _r1 * Math.Cos(iv) * Math.Sin(iu), _r1 * Math.Sin(iv)),
            _ => Ring(iu, _r1 + (_r2 * Math.Cos(iv)), _r2 * Math.Sin(iv)),
        };
        return _frame.Transform(local);
    }

    private static PrcVector Ring(double angle, double radius, double z) => new(radius * Math.Cos(angle), radius * Math.Sin(angle), z);
}
