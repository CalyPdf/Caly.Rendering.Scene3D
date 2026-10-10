using System.Numerics;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// A plain PRC tessellation as triangles: every kind of primitive the format has, by its layout in the
/// index array (ISO 14739-1, PRC tessellation types; PDF Association issue #810 for texture indices).
/// Indices in the file count values, not elements: a point or normal index is three times its number,
/// a texture coordinate index twice.
/// </summary>
public class PrcMesherTests
{
    private const uint Triangle = 0x0002, Fan = 0x0004, Strip = 0x0008,
        TriangleOneNormal = 0x0020, FanOneNormal = 0x0040, StripOneNormal = 0x0080,
        TriangleTextured = 0x0200, FanTextured = 0x0400, TriangleOneNormalTextured = 0x2000, StripOneNormalTextured = 0x8000,
        Single = 0x40000000;

    // Five points in the plane z = 0, counter-clockwise seen from +z, and one above the plane.
    private static readonly double[] Points = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, -1, 1, 0, 0, 0, 1];

    // Normals: +z, +x, +y, -z.
    private static readonly double[] Normals = [0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, -1];

    private static readonly Vector3 Up = Vector3.UnitZ;

    private static PrcTess3D Tess(uint[] indices, PrcTessFace[] faces, double[]? normals = null, double[]? texture = null, bool calculate = false, double crease = 0, double[]? points = null) => new()
    {
        TessellationCoordinates = new PrcContentBaseTessData { Coordinates = points ?? Points },
        HasFaces = true,
        HasLoops = false,
        MustCalculateNormals = calculate,
        CreaseAngle = crease,
        NormalCoordinates = normals ?? (calculate ? [] : Normals),
        WireIndices = [],
        TriangulatedIndexArray = indices,
        FaceTessellationData = faces,
        TextureCoordinates = texture ?? [],
    };

    private static PrcTessFace Face(uint flags, uint start, uint[] sizes, uint textureIndices = 0, uint[]? styles = null, uint[]? colors = null) => new()
    {
        LineAttributes = styles ?? [],
        StartOfWireData = 0,
        SizesWire = [],
        UsedEntitiesFlag = flags,
        StartTriangulated = start,
        Triangulateddata = sizes,
        NumberOfTexturedCoordinateIndices = textureIndices,
        VertexColorData = colors is null ? null : new PrcVertexColors { Colors = colors },
        Behavior = 0,
    };

    private readonly record struct Corner(Vector3 Position, Vector3 Normal, Vector2 Uv, uint Color);

    private sealed record Built(Corner[] Corners, int[] PartSlots, int[] PartTriangles, int VertexCount, uint[] SlotStyles, SceneMeshFlags Flags);

    private static Built Build(PrcTess3D tess, SceneLimits? limits = null)
    {
        using var builder = new SceneBuilder(limits);
        PrcMesh mesh = PrcMesher.AddMesh(builder, tess, builder.AddName("mesh"));
        using Scene scene = builder.Build();
        SceneMesh m = scene.Meshes[mesh.Mesh];
        var corners = new List<Corner>();
        var slots = new List<int>();
        var counts = new List<int>();
        for (int p = m.FirstPart; p < m.FirstPart + m.PartCount; p++)
        {
            SceneMeshPart part = scene.Parts[p];
            slots.Add(part.ShadingId);
            counts.Add(part.IndexCount / 3);
            foreach (uint index in scene.Indices.Slice(part.IndexStart, part.IndexCount))
            {
                Assert.True(index < m.VertexCount);
                int v = (int)index;
                corners.Add(new Corner(
                    new Vector3(scene.Positions.Slice((m.VertexStart + v) * 3, 3)),
                    new Vector3(scene.Normals.Slice((m.NormalStart + v) * 3, 3)),
                    m.TexCoordStart < 0 ? default : new Vector2(scene.TexCoords.Slice((m.TexCoordStart + v) * 2, 2)),
                    m.ColorStart < 0 ? 0 : scene.Colors[m.ColorStart + v]));
            }
        }

        Assert.Equal(mesh.TriangleCount * 3, corners.Count);
        return new Built([.. corners], [.. slots], [.. counts], m.VertexCount, [.. mesh.Slots.Select(s => s.Style)], m.Flags);
    }

    private static Vector3 P(int point) => new((float)Points[point * 3], (float)Points[point * 3 + 1], (float)Points[point * 3 + 2]);

    /// <summary>The points of the triangles, as numbers of <see cref="Points"/>.</summary>
    private static int[] PointNumbers(Built built) => [.. built.Corners.Select(c => Enumerable.Range(0, Points.Length / 3).First(i => P(i) == c.Position))];

    /// <summary>Every triangle faces the same way as its first corner's normal.</summary>
    private static void AssertFrontFacing(Built built)
    {
        for (int t = 0; t < built.Corners.Length; t += 3)
        {
            Vector3 geometric = Vector3.Cross(built.Corners[t + 1].Position - built.Corners[t].Position, built.Corners[t + 2].Position - built.Corners[t].Position);
            Assert.True(Vector3.Dot(geometric, built.Corners[t].Normal) > 0, $"Triangle {t / 3} is turned away from its normal.");
        }
    }

    [Fact]
    public void TrianglesAreANormalAndAPointPerCorner()
    {
        // The square 0 1 2 3 as two triangles, normal +z everywhere: (normal, point) three times each.
        Built built = Build(Tess([0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9], [Face(Triangle, 0, [2])]));

        Assert.Equal([0, 1, 2, 0, 2, 3], PointNumbers(built));
        Assert.All(built.Corners, c => Assert.Equal(Up, c.Normal));
        Assert.Equal(4, built.VertexCount);                // the two shared corners are one vertex each
        Assert.Equal([0], built.PartSlots);
        Assert.Equal(SceneMeshFlags.HasNormals, built.Flags);
        AssertFrontFacing(built);

        // The same point with another normal is another vertex.
        built = Build(Tess([0, 0, 0, 3, 0, 6, 3, 0, 3, 6, 3, 9], [Face(Triangle, 0, [2])]));
        Assert.Equal(6, built.VertexCount);
        Assert.Equal(Vector3.UnitX, built.Corners[3].Normal);
    }

    [Fact]
    public void AFanTurnsAroundItsFirstPoint()
    {
        // Points 0 1 2 3 4: three triangles around point 0.
        Built built = Build(Tess([0, 0, 0, 3, 0, 6, 0, 9, 0, 12], [Face(Fan, 0, [1, 5])]));

        Assert.Equal([0, 1, 2, 0, 2, 3, 0, 3, 4], PointNumbers(built));
        Assert.Equal(5, built.VertexCount);
        AssertFrontFacing(built);
    }

    [Fact]
    public void AStripAlternatesItsTurn()
    {
        // A zigzag 1 2 0 3 4: every second triangle is stored turned the other way.
        Built built = Build(Tess([0, 3, 0, 6, 0, 0, 0, 9, 0, 12], [Face(Strip, 0, [1, 5])]));

        Assert.Equal([1, 2, 0, 0, 2, 3, 0, 3, 4], PointNumbers(built));
        AssertFrontFacing(built);
    }

    [Fact]
    public void KindsFollowEachOtherInTheOrderOfTheirBits()
    {
        // As the example of the ISO text, smaller: a triangle, then two fans of 3 and 4 points, then a strip of 3.
        uint[] indices =
        [
            0, 0, 0, 3, 0, 6,
            0, 0, 0, 6, 0, 9,
            0, 0, 0, 6, 0, 9, 0, 12,
            0, 0, 0, 3, 0, 15,
        ];
        Built built = Build(Tess(indices, [Face(Triangle | Fan | Strip, 0, [1, 2, 3, 4, 1, 3])]));

        Assert.Equal([0, 1, 2, 0, 2, 3, 0, 2, 3, 0, 3, 4, 0, 1, 5], PointNumbers(built));
    }

    [Fact]
    public void FacesStartWhereTheySay()
    {
        // Two faces; the second starts at index 6 and is listed first.
        Built built = Build(Tess([0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9], [Face(Triangle, 6, [1]), Face(Triangle, 0, [1])]));

        Assert.Equal([0, 2, 3, 0, 1, 2], PointNumbers(built));
    }

    [Fact]
    public void ATriangleWithOneNormalIsANormalThenThreePoints()
    {
        Built built = Build(Tess([3, 0, 3, 6, 6, 0, 6, 9], [Face(TriangleOneNormal, 0, [2])]));

        Assert.Equal([0, 1, 2, 0, 2, 3], PointNumbers(built));
        Assert.Equal([Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], built.Corners.Select(c => c.Normal));
    }

    [Fact]
    public void AFanWithOneNormalHasItOnceOrPerTriangle()
    {
        // Flagged as single: the normal, then the points.
        Built built = Build(Tess([0, 0, 3, 6, 9], [Face(FanOneNormal, 0, [1, 4 | Single])]));
        Assert.Equal([0, 1, 2, 0, 2, 3], PointNumbers(built));
        Assert.All(built.Corners, c => Assert.Equal(Up, c.Normal));

        // Not flagged: a normal with every point, which is the normal of the triangle that starts there
        // (the last ones repeat). Triangle 0 gets +x, triangle 1 gets +y.
        built = Build(Tess([3, 0, 6, 3, 6, 6, 6, 9], [Face(FanOneNormal, 0, [1, 4])]));
        Assert.Equal([0, 1, 2, 0, 2, 3], PointNumbers(built));
        Assert.Equal([Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], built.Corners.Select(c => c.Normal));

        // The same for a strip.
        built = Build(Tess([3, 3, 6, 6, 6, 0, 6, 9], [Face(StripOneNormal, 0, [1, 4])]));
        Assert.Equal([1, 2, 0, 0, 2, 3], PointNumbers(built));
        Assert.Equal([Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], built.Corners.Select(c => c.Normal));
    }

    /// <summary>Texture coordinate indices sit between the normal and the point, and count pairs (issue #810).</summary>
    [Fact]
    public void TexturedPrimitivesCarryTextureIndicesCountedInPairs()
    {
        double[] texture = [0, 0, 1, 0, 1, 1, 0.25, 0.75];
        Built built = Build(Tess([0, 0, 0, 0, 2, 3, 0, 4, 6], [Face(TriangleTextured, 0, [1], textureIndices: 1)], texture: texture));

        Assert.Equal([0, 1, 2], PointNumbers(built));
        Assert.Equal([new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1)], built.Corners.Select(c => c.Uv));
        Assert.Equal(SceneMeshFlags.HasNormals | SceneMeshFlags.HasTexCoords, built.Flags);

        // Two sets of texture coordinates per point: the first is used.
        built = Build(Tess([0, 6, 0, 0, 0, 2, 0, 3, 0, 4, 0, 6], [Face(TriangleTextured, 0, [1], textureIndices: 2)], texture: texture));
        Assert.Equal([new Vector2(0.25f, 0.75f), new Vector2(1, 0), new Vector2(1, 1)], built.Corners.Select(c => c.Uv));

        // One normal, then (texture, point) per point: a triangle, and a flagged strip.
        built = Build(Tess([0, 0, 0, 2, 3, 4, 6], [Face(TriangleOneNormalTextured, 0, [1], textureIndices: 1)], texture: texture));
        Assert.Equal([0, 1, 2], PointNumbers(built));
        Assert.Equal(new Vector2(1, 1), built.Corners[2].Uv);

        built = Build(Tess([0, 2, 3, 4, 6, 0, 0, 6, 9], [Face(StripOneNormalTextured, 0, [1, 4 | Single], textureIndices: 1)], texture: texture));
        Assert.Equal([1, 2, 0, 0, 2, 3], PointNumbers(built));
        Assert.Equal(new Vector2(0.25f, 0.75f), built.Corners[5].Uv);

        // A face without texture in a tessellation that has one: its points are at the texture's origin.
        built = Build(Tess([0, 0, 0, 6, 0, 9, /* then the textured fan */ 0, 0, 0, 0, 2, 3, 0, 4, 6], [Face(Fan | FanTextured, 0, [1, 3, 1, 3], textureIndices: 1)], texture: texture));
        Assert.Equal([0, 2, 3, 0, 1, 2], PointNumbers(built));
        Assert.Equal(Vector2.Zero, built.Corners[1].Uv);
        Assert.Equal(new Vector2(1, 1), built.Corners[5].Uv);
    }

    /// <summary>
    /// A texture index with its sign bit set (-2 and other small negative numbers, as in the Fronema files of the
    /// sample set) names no texture coordinate: the corner has none, and its point index is still read.
    /// </summary>
    [Fact]
    public void ANegativeTextureIndexIsACornerWithoutTextureCoordinates()
    {
        double[] texture = [0, 0, 1, 0, 1, 1, 0.25, 0.75];
        Built built = Build(Tess([0, unchecked((uint)-2), 0, 0, 4, 3, 0, unchecked((uint)-46), 6], [Face(TriangleTextured, 0, [1], textureIndices: 1)], texture: texture));

        Assert.Equal([0, 1, 2], PointNumbers(built));
        Assert.Equal([Vector2.Zero, new Vector2(1, 1), Vector2.Zero], built.Corners.Select(c => c.Uv));

        // A positive index beyond the coordinates is still a damaged file.
        Scene3DException e = Assert.Throws<Scene3DException>(() => Build(Tess([0, 2000, 0, 0, 2, 3, 0, 0, 6], [Face(TriangleTextured, 0, [1], textureIndices: 1)], texture: texture)));
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    /// <summary>When normals are to be calculated the file stores no normal index at all (ISO 14739-1, 7.8.5.1).</summary>
    [Fact]
    public void WithoutStoredNormalsOnlyPointsAreIndexed()
    {
        // The example of the ISO text: [0 3 6 0 3 9] would be [0 0 3 3 6 6 ...] with normals.
        Built built = Build(Tess([0, 3, 6, 0, 6, 9], [Face(Triangle, 0, [2])], calculate: true, crease: 0.5));
        Assert.Equal([0, 1, 2, 0, 2, 3], PointNumbers(built));
        Assert.All(built.Corners, c => Assert.Equal(Up, c.Normal));
        Assert.Equal(4, built.VertexCount);

        // A kind with one normal has none either; a textured one keeps its texture indices.
        built = Build(Tess([0, 3, 6, 9], [Face(FanOneNormal, 0, [1, 4 | Single])], calculate: true));
        Assert.Equal([0, 1, 2, 0, 2, 3], PointNumbers(built));

        built = Build(Tess([2, 0, 4, 3, 6, 6], [Face(TriangleTextured, 0, [1], textureIndices: 1)], calculate: true, texture: [0, 0, 1, 0, 1, 1, 0.25, 0.75]));
        Assert.Equal([0, 1, 2], PointNumbers(built));
        Assert.Equal([new Vector2(1, 0), new Vector2(1, 1), new Vector2(0.25f, 0.75f)], built.Corners.Select(c => c.Uv));
    }

    /// <summary>
    /// A face says where its indices start "as if normal indices were stored" (ISO 14739-1, 7.8.5.1),
    /// also when they are not: real files then give starts beyond where the faces really are.
    /// </summary>
    [Fact]
    public void WithoutStoredNormalsFacesStartEarlierThanTheySay()
    {
        // Three faces of one triangle each; with normals they would start at 0, 6 and 12.
        uint[] indices = [0, 3, 6, 0, 6, 9, 0, 9, 12];
        Built built = Build(Tess(indices, [Face(Triangle, 0, [1]), Face(Triangle, 6, [1]), Face(Triangle, 12, [1])], calculate: true));
        Assert.Equal([0, 1, 2, 0, 2, 3, 0, 3, 4], PointNumbers(built));

        // Listed in another order, and with a face that has nothing: the order of the starts decides.
        built = Build(Tess(indices, [Face(Triangle, 12, [1]), Face(0, 6, []), Face(Triangle, 0, [1]), Face(Triangle, 6, [1])], calculate: true));
        Assert.Equal([0, 3, 4, 0, 1, 2, 0, 2, 3], PointNumbers(built));

        // Textured: with normals each triangle would take 9 indices, without it takes 6.
        double[] texture = [0, 0, 1, 0, 1, 1, 0.25, 0.75];
        built = Build(Tess([0, 0, 2, 3, 4, 6, 0, 0, 4, 6, 6, 9], [Face(TriangleTextured, 0, [1], textureIndices: 1), Face(TriangleTextured, 9, [1], textureIndices: 1)], calculate: true, texture: texture));
        Assert.Equal([0, 1, 2, 0, 2, 3], PointNumbers(built));
        Assert.Equal(new Vector2(0.25f, 0.75f), built.Corners[5].Uv);

        // A count the array cannot hold is refused, not counted through.
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => Build(Tess(indices, [Face(Triangle, 0, [4_000_000_000]), Face(Triangle, 6, [1])], calculate: true))).Error);
    }

    /// <summary>Triangles that meet at less than the crease angle share their normal at the points they share.</summary>
    [Fact]
    public void CalculatedNormalsAreSharedBelowTheCreaseAngle()
    {
        // Two triangles sharing the edge 0-1: one in the plane z = 0 facing +z, one in the plane y = 0
        // facing +y (a right angle).
        uint[] indices = [0, 3, 6, 3, 0, 15];
        var faces = new[] { Face(Triangle, 0, [2]) };

        // A crease of half a radian: the fold is sharp, every triangle keeps its own normal.
        Built sharp = Build(Tess(indices, faces, calculate: true, crease: 0.5));
        Assert.Equal(6, sharp.VertexCount);
        Assert.Equal([Up, Up, Up], sharp.Corners.Take(3).Select(c => c.Normal));
        Assert.Equal([Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], sharp.Corners.Skip(3).Select(c => c.Normal));

        // A crease of two radians: the fold is smooth; the shared points have the mean of both normals.
        Built smooth = Build(Tess(indices, faces, calculate: true, crease: 2));
        Assert.Equal(4, smooth.VertexCount);
        Vector3 mean = Vector3.Normalize(Up + Vector3.UnitY);
        Assert.Equal(mean.Y, smooth.Corners[0].Normal.Y, 5);
        Assert.Equal(mean.Z, smooth.Corners[0].Normal.Z, 5);
        Assert.Equal(smooth.Corners[0].Normal, smooth.Corners[4].Normal);   // point 0 in both triangles
        Assert.Equal(Up, smooth.Corners[2].Normal);                         // point 2 belongs to one triangle only
        Assert.All(smooth.Corners, c => Assert.Equal(1, c.Normal.Length(), 5));

        // An angle given in degrees is taken as such: 120 is not a number of radians.
        Assert.Equal(4, Build(Tess(indices, faces, calculate: true, crease: 120)).VertexCount);
        Assert.Equal(6, Build(Tess(indices, faces, calculate: true, crease: 30)).VertexCount);
    }

    [Fact]
    public void ATriangleWithoutAreaHasNoNormalAndBreaksNothing()
    {
        Built built = Build(Tess([0, 0, 3, 0, 3, 6], [Face(Triangle, 0, [2])], calculate: true, crease: 1));
        Assert.Equal(Vector3.Zero, built.Corners[0].Normal);
        Assert.Equal(Up, built.Corners[5].Normal);
        Assert.All(built.Corners, c => Assert.True(float.IsFinite(c.Normal.X + c.Normal.Y + c.Normal.Z)));
    }

    /// <summary>Vertex colours are one per point of the face, in the order of its points (issue #820).</summary>
    [Fact]
    public void VertexColoursFollowThePointsOfTheFace()
    {
        uint[] indices = [0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9, 0, 12];
        uint[] colors = [0xFF0000FF, 0xFF00FF00, 0xFFFF0000, 1, 2, 3, 4];
        Built built = Build(Tess(indices, [Face(Triangle | Fan, 0, [1, 1, 4], colors: colors)]));

        Assert.Equal(SceneMeshFlags.HasNormals | SceneMeshFlags.HasColors, built.Flags);
        Assert.Equal([0xFF0000FF, 0xFF00FF00, 0xFFFF0000, 1, 2, 3, 1, 3, 4], built.Corners.Select(c => c.Color));

        // The same point with two colours is two vertices. A face without colours is white.
        Assert.Equal(7, built.VertexCount);
        built = Build(Tess([0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9], [Face(Triangle, 0, [1], colors: [5, 6, 7]), Face(Triangle, 6, [1])]));
        Assert.Equal([5u, 6u, 7u, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF], built.Corners.Select(c => c.Color));
    }

    /// <summary>
    /// Triangles are grouped by the style their face names: slot 0 for none (the appearance comes from
    /// whatever shows the tessellation), then a slot per style in order of first use.
    /// </summary>
    [Fact]
    public void TrianglesAreGroupedByTheStyleOfTheirFace()
    {
        uint[] indices = [0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9, 0, 0, 0, 9, 0, 12, 0, 0, 0, 3, 0, 15];
        Built built = Build(Tess(indices,
        [
            Face(Triangle, 0, [1], styles: [7]),
            Face(Triangle, 6, [1]),
            Face(Triangle, 12, [1], styles: [3]),
            Face(Triangle, 18, [1], styles: [7]),
        ]));

        Assert.Equal(new uint[] { 7, 3 }, built.SlotStyles);
        Assert.Equal([0, 1, 2], built.PartSlots);
        Assert.Equal([1, 2, 1], built.PartTriangles);
        Assert.Equal([0, 2, 3, /* style 7 */ 0, 1, 2, 0, 1, 5, /* style 3 */ 0, 3, 4], PointNumbers(built));

        // Without any face that names no style, there is no part for slot 0.
        built = Build(Tess(indices, [Face(Triangle, 0, [4], styles: [9])]));
        Assert.Equal([1], built.PartSlots);
        Assert.Equal(new uint[] { 9 }, built.SlotStyles);

        // Several styles on a face: one per primitive, in order; a triangle is a primitive.
        built = Build(Tess([0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9, 0, 0, 0, 9, 0, 12, 0, 15], [Face(Triangle | Fan, 0, [2, 1, 4], styles: [4, 5, 4])]));
        Assert.Equal(new uint[] { 4, 5 }, built.SlotStyles);
        Assert.Equal([1, 2], built.PartSlots);
        Assert.Equal([3, 1], built.PartTriangles);
    }

    [Fact]
    public void FansAndStripsOfFewerThanThreePointsMakeNoTriangle()
    {
        Built built = Build(Tess([0, 0, 0, 3, 0, 0, 0, 3, 0, 6], [Face(Fan | Strip, 0, [2, 2, 0, 1, 3])]));
        Assert.Equal([0, 1, 2], PointNumbers(built));

        built = Build(Tess([], []));
        Assert.Empty(built.Corners);
        Assert.Equal(0, built.VertexCount);
    }

    /// <summary>Review focus: counts and indices that the arrays cannot honour.</summary>
    [Fact]
    public void WhatTheArraysCannotHonourIsRefused()
    {
        Scene3DError Fails(PrcTess3D tess, SceneLimits? limits = null) => Assert.Throws<Scene3DException>(() => Build(tess, limits)).Error;

        // More triangles than there are indices for; a start past the array; a fan longer than the array.
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 0, 0, 3, 0, 6], [Face(Triangle, 0, [2])])));
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 0, 0, 3, 0, 6], [Face(Triangle, 1, [1])])));
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 0, 0, 3, 0, 6], [Face(Fan, 0, [1, 0x3FFFFFFF])])));
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 0, 0, 3, 0, 6], [Face(Fan, 0, [0xFFFFFFFF, 3])])));

        // More primitives than sizes; a reserved kind.
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 0, 0, 3, 0, 6], [Face(Triangle | Fan, 0, [1])])));
        Assert.Equal(Scene3DError.Unsupported, Fails(Tess([0, 0, 0, 3, 0, 6], [Face(0x0001, 0, [1])])));

        // An index that is not a multiple of its element size, or past its array: point, normal, texture.
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 0, 0, 4, 0, 6], [Face(Triangle, 0, [1])])));
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 0, 0, 3, 0, 18], [Face(Triangle, 0, [1])])));
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([12, 0, 0, 3, 0, 6], [Face(Triangle, 0, [1])])));
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 3, 0, 0, 0, 3, 0, 0, 6], [Face(TriangleTextured, 0, [1], textureIndices: 1)], texture: [0, 0, 1, 1])));
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 4, 0, 0, 0, 3, 0, 0, 6], [Face(TriangleTextured, 0, [1], textureIndices: 1)], texture: [0, 0, 1, 1])));

        // Faces that together use the index array more than once: the work would grow with the number
        // of faces instead of with the data.
        var faces = new PrcTessFace[3];
        Array.Fill(faces, Face(Triangle, 0, [2]));
        Assert.Equal(Scene3DError.InvalidValue, Fails(Tess([0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9], faces)));

        // The limits of the scene.
        Assert.Equal(Scene3DError.LimitExceeded, Fails(Tess([0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9], [Face(Triangle, 0, [2])]), new SceneLimits { MaxIndices = 5 }));
        Assert.Equal(Scene3DError.LimitExceeded, Fails(Tess([0, 0, 0, 3, 0, 6, 0, 0, 0, 6, 0, 9], [Face(Triangle, 0, [2])]), new SceneLimits { MaxVertices = 3 }));
    }

    [Fact]
    public void ValuesThatAreNotNumbersAreZeroedAndReported()
    {
        double[] points = [0, 0, 0, 1, 0, 0, double.NaN, 1, 0];
        using var builder = new SceneBuilder();
        PrcMesher.AddMesh(builder, Tess([0, 0, 0, 3, 0, 6], [Face(Triangle, 0, [1])], points: points), builder.AddName("mesh"), TestContext.Current.CancellationToken);
        using Scene scene = builder.Build();

        Assert.Equal(0, scene.Positions[6]);
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-non-finite-vertex");
        Assert.False(scene.Meshes[0].Bounds.IsEmpty);
    }
}
