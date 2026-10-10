using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>The surfaces of compressed faces, rebuilt from what the faces store and the curves that bound them.</summary>
public sealed class PrcSurfacesTests
{
    private const double Precision = 1e-9;

    private static PrcSurface Build(PrcBrepCurvesTests.Body body, int face)
    {
        PrcTopoBrepDataCompress brep = body.Build();
        var edges = new PrcEdgeSampler(brep, 0.01);
        Assert.True(PrcSurfaces.TryBuild(brep, face, edges, out PrcSurface surface), "the surface is rebuilt");
        return surface;
    }

    /// <summary>The distance of a point from the surface, by taking it to its parameters and back.</summary>
    private static double Distance(PrcSurface s, PrcVector p, double hintU = 0, double hintV = 0)
    {
        Assert.True(s.TryInvert(p, hintU, hintV, out double u, out double v));
        return Math.Sqrt((s.Evaluate(u, v, out _, out _) - p).LengthSquared());
    }

    private static void AssertOnSurface(PrcSurface s, PrcVector p)
    {
        Assert.True(Distance(s, p) < 1e-7, $"{p} lies {Distance(s, p)} from the surface");
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(-2.0)]
    public void TheNormalOfAConePointsAwayFromItsAxisOnEitherNappe(double v)
    {
        var cone = new PrcSurface(PrcSurfaceKind.Cone, Matrix4x4d.Identity, 0.5, 0);
        PrcVector p = cone.Evaluate(0.3, v, out PrcVector du, out PrcVector dv);
        PrcVector n = cone.Normal(0.3, v);

        PrcVector outward = new(p.X, p.Y, 0);
        Assert.True(n.Dot(outward) > 0, $"{n} at {p}");
        Assert.True(n.Dot(du.Cross(dv)) > 0, "the normal is the product of the derivatives");
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(-2.0)]
    public void APointOnEitherNappeOfAConeIsFoundAgain(double v)
    {
        // A cone is both nappes (an uncompressed cone with a negative radius lies on the far one): below the apex the radius is
        // negative and the point is on the other side of the axis.
        var cone = new PrcSurface(PrcSurfaceKind.Cone, Matrix4x4d.Identity, 0.5, 0);
        PrcVector p = cone.Evaluate(0.3, v, out _, out _);

        Assert.True(cone.TryInvert(p, 0, 0, out double u2, out double v2));

        Assert.True(Math.Sqrt((cone.Evaluate(u2, v2, out _, out _) - p).LengthSquared()) < 1e-12);
    }

    [Fact]
    public void AnIsoPlaneIsThroughItsFirstVertexWithTheNormalItStores()
    {
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(1, 2, 3), c = b.Vertex(4, 2, 3);
        int line = b.Line(a, c);
        int face = b.Face(PrcFaceKind.IsoPlane, [0, 0, 1], [line]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Plane, s.Kind);
        AssertOnSurface(s, new PrcVector(7, -3, 3));
        Assert.True(Distance(s, new PrcVector(7, -3, 4)) > 0.5);
        PrcVector n = s.Normal(0.3, 0.4);
        Assert.Equal(1.0, n.Z, Precision);
    }

    [Fact]
    public void ANormalOfAPlaneThatIsTiltedAndPointsDownIsRebuiltFromItsTwoNumbersAndTheSign()
    {
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(0, 0, 0), c = b.Vertex(1, 0, 0.6);
        int face = b.Face(PrcFaceKind.AnaPlane, [0.6, 0, 0], [b.Line(a, c)]);          // n = (0.6, 0, -0.8)

        PrcSurface s = Build(b, face);

        PrcVector n = s.Normal(0, 0);
        Assert.Equal(0.6, n.X, Precision);
        Assert.Equal(-0.8, n.Z, Precision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIsoCylinderIsRebuiltFromItsLineAndItsArcInEitherOrder(bool arcFirst)
    {
        // Radius 2 about the z axis; the line is a generator from (2,0,0) to (2,0,5), the arc a quarter turn at z = 5.
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(2, 0, 0), c = b.Vertex(2, 0, 5), d = b.Vertex(0, 2, 5);
        int line = b.Line(a, c);
        int arc = b.Arc(c, d, new PrcVector(0, 0, 5), false);
        int face = b.Face(PrcFaceKind.IsoCylinder, [], arcFirst ? [arc, line] : [line, arc]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Cylinder, s.Kind);
        foreach (PrcVector p in new[] { new PrcVector(2, 0, 0), new PrcVector(0, 2, 3), new PrcVector(-2, 0, 1), new PrcVector(Math.Sqrt(2), Math.Sqrt(2), 5) })
        {
            AssertOnSurface(s, p);
        }

        Assert.True(Distance(s, new PrcVector(2.5, 0, 1)) > 0.4);
    }

    [Fact]
    public void AnIsoConeIsRebuiltFromAGeneratorAndAnArc()
    {
        // Apex at the origin, axis z, half-angle 45 degrees: the circle at z = 2 has radius 2.
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(1, 0, 1), c = b.Vertex(2, 0, 2), d = b.Vertex(0, 2, 2);
        int line = b.Line(a, c);
        int arc = b.Arc(c, d, new PrcVector(0, 0, 2), false);
        int face = b.Face(PrcFaceKind.IsoCone, [], [line, arc]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Cone, s.Kind);
        foreach (PrcVector p in new[] { new PrcVector(0, 1, 1), new PrcVector(3, 0, 3), new PrcVector(-1.5, 0, 1.5), new PrcVector(2, 0, 2) })
        {
            AssertOnSurface(s, p);
        }

        Assert.True(Distance(s, new PrcVector(2, 0, 1)) > 0.3);
    }

    [Fact]
    public void AnIsoSphereIsRebuiltFromAMeridianAndAParallel()
    {
        // Centre (1,2,3), radius 5, polar axis z: a quarter of the meridian in the plane y = 2 and a quarter of the parallel at latitude 30 degrees.
        var b = new PrcBrepCurvesTests.Body();
        double lat = Math.PI / 6;
        int m0 = b.Vertex(6, 2, 3), m1 = b.Vertex(1, 2, 8);
        int meridian = b.Arc(m0, m1, new PrcVector(1, 2, 3), false);
        double ring = 5 * Math.Cos(lat), height = 3 + (5 * Math.Sin(lat));
        int p0 = b.Vertex(1 + ring, 2, height), p1 = b.Vertex(1, 2 + ring, height);
        int parallel = b.Arc(p0, p1, new PrcVector(1, 2, height), false);
        int face = b.Face(PrcFaceKind.IsoSphere, [], [meridian, parallel]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Sphere, s.Kind);
        foreach (PrcVector p in new[] { new PrcVector(6, 2, 3), new PrcVector(1, 7, 3), new PrcVector(1, 2, 8), new PrcVector(1 - (5 * Math.Cos(0.3)), 2 + (5 * Math.Sin(0.3)), 3) })
        {
            AssertOnSurface(s, p);
        }

        Assert.True(Distance(s, new PrcVector(1, 2, 3)) > 4.9);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnIsoTorusIsRebuiltFromItsTwoCirclesAndTheFlagThatSaysWhichIsTheMajorOne(bool majorFirst)
    {
        // Centre at the origin, axis z, major radius 5, minor radius 1.
        var b = new PrcBrepCurvesTests.Body();
        int s0 = b.Vertex(6, 0, 0);
        int major = b.FullCircle(b.Vertex(5, 0, 0), new PrcVector(0, 0, 0), new PrcVector(0, 0, 1));
        int minor = b.Arc(s0, b.Vertex(5, 0, 1), new PrcVector(5, 0, 0), false);
        int face = b.Face(PrcFaceKind.IsoTorus, [majorFirst ? 1 : 0], majorFirst ? [major, minor] : [minor, major]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Torus, s.Kind);
        foreach (PrcVector p in new[] { new PrcVector(6, 0, 0), new PrcVector(4, 0, 0), new PrcVector(0, 5, 1), new PrcVector(-5, 0, -1), new PrcVector(5 * Math.Cos(1), 5 * Math.Sin(1), 1) })
        {
            AssertOnSurface(s, p);
        }

        Assert.True(Distance(s, new PrcVector(0, 0, 0)) > 3.9);
    }

    [Fact]
    public void AnAnaCylinderTakesItsRadiusFromTheAverageDistanceOfItsLoopVertices()
    {
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(3, 0, 0), c = b.Vertex(0, 3.0004, 1), d = b.Vertex(-2.9998, 0, 2);
        int face = b.Face(PrcFaceKind.AnaCylinder, [0, 0, 0, 0, 0, 2], [b.Line(a, c), b.Line(c, d)]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Cylinder, s.Kind);
        Assert.Equal(3.0, Math.Sqrt(((s.Evaluate(0.7, 4, out _, out _)) - new PrcVector(0, 0, 4)).LengthSquared()), 1e-3);
        Assert.True(Distance(s, new PrcVector(0, -3.0001, 9)) < 1e-3);                       // the radius is the average of three vertices that differ by the stored precision
    }

    [Fact]
    public void AnAnaSphereTakesItsRadiusFromItsFirstLoopVertex()
    {
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(1, 2, 8), c = b.Vertex(6, 2, 3);
        int face = b.Face(PrcFaceKind.AnaSphere, [1, 2, 3], [b.Line(a, c)]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Sphere, s.Kind);
        AssertOnSurface(s, new PrcVector(1, 7, 3));
        Assert.True(Distance(s, new PrcVector(1, 2, 3)) > 4.9);
    }

    [Fact]
    public void AnAnaTorusIsPlacedByItsCentreAndTheTwoAxesWhoseLengthsAreItsRadii()
    {
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(6, 0, 0), c = b.Vertex(0, 6, 0);
        int face = b.Face(PrcFaceKind.AnaTorus, [0, 0, 0, 5, 0, 0, 0, 1, 0], [b.Line(a, c)]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Torus, s.Kind);
        foreach (PrcVector p in new[] { new PrcVector(6, 0, 0), new PrcVector(0, 5, 1), new PrcVector(-4, 0, 0), new PrcVector(0, -5, -1) })
        {
            AssertOnSurface(s, p);
        }
    }

    [Fact]
    public void AnAnaConeTakesItsAngleFromTheLoopVertexFurthestFromItsAxis()
    {
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(2, 0, 2), c = b.Vertex(0, 2, 2), d = b.Vertex(0.5, 0, 0.5);
        int face = b.Face(PrcFaceKind.AnaCone, [0, 0, 1, 0, 0, 0], [b.Line(a, c), b.Line(c, d)]);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Cone, s.Kind);
        foreach (PrcVector p in new[] { new PrcVector(1, 0, 1), new PrcVector(0, 3, 3), new PrcVector(-1.5, 0, 1.5) })
        {
            AssertOnSurface(s, p);
        }
    }

    [Fact]
    public void AnAnaGenericFaceOnACylinderTakesItsFrameAndRadius()
    {
        var b = new PrcBrepCurvesTests.Body();
        b.Generic.Add(new PrcGenericSurface(PrcGenericSurfaceKind.Cylinder, 0, new Matrix4x4d(new PrcVector(1, 1, 0), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0), new PrcVector(0, 0, 1)), 2.5, 0));
        int face = b.Face(PrcFaceKind.AnaGenericFace, [], [], surface: 0);

        PrcSurface s = Build(b, face);

        Assert.Equal(PrcSurfaceKind.Cylinder, s.Kind);
        AssertOnSurface(s, new PrcVector(3.5, 1, 7));
        AssertOnSurface(s, new PrcVector(1, -1.5, -2));
    }

    [Fact]
    public void AnAnaGenericFaceOnASurfaceOfAnotherKindIsNotRebuilt()
    {
        var b = new PrcBrepCurvesTests.Body();
        b.Generic.Add(new PrcGenericSurface(PrcGenericSurfaceKind.Unsupported, 0, Matrix4x4d.Identity, 0, 0));
        int face = b.Face(PrcFaceKind.AnaGenericFace, [], [], surface: 0);
        PrcTopoBrepDataCompress brep = b.Build();

        Assert.False(PrcSurfaces.TryBuild(brep, face, new PrcEdgeSampler(brep, 0.01), out _));
    }

    [Fact]
    public void AFaceWhoseCurvesDoNotMakeTheSurfaceItNamesIsNotRebuilt()
    {
        // An iso cylinder whose two curves are lines: there is no circle to take the axis from.
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(0, 0, 0), c = b.Vertex(1, 0, 0), d = b.Vertex(1, 1, 0);
        int face = b.Face(PrcFaceKind.IsoCylinder, [], [b.Line(a, c), b.Line(c, d)]);
        PrcTopoBrepDataCompress brep = b.Build();

        Assert.False(PrcSurfaces.TryBuild(brep, face, new PrcEdgeSampler(brep, 0.01), out _));
    }

    [Theory]
    [InlineData((int)PrcSurfaceKind.Cylinder)]
    [InlineData((int)PrcSurfaceKind.Cone)]
    [InlineData((int)PrcSurfaceKind.Sphere)]
    [InlineData((int)PrcSurfaceKind.Torus)]
    public void EverySurfaceTakesAPointBackToTheParametersItWasMadeFrom(int kindNumber)
    {
        var kind = (PrcSurfaceKind)kindNumber;
        PrcSurface s = kind switch
        {
            PrcSurfaceKind.Cylinder => new PrcSurface(PrcSurfaceKind.Cylinder, Matrix4x4d.Identity, 2, 0),
            PrcSurfaceKind.Cone => new PrcSurface(PrcSurfaceKind.Cone, Matrix4x4d.Identity, 0.5, 0),
            PrcSurfaceKind.Sphere => new PrcSurface(PrcSurfaceKind.Sphere, Matrix4x4d.Identity, 3, 0),
            _ => new PrcSurface(PrcSurfaceKind.Torus, Matrix4x4d.Identity, 5, 1),
        };

        for (int i = 0; i < 5; i++)
        {
            for (int j = 0; j < 5; j++)
            {
                double u = -2.5 + (i * 1.1), v = kind == PrcSurfaceKind.Sphere ? -1.2 + (j * 0.6) : (kind == PrcSurfaceKind.Torus ? -2.4 + (j * 1.2) : 0.5 + j);
                PrcVector p = s.Evaluate(u, v, out PrcVector du, out PrcVector dv);
                Assert.True(s.TryInvert(p, u + 0.2, v - 0.1, out double u2, out double v2));
                Assert.Equal(u, u2, 1e-7);
                Assert.Equal(v, v2, 1e-7);
                PrcVector n = s.Normal(u, v);
                Assert.Equal(1.0, Math.Sqrt(n.LengthSquared()), 1e-9);
                Assert.True(Math.Abs(n.Dot(du)) < 1e-7 * (1 + Math.Sqrt(du.LengthSquared())) && Math.Abs(n.Dot(dv)) < 1e-7 * (1 + Math.Sqrt(dv.LengthSquared())), "the normal is perpendicular to both derivatives");
            }
        }
    }

    [Fact]
    public void TheDerivativesAreThoseOfTheEvaluation()
    {
        var s = new PrcSurface(PrcSurfaceKind.Torus, Matrix4x4d.Identity, 5, 1);
        const double h = 1e-6;
        PrcVector p = s.Evaluate(0.7, 1.9, out PrcVector du, out PrcVector dv);
        PrcVector pu = s.Evaluate(0.7 + h, 1.9, out _, out _), pv = s.Evaluate(0.7, 1.9 + h, out _, out _);

        Assert.True(Math.Sqrt((((pu - p) / h) - du).LengthSquared()) < 1e-4);
        Assert.True(Math.Sqrt((((pv - p) / h) - dv).LengthSquared()) < 1e-4);
    }

    [Fact]
    public void TheStepsKeepChordsWithinTheToleranceAndTheAngleCap()
    {
        var cylinder = new PrcSurface(PrcSurfaceKind.Cylinder, Matrix4x4d.Identity, 10, 0);
        (double du, double dv) = cylinder.Steps(0.01, 0, 5);

        Assert.Equal(2 * Math.Acos(1 - 0.001), du, 1e-12);
        Assert.True(double.IsPositiveInfinity(dv));

        var sphere = new PrcSurface(PrcSurfaceKind.Sphere, Matrix4x4d.Identity, 1, 0);
        (double su, double sv) = sphere.Steps(100, -1, 1);                            // a tolerance that would allow one step: the cap of 15 degrees rules
        Assert.Equal(PrcBrepTolerance.MaxTurnRadians, su, 1e-12);
        Assert.Equal(PrcBrepTolerance.MaxTurnRadians, sv, 1e-12);

        var plane = new PrcSurface(PrcSurfaceKind.Plane, Matrix4x4d.Identity, 0, 0);
        (double pu, double pv) = plane.Steps(0.01, 0, 1);
        Assert.True(double.IsPositiveInfinity(pu) && double.IsPositiveInfinity(pv));
    }

    [Fact]
    public void ACurveDeducedByAnotherFaceIsDescribedFromItsSamples()
    {
        // A circle of radius 3 about (1,1,1) in the plane z = 1, registered as samples of a deduced curve, is recognised as an arc again.
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(4, 1, 1), c = b.Vertex(1, 4, 1);
        b.Curves.Add(new PrcBrepCurve(PrcCurveKind.Implicit, -1, -1, 0, 2, 0));
        int implicitCurve = b.Curves.Count - 1;
        PrcTopoBrepDataCompress brep = b.Build();
        var edges = new PrcEdgeSampler(brep, 0.01);
        var samples = Enumerable.Range(0, 9).Select(i => new PrcVector(1 + (3 * Math.Cos(i * Math.PI / 16)), 1 + (3 * Math.Sin(i * Math.PI / 16)), 1)).ToArray();
        edges.Register(implicitCurve, samples);

        Assert.True(PrcBrepCurves.TryDescribe(brep, edges, implicitCurve, out bool isLine, out _, out _, out PrcBrepCurves.Arc arc));

        Assert.False(isLine);
        Assert.Equal(3.0, arc.StartRadius, 1e-9);
        Assert.Equal(1.0, arc.Centre.X, 1e-9);
        Assert.Equal(Math.PI / 2, arc.Sweep, 1e-9);
        _ = a;
        _ = c;
    }
}
