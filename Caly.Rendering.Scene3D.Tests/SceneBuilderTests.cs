using System.Numerics;

namespace Caly.Rendering.Scene3D.Tests;

public class SceneBuilderTests
{
    /// <summary>A quad made of two triangles, with normals, under one model node.</summary>
    private static Scene BuildQuad(SceneLimits? limits = null)
    {
        using var builder = new SceneBuilder(limits);

        int material = builder.AddMaterial(new SceneMaterial { Diffuse = new Vector3(1, 0, 0), Opacity = 1, Name = builder.AddName("red") });

        builder.BeginMesh(builder.AddName("quad"), SceneMeshFlags.HasNormals);
        SceneVertexSpans vertices = builder.AllocateVertices(4);
        float[] positions = [0, 0, 0, 1, 0, 0, 1, 2, 0, 0, 2, 0];
        positions.CopyTo(vertices.Positions);
        for (int i = 0; i < 4; i++)
        {
            vertices.Normals[i * 3 + 2] = 1;
        }

        Span<uint> indices = builder.AllocateIndices(6);
        uint[] quad = [0, 1, 2, 0, 2, 3];
        quad.CopyTo(indices);
        builder.AddPart(6, shadingId: 0);
        int mesh = builder.EndMesh();

        int root = builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Group, Mesh = -1, Light = -1, Camera = -1, Name = builder.AddName("root"), Visible = true });
        int binding = builder.AddBinding(material);
        builder.AddNode(new SceneNode
        {
            Parent = root, Local = Matrix4x4.CreateTranslation(10, 0, 0), Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1,
            FirstBinding = binding, BindingCount = 1, Name = builder.AddName("quad-instance"), Visible = true
        });

        return builder.Build();
    }

    [Fact]
    public void BuildsAndReadsBack()
    {
        using Scene scene = BuildQuad();

        Assert.Equal(2, scene.Nodes.Length);
        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal(1, scene.Parts.Length);
        Assert.Equal(1, scene.Materials.Length);
        Assert.True(scene.HasGeometry);

        SceneMesh mesh = scene.Meshes[0];
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal("quad", scene.GetName(mesh.Name));
        Assert.True(mesh.Flags.HasFlag(SceneMeshFlags.HasNormals));
        Assert.False(mesh.Flags.HasFlag(SceneMeshFlags.HasTexCoords));
        Assert.Equal(-1, mesh.TexCoordStart);
        Assert.Equal(-1, mesh.ColorStart);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Bounds.Min);
        Assert.Equal(new Vector3(1, 2, 0), mesh.Bounds.Max);

        Assert.Equal(12, scene.Positions.Length);
        Assert.Equal(12, scene.Normals.Length);
        Assert.Equal(0, scene.TexCoords.Length);
        Assert.Equal(2f, scene.Positions[mesh.VertexStart * 3 + 7]);
        Assert.Equal(1f, scene.Normals[mesh.NormalStart * 3 + 2]);

        SceneMeshPart part = scene.Parts[mesh.FirstPart];
        Assert.Equal(6, part.IndexCount);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 2, 3 }, scene.Indices.Slice(part.IndexStart, part.IndexCount).ToArray());

        SceneNode instance = scene.Nodes[1];
        Assert.Equal(0, instance.Parent);
        Assert.Equal(SceneNodeKind.Model, instance.Kind);
        Assert.Equal("quad-instance", scene.GetName(instance.Name));
        Assert.Equal(0, scene.Bindings[instance.FirstBinding]);
        Assert.Equal("red", scene.GetName(scene.Materials[0].Name));
    }

    /// <summary>A mesh is triangles unless it says otherwise: line segments take two indices each, points one.</summary>
    [Fact]
    public void AMeshSaysWhetherItIsTrianglesLinesOrPoints()
    {
        using var builder = new SceneBuilder();
        foreach (SceneTopology topology in new[] { SceneTopology.Lines, SceneTopology.Points })
        {
            builder.BeginMesh(builder.AddName(topology.ToString()), SceneMeshFlags.None, topology);
            SceneVertexSpans vertices = builder.AllocateVertices(3);
            new float[] { 0, 0, 0, 1, 0, 0, 1, 2, 0 }.CopyTo(vertices.Positions);
            Span<uint> indices = builder.AllocateIndices(topology == SceneTopology.Lines ? 4 : 3);
            (topology == SceneTopology.Lines ? new uint[] { 0, 1, 1, 2 } : [0, 1, 2]).CopyTo(indices);
            builder.AddPart(indices.Length, shadingId: 0);
            builder.EndMesh();
        }

        using Scene quad = BuildQuad();
        using Scene scene = builder.Build();

        Assert.Equal(SceneTopology.Triangles, quad.Meshes[0].Topology);
        Assert.Equal((SceneTopology.Lines, SceneTopology.Points), (scene.Meshes[0].Topology, scene.Meshes[1].Topology));
        Assert.Equal((4, 3), (scene.Parts[0].IndexCount, scene.Parts[1].IndexCount));
        Assert.Equal(new Vector3(1, 2, 0), scene.Meshes[1].Bounds.Max);
    }

    [Fact]
    public void NamesAreShared()
    {
        using var builder = new SceneBuilder();

        int a = builder.AddName("part");
        int b = builder.AddName("part");
        int c = builder.AddName("other");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void WorldMatrixAndBoundsFollowTheNodeTree()
    {
        using Scene scene = BuildQuad();

        Assert.Equal(Matrix4x4.CreateTranslation(10, 0, 0), scene.GetWorldMatrix(1));

        SceneBounds bounds = scene.ComputeWorldBounds();
        Assert.False(bounds.IsEmpty);
        Assert.Equal(new Vector3(10, 0, 0), bounds.Min);
        Assert.Equal(new Vector3(11, 2, 0), bounds.Max);
    }

    [Fact]
    public void BuffersGrowWithoutLosingData()
    {
        using var builder = new SceneBuilder();
        const int meshes = 300, perMesh = 1000;

        for (int m = 0; m < meshes; m++)
        {
            builder.BeginMesh(builder.AddName("m" + m), SceneMeshFlags.None);
            SceneVertexSpans v = builder.AllocateVertices(perMesh);
            for (int i = 0; i < perMesh; i++)
            {
                v.Positions[i * 3] = m;
                v.Positions[i * 3 + 1] = i;
            }

            Span<uint> indices = builder.AllocateIndices(3);
            indices[0] = 0;
            indices[1] = 1;
            indices[2] = (uint)(perMesh - 1);
            builder.AddPart(3, 0);
            builder.EndMesh();
        }

        using Scene scene = builder.Build();

        Assert.Equal(meshes, scene.Meshes.Length);
        Assert.Equal(meshes * perMesh * 3, scene.Positions.Length);
        for (int m = 0; m < meshes; m += 37)
        {
            SceneMesh mesh = scene.Meshes[m];
            Assert.Equal((float)m, scene.Positions[mesh.VertexStart * 3]);
            Assert.Equal(999f, scene.Positions[(mesh.VertexStart + 999) * 3 + 1]);
            Assert.Equal((uint)(perMesh - 1), scene.Indices[scene.Parts[mesh.FirstPart].IndexStart + 2]);
        }
    }

    [Fact]
    public void TrimmingVerticesShrinksTheMesh()
    {
        using var builder = new SceneBuilder();
        builder.BeginMesh(builder.AddName("m"), SceneMeshFlags.HasNormals | SceneMeshFlags.HasTexCoords);
        builder.AllocateVertices(100);
        builder.TrimVertices(7);
        builder.AllocateIndices(3);
        builder.AddPart(3, 0);
        builder.EndMesh();

        using Scene scene = builder.Build();

        Assert.Equal(7, scene.Meshes[0].VertexCount);
        Assert.Equal(21, scene.Positions.Length);
        Assert.Equal(21, scene.Normals.Length);
        Assert.Equal(14, scene.TexCoords.Length);
    }

    [Fact]
    public void ExceedingALimitThrows()
    {
        var limits = SceneLimits.Default with { MaxVertices = 3 };
        var vertexError = Assert.Throws<Scene3DException>(() => BuildQuad(limits));
        Assert.Equal(Scene3DError.LimitExceeded, vertexError.Error);

        var indexError = Assert.Throws<Scene3DException>(() => BuildQuad(SceneLimits.Default with { MaxIndices = 5 }));
        Assert.Equal(Scene3DError.LimitExceeded, indexError.Error);

        var nodeError = Assert.Throws<Scene3DException>(() => BuildQuad(SceneLimits.Default with { MaxNodes = 1 }));
        Assert.Equal(Scene3DError.LimitExceeded, nodeError.Error);

        var meshError = Assert.Throws<Scene3DException>(() => BuildQuad(SceneLimits.Default with { MaxMeshes = 0 }));
        Assert.Equal(Scene3DError.LimitExceeded, meshError.Error);

        var materialError = Assert.Throws<Scene3DException>(() => BuildQuad(SceneLimits.Default with { MaxMaterials = 0 }));
        Assert.Equal(Scene3DError.LimitExceeded, materialError.Error);

        var totalError = Assert.Throws<Scene3DException>(() => BuildQuad(SceneLimits.Default with { MaxTotalBytes = 16 }));
        Assert.Equal(Scene3DError.LimitExceeded, totalError.Error);
    }

    [Fact]
    public void ReleaseGeometryKeepsEverythingElse()
    {
        using Scene scene = BuildQuad();
        long before = scene.GeometryBytes;

        scene.ReleaseGeometry();

        Assert.True(before > 0);
        Assert.False(scene.HasGeometry);
        Assert.Equal(0, scene.GeometryBytes);
        Assert.Equal(0, scene.Positions.Length);
        Assert.Equal(0, scene.Indices.Length);
        Assert.Equal(2, scene.Nodes.Length);
        Assert.Equal(4, scene.Meshes[0].VertexCount);
        Assert.Equal(new Vector3(1, 2, 0), scene.Meshes[0].Bounds.Max);
        Assert.False(scene.ComputeWorldBounds().IsEmpty);
    }

    [Fact]
    public void AccessAfterDisposeThrows()
    {
        Scene scene = BuildQuad();
        scene.Dispose();
        scene.Dispose(); // idempotent

        Assert.Throws<ObjectDisposedException>(() => scene.Nodes.Length);
        Assert.Throws<ObjectDisposedException>(() => scene.Positions.Length);
        Assert.Throws<ObjectDisposedException>(() => scene.GetName(0));
        Assert.Throws<ObjectDisposedException>(scene.ReleaseGeometry);
    }

    [Fact]
    public void BuilderCanBeAbandoned()
    {
        var builder = new SceneBuilder();
        builder.BeginMesh(builder.AddName("m"), SceneMeshFlags.None);
        builder.AllocateVertices(10_000);
        builder.Dispose();
        builder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => builder.AddName("x"));
    }

    [Fact]
    public void BuildTwiceIsRejected()
    {
        var builder = new SceneBuilder();
        using Scene scene = builder.Build();

        Assert.Throws<ObjectDisposedException>(() => builder.Build());
    }

    [Fact]
    public void DiagnosticsAreMergedByCode()
    {
        using var builder = new SceneBuilder();
        builder.Report(SceneDiagnosticSeverity.Warning, "skipped-block", "Point Set");
        builder.Report(SceneDiagnosticSeverity.Warning, "skipped-block", "Point Set");
        builder.Report(SceneDiagnosticSeverity.Info, "partial-detail", "mesh A");

        using Scene scene = builder.Build();

        Assert.Equal(2, scene.Diagnostics.Count);
        Assert.Equal(2, scene.Diagnostics[0].Count);
        Assert.Equal("skipped-block", scene.Diagnostics[0].Code);
        Assert.Equal(1, scene.Diagnostics[1].Count);
    }

    [Fact]
    public void MisuseOfTheMeshApiIsReported()
    {
        using var builder = new SceneBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.AllocateVertices(1));
        Assert.Throws<InvalidOperationException>(() => builder.EndMesh());

        builder.BeginMesh(builder.AddName("m"), SceneMeshFlags.None);
        Assert.Throws<InvalidOperationException>(() => builder.BeginMesh(0, SceneMeshFlags.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddPart(3, 0)); // no indices allocated
    }

    /// <summary>
    /// A hidden instance is not part of what the scene shows: exporters park helper geometry (a bounding
    /// box many times the model's size, in one real file) in hidden nodes.
    /// </summary>
    [Fact]
    public void WorldBoundsCoverOnlyVisibleInstances()
    {
        using var builder = new SceneBuilder();
        builder.BeginMesh(builder.AddName("Triangle"), SceneMeshFlags.None);
        new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }.CopyTo(builder.AllocateVertices(3).Positions);
        new uint[] { 0, 1, 2 }.CopyTo(builder.AllocateIndices(3));
        builder.AddPart(3, 0);
        int mesh = builder.EndMesh();

        int name = builder.AddName("Node");
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, Name = name, Visible = true });
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.CreateScale(100), Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, Name = name, Visible = false });
        using Scene scene = builder.Build();

        SceneBounds bounds = scene.ComputeWorldBounds();
        Assert.Equal(Vector3.Zero, bounds.Min);
        Assert.Equal(new Vector3(1, 1, 0), bounds.Max);
    }

    /// <summary>
    /// Coordinates can be finite and still too large to transform: an instance whose box cannot be placed
    /// is left out, so that the bounds a camera is fitted to are always finite.
    /// </summary>
    [Fact]
    public void WorldBoundsStayFiniteWhateverTheTransforms()
    {
        using var builder = new SceneBuilder();
        builder.BeginMesh(builder.AddName("Triangle"), SceneMeshFlags.None);
        new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }.CopyTo(builder.AllocateVertices(3).Positions);
        new uint[] { 0, 1, 2 }.CopyTo(builder.AllocateIndices(3));
        builder.AddPart(3, 0);
        int mesh = builder.EndMesh();

        var nan = Matrix4x4.Identity;
        nan.M11 = float.NaN;
        int name = builder.AddName("Node");
        foreach (Matrix4x4 local in new[] { Matrix4x4.CreateTranslation(5, 0, 0), Matrix4x4.CreateScale(float.MaxValue) * Matrix4x4.CreateScale(4), nan, Matrix4x4.CreateTranslation(float.PositiveInfinity, 0, 0) })
        {
            builder.AddNode(new SceneNode { Parent = -1, Local = local, Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, Name = name, Visible = true });
        }

        using Scene scene = builder.Build();

        SceneBounds bounds = scene.ComputeWorldBounds();
        Assert.False(bounds.IsEmpty);
        Assert.Equal(new Vector3(5, 0, 0), bounds.Min);
        Assert.Equal(new Vector3(6, 1, 0), bounds.Max);
    }
}
