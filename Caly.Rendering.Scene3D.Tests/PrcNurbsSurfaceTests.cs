using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Brep;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>B-spline surfaces: their points, derivatives, inverses, and how finely they have to be cut.</summary>
public sealed class PrcNurbsSurfaceTests
{
    private const double Precision = 1e-12;

    /// <summary>A bilinear patch over the unit square, lifted at one corner.</summary>
    private static PrcNurbs Bilinear(double lift) => new()
    {
        DegreeU = 1,
        DegreeV = 1,
        KnotsU = [0, 0, 1, 1],
        KnotsV = [0, 0, 1, 1],
        CountU = 2,
        CountV = 2,
        ControlPoints = [new PrcVector(0, 0, 0), new PrcVector(0, 1, 0), new PrcVector(1, 0, 0), new PrcVector(1, 1, lift)],
    };

    /// <summary>A quarter of the cylinder of radius 1 about the z axis, 2 high: a rational quadratic in u, linear in v.</summary>
    private static PrcNurbs QuarterCylinder()
    {
        double w = Math.Sqrt(0.5);
        return new PrcNurbs
        {
            DegreeU = 2,
            DegreeV = 1,
            KnotsU = [0, 0, 0, 1, 1, 1],
            KnotsV = [0, 0, 1, 1],
            CountU = 3,
            CountV = 2,
            ControlPoints =
            [
                new PrcVector(1, 0, 0), new PrcVector(1, 0, 2),
                new PrcVector(1, 1, 0), new PrcVector(1, 1, 2),
                new PrcVector(0, 1, 0), new PrcVector(0, 1, 2),
            ],
            Weights = [1, 1, w, w, 1, 1],
        };
    }

    [Fact]
    public void AnEdgeALittleBeyondThePatchIsOnItsNaturalExtension()
    {
        // Writers trim a face with edges that run a little past the end of its patch (on the surface the patch's last pieces go on to
        // make). The bilinear patch lifted at one corner, continued to u = 1.2: z = 2 u v.
        PrcNurbs n = Bilinear(2);
        var beyond = new PrcVector(1.2, 0.5, 2 * 1.2 * 0.5);

        PrcVector at = PrcNurbsSurface.Evaluate(n, 1.2, 0.5, out _, out _);
        bool found = PrcNurbsSurface.TryInvert(n, beyond, 0.9, 0.5, 1e-3, out double u, out double v);

        Assert.Equal(beyond.X, at.X, Precision);
        Assert.Equal(beyond.Z, at.Z, Precision);
        Assert.True(found);
        Assert.Equal(1.2, u, 1e-9);
        Assert.Equal(0.5, v, 1e-9);
    }

    [Fact]
    public void TheExtensionGoesNoFurtherThanTheEndSpanIsWide()
    {
        // A plane z = 0 over u in [0, 1] of twenty linear spans (each 0.05 wide): past the end, the last span's polynomial is followed
        // as far as that span is wide, not a quarter of the domain (five spans, where a curved end piece may have turned back).
        var knots = new double[23];
        for (int i = 0; i < 23; i++)
        {
            knots[i] = Math.Clamp((i - 1) / 20.0, 0, 1);
        }

        var points = new List<PrcVector>();
        for (int i = 0; i <= 20; i++)
        {
            points.Add(new PrcVector(i / 20.0, 0, 0));
            points.Add(new PrcVector(i / 20.0, 1, 0));
        }

        var n = new PrcNurbs { DegreeU = 1, DegreeV = 1, KnotsU = knots, KnotsV = [0, 0, 1, 1], CountU = 21, CountV = 2, ControlPoints = [.. points] };

        Assert.True(PrcNurbsSurface.TryInvert(n, new PrcVector(1.025, 0.5, 0), 0.9, 0.5, 1e-3, out double u, out _));
        Assert.Equal(1.025, u, 1e-9);
        Assert.False(PrcNurbsSurface.TryInvert(n, new PrcVector(1.1, 0.5, 0), 0.9, 0.5, 1e-3, out _, out _));
    }

    [Fact]
    public void APointOnTheExtensionOfASurfaceClosedOneWayIsOnTheHintsSideOfTheSeam()
    {
        // A square prism closed in u (its first and last edges the same, at u = 0 and 1), open in v from z = 0 to 1. A point a little
        // above the top on the seam, asked with a hint of u = 0.6: of u = 0 and u = 1, the one nearer the hint, as inside the patch.
        var n = new PrcNurbs
        {
            DegreeU = 1,
            DegreeV = 1,
            KnotsU = [0, 0, 0.25, 0.5, 0.75, 1, 1],
            KnotsV = [0, 0, 1, 1],
            CountU = 5,
            CountV = 2,
            ClosedU = true,
            ControlPoints =
            [
                new PrcVector(1, 0, 0), new PrcVector(1, 0, 1), new PrcVector(0, 1, 0), new PrcVector(0, 1, 1),
                new PrcVector(-1, 0, 0), new PrcVector(-1, 0, 1), new PrcVector(0, -1, 0), new PrcVector(0, -1, 1),
                new PrcVector(1, 0, 0), new PrcVector(1, 0, 1),
            ],
        };

        bool found = PrcNurbsSurface.TryInvert(n, new PrcVector(1, 0, 1.1), 0.6, 0.5, 1e-3, out double u, out double v);

        Assert.True(found);
        Assert.Equal(1.0, u, 1e-9);
        Assert.Equal(1.1, v, 1e-9);
    }

    [Fact]
    public void AKnotRepeatedOnceTooOftenAtTheStartLeavesTheStartOfTheSurfaceOnIt()
    {
        var n = new PrcNurbs
        {
            DegreeU = 1,
            DegreeV = 1,
            KnotsU = [0, 0, 1, 1],
            KnotsV = [0, 0, 0, 1, 1],
            CountU = 2,
            CountV = 3,
            ControlPoints = [new PrcVector(0, -5, 0), new PrcVector(0, 0, 0), new PrcVector(0, 1, 0), new PrcVector(1, -5, 0), new PrcVector(1, 0, 0), new PrcVector(1, 1, 0)],
        };

        Assert.Equal(new PrcVector(0.5, 0, 0), PrcNurbsSurface.Evaluate(n, 0.5, 0, out _, out _));
    }

    [Fact]
    public void AKnotRepeatedOnceTooOftenAtAnEndLeavesTheEndsOfTheSurfaceOnIt()
    {
        // Degree 1 in v with three points and the knots 0 0 1 1 1 (one 1 more than a clamped end needs; writers do this): the domain
        // ends at the third knot, and its last span [1, 1) has no width. The end is on the second row, the third row's basis is nothing.
        var n = new PrcNurbs
        {
            DegreeU = 1,
            DegreeV = 1,
            KnotsU = [0, 0, 1, 1],
            KnotsV = [0, 0, 1, 1, 1],
            CountU = 2,
            CountV = 3,
            ControlPoints = [new PrcVector(0, 0, 0), new PrcVector(0, 1, 0), new PrcVector(0, 5, 0), new PrcVector(1, 0, 0), new PrcVector(1, 1, 0), new PrcVector(1, 5, 0)],
        };

        PrcVector end = PrcNurbsSurface.Evaluate(n, 0.5, 1, out PrcVector du, out PrcVector dv);

        Assert.Equal(new PrcVector(0.5, 1, 0), end);
        Assert.Equal(new PrcVector(1, 0, 0), du);
        Assert.Equal(new PrcVector(0, 1, 0), dv);
    }

    [Fact]
    public void ABilinearPatchIsTheBilinearInterpolationOfItsCorners()
    {
        PrcNurbs n = Bilinear(2);

        PrcVector p = PrcNurbsSurface.Evaluate(n, 0.25, 0.5, out PrcVector du, out PrcVector dv);

        Assert.Equal(0.25, p.X, Precision);
        Assert.Equal(0.5, p.Y, Precision);
        Assert.Equal(2 * 0.25 * 0.5, p.Z, Precision);
        Assert.Equal(new PrcVector(1, 0, 2 * 0.5), du);
        Assert.Equal(new PrcVector(0, 1, 2 * 0.25), dv);
        Assert.Equal(new PrcVector(1, 1, 2), PrcNurbsSurface.Evaluate(n, 1, 1, out _, out _));
        Assert.Equal(new PrcVector(0, 0, 0), PrcNurbsSurface.Evaluate(n, 0, 0, out _, out _));
    }

    [Fact]
    public void ARationalQuadraticQuarterIsExactlyAQuarterCylinder()
    {
        PrcNurbs n = QuarterCylinder();

        for (int i = 0; i <= 20; i++)
        {
            for (int j = 0; j <= 4; j++)
            {
                PrcVector p = PrcNurbsSurface.Evaluate(n, i / 20.0, j / 4.0, out _, out _);
                Assert.Equal(1.0, Math.Sqrt((p.X * p.X) + (p.Y * p.Y)), 1e-12);
                Assert.Equal(2.0 * j / 4.0, p.Z, 1e-12);
            }
        }
    }

    [Fact]
    public void TheDerivativesAreThoseOfTheEvaluation()
    {
        PrcNurbs n = QuarterCylinder();
        const double h = 1e-6;

        foreach ((double u, double v) in new[] { (0.1, 0.2), (0.5, 0.5), (0.9, 0.8) })
        {
            PrcVector p = PrcNurbsSurface.Evaluate(n, u, v, out PrcVector du, out PrcVector dv);
            PrcVector pu = PrcNurbsSurface.Evaluate(n, u + h, v, out _, out _), pv = PrcNurbsSurface.Evaluate(n, u, v + h, out _, out _);
            Assert.True(Math.Sqrt((((pu - p) / h) - du).LengthSquared()) < 1e-4);
            Assert.True(Math.Sqrt((((pv - p) / h) - dv).LengthSquared()) < 1e-4);
        }
    }

    [Fact]
    public void ACubicWithAnInteriorKnotIsContinuousAcrossIt()
    {
        // Degree 3 in u over knots 0,0,0,0,0.5,1,1,1,1 (five control points), degree 1 in v: points on both sides of 0.5 meet.
        var points = new List<PrcVector>();
        for (int i = 0; i < 5; i++)
        {
            points.Add(new PrcVector(i, Math.Sin(i), 0));
            points.Add(new PrcVector(i, Math.Sin(i), 1));
        }

        var n = new PrcNurbs { DegreeU = 3, DegreeV = 1, KnotsU = [0, 0, 0, 0, 0.5, 1, 1, 1, 1], KnotsV = [0, 0, 1, 1], CountU = 5, CountV = 2, ControlPoints = [.. points] };

        PrcVector below = PrcNurbsSurface.Evaluate(n, 0.5 - 1e-9, 0.3, out PrcVector du1, out _);
        PrcVector above = PrcNurbsSurface.Evaluate(n, 0.5 + 1e-9, 0.3, out PrcVector du2, out _);

        Assert.True(Math.Sqrt((below - above).LengthSquared()) < 1e-6);
        Assert.True(Math.Sqrt((du1 - du2).LengthSquared()) < 1e-4);                       // C2 at a simple knot of a cubic
        Assert.Equal(new PrcVector(0, 0, 0), PrcNurbsSurface.Evaluate(n, 0, 0, out _, out _));
        Assert.Equal(new PrcVector(4, Math.Sin(4), 1), PrcNurbsSurface.Evaluate(n, 1, 1, out _, out _));
    }

    [Fact]
    public void APointOnTheSurfaceIsTakenBackToItsParameters()
    {
        PrcNurbs n = QuarterCylinder();

        for (int i = 1; i < 8; i++)
        {
            for (int j = 1; j < 8; j++)
            {
                double u = i / 8.0, v = j / 8.0;
                PrcVector p = PrcNurbsSurface.Evaluate(n, u, v, out _, out _);
                Assert.True(PrcNurbsSurface.TryInvert(n, p, 0.5, 0.5, 0.01, out double u2, out double v2));
                Assert.Equal(u, u2, 1e-6);
                Assert.Equal(v, v2, 1e-6);
            }
        }
    }

    [Fact]
    public void ThePointNearestTheSurfaceIsFoundFromAPointABitOffIt()
    {
        PrcNurbs n = QuarterCylinder();
        PrcVector on = PrcNurbsSurface.Evaluate(n, 0.4, 0.6, out _, out _);

        // 0.003 outside along the radius: within the distance allowed, and it lands on the same parameters.
        PrcVector off = on + (new PrcVector(on.X, on.Y, 0) * 0.003);
        Assert.True(PrcNurbsSurface.TryInvert(n, off, 0.5, 0.5, 0.01, out double u, out double v));

        Assert.Equal(0.4, u, 1e-6);
        Assert.Equal(0.6, v, 1e-6);
    }

    [Fact]
    public void APointFarFromTheSurfaceIsNotTakenToIt()
    {
        PrcNurbs n = QuarterCylinder();

        Assert.False(PrcNurbsSurface.TryInvert(n, new PrcVector(3, 3, 1), 0.5, 0.5, 0.01, out _, out _));
    }

    [Fact]
    public void AGoodHintIsNotNeeded()
    {
        PrcNurbs n = QuarterCylinder();
        PrcVector p = PrcNurbsSurface.Evaluate(n, 0.93, 0.07, out _, out _);

        Assert.True(PrcNurbsSurface.TryInvert(n, p, 0.0, 1.0, 0.01, out double u, out double v));

        Assert.Equal(0.93, u, 1e-6);
        Assert.Equal(0.07, v, 1e-6);
    }

    [Fact]
    public void AClosedDirectionIsCutOnceAndEvaluatedAroundItsSeam()
    {
        // A full cylinder of radius 1 as a rational quadratic ring of 9 control points (four quarter turns), closed in u.
        double w = Math.Sqrt(0.5);
        var ring = new (double X, double Y, double W)[] { (1, 0, 1), (1, 1, w), (0, 1, 1), (-1, 1, w), (-1, 0, 1), (-1, -1, w), (0, -1, 1), (1, -1, w), (1, 0, 1) };
        var points = new List<PrcVector>();
        var weights = new List<double>();
        foreach ((double x, double y, double wt) in ring)
        {
            points.Add(new PrcVector(x, y, 0));
            points.Add(new PrcVector(x, y, 1));
            weights.Add(wt);
            weights.Add(wt);
        }

        var n = new PrcNurbs
        {
            DegreeU = 2, DegreeV = 1, KnotsU = [0, 0, 0, 0.25, 0.25, 0.5, 0.5, 0.75, 0.75, 1, 1, 1], KnotsV = [0, 0, 1, 1], CountU = 9, CountV = 2,
            ControlPoints = [.. points], Weights = [.. weights], ClosedU = true,
        };

        // A hint just past the seam: the answer is just past it too, not a turn away.
        PrcVector p = PrcNurbsSurface.Evaluate(n, 0.02, 0.5, out _, out _);
        Assert.True(PrcNurbsSurface.TryInvert(n, p, 1.01, 0.5, 0.01, out double u, out _));
        Assert.Equal(1.02, u, 1e-6);
    }

    [Fact]
    public void AFlatPatchNeedsOneStepAndACurvedOneMeetsTheChordBound()
    {
        PrcNurbs flat = Bilinear(0);
        int[] fu = new int[1], fv = new int[1];
        PrcNurbsSurface.SpanSteps(flat, 0.001, fu, fv);
        Assert.Equal([1], fu);
        Assert.Equal([1], fv);

        PrcNurbs curved = QuarterCylinder();
        int[] su = new int[1], sv = new int[1];
        PrcNurbsSurface.SpanSteps(curved, 0.001, su, sv);
        Assert.Equal(1, sv[0]);                       // straight along the axis
        Assert.True(su[0] >= 18, $"{su[0]} steps for a quarter turn at 0.001");   // at least: pi/2 over the 15 degree cap is 6, and chord 0.001 on radius 1 asks for 18 at least

        // Every step's middle is within the chord of the surface.
        for (int k = 0; k < su[0]; k++)
        {
            double u0 = (double)k / su[0], u1 = (double)(k + 1) / su[0];
            PrcVector a = PrcNurbsSurface.Evaluate(curved, u0, 0.5, out _, out _), b = PrcNurbsSurface.Evaluate(curved, u1, 0.5, out _, out _);
            PrcVector mid = PrcNurbsSurface.Evaluate(curved, (u0 + u1) / 2, 0.5, out _, out _);
            Assert.True(Math.Sqrt((mid - ((a + b) / 2)).LengthSquared()) <= 0.001 * 1.05);
        }
    }

    [Fact]
    public void TheDomainIsWhereTheKnotsSayItIs()
    {
        PrcNurbs n = QuarterCylinder();

        Assert.Equal(0.0, PrcNurbsSurface.MinU(n));
        Assert.Equal(1.0, PrcNurbsSurface.MaxU(n));
        Assert.Equal(0.0, PrcNurbsSurface.MinV(n));
        Assert.Equal(1.0, PrcNurbsSurface.MaxV(n));
    }
}
