using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>Curves of a compressed body: the points they pass through, to a chord tolerance, the same for everyone who asks.</summary>
public sealed class PrcBrepCurvesTests
{
    private const double Precision = 1e-9;

    /// <summary>A body model made of the given pieces, for tests that need no bits.</summary>
    internal sealed class Body
    {
        public List<PrcVector> Vertices { get; } = [];

        public List<PrcBrepCurve> Curves { get; } = [];

        public List<double> Data { get; } = [];

        public List<int> Members { get; } = [];

        public List<PrcBrepFace> Faces { get; } = [];

        public List<PrcBrepLoop> Loops { get; } = [];

        public List<int> Coedges { get; } = [];

        public List<double> SurfaceData { get; } = [];

        public List<PrcGenericSurface> Generic { get; } = [];

        public List<PrcNurbsCurve> NurbsCurves { get; } = [];

        public double Tolerance { get; set; } = 0.01;

        public int Vertex(double x, double y, double z)
        {
            Vertices.Add(new PrcVector(x, y, z));
            return Vertices.Count - 1;
        }

        public int Line(int start, int end)
        {
            Curves.Add(new PrcBrepCurve(PrcCurveKind.Line, start, end, Data.Count, 0, 0));
            return Curves.Count - 1;
        }

        public int FullCircle(int start, PrcVector centre, PrcVector normal)
        {
            int at = Data.Count;
            Data.AddRange([centre.X, centre.Y, centre.Z, normal.X, normal.Y, normal.Z]);
            Curves.Add(new PrcBrepCurve(PrcCurveKind.Circle, start, start, at, 6, (byte)(PrcBrepCurveFlags.Particular | PrcBrepCurveFlags.Full)));
            return Curves.Count - 1;
        }

        public int HalfCircle(int start, int end, PrcVector middle)
        {
            int at = Data.Count;
            Data.AddRange([middle.X, middle.Y, middle.Z]);
            Curves.Add(new PrcBrepCurve(PrcCurveKind.Circle, start, end, at, 3, (byte)PrcBrepCurveFlags.Particular));
            return Curves.Count - 1;
        }

        public int Arc(int start, int end, PrcVector centre, bool overHalfTurn)
        {
            int at = Data.Count;
            Data.AddRange([centre.X, centre.Y, centre.Z]);
            Curves.Add(new PrcBrepCurve(PrcCurveKind.Circle, start, end, at, 3, (byte)(overHalfTurn ? PrcBrepCurveFlags.OverHalfTurn : PrcBrepCurveFlags.None)));
            return Curves.Count - 1;
        }

        /// <summary>An arc of an ellipse: centre, the two half-axes, from one angle to another (uncompressed bodies).</summary>
        public int Conic(int start, int end, PrcVector centre, PrcVector a, PrcVector b, double from, double to)
        {
            int at = Data.Count;
            Data.AddRange([centre.X, centre.Y, centre.Z, a.X, a.Y, a.Z, b.X, b.Y, b.Z, from, to]);
            Curves.Add(new PrcBrepCurve(PrcCurveKind.Conic, start, end, at, 11, 0));
            return Curves.Count - 1;
        }

        /// <summary>A NURBS curve over a part of its parameters (uncompressed bodies).</summary>
        public int NurbsCurve(int start, int end, PrcNurbsCurve curve, double from, double to)
        {
            int at = Data.Count;
            NurbsCurves.Add(curve);
            Data.AddRange([NurbsCurves.Count - 1, from, to]);
            Curves.Add(new PrcBrepCurve(PrcCurveKind.NurbsCurve, start, end, at, 3, 0));
            return Curves.Count - 1;
        }

        /// <summary>A polyline through its points (uncompressed bodies).</summary>
        public int Polyline(int start, int end, params PrcVector[] points)
        {
            int at = Data.Count;
            foreach (PrcVector p in points)
            {
                Data.AddRange([p.X, p.Y, p.Z]);
            }

            Curves.Add(new PrcBrepCurve(PrcCurveKind.Polyline, start, end, at, 3 * points.Length, 0));
            return Curves.Count - 1;
        }

        public int Hermite(int start, int end, PrcVector[] inner, PrcVector[] tangents)
        {
            int at = Data.Count;
            Data.Add(tangents.Length);
            foreach (PrcVector p in inner)
            {
                Data.AddRange([p.X, p.Y, p.Z]);
            }

            foreach (PrcVector t in tangents)
            {
                Data.AddRange([t.X, t.Y, t.Z]);
            }

            Curves.Add(new PrcBrepCurve(PrcCurveKind.HermiteSpline, start, end, at, Data.Count - at, 0));
            return Curves.Count - 1;
        }

        public int Composite(int start, int end, params int[] members)
        {
            int at = Members.Count;
            Members.AddRange(members);
            Curves.Add(new PrcBrepCurve(PrcCurveKind.Composite, start, end, at, members.Length, 0));
            return Curves.Count - 1;
        }

        /// <summary>A face of the given kind with one loop of the given curves (none for an untrimmed face), the numbers its kind stores, and a surface index.</summary>
        public int Face(PrcFaceKind kind, double[] data, int[] coedges, int surface = -1, int[]? secondLoop = null, int[]? thirdLoop = null)
        {
            int firstLoop = Loops.Count, dataAt = SurfaceData.Count;
            if (coedges.Length > 0)
            {
                Loops.Add(new PrcBrepLoop(Coedges.Count, coedges.Length, true));
                Coedges.AddRange(coedges);
            }

            foreach (int[]? more in new[] { secondLoop, thirdLoop })
            {
                if (more is not null)
                {
                    Loops.Add(new PrcBrepLoop(Coedges.Count, more.Length, false));
                    Coedges.AddRange(more);
                }
            }

            SurfaceData.AddRange(data);
            Faces.Add(new PrcBrepFace(kind, 0, true, coedges.Length > 0, firstLoop, Loops.Count - firstLoop, dataAt, data.Length, 0, 0, surface, default, false));
            return Faces.Count - 1;
        }

        public PrcTopoBrepDataCompress Build() => new()
        {
            Tolerance = Tolerance,
            Vertices = [.. Vertices],
            Curves = [.. Curves],
            CurveData = [.. Data],
            CompositeMembers = [.. Members],
            Coedges = [.. Coedges],
            Loops = [.. Loops],
            Faces = [.. Faces],
            SurfaceData = [.. SurfaceData],
            FaceVertices = [],
            GenericSurfaces = [.. Generic],
            Nurbs = [],
            CurveNurbs = [.. NurbsCurves],
        };
    }

    private static double DistanceToCircle(PrcVector p, PrcVector centre, PrcVector normal, double radius)
    {
        PrcVector d = p - centre;
        double along = d.Dot(normal);
        double inPlane = Math.Sqrt(Math.Max(0, d.LengthSquared() - (along * along)));
        return Math.Sqrt(((inPlane - radius) * (inPlane - radius)) + (along * along));
    }

    [Fact]
    public void ADeducedSideThatIsAShortArcIsAnArcThoughOneSegmentMeetsTheTolerance()
    {
        // A tenth of a turn of radius 1 at a tolerance of 0.05: one chord is close enough, so the side has two points. The face that
        // deduced it knows it is an arc of its surface and says so; a face built from that side (a torus from its meridian) needs the arc.
        var b = new PrcBrepCurvesTests.Body();
        b.Curves.Add(new PrcBrepCurve(PrcCurveKind.Implicit, -1, -1, 0, 0, 0));
        var sampler = new PrcEdgeSampler(b.Build(), 0.05);
        double angle = 0.2 * Math.PI;
        PrcVector from = new(1, 0, 0), to = new(Math.Cos(angle), Math.Sin(angle), 0);
        sampler.Register(0, [from, to]);
        sampler.Describe(0, new PrcBrepCurves.Arc(default, new PrcVector(1, 0, 0), new PrcVector(0, 1, 0), 1, 1, angle));

        bool described = PrcBrepCurves.TryDescribe(b.Build(), sampler, 0, out bool isLine, out PrcVector start, out PrcVector end, out PrcBrepCurves.Arc arc);
        bool finer = PrcBrepCurves.TryDescribe(b.Build(), sampler.Finer(8), 0, out bool finerIsLine, out _, out _, out PrcBrepCurves.Arc finerArc);

        Assert.True(described);
        Assert.False(isLine);
        Assert.Equal(from, start);
        Assert.Equal(to, end);
        Assert.Equal(1.0, arc.StartRadius, Precision);
        Assert.Equal(angle, arc.Sweep, Precision);
        Assert.True(finer);
        Assert.False(finerIsLine);
        Assert.Equal(angle, finerArc.Sweep, Precision);
    }

    [Fact]
    public void ALineIsItsTwoEnds()
    {
        var b = new Body();
        int curve = b.Line(b.Vertex(0, 0, 0), b.Vertex(3, 4, 0));
        var sampler = new PrcEdgeSampler(b.Build(), 0.01);

        ReadOnlySpan<PrcVector> samples = sampler.Samples(curve);

        Assert.Equal(2, samples.Length);
        Assert.Equal(new PrcVector(0, 0, 0), samples[0]);
        Assert.Equal(new PrcVector(3, 4, 0), samples[1]);
    }

    [Fact]
    public void AQuarterCircleHasTheSegmentsItsToleranceAndAngleCapAsk()
    {
        // Radius 10, chord tolerance 0.01: 2·acos(1 − 0.001) = 0.0894 rad per segment (5.1°); a quarter turn is 1.5708 / 0.0894 = 17.6 → 18 segments.
        var b = new Body();
        int s = b.Vertex(10, 0, 0), e = b.Vertex(0, 10, 0);
        int curve = b.Arc(s, e, new PrcVector(0, 0, 0), overHalfTurn: false);
        var sampler = new PrcEdgeSampler(b.Build(), 0.01);

        ReadOnlySpan<PrcVector> samples = sampler.Samples(curve);

        Assert.Equal(19, samples.Length);
        Assert.Equal(10.0, samples[0].X, Precision);
        Assert.Equal(10.0, samples[^1].Y, Precision);
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(10.0, Math.Sqrt(samples[i].LengthSquared()), 1e-9);
            Assert.Equal(0.0, samples[i].Z, Precision);
        }

        // Every chord's middle is within the tolerance of the circle.
        for (int i = 0; i + 1 < samples.Length; i++)
        {
            PrcVector middle = (samples[i] + samples[i + 1]) / 2;
            Assert.True(10.0 - Math.Sqrt(middle.LengthSquared()) <= 0.01 + 1e-9);
        }
    }

    [Fact]
    public void ABigRadiusAndATinyToleranceAreBoundByTheAngleCapOfFifteenDegrees()
    {
        // A half turn with a tolerance so loose that one segment would do: the cap of 15 degrees makes it 12.
        var b = new Body();
        int s = b.Vertex(1, 0, 0), e = b.Vertex(-1, 0, 0);
        int curve = b.HalfCircle(s, e, new PrcVector(0, 1, 0));
        var sampler = new PrcEdgeSampler(b.Build(), 10.0);

        ReadOnlySpan<PrcVector> samples = sampler.Samples(curve);

        Assert.Equal(13, samples.Length);
        Assert.Equal(1.0, samples[6].Y, Precision);                      // the middle of the arc passes through the given middle point
    }

    [Fact]
    public void AFullCircleStartsAndEndsAtItsVertex()
    {
        var b = new Body();
        int s = b.Vertex(0, 2, 0);
        int curve = b.FullCircle(s, new PrcVector(0, 0, 0), new PrcVector(0, 0, 1));
        var sampler = new PrcEdgeSampler(b.Build(), 0.01);

        ReadOnlySpan<PrcVector> samples = sampler.Samples(curve);

        Assert.True(samples.Length > 24, "at least one segment for every 15 degrees");
        Assert.Equal(samples[0], samples[^1]);
        foreach (PrcVector p in samples)
        {
            Assert.True(DistanceToCircle(p, new PrcVector(0, 0, 0), new PrcVector(0, 0, 1), 2) < 1e-9);
        }
    }

    [Fact]
    public void AnArcThatPassesAHalfTurnGoesTheLongWayRound()
    {
        // From (1,0,0) to (0,1,0) about the origin: the short way is a quarter turn through (0.7, 0.7); the long way is three quarters, through (-0.7,-0.7).
        var b = new Body();
        int s = b.Vertex(1, 0, 0), e = b.Vertex(0, 1, 0);
        int shortWay = b.Arc(s, e, new PrcVector(0, 0, 0), false);
        int longWay = b.Arc(s, e, new PrcVector(0, 0, 0), true);
        var sampler = new PrcEdgeSampler(b.Build(), 0.001);

        ReadOnlySpan<PrcVector> a = sampler.Samples(shortWay);
        ReadOnlySpan<PrcVector> l = sampler.Samples(longWay);

        Assert.True(a[a.Length / 2].X > 0 && a[a.Length / 2].Y > 0);
        Assert.True(l[l.Length / 2].X < 0 && l[l.Length / 2].Y < 0);
        Assert.Equal(1.0, l[^1].Y, Precision);
        Assert.True(l.Length > 2.5 * a.Length);
    }

    [Fact]
    public void AHermiteCurveThroughItsPointsFollowsItsTangents()
    {
        // Points (0,0,0), (1,0,0), (2,0,0) on a straight line with tangents along it: the curve stays on the line.
        var b = new Body();
        int s = b.Vertex(0, 0, 0), e = b.Vertex(2, 0, 0);
        PrcVector[] inner = [new PrcVector(1, 0, 0)];
        PrcVector[] tangents = [new PrcVector(1, 0, 0), new PrcVector(1, 0, 0), new PrcVector(1, 0, 0)];
        int curve = b.Hermite(s, e, inner, tangents);
        var sampler = new PrcEdgeSampler(b.Build(), 0.01);

        ReadOnlySpan<PrcVector> samples = sampler.Samples(curve);

        Assert.Equal(new PrcVector(0, 0, 0), samples[0]);
        Assert.Equal(new PrcVector(2, 0, 0), samples[^1]);
        Assert.Contains(new PrcVector(1, 0, 0), samples.ToArray());       // it passes through the inner point
        foreach (PrcVector p in samples)
        {
            Assert.Equal(0.0, p.Y, Precision);
            Assert.Equal(0.0, p.Z, Precision);
        }
    }

    [Fact]
    public void ACurvedHermiteCurveStaysWithinTheToleranceOfItsTrueShape()
    {
        // A quarter circle of radius 4 through (4,0,0), (2.828,2.828,0), (0,4,0). The tangents are the stored ones: along the circle,
        // only their direction is used; the stored inner points are differences from the point before.
        double r = 4;
        PrcVector p0 = new(r, 0, 0), p1 = new(r * Math.Cos(Math.PI / 4), r * Math.Sin(Math.PI / 4), 0), p2 = new(0, r, 0);
        double chord = Math.Sqrt((p1 - p0).LengthSquared());
        double handle = r * 4 / 3 * Math.Tan(Math.PI / 16);                       // the length of a Bézier handle for an eighth of a circle
        PrcVector Tangent(double angle) => new PrcVector(-Math.Sin(angle), Math.Cos(angle), 0) * (handle * chord);
        var b = new Body();
        int s = b.Vertex(p0.X, p0.Y, 0), e = b.Vertex(p2.X, p2.Y, 0);
        int curve = b.Hermite(s, e, [p1 - p0], [Tangent(0), Tangent(Math.PI / 4), Tangent(Math.PI / 2)]);
        var sampler = new PrcEdgeSampler(b.Build(), 0.005);

        ReadOnlySpan<PrcVector> samples = sampler.Samples(curve);

        Assert.Equal(p0, samples[0]);
        Assert.Equal(p2, samples[^1]);
        foreach (PrcVector p in samples)
        {
            Assert.Equal(r, Math.Sqrt(p.LengthSquared()), 0.05);   // handles of a third of the chord: 1% off a circle, as the files have them
        }

        Assert.True(samples.Length > 8);
    }

    [Fact]
    public void ACompositeCurveJoinsItsMembersWithoutRepeatingAPoint()
    {
        var b = new Body();
        int v0 = b.Vertex(0, 0, 0), v1 = b.Vertex(1, 0, 0), v2 = b.Vertex(1, 1, 0);
        int first = b.Line(v0, v1);
        int second = b.Line(v2, v1);                       // stored the other way round: the chain turns it
        int composite = b.Composite(v0, v2, first, second);
        var sampler = new PrcEdgeSampler(b.Build(), 0.01);

        ReadOnlySpan<PrcVector> samples = sampler.Samples(composite);

        Assert.Equal([new PrcVector(0, 0, 0), new PrcVector(1, 0, 0), new PrcVector(1, 1, 0)], samples.ToArray());
    }

    [Fact]
    public void AnArcOfAnEllipseLiesOnItWithinTheChordFromVertexToVertex()
    {
        var b = new Body();
        // x²/9 + y²/4 = 1 in the plane z = 1, a quarter from (3, 0) to (0, 2).
        int s = b.Vertex(3, 0, 1), e = b.Vertex(0, 2, 1);
        int arc = b.Conic(s, e, new PrcVector(0, 0, 1), new PrcVector(3, 0, 0), new PrcVector(0, 2, 0), 0, Math.PI / 2);
        var sampler = new PrcEdgeSampler(b.Build(), 0.001);

        PrcVector[] samples = sampler.Samples(arc).ToArray();

        Assert.Equal(new PrcVector(3, 0, 1), samples[0]);
        Assert.Equal(new PrcVector(0, 2, 1), samples[^1]);
        Assert.True(samples.Length > 6);
        foreach (PrcVector p in samples)
        {
            Assert.True(Math.Abs((p.X * p.X / 9) + (p.Y * p.Y / 4) - 1) < 1e-9 && Math.Abs(p.Z - 1) < 1e-12);
        }

        for (int i = 1; i < samples.Length; i++)
        {
            // The middle of each chord is within the tolerance of the ellipse (the radius there is at most 3).
            PrcVector m = (samples[i - 1] + samples[i]) / 2;
            double scale = Math.Sqrt((m.X * m.X / 9) + (m.Y * m.Y / 4));
            Assert.True((1 - scale) * 3 <= 0.001 * 1.01, $"chord {i} sags {(1 - scale) * 3}");
        }
    }

    [Fact]
    public void ARationalQuadraticQuarterCircleSamplesOnTheCircle()
    {
        var b = new Body();
        int s = b.Vertex(1, 0, 0), e = b.Vertex(0, 1, 0);
        double w = Math.Sqrt(0.5);
        var curve = new PrcNurbsCurve(2, [0, 0, 0, 1, 1, 1], [new PrcVector(1, 0, 0), new PrcVector(1, 1, 0), new PrcVector(0, 1, 0)], [1, w, 1]);
        int c = b.NurbsCurve(s, e, curve, 0, 1);
        var sampler = new PrcEdgeSampler(b.Build(), 0.0001);

        PrcVector[] samples = sampler.Samples(c).ToArray();

        Assert.Equal(new PrcVector(1, 0, 0), samples[0]);
        Assert.Equal(new PrcVector(0, 1, 0), samples[^1]);
        Assert.True(samples.Length > 8);
        foreach (PrcVector p in samples)
        {
            Assert.True(Math.Abs(Math.Sqrt((p.X * p.X) + (p.Y * p.Y)) - 1) < 1e-9, $"{p}");
        }
    }

    [Fact]
    public void APolylineIsItsPoints()
    {
        var b = new Body();
        int s = b.Vertex(0, 0, 0), e = b.Vertex(2, 1, 0);
        int c = b.Polyline(s, e, new PrcVector(0, 0, 0), new PrcVector(1, 0, 0), new PrcVector(1, 1, 0), new PrcVector(2, 1, 0));
        var sampler = new PrcEdgeSampler(b.Build(), 0.01);

        Assert.Equal([new PrcVector(0, 0, 0), new PrcVector(1, 0, 0), new PrcVector(1, 1, 0), new PrcVector(2, 1, 0)], sampler.Samples(c).ToArray());
    }

    [Fact]
    public void AChainOfCompositesThousandsDeepIsRefusedNotARunawayRecursion()
    {
        var b = new Body();
        int v0 = b.Vertex(0, 0, 0), v1 = b.Vertex(1, 0, 0);
        int curve = b.Line(v0, v1);
        for (int i = 0; i < 20_000; i++)
        {
            curve = b.Composite(v0, v1, curve);
        }

        var sampler = new PrcEdgeSampler(b.Build(), 0.01);

        Assert.Throws<InvalidOperationException>(() => sampler.Samples(curve));
    }

    [Fact]
    public void ACompositeOfTwoCopiesOfTheCurveBeforeItDoesNotDoubleWithoutEnd()
    {
        var b = new Body();
        int v0 = b.Vertex(0, 0, 0), v1 = b.Vertex(1, 0, 0);
        int curve = b.Line(v0, v1);
        for (int i = 0; i < 40; i++)
        {
            curve = b.Composite(v0, v1, curve, curve);
        }

        var sampler = new PrcEdgeSampler(b.Build(), 0.01);

        Assert.Throws<InvalidOperationException>(() => sampler.Samples(curve));
    }

    [Fact]
    public void TheSamplesOfACurveAreTheSameWhoeverAsksFirst()
    {
        var b = new Body();
        int s = b.Vertex(10, 0, 0), e = b.Vertex(0, 10, 0);
        int arc = b.Arc(s, e, new PrcVector(0, 0, 0), false);
        PrcTopoBrepDataCompress body = b.Build();
        var a = new PrcEdgeSampler(body, 0.01);
        var c = new PrcEdgeSampler(body, 0.01);

        PrcVector[] first = a.Samples(arc).ToArray();
        PrcVector[] second = a.Samples(arc).ToArray();
        PrcVector[] other = c.Samples(arc).ToArray();

        Assert.Equal(first, second);
        Assert.Equal(first, other);
        Assert.Equal(1, a.CurvesSampled);               // the second ask did not sample again
    }

    [Fact]
    public void AnEdgeThatWouldNeedMoreThanTheCapIsSampledAtTheCapAndSaysSo()
    {
        // A radius of a million with a tolerance of a millionth asks for far more than 1024 segments of a full circle.
        var b = new Body();
        int s = b.Vertex(1e6, 0, 0);
        int curve = b.FullCircle(s, new PrcVector(0, 0, 0), new PrcVector(0, 0, 1));
        var sampler = new PrcEdgeSampler(b.Build(), 1e-6);

        ReadOnlySpan<PrcVector> samples = sampler.Samples(curve);

        Assert.Equal(PrcBrepTolerance.MaxSamplesPerEdge + 1, samples.Length);
        Assert.True(sampler.Capped);
    }

    [Fact]
    public void TheChordToleranceOfABodyIsAThousandthOfItsDiagonalNeverFinerThanTheFilesOwn()
    {
        var b = new Body { Tolerance = 0.5 };
        b.Vertex(0, 0, 0);
        b.Vertex(100, 0, 0);
        b.Vertex(0, 0, 0);
        Assert.Equal(0.5, PrcBrepTolerance.Chord(b.Build()), Precision);      // 100 / 1000 = 0.1 < 0.5

        var big = new Body { Tolerance = 0.001 };
        big.Vertex(0, 0, 0);
        big.Vertex(300, 400, 0);
        Assert.Equal(0.5, PrcBrepTolerance.Chord(big.Build()), Precision);    // 500 / 1000

        Assert.Equal(0.25, PrcBrepTolerance.Chord(new Body { Tolerance = 0.25 }.Build()), Precision);   // nothing to measure
    }
}
