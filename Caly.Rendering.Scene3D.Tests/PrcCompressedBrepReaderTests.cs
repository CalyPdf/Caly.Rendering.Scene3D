using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>Compressed bodies: vertices, curves, faces, shells (ISO 14739-1 draft 7.9.20 and the issues its errata come from).</summary>
public sealed class PrcCompressedBrepReaderTests
{
    private const double Tolerance = 0.01;
    private const int ReferenceBits = 5;
    private const double Precision = 1e-9;

    private static PrcCompressedBrepReader Reader(uint isoVertices = 1000, uint isoEdges = 1000) =>
        new(new PrcContext(8137, 1), Tolerance, ReferenceBits, isoVertices, isoEdges) { IsoFace = true };

    private static PrcBrepWriter Writer() => new(Tolerance, ReferenceBits);

    private static void AssertNear(PrcVector expected, PrcVector actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Z, actual.Z, Precision);
    }

    [Fact]
    public void AStoredVertexIsAddedAndAReferencedOneIsFound()
    {
        PrcBrepWriter w = Writer();
        w.Vertex(BrepVertex.New(0.5, -1.25, 2)).Vertex(BrepVertex.New(1, 1, 1)).Vertex(BrepVertex.Ref(0));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        Assert.Equal(0, reader.ReadVertex(ref r));
        Assert.Equal(1, reader.ReadVertex(ref r));
        Assert.Equal(0, reader.ReadVertex(ref r));
        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        Assert.Equal(2, body.Vertices.Length);
        AssertNear(new PrcVector(0.5, -1.25, 2), body.Vertices[0]);
    }

    [Fact]
    public void AVertexReferenceBeyondTheVerticesStoredIsRefused()
    {
        PrcBrepWriter w = Writer();
        w.Vertex(BrepVertex.New(1, 2, 3)).Vertex(BrepVertex.Ref(3));
        byte[] bits = w.Bits.ToArray();
        PrcCompressedBrepReader reader = Reader();

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            reader.ReadVertex(ref r);
            reader.ReadVertex(ref r);
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    [Fact]
    public void VerticesOfIsoFacesAndOfAnaFacesAreTwoCachesThatTheIndexTellsApart()
    {
        // Two iso vertices (indices 0, 1), then ana vertices, which are numbered from the number of iso vertices on (issue #705).
        PrcBrepWriter w = Writer();
        w.Vertex(BrepVertex.New(1, 0, 0)).Vertex(BrepVertex.New(2, 0, 0));       // iso: 0 and 1
        w.Vertex(BrepVertex.New(3, 0, 0));                                       // ana: stored third, referred to as 2
        w.Vertex(BrepVertex.Ref(2)).Vertex(BrepVertex.Ref(1)).Vertex(BrepVertex.Ref(0));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader(isoVertices: 2);

        Assert.Equal(0, reader.ReadVertex(ref r));
        Assert.Equal(1, reader.ReadVertex(ref r));
        reader.IsoFace = false;
        Assert.Equal(2, reader.ReadVertex(ref r));
        Assert.Equal(2, reader.ReadVertex(ref r));
        Assert.Equal(1, reader.ReadVertex(ref r));
        Assert.Equal(0, reader.ReadVertex(ref r));
    }

    [Fact]
    public void AReferenceJustPastTheLastVertexOfEitherCacheIsRefused()
    {
        PrcBrepWriter w = Writer();
        w.Vertex(BrepVertex.New(1, 0, 0)).Vertex(BrepVertex.New(2, 0, 0)).Vertex(BrepVertex.New(3, 0, 0));
        w.Vertex(BrepVertex.Ref(3));                    // iso 0 and 1, ana 0 (index 2): index 3 is the first one not stored
        w.Vertex(BrepVertex.Ref(2));                    // iso numbers end at 1 with two iso vertices announced... 2 is the ana vertex
        byte[] bits = w.Bits.ToArray();
        PrcCompressedBrepReader reader = Reader(isoVertices: 2);

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            reader.ReadVertex(ref r);
            reader.ReadVertex(ref r);
            reader.IsoFace = false;
            reader.ReadVertex(ref r);
            reader.ReadVertex(ref r);
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);

        // And a body that announced more iso vertices than it stored: a reference into the gap is refused too.
        PrcBrepWriter gap = Writer();
        gap.Vertex(BrepVertex.New(1, 0, 0)).Vertex(BrepVertex.Ref(1));
        byte[] gapBits = gap.Bits.ToArray();
        PrcCompressedBrepReader sparse = Reader(isoVertices: 3);
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(gapBits);
            sparse.ReadVertex(ref r);
            sparse.ReadVertex(ref r);
        }).Error);
    }

    [Fact]
    public void WhenAnIsoFaceOfALaterShellComesAfterAnAnaFaceTheTwoNumberingsDiffer()
    {
        // Storage order: iso A, ana B, iso C. A is index 0, C is index 1 (iso cache), B is index 2 + 0 (ana cache, after two iso vertices).
        PrcBrepWriter w = Writer();
        w.Vertex(BrepVertex.New(1, 0, 0)).Vertex(BrepVertex.New(2, 0, 0)).Vertex(BrepVertex.New(3, 0, 0));
        w.Vertex(BrepVertex.Ref(1)).Vertex(BrepVertex.Ref(2));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader(isoVertices: 2);

        Assert.Equal(0, reader.ReadVertex(ref r));      // A: iso 0
        reader.IsoFace = false;
        Assert.Equal(1, reader.ReadVertex(ref r));      // B: ana 0 (index 2)
        reader.IsoFace = true;
        Assert.Equal(2, reader.ReadVertex(ref r));      // C: iso 1
        Assert.Equal(2, reader.ReadVertex(ref r));      // index 1 → C
        Assert.Equal(1, reader.ReadVertex(ref r));      // index 2 → B
    }

    [Fact]
    public void ALineIsItsTwoVertices()
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.Line(BrepVertex.New(0, 0, 0), BrepVertex.New(1, 0, 0)));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        Assert.Equal(0, reader.ReadRefOrCurve(ref r));
        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepCurve curve = Assert.Single(body.Curves);
        Assert.Equal(PrcCurveKind.Line, curve.Kind);
        Assert.Equal(0, curve.Start);
        Assert.Equal(1, curve.End);
        Assert.Equal(2, body.Vertices.Length);
    }

    [Fact]
    public void AFullCircleKeepsItsCentreAndNormal()
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.FullCircle(BrepVertex.New(1, 0, 0), BrepVertex.Ref(0), new PrcVector(0, 0, 0), new PrcVector(0, 0, 1)));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        reader.ReadRefOrCurve(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepCurve curve = Assert.Single(body.Curves);
        Assert.Equal(PrcCurveKind.Circle, curve.Kind);
        Assert.Equal(0, curve.Start);
        Assert.Equal(0, curve.End);
        Assert.Equal(PrcBrepCurveFlags.Particular | PrcBrepCurveFlags.Full, (PrcBrepCurveFlags)curve.Flags);
        Assert.Equal(6, curve.DataCount);
        AssertNear(new PrcVector(0, 0, 1), new PrcVector(body.CurveData[curve.DataStart + 3], body.CurveData[curve.DataStart + 4], body.CurveData[curve.DataStart + 5]));
    }

    [Fact]
    public void AHalfCircleKeepsAPointInTheMiddleOfItsArc()
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.HalfCircle(BrepVertex.New(1, 0, 0), BrepVertex.New(-1, 0, 0), new PrcVector(0, 1, 0)));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        reader.ReadRefOrCurve(ref r);

        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepCurve curve = Assert.Single(body.Curves);
        Assert.Equal(PrcBrepCurveFlags.Particular, (PrcBrepCurveFlags)curve.Flags);
        Assert.Equal(3, curve.DataCount);
        AssertNear(new PrcVector(0, 1, 0), new PrcVector(body.CurveData[curve.DataStart], body.CurveData[curve.DataStart + 1], body.CurveData[curve.DataStart + 2]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnArcKeepsItsCentreAndWhetherItPassesAHalfTurn(bool overHalfTurn)
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.Arc(BrepVertex.New(1, 0, 0), BrepVertex.New(0, 1, 0), new PrcVector(0, 0, 0), overHalfTurn));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        reader.ReadRefOrCurve(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcBrepCurve curve = Assert.Single(reader.Result().Curves);
        Assert.Equal(overHalfTurn ? PrcBrepCurveFlags.OverHalfTurn : PrcBrepCurveFlags.None, (PrcBrepCurveFlags)curve.Flags);
        Assert.Equal(3, curve.DataCount);
    }

    [Fact]
    public void ACircleThatTrimsAnIsoNurbsFaceHasNeitherTypeNorEnds()
    {
        // compressed_iso_spline: the type token and the start/end data are left out (issue #582).
        PrcBrepWriter w = Writer();
        w.FullCircle(default, default, new PrcVector(1, 2, 3), new PrcVector(0, 0, 1), isoSpline: true);
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        reader.ReadIsoSplineCircle(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcBrepCurve curve = Assert.Single(reader.Result().Curves);
        Assert.Equal(-1, curve.Start);
        Assert.Equal(-1, curve.End);
        Assert.Empty(reader.Result().Vertices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHermiteCurveKeepsItsPointsAndTangentsCompressedOrNot(bool wide)
    {
        PrcVector[] points = [new(0.5, 0.25, 0), new(1.5, 1, 0.75)];
        PrcVector[] tangents = [new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(-1, 0, 0)];
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.Hermite(BrepVertex.New(0, 0, 0), BrepVertex.New(2, 2, 1), points, tangents, wide));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        reader.ReadRefOrCurve(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepCurve curve = Assert.Single(body.Curves);
        Assert.Equal(PrcCurveKind.HermiteSpline, curve.Kind);
        Assert.Equal(1 + 3 * 2 + 3 * 4, curve.DataCount);
        Assert.Equal(4, body.CurveData[curve.DataStart]);
        AssertNear(points[1] - points[0], new PrcVector(body.CurveData[curve.DataStart + 4], body.CurveData[curve.DataStart + 5], body.CurveData[curve.DataStart + 6]));
        AssertNear(tangents[3], new PrcVector(body.CurveData[curve.DataStart + 7 + 9], body.CurveData[curve.DataStart + 7 + 10], body.CurveData[curve.DataStart + 7 + 11]));
    }

    [Fact]
    public void ACompositeCurveKeepsItsMembersInOrderAndMayReferToACurveStoredBefore()
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.Line(BrepVertex.New(0, 0, 0), BrepVertex.New(1, 0, 0)));                   // curve 0
        w.NewCurve(c => c.Composite(BrepVertex.Ref(0), BrepVertex.New(2, 1, 0), closed: false,
            m => m.CurveRef(0),                                                                      // a curve stored before
            m => m.NewCurve(n => n.Line(BrepVertex.Ref(1), BrepVertex.New(2, 1, 0)))));              // a new one: curve 1; the composite is curve 2
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        reader.ReadRefOrCurve(ref r);
        int composite = reader.ReadRefOrCurve(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        Assert.Equal(3, body.Curves.Length);
        PrcBrepCurve curve = body.Curves[composite];
        Assert.Equal(PrcCurveKind.Composite, curve.Kind);
        Assert.Equal(2, curve.DataCount);
        Assert.Equal([0, 1], body.CompositeMembers.AsSpan(curve.DataStart, curve.DataCount).ToArray());
    }

    [Fact]
    public void ACurveReferenceToACurveNotYetStoredIsRefused()
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.Line(BrepVertex.New(0, 0, 0), BrepVertex.New(1, 0, 0))).CurveRef(4);
        byte[] bits = w.Bits.ToArray();
        PrcCompressedBrepReader reader = Reader();

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            reader.ReadRefOrCurve(ref r);
            reader.ReadRefOrCurve(ref r);
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    [Fact]
    public void CurvesOfIsoFacesAndOfAnaFacesAreTwoCachesToo()
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.Line(BrepVertex.New(0, 0, 0), BrepVertex.New(1, 0, 0)));                   // iso curve 0
        w.NewCurve(c => c.Line(BrepVertex.New(1, 0, 0), BrepVertex.New(1, 1, 0)));                   // ana curve: index 1 + 0 (one iso curve)
        w.CurveRef(1).CurveRef(0);
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader(isoEdges: 1);

        Assert.Equal(0, reader.ReadRefOrCurve(ref r));
        reader.IsoFace = false;
        Assert.Equal(1, reader.ReadRefOrCurve(ref r));
        Assert.Equal(1, reader.ReadRefOrCurve(ref r));
        Assert.Equal(0, reader.ReadRefOrCurve(ref r));
    }

    [Fact]
    public void AnEllipseIsRefusedAsUnsupported()
    {
        byte[] bits = new PrcBitWriter().Bit(true).CompressedEntityType(true, 12).Bits(0, 16).ToArray();
        PrcCompressedBrepReader reader = Reader();

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            reader.ReadRefOrCurve(ref r);
        });
        Assert.Equal(Scene3DError.Unsupported, e.Error);
    }

    [Fact]
    public void AFaceTypeWhereACurveIsExpectedIsRefused()
    {
        byte[] bits = new PrcBitWriter().Bit(true).CompressedEntityType(false, 3).Bits(0, 16).ToArray();
        PrcCompressedBrepReader reader = Reader();

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            reader.ReadRefOrCurve(ref r);
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    [Fact]
    public void AToleranceThatIsNoLengthRefusesTheBodyAtOnce()
    {
        // Review focus: a body whose tolerance is zero, negative or not a number cannot scale a compressed point.
        foreach (double tolerance in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => new PrcCompressedBrepReader(new PrcContext(8137, 1), tolerance, 5, 0, 0)).Error);
        }
    }

    [Fact]
    public void AHermiteCurveWithMorePointsThanTheDataHoldsIsRefusedBeforeAnythingIsAllocated()
    {
        // 15 bits for the number of points: 32767 points announced, a few bits left.
        byte[] bits = new PrcBitWriter().Bit(true).CompressedEntityType(true, 2).Bit(true).CompressedPoint(0, 0, 0, 0.0001).Bit(true).CompressedPoint(1, 0, 0, 0.0001)
            .Bits(15, 4).Bits(32767, 15).Bits(0, 12).ToArray();
        PrcCompressedBrepReader reader = Reader();

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            reader.ReadRefOrCurve(ref r);
        });
        Assert.Equal(Scene3DError.Truncated, e.Error);
    }

    // ---- faces, shells, bodies

    private static Action<PrcBrepWriter> Line(BrepVertex a, BrepVertex b) => c => c.NewCurve(l => l.Line(a, b));

    private static double[] Slice(double[] data, int start, int count) => data.AsSpan(start, count).ToArray();

    [Fact]
    public void AnIsoPlaneFaceKeepsItsNormalAndLeavesTheOtherTwoSidesToBeDeduced()
    {
        PrcBrepWriter w = Writer();
        w.IsoFace(2, PrcBrepWriter.PlaneNormal(0.5, 0.25, true), orientationWithShell: true, orientationLoop: true,
            Line(BrepVertex.New(0, 0, 0), BrepVertex.New(1, 0, 0)), Line(BrepVertex.Ref(1), BrepVertex.New(1, 1, 0)),
            third: null, fourth: null, common: BrepVertex.New(0, 1, 0));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepFace f = body.Faces[face];
        Assert.Equal(PrcFaceKind.IsoPlane, f.Kind);
        Assert.True(f.OrientationWithShell);
        Assert.True(f.Trimmed);
        Assert.Equal([0.5, 0.25, 1.0], Slice(body.SurfaceData, f.SurfaceDataStart, f.SurfaceDataCount));
        PrcBrepLoop loop = Assert.Single(body.Loops.AsSpan(f.FirstLoop, f.LoopCount).ToArray());
        Assert.True(loop.OrientationWithSurface);
        Assert.Equal([0, 1, 2, 3], body.Coedges.AsSpan(loop.FirstCoedge, loop.CoedgeCount).ToArray());
        // The two sides that are not stored are deduced and numbered all the same: curves of the face (0), sides 2 and 3.
        Assert.Equal(PrcCurveKind.Implicit, body.Curves[2].Kind);
        Assert.Equal((face, 2), (body.Curves[2].DataStart, body.Curves[2].DataCount));
        Assert.Equal(PrcCurveKind.Implicit, body.Curves[3].Kind);
        Assert.Equal((face, 3), (body.Curves[3].DataStart, body.Curves[3].DataCount));
        Assert.Equal(3, body.FaceVertices[f.FirstVertex]);      // the vertex the deduced sides share
        AssertNear(new PrcVector(0, 1, 0), body.Vertices[3]);
    }

    [Fact]
    public void AnIsoFaceMayReferToItsThirdAndFourthCurves()
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.Line(BrepVertex.New(0, 0, 0), BrepVertex.New(1, 0, 0)));
        w.NewCurve(c => c.Line(BrepVertex.New(1, 0, 0), BrepVertex.New(1, 1, 0)));
        w.IsoFace(3, null, orientationWithShell: false, orientationLoop: false, c => c.CurveRef(0), c => c.CurveRef(1), third: 0, fourth: 1, common: null);
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();
        reader.ReadRefOrCurve(ref r);
        reader.ReadRefOrCurve(ref r);

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepFace f = body.Faces[face];
        Assert.Equal(PrcFaceKind.IsoCylinder, f.Kind);
        Assert.False(f.OrientationWithShell);
        Assert.Equal(0, f.SurfaceDataCount);
        PrcBrepLoop loop = body.Loops[f.FirstLoop];
        Assert.False(loop.OrientationWithSurface);
        Assert.Equal([0, 1, 0, 1], body.Coedges.AsSpan(loop.FirstCoedge, 4).ToArray());
        Assert.Equal(2, body.Curves.Length);       // nothing was deduced: both sides were referred to
        Assert.Equal(0, f.VertexCount);
    }

    [Fact]
    public void AnAnaFaceKeepsEachLoopUpToTheSentinelThatEndsIt()
    {
        PrcBrepWriter w = Writer();
        var outer = new PrcBrepWriter.AnaLoop(true,
            Line(BrepVertex.New(0, 0, 0), BrepVertex.New(4, 0, 0)), Line(BrepVertex.Ref(1), BrepVertex.New(4, 4, 0)), Line(BrepVertex.Ref(2), BrepVertex.Ref(0)));
        var hole = new PrcBrepWriter.AnaLoop(false,
            Line(BrepVertex.New(1, 1, 0), BrepVertex.New(2, 1, 0)), Line(BrepVertex.Ref(4), BrepVertex.New(2, 2, 0)), Line(BrepVertex.Ref(5), BrepVertex.Ref(3)));
        w.AnaFace(8, PrcBrepWriter.PlaneNormal(0, 0, true), true, [outer, hole], null, null);
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader(isoVertices: 0, isoEdges: 0);

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepFace f = body.Faces[face];
        Assert.Equal(PrcFaceKind.AnaPlane, f.Kind);
        Assert.True(f.Trimmed);
        Assert.False(f.HasPointOnTorus);
        Assert.Equal(2, f.LoopCount);
        PrcBrepLoop first = body.Loops[f.FirstLoop], second = body.Loops[f.FirstLoop + 1];
        Assert.True(first.OrientationWithSurface);
        Assert.False(second.OrientationWithSurface);
        Assert.Equal(3, first.CoedgeCount);
        Assert.Equal(3, second.CoedgeCount);
        Assert.Equal(6, body.Curves.Length);
        Assert.Equal(6, body.Vertices.Length);
    }

    [Fact]
    public void ALoopMayReferToACurveStoredBefore()
    {
        PrcBrepWriter w = Writer();
        w.NewCurve(c => c.Line(BrepVertex.New(0, 0, 0), BrepVertex.New(1, 0, 0)));
        w.AnaFace(8, PrcBrepWriter.PlaneNormal(0, 0, true), true,
            [new PrcBrepWriter.AnaLoop(true, c => c.CurveRef(0), Line(BrepVertex.Ref(1), BrepVertex.New(1, 1, 0)), Line(BrepVertex.Ref(2), BrepVertex.Ref(0)))], null, null);
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader(isoVertices: 2, isoEdges: 1);
        reader.ReadRefOrCurve(ref r);

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepLoop loop = body.Loops[body.Faces[face].FirstLoop];
        Assert.Equal([0, 1, 2], body.Coedges.AsSpan(loop.FirstCoedge, loop.CoedgeCount).ToArray());
    }

    [Fact]
    public void AnAnaTorusWhoseLoopsAreAllVertexLoopsKeepsItsPointOnTheTorus()
    {
        PrcBrepWriter w = Writer();
        w.AnaFace(10, null, true,
            [new PrcBrepWriter.AnaLoop(true, Line(BrepVertex.New(1, 0, 0), BrepVertex.Ref(0))), new PrcBrepWriter.AnaLoop(false, Line(BrepVertex.New(3, 0, 0), BrepVertex.Ref(1)))],
            pointOnTorus: new PrcVector(2, 0, 1),
            after: f => { f.Point(new PrcVector(0, 0, 0)); f.Point(new PrcVector(2, 0, 0)); f.Bits.Double(0).Double(1).Double(0); });
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader(isoVertices: 0, isoEdges: 0);

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepFace f = body.Faces[face];
        Assert.Equal(PrcFaceKind.AnaTorus, f.Kind);
        Assert.True(f.HasPointOnTorus);
        AssertNear(new PrcVector(2, 0, 1), f.PointOnTorus);
        Assert.Equal([0.0, 0, 0, 2, 0, 0, 0, 1, 0], Slice(body.SurfaceData, f.SurfaceDataStart, f.SurfaceDataCount));
    }

    [Fact]
    public void AnAnaTorusWithARealLoopHasNoPointOnTheTorus()
    {
        PrcBrepWriter w = Writer();
        w.AnaFace(10, null, true,
            [new PrcBrepWriter.AnaLoop(true, Line(BrepVertex.New(1, 0, 0), BrepVertex.New(0, 1, 0)), Line(BrepVertex.Ref(1), BrepVertex.Ref(0)))],
            pointOnTorus: null,
            after: f => { f.Point(new PrcVector(0, 0, 0)); f.Point(new PrcVector(2, 0, 0)); f.Bits.Double(0).Double(1).Double(0); });
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader(isoVertices: 0, isoEdges: 0);

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        Assert.False(reader.Result().Faces[face].HasPointOnTorus);
    }

    [Fact]
    public void AnUntrimmedAnaFaceHasNoLoops()
    {
        PrcBrepWriter w = Writer();
        w.AnaFace(11, null, false, null, null, f => f.Point(new PrcVector(1, 2, 3)));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepFace f = body.Faces[face];
        Assert.Equal(PrcFaceKind.AnaSphere, f.Kind);
        Assert.False(f.Trimmed);
        Assert.Equal(0, f.LoopCount);
        Assert.Equal([1.0, 2, 3], Slice(body.SurfaceData, f.SurfaceDataStart, f.SurfaceDataCount));
    }

    [Fact]
    public void AnAnaCylinderAndAnAnaConeKeepTheirAxis()
    {
        PrcBrepWriter w = Writer();
        w.AnaFace(9, null, true, null, null, f => f.Point(new PrcVector(1, 0, 0)).Point(new PrcVector(0, 0, 1)));
        w.AnaFace(12, null, true, null, null, f => f.Point(new PrcVector(0, 0, 0)).Point(new PrcVector(0, 0, 3)));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        int cylinder = reader.ReadFace(ref r);
        int cone = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        Assert.Equal(PrcFaceKind.AnaCylinder, body.Faces[cylinder].Kind);
        Assert.Equal([1.0, 0, 0, 0, 0, 1], Slice(body.SurfaceData, body.Faces[cylinder].SurfaceDataStart, body.Faces[cylinder].SurfaceDataCount));
        Assert.Equal(PrcFaceKind.AnaCone, body.Faces[cone].Kind);
        Assert.Equal([0.0, 0, 0, 0, 0, 3], Slice(body.SurfaceData, body.Faces[cone].SurfaceDataStart, body.Faces[cone].SurfaceDataCount));
    }

    [Fact]
    public void AnIsoTorusKeepsWhetherItsFirstCircleIsTheMajorOne()
    {
        PrcBrepWriter w = Writer();
        w.IsoFace(4, f => f.Bits.Bit(true), true, true,
            c => c.NewCurve(l => l.FullCircle(BrepVertex.New(2, 0, 0), BrepVertex.Ref(0), new PrcVector(0, 0, 0), new PrcVector(0, 0, 1))),
            c => c.NewCurve(l => l.FullCircle(BrepVertex.New(3, 0, 0), BrepVertex.Ref(1), new PrcVector(0, 0, 0), new PrcVector(0, 0, 1))),
            null, null, BrepVertex.New(2, 0, 1));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        Assert.Equal(PrcFaceKind.IsoTorus, body.Faces[face].Kind);
        Assert.Equal([1.0], Slice(body.SurfaceData, body.Faces[face].SurfaceDataStart, body.Faces[face].SurfaceDataCount));
    }

    [Fact]
    public void AnAnaGenericFaceKeepsTheSurfaceItLiesOn()
    {
        PrcBrepWriter w = Writer();
        w.AnaFace(14, null, true, null, null, PrcBrepWriter.GenericCylinder(2.5));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepFace f = body.Faces[face];
        Assert.Equal(PrcFaceKind.AnaGenericFace, f.Kind);
        PrcGenericSurface surface = body.GenericSurfaces[f.Surface];
        Assert.Equal(PrcGenericSurfaceKind.Cylinder, surface.Kind);
        Assert.Equal(2.5, surface.Radius);
        Assert.Equal(Matrix4x4d.Identity, surface.Frame);
    }

    [Fact]
    public void AFaceTypeThatIsALoopMarkerIsRefusedWhereAFaceIsExpected()
    {
        byte[] bits = new PrcBitWriter().CompressedEntityType(false, 1).Bits(0, 32).ToArray();
        PrcCompressedBrepReader reader = Reader();

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            reader.ReadFace(ref r);
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    private static (Action<PrcBrepWriter> Face, bool Iso) IsoCylinderFace(double x) =>
        (w => w.IsoFace(3, null, true, true, Line(BrepVertex.New(x, 0, 0), BrepVertex.New(x, 0, 1)), Line(BrepVertex.New(x + 1, 0, 0), BrepVertex.New(x + 1, 0, 1)), null, null, BrepVertex.New(x, 1, 0)), true);

    private static (Action<PrcBrepWriter> Face, bool Iso) AnaSphereFace(double x) =>
        (w => w.AnaFace(11, null, true, null, null, f => f.Point(new PrcVector(x, 0, 0))), false);

    [Fact]
    public void AShellOfOneFaceHasNoCount()
    {
        PrcBrepWriter w = Writer();
        w.Shell(AnaSphereFace(1));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        int faces = reader.ReadShell(ref r);

        Assert.Equal(1, faces);
        Assert.Equal(w.Bits.BitLength, r.BitPosition);
    }

    [Fact]
    public void AShellKeepsItsFacesInTheOrderTheyAreStoredIsoFacesFirst()
    {
        PrcBrepWriter w = Writer();
        w.Shell(AnaSphereFace(1), IsoCylinderFace(5), AnaSphereFace(2));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        int faces = reader.ReadShell(ref r);

        Assert.Equal(3, faces);
        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        Assert.Equal([PrcFaceKind.IsoCylinder, PrcFaceKind.AnaSphere, PrcFaceKind.AnaSphere], body.Faces.Select(f => f.Kind).ToArray());
        Assert.All(body.Faces, f => Assert.Equal(0, f.Shell));
    }

    [Fact]
    public void ABodyOfOneShellIsReadToItsLastBaseTopology()
    {
        PrcBrepWriter w = Writer();
        w.BodyStart(isoVertices: 0, isoEdges: 0, singleConnex: true).Shell(IsoCylinderFace(0), AnaSphereFace(3));
        PrcBitWriter bits = w.EndBody();
        var r = new PrcBitReader(bits.ToArray());

        PrcTopoBrepDataCompress body = PrcReaders.ReadTopoBrepDataCompress(ref r, new PrcContext(8137, 1));

        Assert.Equal(bits.BitLength, r.BitPosition);
        Assert.Equal(Tolerance, body.Tolerance);
        Assert.Equal(2, body.Faces.Length);
    }

    [Fact]
    public void ABodyOfSeveralConnexReadsEveryShellOfEveryOne()
    {
        PrcBrepWriter w = Writer();
        w.BodyStart(isoVertices: 0, isoEdges: 0, singleConnex: false);
        w.Bits.NumberOfBitsThenUnsigned(2);                                  // two connex
        w.Bits.NumberOfBitsThenUnsigned(1);                                  //   the first has one shell
        w.Shell(AnaSphereFace(1));
        w.Bits.NumberOfBitsThenUnsigned(2);                                  //   the second has two
        w.Shell(AnaSphereFace(2), AnaSphereFace(3));
        w.Shell(IsoCylinderFace(9));
        PrcBitWriter bits = w.EndBody();
        var r = new PrcBitReader(bits.ToArray());

        PrcTopoBrepDataCompress body = PrcReaders.ReadTopoBrepDataCompress(ref r, new PrcContext(8137, 1));

        Assert.Equal(bits.BitLength, r.BitPosition);
        Assert.Equal(4, body.Faces.Length);
        Assert.Equal([0, 1, 1, 2], body.Faces.Select(f => f.Shell).ToArray());
    }

    [Fact]
    public void ABodyWithATypeTokenOfAnotherKindIsRefused()
    {
        byte[] bits = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TOPO_BrepData).Bits(0, 64).ToArray();

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            PrcReaders.ReadTopoBrepDataCompress(ref r, new PrcContext(8137, 1));
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }
}