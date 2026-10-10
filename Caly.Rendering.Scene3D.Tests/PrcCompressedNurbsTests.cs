using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>Compressed NURBS surfaces (draft 7.9.20.6) and the faces that carry them.</summary>
public sealed class PrcCompressedNurbsTests
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

    /// <summary>A bilinear patch: degree 1 each way, two control points each way, uniform knots; the interior point as given.</summary>
    private static PrcBrepWriter.NurbsSpec Bilinear(PrcBrepWriter.InteriorPoint interior, PrcVector stepV, PrcVector stepU) => new(
        1, 1, [2, 2], [2, 2], new PrcVector(0, 0, 0), [stepV], [stepU], [interior], PrcBrepWriter.KnotSpec.Uniform, PrcBrepWriter.KnotSpec.Uniform);

    private static void AssertKnots(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], Precision);
        }
    }

    private static PrcNurbs Read(PrcBrepWriter.NurbsSpec spec, out long bits, out long consumed)
    {
        PrcBrepWriter w = Writer();
        w.CompressedNurbs(spec);
        bits = w.Bits.BitLength;
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcNurbs n = Reader().ReadCompressedNurbs(ref r);
        consumed = r.BitPosition;
        return n;
    }

    [Fact]
    public void ABilinearPatchIsReadWithItsKnotsAndItsControlPoints()
    {
        // The first row (v) and column (u) are steps from the corner; the interior point is predicted as the fourth corner of the
        // parallelogram and corrected by what is stored, as plain coordinates (type 3: x, y, z; the draft's frame does not fit the corpus).
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(3, 0, 0, 2), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0));

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        Assert.Equal(1, n.DegreeU);
        Assert.Equal(1, n.DegreeV);
        Assert.Equal(2, n.CountU);
        Assert.Equal(2, n.CountV);
        AssertKnots([0.0, 0, 1, 1], n.KnotsU);
        AssertKnots([0.0, 0, 1, 1], n.KnotsV);
        Assert.Null(n.Weights);
        // index = u * CountV + v
        AssertNear(new PrcVector(0, 0, 0), n.ControlPoints[0]);
        AssertNear(new PrcVector(1, 0, 0), n.ControlPoints[1]);
        AssertNear(new PrcVector(0, 1, 0), n.ControlPoints[2]);
        AssertNear(new PrcVector(1, 1, 2), n.ControlPoints[3]);
    }

    [Fact]
    public void AnInteriorPointOfType2AddsItsXAndYInTheLocalFrame()
    {
        // The local frame (draft 7.9.20.6.3.1): U the step along u (here (0, 1, 0)), V along v ((1, 0, 0)), N = U × V = (0, 0, -1);
        // x along U, y along N × U = (1, 0, 0).
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(2, 0.5, 0.25), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0));

        PrcNurbs n = Read(spec, out _, out _);

        AssertNear(new PrcVector(1.25, 1.5, 0), n.ControlPoints[3]);
    }

    [Fact]
    public void AnInteriorPointOfType1AddsItsZAlongTheNormal()
    {
        // z along N = U × V = (0, 1, 0) × (1, 0, 0) = (0, 0, -1).
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(1, Z: 0.5), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0));

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        AssertNear(new PrcVector(1, 1, -0.5), n.ControlPoints[3]);
    }

    [Fact]
    public void AnInteriorPointOfType0IsThePredictedOne()
    {
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(0), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0));

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        AssertNear(new PrcVector(1, 1, 0), n.ControlPoints[3]);
    }

    [Fact]
    public void WhenTheTwoStepsAreTheSameLineTheFrameIsLostAndTheCoordinatesAreGlobal()
    {
        // The step along u is nothing: U has no length (< 1e-12), so type 3 is used and x, y, z are the plain coordinates.
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(3, 0.5, 0.25, 0.25), new PrcVector(1, 0, 0), new PrcVector(0, 0, 0));

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        AssertNear(new PrcVector(1.5, 0.25, 0.25), n.ControlPoints[3]);
    }

    [Fact]
    public void ABicubicDirectionWithAnInteriorKnotHasItsMultiplicitiesExpanded()
    {
        // Degree 3 in u with multiplicities 4, 1, 4: nine knots, 9 - 3 - 1 = 5 control points; degree 1 in v with two.
        var interior = new PrcBrepWriter.InteriorPoint[4];
        Array.Fill(interior, new PrcBrepWriter.InteriorPoint(0));
        var spec = new PrcBrepWriter.NurbsSpec(
            3, 1, [4, 1, 4], [2, 2], new PrcVector(0, 0, 0), [new PrcVector(0, 1, 0)], [new PrcVector(1, 0, 0), new PrcVector(1, 0, 0), new PrcVector(1, 0, 0), new PrcVector(1, 0, 0)],
            interior, PrcBrepWriter.KnotSpec.Uniform, PrcBrepWriter.KnotSpec.Uniform);

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        Assert.Equal(5, n.CountU);
        Assert.Equal(2, n.CountV);
        AssertKnots([0.0, 0, 0, 0, 0.5, 1, 1, 1, 1], n.KnotsU);
        Assert.Equal(10, n.ControlPoints.Length);
        AssertNear(new PrcVector(4, 0, 0), n.ControlPoints[8]);           // u = 4, v = 0
        AssertNear(new PrcVector(4, 1, 0), n.ControlPoints[9]);           // predicted: the patch is flat
    }

    [Fact]
    public void AMultiplicityThatRepeatsTheOneBeforeIsNotStored()
    {
        // 4, 1, 1, 4 stores the 1 once; the knots, uniform over four distinct values: 0, 1/3, 2/3, 1.
        var interior = new PrcBrepWriter.InteriorPoint[5];
        Array.Fill(interior, new PrcBrepWriter.InteriorPoint(0));
        var steps = Enumerable.Repeat(new PrcVector(1, 0, 0), 5).ToArray();
        var spec = new PrcBrepWriter.NurbsSpec(
            3, 1, [4, 1, 1, 4], [2, 2], new PrcVector(0, 0, 0), [new PrcVector(0, 1, 0)], steps, interior, PrcBrepWriter.KnotSpec.Uniform, PrcBrepWriter.KnotSpec.Uniform);

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        Assert.Equal(6, n.CountU);
        AssertKnots([0.0, 0, 0, 0, 1.0 / 3, 2.0 / 3, 1, 1, 1, 1], n.KnotsU);
    }

    [Fact]
    public void InteriorKnotsAreStoredAsDifferencesFromTheOneBefore()
    {
        // Distinct knots 0, a, a + b, 1 with a = b = 0.25: the stored values are the steps. (Candidate reading of the draft, 7.9.20.6.4.1.)
        var interior = new PrcBrepWriter.InteriorPoint[5];
        Array.Fill(interior, new PrcBrepWriter.InteriorPoint(0));
        var steps = Enumerable.Repeat(new PrcVector(1, 0, 0), 5).ToArray();
        var spec = new PrcBrepWriter.NurbsSpec(
            3, 1, [4, 1, 1, 4], [2, 2], new PrcVector(0, 0, 0), [new PrcVector(0, 1, 0)], steps, interior, PrcBrepWriter.KnotSpec.Interior(8, 0.25, 0.25), PrcBrepWriter.KnotSpec.Uniform);

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        AssertKnots([0.0, 0, 0, 0, 0.25, 0.5, 1, 1, 1, 1], n.KnotsU);
    }

    [Fact]
    public void APseudoUniformKnotVectorGivesTheFirstAndLastIntervalsAndSpacesTheRestEvenly()
    {
        // Distinct knots: 0, 0.125, 0.5, 0.875, 1: first interval 0.125, last 0.125, the inner one evenly between.
        var spec = new PrcBrepWriter.NurbsSpec(
            1, 1, [2, 1, 1, 1, 2], [2, 2], new PrcVector(0, 0, 0), [new PrcVector(0, 1, 0)], Enumerable.Repeat(new PrcVector(1, 0, 0), 4).ToArray(),
            Enumerable.Repeat(new PrcBrepWriter.InteriorPoint(0), 4).ToArray(), PrcBrepWriter.KnotSpec.Pseudo(8, 0.125, 0.125), PrcBrepWriter.KnotSpec.Uniform);

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        AssertKnots([0.0, 0, 0.125, 0.5, 0.875, 1, 1], n.KnotsU);
    }
    [Fact]
    public void ARationalPatchKeepsItsWeights()
    {
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(0), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0)) with
        {
            Weights = [1.0, 0.5, 0.75, 1.25],
            WeightBits = 12,
            WeightTolerance = 0.25,
        };

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        Assert.Equal([1.0, 0.5, 0.75, 1.25], n.Weights!);
    }

    [Fact]
    public void WeightsOfMoreThanThirtyBitsAreStoredAsDoubles()
    {
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(0), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0)) with
        {
            Weights = [1.0, 0.123456789, 2.5, 3.0],
            WeightBits = 31,
        };

        PrcNurbs n = Read(spec, out long bits, out long consumed);

        Assert.Equal(bits, consumed);
        Assert.Equal([1.0, 0.123456789, 2.5, 3.0], n.Weights!);
    }

    [Fact]
    public void ASurfaceThatClaimsMoreControlPointsThanTheDataCouldHoldIsRefusedBeforeAnythingIsAllocated()
    {
        // Review focus: 65535 multiplicities of 31 in u and in v announce a net of billions of points in a handful of bytes.
        PrcBrepWriter w = Writer();
        w.Bits.Bits(30, 5).Bits(30, 5);
        foreach (int degree in new[] { 30, 30 })
        {
            w.Bits.Bits(65535, 16);
            w.Bits.Bit(false).Bits(31, 5);
            for (int i = 1; i < 65535; i++)
            {
                w.Bits.Bit(true);
            }
        }

        w.Bits.Bits(0, 64);
        byte[] bits = w.Bits.ToArray();
        PrcCompressedBrepReader reader = Reader();

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            reader.ReadCompressedNurbs(ref r);
        });
        Assert.Contains(e.Error, new[] { Scene3DError.Truncated, Scene3DError.LimitExceeded, Scene3DError.InvalidValue });
    }

    [Fact]
    public void AnAnaNurbsFaceKeepsItsSurface()
    {
        PrcBrepWriter w = Writer();
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(0), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0));
        w.AnaFace(13, null, true, null, null, f => f.CompressedNurbs(spec));
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepFace f = body.Faces[face];
        Assert.Equal(PrcFaceKind.AnaNurbs, f.Kind);
        Assert.Equal(0, f.Surface);
        Assert.Equal(4, body.Nurbs[0].ControlPoints.Length);
    }

    [Fact]
    public void AnIsoNurbsFaceKeepsItsFourTrimCurvesAndTheLoopVerticesThatAreWritten()
    {
        PrcBrepWriter w = Writer();
        PrcBrepWriter.NurbsSpec spec = Bilinear(new PrcBrepWriter.InteriorPoint(0), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0));
        w.NewCurve(c => c.Line(BrepVertex.New(9, 9, 9), BrepVertex.New(8, 8, 8)));        // curve 0, for the fourth side to refer to
        PrcBrepWriter.IsoTrim[] trims = [new PrcBrepWriter.IsoTrim.Circle(new PrcVector(0, 0, 0), new PrcVector(0, 0, 1)), new PrcBrepWriter.IsoTrim.ImplicitLine(), new PrcBrepWriter.IsoTrim.IsoBoundary(), new PrcBrepWriter.IsoTrim.Referenced(0)];
        // Loop vertex 3 needs sides 3 and 0 not referred to (3 is), 0 needs 0 and 1 (yes), 1 needs 1 and 2 (yes), 2 needs 2 and 3 (3 is referred to).
        w.IsoNurbsFace(true, false, (true, false, true), spec, trims, [null, BrepVertex.New(1, 0, 0), BrepVertex.New(1, 1, 0), null]);
        var r = new PrcBitReader(w.Bits.ToArray());
        PrcCompressedBrepReader reader = Reader();
        reader.ReadRefOrCurve(ref r);

        int face = reader.ReadFace(ref r);

        Assert.Equal(w.Bits.BitLength, r.BitPosition);
        PrcTopoBrepDataCompress body = reader.Result();
        PrcBrepFace f = body.Faces[face];
        Assert.Equal(PrcFaceKind.IsoNurbs, f.Kind);
        Assert.True(f.OrientationWithShell);
        Assert.Equal([1.0, 0, 1], body.SurfaceData.AsSpan(f.SurfaceDataStart, f.SurfaceDataCount).ToArray());
        PrcBrepLoop loop = body.Loops[f.FirstLoop];
        Assert.False(loop.OrientationWithSurface);
        // Every side that was not referred to is a new curve, stored or deduced: the circle (1), the line between loop vertices (2), the boundary (3).
        int[] sides = body.Coedges.AsSpan(loop.FirstCoedge, 4).ToArray();
        Assert.Equal([1, 2, 3, 0], sides);
        Assert.Equal(PrcCurveKind.Circle, body.Curves[1].Kind);
        Assert.Equal(-1, body.Curves[1].Start);
        Assert.Equal(PrcCurveKind.Implicit, body.Curves[2].Kind);
        Assert.Equal(PrcCurveKind.IsoBoundary, body.Curves[3].Kind);
        Assert.Equal((face, 2), (body.Curves[3].DataStart, body.Curves[3].DataCount));
        int[] vertices = body.FaceVertices.AsSpan(f.FirstVertex, f.VertexCount).ToArray();
        Assert.Equal(4, vertices.Length);
        Assert.Equal(-1, vertices[0]);
        Assert.NotEqual(-1, vertices[1]);
        Assert.NotEqual(-1, vertices[2]);
        Assert.Equal(-1, vertices[3]);
        AssertNear(new PrcVector(1, 1, 0), body.Vertices[vertices[2]]);
    }
}
