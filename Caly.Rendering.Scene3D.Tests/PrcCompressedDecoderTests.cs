using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The decoder of compressed tessellations on tessellations written out by hand: each test fixes one rule
/// of <c>PrcCompressedDecoder</c> (seeds, the frame of a new point, the made frame after a flat triangle,
/// the neighbours' sides, stored normals and references to them).
/// </summary>
public class PrcCompressedDecoderTests
{
    private const double Tolerance = 0.5;

    internal static PrcBits Bits(string bits)
    {
        var words = new uint[(bits.Length + 31) / 32];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
            {
                words[i >> 5] |= 0x80000000u >> (i & 31);
            }
        }

        return new PrcBits(words, bits.Length);
    }

    /// <summary>A tessellation with calculated normals: the neighbour flags, the point flags, the references, the stored triples and the orientation bits.</summary>
    internal static PrcTess3DCompressed Calculated(string status, string flags, int[] references, int[] points, string reversed) => new()
    {
        IsCalculated = false,
        HasFaces = false,
        Tolerance = Tolerance,
        HasOrigin = true,
        OriginX = 10,
        OriginY = 20,
        OriginZ = 30,
        Points = points,
        EdgeStatus = status.Select(c => (byte)(c - '0')).ToArray(),
        TriangleFaces = new int[status.Length],
        FaceCount = 1,
        PointIsReference = Bits(flags),
        PointReferences = references,
        MustRecalculateNormals = true,
        NormalIsReversed = Bits(reversed),
        LineAttributes = [0],
    };

    private static double[] Point(PrcCompressedGeometry geometry, int index) => geometry.Points.AsSpan(index * 3, 3).ToArray();

    // Within a millionth: the standard's lengths are roots of sums kept in single precision, so its
    // unit vectors are a few parts in a hundred million off.
    private static void Near(double[] expected, double[] actual)
    {
        for (int i = 0; i < 3; i++)
        {
            Assert.InRange(actual[i], expected[i] - 1e-6, expected[i] + 1e-6);
        }
    }

    [Fact]
    public void ASeedIsThreePointsFromTheOriginTheFirstPointAndTheMiddleOfBoth()
    {
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Calculated("0", "000", [], [2, 0, 0, 4, 0, 0, 0, 6, 0], "0"), TestContext.Current.CancellationToken);

        Assert.Equal(3, geometry.PointCount);
        Near([11, 20, 30], Point(geometry, 0));
        Near([13, 20, 30], Point(geometry, 1));
        Near([12, 23, 30], Point(geometry, 2));
    }

    [Fact]
    public void ATriangleThatIsNotReversedIsWoundFirstThirdSecond()
    {
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Calculated("0", "000", [], [2, 0, 0, 4, 0, 0, 0, 6, 0], "0"), TestContext.Current.CancellationToken);
        using PrcCompressedGeometry reversed = PrcCompressedDecoder.Decode(Calculated("0", "000", [], [2, 0, 0, 4, 0, 0, 0, 6, 0], "1"), TestContext.Current.CancellationToken);

        Assert.Equal([0, 2, 1], geometry.Corners.AsSpan(0, 3).ToArray());
        Assert.Equal([0, 1, 2], reversed.Corners.AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void ANewPointIsStoredInTheFrameOfTheEdgeItIsEnteredThrough()
    {
        // Seed (0,0,0) (4,0,0) (2,3,0) from the origin. Not reversed: its left neighbour hangs on the edge
        // (first, third) = (0, 2), whose far point is 1. Frame: O = (1,1.5,0); X from point 2 to point 0;
        // Y towards the far point; Z = X ^ Y.
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Calculated("20", "0000", [], [0, 0, 0, 8, 0, 0, 0, 6, 0, 0, -4, 2], "00"), TestContext.Current.CancellationToken);

        double length = Math.Sqrt(13);
        double[] x = [-2 / length, -3 / length, 0];
        double[] y = [3 / length, -2 / length, 0];
        double[] z = [0, 0, x[0] * y[1] - x[1] * y[0]];
        double[] expected = [10 + 1 - 2 * y[0] + 1 * z[0], 20 + 1.5 - 2 * y[1] + 1 * z[1], 30 - 2 * y[2] + 1 * z[2]];
        Near(expected, Point(geometry, 3));

        // The new triangle is (0, 2, 3): the entering edge in the order of its points, then the new point.
        Assert.Equal(new HashSet<int> { 0, 2, 3 }, geometry.Corners.AsSpan(3, 3).ToArray().ToHashSet());
    }

    [Fact]
    public void AReversedTriangleHangsItsLeftNeighbourOnItsSecondPoint()
    {
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Calculated("20", "0000", [], [0, 0, 0, 8, 0, 0, 0, 6, 0, 0, -4, 0], "10"), TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<int> { 1, 2, 3 }, geometry.Corners.AsSpan(3, 3).ToArray().ToHashSet());
    }

    [Fact]
    public void TheRightNeighbourWaitsWhileTheLeftOneIsTreated()
    {
        // Both neighbours of the seed: the left one (on edge 0-2) is the second triangle, the right one (on edge 1-2) the third.
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Calculated("300", "00000", [], [0, 0, 0, 8, 0, 0, 0, 6, 0, 0, -4, 0, 0, -4, 0], "000"), TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<int> { 0, 2, 3 }, geometry.Corners.AsSpan(3, 3).ToArray().ToHashSet());
        Assert.Equal(new HashSet<int> { 1, 2, 4 }, geometry.Corners.AsSpan(6, 3).ToArray().ToHashSet());
    }

    [Fact]
    public void AReferenceNamesAPointAlreadyMade()
    {
        // Two triangles on the seed's edges, the second closing onto the first one's new point.
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Calculated("300", "00001", [3], [0, 0, 0, 8, 0, 0, 0, 6, 0, 0, -4, 0], "000"), TestContext.Current.CancellationToken);

        Assert.Equal(4, geometry.PointCount);
        Assert.Equal(new HashSet<int> { 1, 2, 3 }, geometry.Corners.AsSpan(6, 3).ToArray().ToHashSet());
    }

    [Fact]
    public void AfterAFlatTriangleTheAxesAreMadeAndKeptOnTheSideOfItsThirdPoint()
    {
        // Seed (0,0,0) (4,0,0) and a third point ON their line at (2,0,0): flat. Its left neighbour
        // (edge 0-2, far point 1) cannot take its plane from it: X = (-1,0,0), and MakeOrthoRep gives
        // Y = (0,1,0), Z = (0,0,-1). The far point is at X only, so nothing is turned.
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Calculated("20", "0000", [], [0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 4, 6], "00"), TestContext.Current.CancellationToken);

        Near([10 + 1, 20 + 2, 30 - 3], Point(geometry, 3));
        Assert.Equal(1, geometry.MadeFrames);
    }

    [Fact]
    public void TheMadeAxesTakeTheirYFromZAndTheEdgeBeforeTheSideIsAsked()
    {
        // The same flat seed, along a line that is not an axis. MakeOrthoRep works on its own copy of X,
        // made a unit vector once more, and that copy is not X to the last bit when the squares of X do
        // not add up to one in single precision; the writer takes Z from it and then Y = Z ^ X with the
        // edge's own X, as for any other edge, and only then asks on which side the far point is. The
        // difference is in the last bits, and a tube of long thin triangles makes shards of it.
        const double tolerance = 0.3;
        int[] second = [1, 1, 2];
        PrcVector origin = new(10, 20, 30);
        PrcVector v0 = origin, v1 = v0 + (new PrcVector(second[0], second[1], second[2]) * tolerance), v2 = (v0 + v1) * 0.5;
        PrcVector o = (v0 + v2) * 0.5, x = v0 - v2;
        Assert.True(PrcBasis.TryUnitize(ref x));
        Assert.True(PrcBasis.TryMakeOrthoRep(x, out PrcVector madeY, out PrcVector z));
        PrcVector y = z.Cross(x);
        Assert.True(PrcBasis.TryUnitize(ref y));
        Assert.NotEqual(madeY, y);
        if ((v1 - o).Dot(y) < 0)
        {
            y = -y;
            z = -z;
        }

        PrcVector expected = o + (x * (2 * tolerance)) + (y * (9 * tolerance)) + (z * (6 * tolerance));
        PrcTess3DCompressed tess = Calculated("20", "0000", [], [0, 0, 0, second[0], second[1], second[2], 0, 0, 0, 2, 9, 6], "00");
        tess.Tolerance = tolerance;

        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);

        Assert.Equal(1, geometry.MadeFrames);
        Assert.Equal([expected.X, expected.Y, expected.Z], Point(geometry, 3));
    }

    [Fact]
    public void WhatIsLeftOverRefusesTheTessellation()
    {
        // One point flag too many.
        Scene3DException error = Assert.Throws<Scene3DException>(() => PrcCompressedDecoder.Decode(Calculated("0", "0000", [], [2, 0, 0, 4, 0, 0, 0, 6, 0], "0"), TestContext.Current.CancellationToken));
        Assert.Equal(Scene3DError.InvalidValue, error.Error);

        // A neighbour announced and never given.
        Assert.Throws<Scene3DException>(() => PrcCompressedDecoder.Decode(Calculated("2", "000", [], [2, 0, 0, 4, 0, 0, 0, 6, 0], "0"), TestContext.Current.CancellationToken));

        // A reference to a point that does not exist yet.
        Assert.Throws<Scene3DException>(() => PrcCompressedDecoder.Decode(Calculated("0", "001", [7], [2, 0, 0, 4, 0, 0], "0"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 5)]
    public void TheWidthOfAReferenceToANormalIsTheStandardsCountOfBits(int value, int bits)
    {
        Assert.Equal(bits, PrcCompressedDecoder.BitsToStore(value));
    }

    internal static PrcTess3DCompressed Textured(string bits, uint[] references, int pairs)
    {
        PrcTess3DCompressed tess = Calculated("300", "00000", [], [0, 0, 0, 8, 0, 0, 0, 6, 0, 0, -4, 0, 0, -4, 0], "000");
        tess.Texture = new PrcCompressedTexture
        {
            Bits = Bits(bits),
            References = references,
            Parameters = Enumerable.Range(0, pairs * 2).Select(i => i / 2 + (i % 2) * 0.5f).ToArray(),
        };
        return tess;
    }

    private static int TextureOf(PrcCompressedGeometry geometry, int triangle, int point)
    {
        int corner = triangle * 3 + geometry.Corners.AsSpan(triangle * 3, 3).IndexOf(point);
        return geometry.CornerTextures[corner];
    }

    [Fact]
    public void TextureCoordinatesAreStoredOrReferredToCornerByCorner()
    {
        // Seed (0,1,2): three pairs stored. Left neighbour (0,2,3): point 0 refers to its first pair,
        // point 2 stores a second one, point 3 its first. Right neighbour (1,2,4): point 1 refers to
        // its pair, point 2 to the SECOND of its two (references count from the first), point 4 stores.
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Textured("000100110", [0, 0, 1], 6), TestContext.Current.CancellationToken);

        Assert.False(geometry.TextureDropped);
        Assert.Equal(6, geometry.TextureCount);
        Assert.Equal((0, 1, 2), (TextureOf(geometry, 0, 0), TextureOf(geometry, 0, 1), TextureOf(geometry, 0, 2)));
        Assert.Equal((0, 3, 4), (TextureOf(geometry, 1, 0), TextureOf(geometry, 1, 2), TextureOf(geometry, 1, 3)));
        Assert.Equal((1, 3, 5), (TextureOf(geometry, 2, 1), TextureOf(geometry, 2, 2), TextureOf(geometry, 2, 4)));
        Assert.Equal(3.5, geometry.TextureCoordinates[3 * 2 + 1]);
    }

    [Fact]
    public void TextureCoordinatesThatDoNotComeOutEvenAreLeftOutAndTheMeshKept()
    {
        // A pair too many; a reference to a pair the point does not have; bits that end early.
        foreach (PrcTess3DCompressed tess in new[] { Textured("000100110", [0, 0, 1], 7), Textured("000100110", [0, 0, 5], 6), Textured("0001001", [0], 6) })
        {
            using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);
            Assert.True(geometry.TextureDropped);
            Assert.Empty(geometry.CornerTextures);
            Assert.Equal(3, geometry.TriangleCount);
        }
    }

    /// <summary>One triangle with stored normals: the normal bits and the angles (10 bits each).</summary>
    /// <summary>Two triangles of one face that has colours on its points: five bytes a corner, numbered from <paramref name="corners"/> corners.</summary>
    internal static PrcTess3DCompressed Coloured(int corners)
    {
        PrcTess3DCompressed tess = Calculated("20", "0000", [], [0, 0, 0, 8, 0, 0, 0, 8, 0, 0, -8, 0], "00");
        tess.IsPointColorOnFace = Bits("1");
        tess.PointColors = new byte[corners * 5];
        for (int k = 0; k < corners; k++)
        {
            tess.PointColors[k * 5] = (byte)(k & 1);
            for (int channel = 1; channel <= 4; channel++)
            {
                tess.PointColors[(k * 5) + channel] = (byte)((10 * k) + channel);
            }
        }

        return tess;
    }

    /// <summary>The colour of the numbered corner as the scene packs it: red lowest; opaque unless its first byte says it has an alpha.</summary>
    private static int Colour(int k) => (int)(((10u * (uint)k) + 1) | (((10u * (uint)k) + 2) << 8) | (((10u * (uint)k) + 3) << 16) | (((k & 1) != 0 ? (10u * (uint)k) + 4 : 255u) << 24));

    [Fact]
    public void ColoursOnPointsAreFiveBytesACornerInTheOrderOfTheWalk()
    {
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Coloured(6), TestContext.Current.CancellationToken);

        // Neither triangle is reversed: each is wound first, third, second, and its colours with it.
        Assert.False(geometry.ColorsDropped);
        Assert.Equal([Colour(0), Colour(2), Colour(1), Colour(3), Colour(5), Colour(4)], geometry.CornerColors.AsSpan(0, 6).ToArray());
    }

    [Fact]
    public void ColoursThatDoNotComeOutEvenAreLeftOutAndTheMeshKept()
    {
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Coloured(5), TestContext.Current.CancellationToken);

        Assert.True(geometry.ColorsDropped);
        Assert.Empty(geometry.CornerColors);
        Assert.Equal(2, geometry.TriangleCount);
    }

    [Fact]
    public void WithoutFacesTheOneFlagOfColoursIsForEveryTriangle()
    {
        // A tessellation that says it has no faces still numbers its triangles' faces, and stores one
        // flag for all of them (the castle of the second sample set: 65,804 "faces", one flag).
        PrcTess3DCompressed tess = Coloured(6);
        tess.TriangleFaces = [0, 1];
        tess.FaceCount = 2;
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);

        Assert.False(geometry.ColorsDropped);
        Assert.Equal([Colour(0), Colour(2), Colour(1), Colour(3), Colour(5), Colour(4)], geometry.CornerColors.AsSpan(0, 6).ToArray());
    }

    [Fact]
    public void AFaceWithoutColoursHasNoneOnItsCorners()
    {
        PrcTess3DCompressed tess = Coloured(0);
        tess.IsPointColorOnFace = Bits("0");
        tess.PointColors = [];
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);

        Assert.False(geometry.ColorsDropped);
        Assert.Empty(geometry.CornerColors);
    }

    internal static PrcTess3DCompressed Stored(string normalBits, ushort[] angles, bool planar = false)
    {
        PrcTess3DCompressed tess = Calculated("0", "000", [], [0, 0, 0, 8, 0, 0, 0, 6, 0], "");
        return new PrcTess3DCompressed
        {
            IsCalculated = tess.IsCalculated,
            HasFaces = true,
            Tolerance = tess.Tolerance,
            HasOrigin = true,
            OriginX = 10,
            OriginY = 20,
            OriginZ = 30,
            Points = tess.Points,
            EdgeStatus = tess.EdgeStatus,
            TriangleFaces = tess.TriangleFaces,
            FaceCount = 1,
            PointIsReference = tess.PointIsReference,
            PointReferences = [],
            MustRecalculateNormals = false,
            NormalAngleBits = 10,
            NormalBits = Bits(normalBits),
            NormalAngles = angles,
            IsFacePlanar = Bits(planar ? "1" : "0"),
            LineAttributes = [0],
        };
    }

    [Fact]
    public void AStoredNormalAlongTheTrianglesOwnIsReadInItsFrame()
    {
        // Each corner for the first time: one normal, not reversed, no sign turned, theta 0, phi a quarter turn.
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Stored("000000000000", [0, 1023, 0, 1023, 0, 1023]), TestContext.Current.CancellationToken);

        // The triangle (0,0,0) (4,0,0) (2,3,0) in listing order has its normal along +Z.
        Assert.Equal(3, geometry.NormalCount);
        for (int corner = 0; corner < 3; corner++)
        {
            int normal = geometry.CornerNormals[corner];
            Assert.InRange(geometry.Normals[normal * 3 + 2], 1 - 1e-6, 1 + 1e-6);
        }

        // Its side follows the normal: wound so that the front faces +Z.
        Assert.Equal([0, 1, 2], geometry.Corners.AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void AReversedStoredNormalPointsTheOtherWayAndTurnsTheTriangle()
    {
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Stored("010001000100", [0, 1023, 0, 1023, 0, 1023]), TestContext.Current.CancellationToken);

        Assert.InRange(geometry.Normals[geometry.CornerNormals[0] * 3 + 2], -1 - 1e-6, -1 + 1e-6);
        Assert.Equal([0, 2, 1], geometry.Corners.AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void APlanarFaceStoresOneNormalForAllItsCorners()
    {
        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(Stored("0000", [0, 1023], planar: true), TestContext.Current.CancellationToken);

        Assert.Equal(1, geometry.NormalCount);
        Assert.Equal([0, 0, 0], geometry.CornerNormals.AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void NormalDataLeftOverRefusesTheTessellation()
    {
        Assert.Throws<Scene3DException>(() => PrcCompressedDecoder.Decode(Stored("0000000000001", [0, 1023, 0, 1023, 0, 1023]), TestContext.Current.CancellationToken));
        Assert.Throws<Scene3DException>(() => PrcCompressedDecoder.Decode(Stored("000000000000", [0, 1023, 0, 1023]), TestContext.Current.CancellationToken));
    }
}
