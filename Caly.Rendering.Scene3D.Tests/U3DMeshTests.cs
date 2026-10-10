using System.Globalization;
using System.Numerics;
using Caly.Rendering.Scene3D.Tests.Idtf;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

public partial class U3DMeshTests
{
    public static TheoryData<string> FullBaseScenes => ["box_base", "shading2_base", "plane64x64_base", "surface_base"];

    private static string F(float value, int decimals)
    {
        // Adding zero turns -0 into 0, which would otherwise print differently.
        return (MathF.Round(value, decimals) + 0f).ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Corner(Vector3 position, Vector3? normal, Vector2? uv)
    {
        string key = $"{position.X + 0f:R},{position.Y + 0f:R},{position.Z + 0f:R}";
        if (normal is { } n)
        {
            key += $"|{F(n.X, 4)},{F(n.Y, 4)},{F(n.Z, 4)}";
        }

        if (uv is { } t)
        {
            key += $"|{F(t.X, 5)},{F(t.Y, 5)}";
        }

        return key;
    }

    /// <summary>A triangle as text, the same whichever corner it starts from; the winding is kept.</summary>
    private static string Triangle(int shading, string a, string b, string c)
    {
        string best = $"{a} {b} {c}";
        foreach (string rotation in new[] { $"{b} {c} {a}", $"{c} {a} {b}" })
        {
            if (string.CompareOrdinal(rotation, best) < 0)
            {
                best = rotation;
            }
        }

        return $"{shading}: {best}";
    }

    private static Vector3[] Vectors(IdtfElement list)
    {
        List<double> n = list.Numbers;
        var result = new Vector3[n.Count / 3];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new Vector3((float)n[i * 3], (float)n[i * 3 + 1], (float)n[i * 3 + 2]);
        }

        return result;
    }

    /// <summary>The triangles of an IDTF mesh, resolved to values.</summary>
    private static List<string> IdtfTriangles(IdtfElement mesh, out Vector3[] positions)
    {
        positions = Vectors(mesh.Get("MODEL_POSITION_LIST"));
        Vector3[] normals = Vectors(mesh.Get("MODEL_NORMAL_LIST"));
        List<double> facePositions = mesh.Get("MESH_FACE_POSITION_LIST").Numbers;
        List<double> faceNormals = mesh.Get("MESH_FACE_NORMAL_LIST").Numbers;
        List<double> faceShading = mesh.Get("MESH_FACE_SHADING_LIST").Numbers;

        Vector2[]? texCoords = null;
        IdtfElement[]? faceTexCoords = null;
        if (mesh.Find("MODEL_TEXTURE_COORD_LIST") is { } coordList && mesh.Find("MESH_FACE_TEXTURE_COORD_LIST") is { } faceList)
        {
            List<double> n = coordList.Numbers;
            texCoords = new Vector2[n.Count / 4];
            for (int i = 0; i < texCoords.Length; i++)
            {
                texCoords[i] = new Vector2((float)n[i * 4], (float)n[i * 4 + 1]);
            }

            faceTexCoords = faceList.All("FACE").OrderBy(f => int.Parse(f.Argument)).ToArray();
        }

        var triangles = new List<string>();
        for (int f = 0; f < faceShading.Count; f++)
        {
            // Layer 0 of the face: "TEXTURE_LAYER 0 TEX_COORD: a b c".
            string[]? uvIndices = faceTexCoords?[f].Find("TEX_COORD:")?.Arguments.ToArray();
            var corners = new string[3];
            for (int k = 0; k < 3; k++)
            {
                Vector2? uv = uvIndices is null ? null : texCoords![int.Parse(uvIndices[k])];
                corners[k] = Corner(positions[(int)facePositions[f * 3 + k]], normals[(int)faceNormals[f * 3 + k]], uv);
            }

            triangles.Add(Triangle((int)faceShading[f], corners[0], corners[1], corners[2]));
        }

        triangles.Sort(StringComparer.Ordinal);
        return triangles;
    }

    private static Vector3 Position(Scene scene, in SceneMesh mesh, uint vertex)
    {
        ReadOnlySpan<float> p = scene.Positions.Slice((mesh.VertexStart + (int)vertex) * 3, 3);
        return new Vector3(p[0], p[1], p[2]);
    }

    private static Vector3? Normal(Scene scene, in SceneMesh mesh, uint vertex)
    {
        if (mesh.NormalStart < 0)
        {
            return null;
        }

        ReadOnlySpan<float> n = scene.Normals.Slice((mesh.NormalStart + (int)vertex) * 3, 3);
        return new Vector3(n[0], n[1], n[2]);
    }

    private static Vector2? TexCoord(Scene scene, in SceneMesh mesh, uint vertex)
    {
        if (mesh.TexCoordStart < 0)
        {
            return null;
        }

        ReadOnlySpan<float> t = scene.TexCoords.Slice((mesh.TexCoordStart + (int)vertex) * 2, 2);
        return new Vector2(t[0], t[1]);
    }

    private static List<string> SceneTriangles(Scene scene, in SceneMesh mesh)
    {
        var triangles = new List<string>();
        for (int p = 0; p < mesh.PartCount; p++)
        {
            SceneMeshPart part = scene.Parts[mesh.FirstPart + p];
            Assert.Equal(0, part.IndexCount % 3);
            ReadOnlySpan<uint> indices = scene.Indices.Slice(part.IndexStart, part.IndexCount);
            for (int i = 0; i < indices.Length; i += 3)
            {
                var corners = new string[3];
                for (int k = 0; k < 3; k++)
                {
                    uint v = indices[i + k];
                    Assert.True(v < (uint)mesh.VertexCount, "index out of range");
                    corners[k] = Corner(Position(scene, mesh, v), Normal(scene, mesh, v), TexCoord(scene, mesh, v));
                }

                triangles.Add(Triangle(part.ShadingId, corners[0], corners[1], corners[2]));
            }
        }

        triangles.Sort(StringComparer.Ordinal);
        return triangles;
    }

    private static SceneMesh MeshOfResource(Scene scene, IdtfDocument idtf, string resource)
    {
        string nodeName = idtf.Nodes.First(n => n.Argument == "MODEL" && n.Text("RESOURCE_NAME") == resource).Text("NODE_NAME");
        SceneNode node = scene.Nodes.ToArray().First(n => scene.GetName(n.Name) == nodeName);
        Assert.True(node.Mesh >= 0, $"node '{nodeName}' has no mesh");
        return scene.Meshes[node.Mesh];
    }

    /// <summary>
    /// Done-when 1: a mesh whose whole detail is in its base decodes to exactly the triangles of the IDTF
    /// source, corner for corner, whatever order the encoder stored them in.
    /// </summary>
    [Theory]
    [MemberData(nameof(FullBaseScenes))]
    public void FullBaseMeshesMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = U3DSceneGraphTests.Read(name);

        int checkedMeshes = 0;
        foreach (IdtfElement resource in idtf.Resources("MODEL").Where(r => r.Text("MODEL_TYPE") == "MESH"))
        {
            IdtfElement idtfMesh = resource.Get("MESH");
            SceneMesh mesh = MeshOfResource(scene, idtf, resource.Text("RESOURCE_NAME"));
            Assert.Equal(resource.Text("RESOURCE_NAME"), scene.GetName(mesh.Name));

            List<string> expected = IdtfTriangles(idtfMesh, out Vector3[] positions);
            List<string> actual = SceneTriangles(scene, mesh);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected, actual);

            Assert.True((mesh.Flags & SceneMeshFlags.HasNormals) != 0);
            Assert.Equal(idtfMesh.Number("MODEL_TEXTURE_COORD_COUNT") > 0, (mesh.Flags & SceneMeshFlags.HasTexCoords) != 0);
            Assert.True((mesh.Flags & (SceneMeshFlags.PartialDetail | SceneMeshFlags.HasColors)) == 0);

            // One part per shading id in use, in ascending order, together covering the mesh's indices.
            int[] shadingIds = idtfMesh.Get("MESH_FACE_SHADING_LIST").Numbers.Select(n => (int)n).Distinct().Order().ToArray();
            SceneMeshPart[] parts = scene.Parts.Slice(mesh.FirstPart, mesh.PartCount).ToArray();
            Assert.Equal(shadingIds, parts.Select(p => p.ShadingId).ToArray());
            for (int i = 1; i < parts.Length; i++)
            {
                Assert.Equal(parts[i - 1].IndexStart + parts[i - 1].IndexCount, parts[i].IndexStart);
            }

            Assert.False(mesh.Bounds.IsEmpty);
            Assert.Equal(positions.Aggregate(Vector3.Min), mesh.Bounds.Min);
            Assert.Equal(positions.Aggregate(Vector3.Max), mesh.Bounds.Max);

            Assert.True(mesh.VertexCount <= expected.Count * 3);
            checkedMeshes++;
        }

        Assert.True(checkedMeshes > 0);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code.StartsWith("mesh-", StringComparison.Ordinal));
    }

    /// <summary>Corners that share every index become one vertex.</summary>
    [Fact]
    public void CornersSharingTheirIndicesAreUnified()
    {
        // A 64 x 64 grid in which position, normal and texture coordinate i always go together.
        using (Scene plane = U3DSceneGraphTests.Read("plane64x64_base"))
        {
            Assert.Equal(4096, Assert.Single(plane.Meshes.ToArray()).VertexCount);
        }

        // A box with a normal per corner of each face: at least the 24 a box needs, at most one per corner.
        using Scene box = U3DSceneGraphTests.Read("box_base");
        Assert.InRange(Assert.Single(box.Meshes.ToArray()).VertexCount, 24, 36);
    }

    /// <summary>
    /// Every model of the sample scenes has its geometry: a mesh at full detail whether it keeps it in a
    /// base, in progressive blocks or in both, a line set, a point set.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void EveryModelOfTheSamplesHasGeometry(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = U3DSceneGraphTests.Read(name);
        HashSet<string> resources = idtf.Resources("MODEL").Select(r => r.Text("RESOURCE_NAME")).ToHashSet();

        foreach (IdtfElement idtfNode in idtf.Nodes.Where(n => n.Argument == "MODEL" && resources.Contains(n.Text("RESOURCE_NAME"))))
        {
            foreach (SceneNode node in scene.Nodes.ToArray().Where(n => scene.GetName(n.Name) == idtfNode.Text("NODE_NAME")))
            {
                Assert.True(node.Mesh >= 0, $"node '{idtfNode.Text("NODE_NAME")}' has no geometry");
            }
        }

        Assert.DoesNotContain(scene.Diagnostics, d => d.Code is "mesh-progressive-only" or "mesh-partial-detail" or "progressive-mesh-unreadable");
        for (int m = 0; m < scene.Meshes.Length; m++)
        {
            SceneMesh mesh = scene.Meshes[m];
            Assert.True(mesh.PartCount > 0 && mesh.VertexCount > 0);
            Assert.True((mesh.Flags & SceneMeshFlags.PartialDetail) == 0);
        }
    }

    // ---- hand-built meshes (profile "no compression": every value is a plain 32-bit one) ----

    private static readonly Vector3[] Quad = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
    private static readonly Vector3[] Up = [Vector3.UnitZ];

    private static TestU3D QuadFile(uint faces = 2, uint positions = 4)
    {
        return new TestU3D(TestU3D.NoCompression)
            .MeshDeclaration("Quad", 0, faces, positions, 1, 0, 0, 0, [(0, 0)], positions)
            .ModelNode("Node", "Quad", ("", Matrix4x4.Identity));
    }

    [Fact]
    public void AHandBuiltQuadDecodes()
    {
        byte[] file = QuadFile().BaseMesh("Quad", Quad, Up, [], [], [], [0, 0, 0, 1, 0, 2, 0], [0, 0, 0, 2, 0, 3, 0]).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneMesh mesh = Assert.Single(scene.Meshes.ToArray());
        Assert.Equal(0, scene.Nodes[0].Mesh);
        Assert.Equal(SceneMeshFlags.HasNormals, mesh.Flags);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(-1, mesh.TexCoordStart);
        Assert.Equal(-1, mesh.ColorStart);
        Assert.Equal<uint>([0, 1, 2, 0, 2, 3], scene.Indices.ToArray());
        Assert.Equal<float>([0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], scene.Positions.ToArray());
        Assert.Equal<float>([0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1], scene.Normals.ToArray());
        Assert.Equal(new Vector3(0, 0, 0), mesh.Bounds.Min);
        Assert.Equal(new Vector3(1, 1, 0), mesh.Bounds.Max);

        SceneMeshPart part = Assert.Single(scene.Parts.ToArray());
        Assert.Equal((0, 6, 0), (part.IndexStart, part.IndexCount, part.ShadingId));
        Assert.Empty(scene.Diagnostics);
    }

    [Fact]
    public void FacesAreGroupedIntoPartsByShadingAndCarryColoursAndTextureCoordinates()
    {
        Vector4[] colours = [new(1, 0, 0, 1), new(0, 0.5f, 2, -1)];
        Vector4[] uvs = [new(0.25f, 0.75f, 9, 9), new(1, 0, 9, 9)];

        // Shading 0: plain. Shading 1: diffuse and specular colours and two texture layers.
        byte[] file = new TestU3D(TestU3D.NoCompression)
            .MeshDeclaration("Quad", 0, 3, 4, 1, 2, 1, 2, [(0, 0), (3, 2)], 4)
            .ModelNode("Node", "Quad", ("", Matrix4x4.Identity))
            .BaseMesh("Quad", Quad, Up, colours, [Vector4.One], uvs,
                // shading, then per corner: position, normal, [diffuse, specular, uv layer 0, uv layer 1]
                [1, 0, 0, 0, 0, 0, 1, 1, 0, 1, 0, 1, 0, 2, 0, 0, 0, 0, 1],
                [0, 0, 0, 2, 0, 3, 0],
                [1, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 1, 3, 0, 1, 0, 1, 1])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneMesh mesh = Assert.Single(scene.Meshes.ToArray());
        Assert.Equal(SceneMeshFlags.HasNormals | SceneMeshFlags.HasTexCoords | SceneMeshFlags.HasColors, mesh.Flags);

        SceneMeshPart[] parts = scene.Parts.ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal((0, 3, 0), (parts[0].IndexStart, parts[0].IndexCount, parts[0].ShadingId));
        Assert.Equal((3, 6, 1), (parts[1].IndexStart, parts[1].IndexCount, parts[1].ShadingId));

        (Vector3 Position, uint Colour, Vector2 Uv) Vertex(int index)
        {
            int v = (int)scene.Indices[index];
            ReadOnlySpan<float> t = scene.TexCoords.Slice((mesh.TexCoordStart + v) * 2, 2);
            return (Position(scene, mesh, (uint)v), scene.Colors[mesh.ColorStart + v], new Vector2(t[0], t[1]));
        }

        // The plain face: white, no texture coordinate.
        Assert.Equal((Quad[0], 0xFFFFFFFFu, Vector2.Zero), Vertex(0));
        Assert.Equal((Quad[2], 0xFFFFFFFFu, Vector2.Zero), Vertex(1));
        Assert.Equal((Quad[3], 0xFFFFFFFFu, Vector2.Zero), Vertex(2));

        // The coloured faces, in file order. Colours are RGBA with red in the low byte, clamped to 0 – 1.
        const uint red = 0xFF0000FF, clamped = 0x00FF8000;
        Assert.Equal((Quad[0], red, new Vector2(0.25f, 0.75f)), Vertex(3));
        Assert.Equal((Quad[1], clamped, new Vector2(1, 0)), Vertex(4));
        Assert.Equal((Quad[2], red, new Vector2(0.25f, 0.75f)), Vertex(5));
        Assert.Equal((Quad[0], red, new Vector2(0.25f, 0.75f)), Vertex(6));
        Assert.Equal((Quad[2], red, new Vector2(0.25f, 0.75f)), Vertex(7));
        Assert.Equal((Quad[3], clamped, new Vector2(1, 0)), Vertex(8));

        // Position 0 is used plain and coloured: two vertices. Seven distinct corners in all.
        Assert.Equal(7, mesh.VertexCount);
        Assert.Equal(scene.Indices[3], scene.Indices[6]);
    }

    [Fact]
    public void AMeshThatExcludesNormalsHasNoNormalStream()
    {
        byte[] file = new TestU3D(TestU3D.NoCompression)
            .MeshDeclaration("Quad", 1, 2, 4, 0, 0, 0, 0, [(0, 0)], 4)
            .ModelNode("Node", "Quad", ("", Matrix4x4.Identity))
            .BaseMesh("Quad", Quad, [], [], [], [], [0, 0, 1, 2], [0, 0, 2, 3])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneMesh mesh = Assert.Single(scene.Meshes.ToArray());
        Assert.Equal(SceneMeshFlags.None, mesh.Flags);
        Assert.Equal(-1, mesh.NormalStart);
        Assert.True(scene.Normals.IsEmpty);
        Assert.Equal<uint>([0, 1, 2, 0, 2, 3], scene.Indices.ToArray());
    }

    [Fact]
    public void AMeshWithNormalIndicesButNoNormalsHasNoNormalStream()
    {
        byte[] file = new TestU3D(TestU3D.NoCompression)
            .MeshDeclaration("Quad", 0, 1, 4, 0, 0, 0, 0, [(0, 0)], 4)
            .ModelNode("Node", "Quad", ("", Matrix4x4.Identity))
            .BaseMesh("Quad", Quad, [], [], [], [], [0, 0, 77, 1, 78, 2, 79])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SceneMeshFlags.None, Assert.Single(scene.Meshes.ToArray()).Flags);
        Assert.Equal(3, scene.Meshes[0].VertexCount);
    }

    [Fact]
    public void NodesUsingOneResourceShareItsMesh()
    {
        byte[] file = QuadFile()
            .ModelNode("Second", "Quad", ("", Matrix4x4.CreateTranslation(5, 0, 0)))
            .BaseMesh("Quad", Quad, Up, [], [], [], [0, 0, 0, 1, 0, 2, 0], [0, 0, 0, 2, 0, 3, 0])
            .MeshDeclaration("Unused", 0, 1, 3, 1, 0, 0, 0, [(0, 0)], 3)
            .BaseMesh("Unused", Quad[..3], Up, [], [], [], [0, 0, 0, 1, 0, 2, 0])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal(0, scene.Nodes[0].Mesh);
        Assert.Equal(0, scene.Nodes[1].Mesh);
        Assert.Equal(new SceneBounds { Min = new Vector3(0, 0, 0), Max = new Vector3(6, 1, 0) }, scene.ComputeWorldBounds());
    }

    /// <summary>Review focus: counts no block could hold are refused before anything is allocated for them.</summary>
    [Theory]
    [InlineData(0xFFFFFFFFu, 0u, 0u, 0u)]
    [InlineData(4u, 0xFFFFFFFFu, 0u, 0u)]
    [InlineData(4u, 0u, 0x10000000u, 0u)]
    [InlineData(4u, 0u, 0u, 0x0FFFFFFFu)]
    [InlineData(1000u, 0u, 0u, 0u)]
    public void BaseMeshCountsBeyondTheBlockAreRejected(uint positions, uint normals, uint diffuse, uint texCoords)
    {
        var data = new TestU3D.Data().String("Quad").U32(0).U32(2).U32(positions).U32(normals).U32(diffuse).U32(0).U32(texCoords);
        for (int i = 0; i < 64; i++)
        {
            data.F32(i);
        }

        byte[] file = QuadFile().Block(0xFFFFFF3B, data).ToArray();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Scene3DError.Truncated, e.Error);
        Assert.Null(e.InnerException);
        Assert.True(allocated < 1_000_000, $"allocated {allocated} bytes");
    }

    [Fact]
    public void AFaceCountBeyondTheBlockIsRejected()
    {
        var data = new TestU3D.Data().String("Quad").U32(0).U32(50_000_000).U32(4).U32(1).U32(0).U32(0).U32(0);
        foreach (Vector3 v in Quad.Append(Vector3.UnitZ))
        {
            data.Vector(v);
        }

        data.U32(0).U32(0).U32(0).U32(1).U32(0).U32(2).U32(0);
        byte[] file = QuadFile().Block(0xFFFFFF3B, data).ToArray();

        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Scene3DError.Truncated, e.Error);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public void DeclarationCountsBeyondTheBlockAreRejected()
    {
        var shadings = new TestU3D.Data().String("Quad").U32(0).U32(0).U32(2).U32(4).U32(1).U32(0).U32(0).U32(0).U32(0xFFFFFFF0);
        byte[] file = new TestU3D().Block(0xFFFFFF31, shadings).ToArray();
        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken)).Error);

        var layers = new TestU3D.Data().String("Quad").U32(0).U32(0).U32(2).U32(4).U32(1).U32(0).U32(0).U32(0).U32(1).U32(0).U32(9);
        for (int i = 0; i < 40; i++)
        {
            layers.U32(2);
        }

        byte[] tooManyLayers = new TestU3D().Block(0xFFFFFF31, layers).ToArray();
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => U3DReader.Read(tooManyLayers, cancellationToken: TestContext.Current.CancellationToken)).Error);
    }

    [Theory]
    [InlineData(new uint[] { 0, 0, 0, 1, 0, 4, 0 })] // position 4 of 4
    [InlineData(new uint[] { 0, 0, 0, 1, 1, 2, 0 })] // normal 1 of 1
    [InlineData(new uint[] { 1, 0, 0, 1, 0, 2, 0 })] // shading 1 of 1
    public void IndicesOutOfRangeAreRejected(uint[] face)
    {
        byte[] file = QuadFile().BaseMesh("Quad", Quad, Up, [], [], [], face).ToArray();

        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public void MeshesAreBoundedByTheLimits()
    {
        byte[] file = QuadFile().BaseMesh("Quad", Quad, Up, [], [], [], [0, 0, 0, 1, 0, 2, 0], [0, 0, 0, 2, 0, 3, 0]).ToArray();

        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => U3DReader.Read(file, SceneLimits.Default with { MaxIndices = 5 }, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => U3DReader.Read(file, SceneLimits.Default with { MaxVertices = 3 }, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => U3DReader.Read(file, SceneLimits.Default with { MaxMeshes = 0 }, TestContext.Current.CancellationToken)).Error);

        using Scene scene = U3DReader.Read(file, SceneLimits.Default with { MaxIndices = 6, MaxVertices = 4, MaxMeshes = 1 }, TestContext.Current.CancellationToken);
        Assert.Equal(1, scene.Meshes.Length);
    }

    [Fact]
    public void ModelsWithoutUsableGeometryKeepTheirNode()
    {
        byte[] file = new TestU3D(TestU3D.NoCompression)
            // All its detail is in progressive blocks.
            .MeshDeclaration("Progressive", 0, 100, 60, 60, 0, 0, 0, [(0, 0)], 0)
            .ProgressiveMesh("Progressive")
            .ModelNode("A", "Progressive", ("", Matrix4x4.Identity))
            // Declared with faces, but the file has no block holding any.
            .MeshDeclaration("Lost", 0, 100, 60, 60, 0, 0, 0, [(0, 0)], 0)
            .ModelNode("E", "Lost", ("", Matrix4x4.Identity))
            // Declared empty.
            .MeshDeclaration("Empty", 0, 0, 0, 0, 0, 0, 0, [], 0)
            .ModelNode("B", "Empty", ("", Matrix4x4.Identity))
            // Not declared anywhere.
            .ModelNode("C", "Nowhere", ("", Matrix4x4.Identity))
            // A base with positions and no faces.
            .MeshDeclaration("NoFaces", 0, 10, 4, 1, 0, 0, 0, [(0, 0)], 4)
            .BaseMesh("NoFaces", Quad, Up, [], [], [])
            .ModelNode("D", "NoFaces", ("", Matrix4x4.Identity))
            // A base for a mesh nobody declared.
            .BaseMesh("Stray", Quad, Up, [], [], [], [0, 0, 0, 1, 0, 2, 0])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, scene.Nodes.Length);
        Assert.All(scene.Nodes.ToArray(), n => Assert.Equal(-1, n.Mesh));
        Assert.Equal(0, scene.Meshes.Length);
        Assert.Equal(1, scene.Diagnostics.Single(d => d.Code == "mesh-progressive-only" && d.Severity == SceneDiagnosticSeverity.Warning).Count);
        Assert.Equal(2, scene.Diagnostics.Single(d => d.Code == "mesh-data-missing" && d.Severity == SceneDiagnosticSeverity.Warning).Count);
        Assert.Contains(scene.Diagnostics, d => d.Code == "mesh-empty" && d.Severity == SceneDiagnosticSeverity.Info);
        Assert.Contains(scene.Diagnostics, d => d.Code == "model-resource-missing" && d.Severity == SceneDiagnosticSeverity.Warning);
        Assert.Contains(scene.Diagnostics, d => d.Code == "mesh-undeclared");
    }

    /// <summary>
    /// A base is partial when progressive blocks add to it, not when the declaration counts more faces:
    /// real files (SimLab exports) declare twice the faces of a base that is their whole mesh.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABaseIsPartialOnlyWhenProgressiveBlocksFollow(bool progressive)
    {
        TestU3D file = QuadFile(faces: 4, positions: 8).BaseMesh("Quad", Quad, Up, [], [], [], [0, 0, 0, 1, 0, 2, 0], [0, 0, 0, 2, 0, 3, 0]);
        if (progressive)
        {
            file.ProgressiveMesh("Quad");
        }

        using Scene scene = U3DReader.Read(file.ToArray(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(progressive, (Assert.Single(scene.Meshes.ToArray()).Flags & SceneMeshFlags.PartialDetail) != 0);
        Assert.Equal(progressive, scene.Diagnostics.Any(d => d.Code == "mesh-partial-detail"));
        Assert.Equal(2, scene.Indices.Length / 3);
    }

    [Fact]
    public void NonFiniteCoordinatesAreZeroed()
    {
        Vector3[] positions = [new(0, 0, 0), new(float.NaN, 0, 0), new(1, float.PositiveInfinity, 0), new(0, 1, 0)];
        byte[] file = QuadFile().BaseMesh("Quad", positions, [new Vector3(float.NaN, 0, 1)], [], [], [], [0, 0, 0, 1, 0, 2, 0], [0, 0, 0, 2, 0, 3, 0]).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(scene.Positions.ToArray(), v => Assert.True(float.IsFinite(v)));
        Assert.All(scene.Normals.ToArray(), v => Assert.True(float.IsFinite(v)));
        Assert.Equal(Vector3.Zero, scene.Meshes[0].Bounds.Min);
        Assert.Equal(new Vector3(0, 1, 0), scene.Meshes[0].Bounds.Max);
        Assert.Contains(scene.Diagnostics, d => d.Code == "non-finite-geometry" && d.Severity == SceneDiagnosticSeverity.Warning);
    }

    [Fact]
    public void DecodingAMeshCanBeCancelled()
    {
        byte[] file = TestFiles.Scene("plane64x64_base.u3d");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => U3DReader.Read(file, null, cancellation.Token));
    }
}
