using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>Compressed bodies cut into triangles: closed where the body is, outward, within the tolerance of the surfaces.</summary>
public sealed class PrcBrepTessellatorTests
{
    /// <summary>What a body tessellated to, read back from the scene it was added to.</summary>
    private sealed record Mesh(PrcVector[] Positions, PrcVector[] Normals, int[] Indices, PrcBrepTessellation Result);

    private static Mesh Tessellate(PrcTopoBrepDataCompress brep)
    {
        using var builder = new SceneBuilder(SceneLimits.Default);
        PrcMesh mesh = PrcBrepTessellator.AddMesh(builder, brep, builder.AddName("body"), out PrcBrepTessellation result);
        Assert.True(mesh.Mesh >= 0, "a mesh was made");
        int node = builder.AddNode(new SceneNode { Parent = -1, Local = System.Numerics.Matrix4x4.Identity, Kind = SceneNodeKind.Model, Mesh = mesh.Mesh, Light = -1, Camera = -1, Name = builder.AddName("n"), Visible = true, FirstBinding = 0, BindingCount = 0 });
        Assert.Equal(0, node);
        using Scene scene = builder.Build();
        ReadOnlySpan<float> positions = scene.Positions, normals = scene.Normals;
        var p = new PrcVector[positions.Length / 3];
        var n = new PrcVector[positions.Length / 3];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = new PrcVector(positions[3 * i], positions[(3 * i) + 1], positions[(3 * i) + 2]);
            n[i] = new PrcVector(normals[3 * i], normals[(3 * i) + 1], normals[(3 * i) + 2]);
        }

        return new Mesh(p, n, [.. scene.Indices.ToArray().Select(x => (int)x)], result);
    }

    /// <summary>
    /// For every edge of the mesh (a pair of positions, welded to a thousandth), how many triangles use it. A closed mesh has two
    /// for each, in opposite directions.
    /// </summary>
    private static Dictionary<((long, long, long), (long, long, long)), int> EdgeUses(Mesh m)
    {
        static (long, long, long) Key(PrcVector v) => ((long)Math.Round(v.X * 1000), (long)Math.Round(v.Y * 1000), (long)Math.Round(v.Z * 1000));
        var uses = new Dictionary<((long, long, long), (long, long, long)), int>();
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                var a = Key(m.Positions[m.Indices[t + e]]);
                var b = Key(m.Positions[m.Indices[t + ((e + 1) % 3)]]);
                if (a == b)
                {
                    continue;
                }

                uses[(a, b)] = uses.GetValueOrDefault((a, b)) + 1;
            }
        }

        return uses;
    }

    private static void AssertClosed(Mesh m)
    {
        Dictionary<((long, long, long), (long, long, long)), int> uses = EdgeUses(m);
        foreach (((var a, var b), int count) in uses)
        {
            Assert.Equal(1, count);
            Assert.True(uses.TryGetValue((b, a), out int back) && back == 1, $"edge {a}-{b} has no triangle on its other side");
        }
    }

    private static double Volume(Mesh m)
    {
        double v = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            PrcVector a = m.Positions[m.Indices[t]], b = m.Positions[m.Indices[t + 1]], c = m.Positions[m.Indices[t + 2]];
            v += a.Dot(b.Cross(c)) / 6;
        }

        return v;
    }

    private static void AssertNormalsAgreeWithWinding(Mesh m)
    {
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            PrcVector a = m.Positions[m.Indices[t]], b = m.Positions[m.Indices[t + 1]], c = m.Positions[m.Indices[t + 2]];
            PrcVector g = (b - a).Cross(c - a);
            PrcVector n = m.Normals[m.Indices[t]] + m.Normals[m.Indices[t + 1]] + m.Normals[m.Indices[t + 2]];
            Assert.True(g.Dot(n) >= 0, "a triangle runs the other way from its normals");
        }
    }

    /// <summary>A box of eight vertices, twelve lines and six iso planes whose four sides are all stored.</summary>
    private static PrcTopoBrepDataCompress Box(double x, double y, double z)
    {
        var b = new PrcBrepCurvesTests.Body();
        int[] v = new int[8];
        for (int i = 0; i < 8; i++)
        {
            v[i] = b.Vertex((i & 1) != 0 ? x : 0, (i & 2) != 0 ? y : 0, (i & 4) != 0 ? z : 0);
        }

        int Edge(int p, int q) => b.Line(v[p], v[q]);
        int e01 = Edge(0, 1), e13 = Edge(1, 3), e32 = Edge(3, 2), e20 = Edge(2, 0);
        int e45 = Edge(4, 5), e57 = Edge(5, 7), e76 = Edge(7, 6), e64 = Edge(6, 4);
        int e04 = Edge(0, 4), e15 = Edge(1, 5), e37 = Edge(3, 7), e26 = Edge(2, 6);
        // Outward normals: bottom -z, top +z, front -y, back +y, left -x, right +x.
        b.Face(PrcFaceKind.IsoPlane, [0, 0, 0], [e01, e13, e32, e20]);
        b.Face(PrcFaceKind.IsoPlane, [0, 0, 1], [e45, e57, e76, e64]);
        b.Face(PrcFaceKind.IsoPlane, [0, -1, 1], [e01, e15, e45, e04]);
        b.Face(PrcFaceKind.IsoPlane, [0, 1, 1], [e32, e37, e76, e26]);
        b.Face(PrcFaceKind.IsoPlane, [-1, 0, 1], [e20, e26, e64, e04]);
        b.Face(PrcFaceKind.IsoPlane, [1, 0, 1], [e13, e37, e57, e15]);
        return b.Build();
    }

    [Fact]
    public void ABoxOfSixIsoPlanesIsClosedAndHasTheBoxsVolume()
    {
        Mesh m = Tessellate(Box(2, 3, 4));

        Assert.Equal(6, m.Result.Faces);
        Assert.Equal(0, m.Result.FacesDropped);
        Assert.Equal(12, m.Result.Triangles);                      // two for each face: nothing needs more
        AssertClosed(m);
        Assert.Equal(24.0, Volume(m), 1e-9);
        AssertNormalsAgreeWithWinding(m);
    }

    [Fact]
    public void AFaceOfTheShellTurnedTheOtherWayTurnsItsNormalsAndItsTriangles()
    {
        PrcTopoBrepDataCompress box = Box(1, 1, 1);
        PrcBrepFace[] faces = [.. box.Faces];
        faces[1] = faces[1] with { OrientationWithShell = false };
        var flipped = new PrcTopoBrepDataCompress
        {
            Tolerance = box.Tolerance, Vertices = box.Vertices, Curves = box.Curves, CurveData = box.CurveData, CompositeMembers = box.CompositeMembers,
            Coedges = box.Coedges, Loops = box.Loops, Faces = faces, SurfaceData = box.SurfaceData, FaceVertices = box.FaceVertices, GenericSurfaces = box.GenericSurfaces, Nurbs = box.Nurbs,
        };

        Mesh m = Tessellate(flipped);

        AssertNormalsAgreeWithWinding(m);
        Assert.True(Volume(m) < 0.9, "the top face now runs inwards, so the volume it encloses is smaller than the box's");
    }

    [Fact]
    public void AFaceWhoseLoopIsOpenIsDroppedAndTheRestIsDrawn()
    {
        // Review focus: a side whose end is nowhere near the start of the next is not closed up by guessing.
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(0, 0, 0), c = b.Vertex(1, 0, 0), d = b.Vertex(1, 1, 0), e = b.Vertex(0, 1, 0), far = b.Vertex(5, 5, 5);
        int good = b.Face(PrcFaceKind.IsoPlane, [0, 0, 1], [b.Line(a, c), b.Line(c, d), b.Line(d, e), b.Line(e, a)]);
        int open = b.Face(PrcFaceKind.IsoPlane, [0, 0, 1], [b.Line(a, c), b.Line(d, e), b.Line(e, far)]);
        _ = good;
        _ = open;

        Mesh m = Tessellate(b.Build());

        Assert.Equal(2, m.Result.Faces);
        Assert.Equal(1, m.Result.FacesDropped);
        Assert.Equal(2, m.Result.Triangles);
    }

    [Fact]
    public void AnAnaPlaneWithAHoleIsTriangulatedAroundIt()
    {
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(0, 0, 0), c = b.Vertex(4, 0, 0), d = b.Vertex(4, 4, 0), e = b.Vertex(0, 4, 0);
        int h0 = b.Vertex(1, 1, 0), h1 = b.Vertex(1, 3, 0), h2 = b.Vertex(3, 3, 0), h3 = b.Vertex(3, 1, 0);
        int[] outer = [b.Line(a, c), b.Line(c, d), b.Line(d, e), b.Line(e, a)];
        int[] hole = [b.Line(h0, h1), b.Line(h1, h2), b.Line(h2, h3), b.Line(h3, h0)];
        int face = b.Face(PrcFaceKind.AnaPlane, [0, 0, 1], outer);
        // A second loop: the face has two.
        PrcTopoBrepDataCompress built = b.Build();
        int firstCoedge = built.Coedges.Length;
        var coedges = built.Coedges.Concat(hole).ToArray();
        var loops = built.Loops.Concat([new PrcBrepLoop(firstCoedge, 4, false)]).ToArray();
        PrcBrepFace f = built.Faces[face] with { LoopCount = 2 };
        var body = new PrcTopoBrepDataCompress
        {
            Tolerance = built.Tolerance, Vertices = built.Vertices, Curves = built.Curves, CurveData = built.CurveData, CompositeMembers = built.CompositeMembers,
            Coedges = coedges, Loops = loops, Faces = [f], SurfaceData = built.SurfaceData, FaceVertices = built.FaceVertices, GenericSurfaces = built.GenericSurfaces, Nurbs = built.Nurbs,
        };

        Mesh m = Tessellate(body);

        double area = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            area += Math.Sqrt((m.Positions[m.Indices[t + 1]] - m.Positions[m.Indices[t]]).Cross(m.Positions[m.Indices[t + 2]] - m.Positions[m.Indices[t]]).LengthSquared()) / 2;
        }

        Assert.Equal(12.0, area, 1e-9);
        Assert.Equal(1, m.Result.Faces);
        Assert.Equal(0, m.Result.FacesDropped);
    }

    [Fact]
    public void ACylinderWallBetweenTwoCirclesIsStripsOfTheArcsThatTheToleranceAsks()
    {
        // The wall of the cylinder of radius 2 and height 5 about z: an iso cylinder, its four sides stored (two arcs, two generators), a seam.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int q0 = b.Vertex(2, 0, 0), q1 = b.Vertex(0, 2, 0), q2 = b.Vertex(-2, 0, 0), q3 = b.Vertex(0, -2, 0);
        int t0 = b.Vertex(2, 0, 5), t1 = b.Vertex(0, 2, 5), t2 = b.Vertex(-2, 0, 5), t3 = b.Vertex(0, -2, 5);
        // One quarter of the wall: from q0 to q1 along the bottom, up to t1, back to t0, down.
        int bottom = b.Arc(q0, q1, new PrcVector(0, 0, 0), false);
        int right = b.Line(q1, t1);
        int top = b.Arc(t0, t1, new PrcVector(0, 0, 5), false);
        int left = b.Line(q0, t0);
        b.Face(PrcFaceKind.IsoCylinder, [], [bottom, right, top, left]);
        _ = new[] { q2, q3, t2, t3 };

        Mesh m = Tessellate(b.Build());

        Assert.Equal(1, m.Result.Faces);
        Assert.Equal(0, m.Result.FacesDropped);
        // Every point of the wall is within the tolerance of the cylinder, and its normal points away from the axis.
        foreach (PrcVector p in m.Positions)
        {
            Assert.True(Math.Abs(Math.Sqrt((p.X * p.X) + (p.Y * p.Y)) - 2) < 1e-6);
        }

        for (int i = 0; i < m.Positions.Length; i++)
        {
            PrcVector radial = new PrcVector(m.Positions[i].X, m.Positions[i].Y, 0) / 2;
            Assert.True(m.Normals[i].Dot(radial) > 0.99);
        }

        // 2·acos(1 − 0.0005 / 2 ·…) – the quarter turn takes 18 segments at a chord of 0.005 on this radius, so at least that many triangles pairs.
        Assert.True(m.Result.Triangles >= 2 * 6);
    }

    [Fact]
    public void AWallBetweenTwoFullCirclesIsTheWholeStripRoundTheCylinder()
    {
        // Two loops that each go once round: the case that was dropped on 5,000 faces of the corpus.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int bottom = b.Vertex(2, 0, 0), top = b.Vertex(2, 0, 5);
        int low = b.FullCircle(bottom, new PrcVector(0, 0, 0), new PrcVector(0, 0, 1)), high = b.FullCircle(top, new PrcVector(0, 0, 5), new PrcVector(0, 0, 1));
        b.Face(PrcFaceKind.AnaCylinder, [0, 0, 0, 0, 0, 1], [low], secondLoop: [high]);

        Mesh m = Tessellate(b.Build());

        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        double area = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            PrcVector a = m.Positions[m.Indices[t]], c = m.Positions[m.Indices[t + 1]], d = m.Positions[m.Indices[t + 2]];
            area += Math.Sqrt((c - a).Cross(d - a).LengthSquared()) / 2;
        }

        double exact = 2 * Math.PI * 2 * 5;
        Assert.InRange(area, exact * 0.98, exact * 1.001);
        foreach (PrcVector p in m.Positions)
        {
            Assert.True(Math.Abs(Math.Sqrt((p.X * p.X) + (p.Y * p.Y)) - 2) < 1e-6 && p.Z is >= -1e-9 and <= 5 + 1e-9);
        }
    }

    private static double Area(Mesh m)
    {
        double area = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            PrcVector a = m.Positions[m.Indices[t]], c = m.Positions[m.Indices[t + 1]], d = m.Positions[m.Indices[t + 2]];
            area += Math.Sqrt((c - a).Cross(d - a).LengthSquared()) / 2;
        }

        return area;
    }

    [Fact]
    public void ATorusBetweenTwoCirclesRoundItsTubeIsTheStripBetweenThem()
    {
        // Major radius 5 about z, tube radius 1; two meridians, at 0 and at a quarter turn: each loop goes once round the tube (in v),
        // and the face is the quarter of the torus between them.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int first = b.Vertex(6, 0, 0), second = b.Vertex(0, 6, 0);
        int m0 = b.FullCircle(first, new PrcVector(5, 0, 0), new PrcVector(0, 1, 0)), m1 = b.FullCircle(second, new PrcVector(0, 5, 0), new PrcVector(-1, 0, 0));
        b.Face(PrcFaceKind.AnaTorus, [0, 0, 0, 5, 0, 0, 0, 1, 0], [m0], secondLoop: [m1]);

        Mesh m = Tessellate(b.Build());

        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        double exact = 4 * Math.PI * Math.PI * 5 * 1 / 4;
        Assert.InRange(Area(m), exact * 0.98, exact * 1.001);
        foreach (PrcVector p in m.Positions)
        {
            Assert.True(p.X >= -1e-9 && p.Y >= -1e-9, $"{p} is outside the quarter");
        }
    }

    [Fact]
    public void ACylinderWallWithAHoleIsTheStripAroundTheHole()
    {
        // The wall of radius 2 from z = 0 to 5, and a small hole in it: four lines between points of the wall a tenth of a radian apart.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int bottom = b.Vertex(2, 0, 0), top = b.Vertex(2, 0, 5);
        int low = b.FullCircle(bottom, new PrcVector(0, 0, 0), new PrcVector(0, 0, 1)), high = b.FullCircle(top, new PrcVector(0, 0, 5), new PrcVector(0, 0, 1));
        double a = 0.05;
        int h0 = b.Vertex(-2 * Math.Cos(a), -2 * Math.Sin(a), 2), h1 = b.Vertex(-2 * Math.Cos(a), 2 * Math.Sin(a), 2);
        int h2 = b.Vertex(-2 * Math.Cos(a), 2 * Math.Sin(a), 3), h3 = b.Vertex(-2 * Math.Cos(a), -2 * Math.Sin(a), 3);
        int[] hole = [b.Line(h0, h1), b.Line(h1, h2), b.Line(h2, h3), b.Line(h3, h0)];
        b.Face(PrcFaceKind.AnaCylinder, [0, 0, 0, 0, 0, 1], [low], secondLoop: [high], thirdLoop: hole);

        Mesh m = Tessellate(b.Build());

        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        double exact = (2 * Math.PI * 2 * 5) - (2 * 2 * a * 1);
        Assert.InRange(Area(m), exact * 0.98, exact * 1.001);
        Assert.DoesNotContain(m.Positions, p => p.X < -1.9 && Math.Abs(p.Y) < 0.05 && p.Z > 2.2 && p.Z < 2.8);   // nothing in the hole
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-3.0)]
    public void AFaceBoundedByOneCircleOfASphereIsTheCapAroundTheNearerPole(double height)
    {
        // A sphere of radius 5 about the origin cut by the plane z = ±3: the cap is 2π·5·2 in area and lies beyond the plane.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int a = b.Vertex(4, 0, height);
        int circle = b.FullCircle(a, new PrcVector(0, 0, height), new PrcVector(0, 0, 1));
        b.Face(PrcFaceKind.AnaSphere, [0, 0, 0], [circle]);

        Mesh m = Tessellate(b.Build());

        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        double area = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            PrcVector p = m.Positions[m.Indices[t]], q = m.Positions[m.Indices[t + 1]], r = m.Positions[m.Indices[t + 2]];
            area += Math.Sqrt((q - p).Cross(r - p).LengthSquared()) / 2;
        }

        Assert.InRange(area, 2 * Math.PI * 5 * 2 * 0.97, 2 * Math.PI * 5 * 2 * 1.001);
        foreach (PrcVector p in m.Positions)
        {
            Assert.True(Math.Abs(Math.Sqrt((p.X * p.X) + (p.Y * p.Y) + (p.Z * p.Z)) - 5) < 1e-6);
            Assert.True(height > 0 ? p.Z >= height - 1e-6 : p.Z <= height + 1e-6, $"z {p.Z}");
        }
    }

    [Fact]
    public void AHemisphereWhoseLoopRunsThroughBothPolesIsHalfTheSphere()
    {
        // Two half circles through the poles of a sphere of radius 5 (the axis is z): a loop with no angle at the poles.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int north = b.Vertex(0, 0, 5), south = b.Vertex(0, 0, -5);
        int east = b.HalfCircle(north, south, new PrcVector(5, 0, 0)), west = b.HalfCircle(south, north, new PrcVector(-5, 0, 0));
        b.Face(PrcFaceKind.AnaSphere, [0, 0, 0], [east, west]);

        Mesh m = Tessellate(b.Build());

        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        double area = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            PrcVector p = m.Positions[m.Indices[t]], q = m.Positions[m.Indices[t + 1]], r = m.Positions[m.Indices[t + 2]];
            area += Math.Sqrt((q - p).Cross(r - p).LengthSquared()) / 2;
        }

        Assert.InRange(area, 2 * Math.PI * 25 * 0.97, 2 * Math.PI * 25 * 1.001);
    }

    [Fact]
    public void ASheetWhoseExtrusionWasLostIsRebuiltFromItsFace()
    {
        // A writer stores an extruded sheet by its profile and the step to the far end; when that step is lost the two rows of control
        // points are one (here at z = 100). The face, a 2 by 3 rectangle at z 0 to 3, is off the surface along one direction: the sheet
        // is that profile swept along it.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int p0 = b.Vertex(0, 0, 0), p1 = b.Vertex(2, 0, 0), p2 = b.Vertex(2, 0, 3), p3 = b.Vertex(0, 0, 3);
        b.Face(PrcFaceKind.AnaNurbs, [], [b.Line(p0, p1), b.Line(p1, p2), b.Line(p2, p3), b.Line(p3, p0)], surface: 0);
        PrcTopoBrepDataCompress built = b.Build();
        var body = new PrcTopoBrepDataCompress
        {
            Tolerance = built.Tolerance, Vertices = built.Vertices, Curves = built.Curves, CurveData = built.CurveData, CompositeMembers = built.CompositeMembers,
            Coedges = built.Coedges, Loops = built.Loops, Faces = built.Faces, SurfaceData = built.SurfaceData, FaceVertices = built.FaceVertices, GenericSurfaces = built.GenericSurfaces,
            Nurbs =
            [
                new PrcNurbs
                {
                    DegreeU = 1, DegreeV = 1, CountU = 2, CountV = 2, KnotsU = [0, 0, 1, 1], KnotsV = [0, 0, 1, 1],
                    ControlPoints = [new PrcVector(0, 0, 100), new PrcVector(0, 0, 100), new PrcVector(2, 0, 100), new PrcVector(2, 0, 100)],
                },
            ],
        };

        Mesh m = Tessellate(body);

        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        double area = 0;
        for (int i = 0; i < m.Indices.Length; i += 3)
        {
            PrcVector a = m.Positions[m.Indices[i]], c = m.Positions[m.Indices[i + 1]], d = m.Positions[m.Indices[i + 2]];
            area += Math.Sqrt((c - a).Cross(d - a).LengthSquared()) / 2;
        }

        Assert.Equal(6, area, 6);
    }

    [Fact]
    public void ASphereWhoseLoopIsAVertexIsTheWholeSphere()
    {
        // A vertex loop is a point: the face is the closed surface, its radius from the vertex.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int a = b.Vertex(6, 2, 3);
        b.Face(PrcFaceKind.AnaSphere, [1, 2, 3], [b.Line(a, a)]);

        Mesh m = Tessellate(b.Build());

        Assert.Equal(0, m.Result.FacesDropped);
        foreach (PrcVector p in m.Positions)
        {
            Assert.True(Math.Abs(Math.Sqrt(((p.X - 1) * (p.X - 1)) + ((p.Y - 2) * (p.Y - 2)) + ((p.Z - 3) * (p.Z - 3))) - 5) < 1e-6);
        }

        double volume = Math.Abs(Volume(m));
        double exact = 4.0 / 3 * Math.PI * 125;
        // The mesh is inscribed: smaller than the ball by about the chord tolerance over the radius, three times.
        Assert.True(volume < exact && volume > exact * 0.97, $"{volume} of {exact}");
    }
    [Fact]
    public void ATorusFaceBoundedOnlyByVertexLoopsIsTheWholeTorus()
    {
        double Run(PrcVector pointOnTorus)
        {
            var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
            int a = b.Vertex(6, 0, 0);
            int dot = b.Line(a, a);
            int face = b.Face(PrcFaceKind.AnaTorus, [0, 0, 0, 5, 0, 0, 0, 1, 0], [dot]);
            PrcTopoBrepDataCompress built = b.Build();
            PrcBrepFace f = built.Faces[face] with { HasPointOnTorus = true, PointOnTorus = pointOnTorus };
            var body = new PrcTopoBrepDataCompress
            {
                Tolerance = built.Tolerance, Vertices = built.Vertices, Curves = built.Curves, CurveData = built.CurveData, CompositeMembers = built.CompositeMembers,
                Coedges = built.Coedges, Loops = built.Loops, Faces = [f], SurfaceData = built.SurfaceData, FaceVertices = built.FaceVertices, GenericSurfaces = built.GenericSurfaces, Nurbs = built.Nurbs,
            };
            Mesh m = Tessellate(body);
            Assert.Equal(0, m.Result.FacesDropped);
            return Math.Abs(Volume(m));
        }

        double exact = 2 * Math.PI * Math.PI * 5 * 1 * 1;
        Assert.InRange(Run(new PrcVector(6, 0, 0)), exact * 0.95, exact);
        Assert.InRange(Run(new PrcVector(4, 0, 0)), exact * 0.95, exact);
    }

    [Fact]
    public void ATinyToleranceOnAHugeSurfaceStopsAtTheCaps()
    {
        // Review focus: a whole sphere of radius 5 with a file tolerance of 1e-9 asks for billions of cells: it is cut at the caps.
        var b = new PrcBrepCurvesTests.Body { Tolerance = 1e-9 };
        int a = b.Vertex(5, 0, 0);
        b.Face(PrcFaceKind.AnaSphere, [0, 0, 0], [b.Line(a, a)]);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Mesh m = Tessellate(b.Build());

        watch.Stop();
        Assert.True(m.Result.Capped);
        Assert.True(m.Result.Triangles <= PrcBrepTolerance.MaxTrianglesPerFace);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"{watch.ElapsedMilliseconds} ms");
    }
    [Fact]
    public void AFaceOnASurfaceWhoseKnotsRunBackwardsIsDroppedAndTheBodyIsStillRead()
    {
        var b = new PrcBrepCurvesTests.Body { Tolerance = 0.001 };
        int face = b.Face(PrcFaceKind.AnaNurbs, [], [], surface: 0);
        PrcTopoBrepDataCompress built = b.Build();
        var body = new PrcTopoBrepDataCompress
        {
            Tolerance = built.Tolerance, Vertices = built.Vertices, Curves = built.Curves, CurveData = built.CurveData, CompositeMembers = built.CompositeMembers,
            Coedges = built.Coedges, Loops = built.Loops, Faces = built.Faces, SurfaceData = built.SurfaceData, FaceVertices = built.FaceVertices, GenericSurfaces = built.GenericSurfaces,
            Nurbs = [new PrcNurbs { DegreeU = 1, DegreeV = 1, CountU = 2, CountV = 2, KnotsU = [0, 0.7, 0.2, 1], KnotsV = [0, 0, 1, 1], ControlPoints = [new PrcVector(0, 0, 0), new PrcVector(0, 1, 0), new PrcVector(1, 0, 0), new PrcVector(1, 1, 0)] }],
        };
        Assert.Equal(0, face);

        using var builder = new SceneBuilder();
        PrcBrepTessellator.AddMesh(builder, body, builder.AddName("x"), out PrcBrepTessellation result, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal((1, 1, 0), (result.Faces, result.FacesDropped, result.Triangles));
    }

    [Fact]
    public void ThinningALongGridOfEqualLinesStillHalvesIt()
    {
        // Knots out of order make a grid of one line over and over; thinning by "the first index of this value" never removed any.
        var lines = new List<double>(Enumerable.Repeat(0.6, 100_000));

        PrcBrepTessellator.Thin(lines);

        Assert.Equal(50_000, lines.Count);
    }

    [Fact]
    public void CancellationIsSeenBetweenFaces()
    {
        PrcTopoBrepDataCompress box = Box(1, 1, 1);
        using var builder = new SceneBuilder(SceneLimits.Default);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => PrcBrepTessellator.AddMesh(builder, box, builder.AddName("x"), out _, cancellation: cancel.Token));
    }

    [Fact]
    public void AFaceOfAKindThatIsNotRebuiltIsCountedAndTheBodyIsStillDrawn()
    {
        var b = new PrcBrepCurvesTests.Body();
        int a = b.Vertex(0, 0, 0), c = b.Vertex(1, 0, 0), d = b.Vertex(1, 1, 0), e = b.Vertex(0, 1, 0);
        b.Face(PrcFaceKind.IsoPlane, [0, 0, 1], [b.Line(a, c), b.Line(c, d), b.Line(d, e), b.Line(e, a)]);
        b.Generic.Add(new PrcGenericSurface(PrcGenericSurfaceKind.Unsupported, 0, Matrix4x4d.Identity, 0, 0));
        b.Face(PrcFaceKind.AnaGenericFace, [], [b.Line(a, c), b.Line(c, d), b.Line(d, a)], surface: 0);

        Mesh m = Tessellate(b.Build());

        Assert.Equal(2, m.Result.Faces);
        Assert.Equal(1, m.Result.FacesUnsupported);
        Assert.Equal(2, m.Result.Triangles);
    }
}
