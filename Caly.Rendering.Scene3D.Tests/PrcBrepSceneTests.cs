using System.Numerics;
using Caly.Rendering.Scene3D.Prc;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>Solids that have exact geometry in compressed form, drawn in the scene a whole PRC file makes.</summary>
public sealed class PrcBrepSceneTests
{
    private static Action<PrcBrepWriter> Line(BrepVertex a, BrepVertex b) => c => c.NewCurve(l => l.Line(a, b));

    /// <summary>A compressed body of one iso plane face: a 2 by 3 rectangle in the plane z = 0, two sides stored and two deduced.</summary>
    private static PrcBitWriter Rectangle(double x = 0)
    {
        var w = new PrcBrepWriter(0.01, 5);
        w.BodyStart(isoVertices: 4, isoEdges: 4, singleConnex: true);
        w.Shell((f => f.IsoFace(2, PrcBrepWriter.PlaneNormal(0, 0, true), true, true,
            Line(BrepVertex.New(x, 0, 0), BrepVertex.New(x + 2, 0, 0)), Line(BrepVertex.Ref(1), BrepVertex.New(x + 2, 3, 0)),
            third: null, fourth: null, common: BrepVertex.New(x, 3, 0)), true));
        return w.EndBody();
    }

    /// <summary>A file whose parts are solids, one per body of the first topological context (and the tessellation of the first, when asked).</summary>
    private static PrcTestFile Solids(bool withTessellation = false, params PrcBitWriter[] bodies) => Solids(null, withTessellation, bodies);

    private static PrcTestFile Solids(double? scale, bool withTessellation, PrcBitWriter[] bodies)
    {
        var file = new PrcTestFile { ContextScale = scale };
        file.Colors.Add([1, 0, 0]);
        file.Styles.Add((1, -1));
        if (withTessellation)
        {
            file.Tessellations.Add(PrcTestFile.Tessellation(file.AuthoringVersion, [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 3, 6]));
        }

        file.TopologicalContexts.Add([.. bodies]);
        for (int i = 0; i < bodies.Length; i++)
        {
            file.Parts.Add(new PrcTestFile.Part($"part {i}", $"solid {i}", i == 0 && withTessellation ? 1u : 0u, BiasedStyle: 1, Body: (0, (uint)i)));
            file.Occurrences.Add(new PrcTestFile.Occurrence($"occurrence {i}", BiasedPart: (uint)(i + 1)));
        }

        if (bodies.Length > 1)
        {
            file.Occurrences.Add(new PrcTestFile.Occurrence("assembly", Children: [.. Enumerable.Range(0, bodies.Length).Select(i => (uint)i)]));
        }

        return file;
    }

    private static int NodesWithAMesh(Scene scene) => scene.Nodes.ToArray().Count(n => n.Mesh >= 0);

    [Fact]
    public void AnItemWithExactGeometryOnlyIsDrawnFromItsCompressedBody()
    {
        using Scene scene = PrcReader.Read(Solids(false, Rectangle()).Build(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal(1, NodesWithAMesh(scene));
        Assert.Equal(6, scene.Indices.Length);                      // a rectangle: two triangles
        Assert.True(PrcReader.IsComplete(scene));
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "prc-exact-geometry-only");
    }

    [Theory]
    [InlineData(1000.0, 1000f)]
    [InlineData(39.3701, 39.3701f)]
    [InlineData(0.0, 1f)]          // a scale of 0 (one file of the corpus) is no scale
    public void TheScaleOfTheContextScalesItsBodies(double scale, float factor)
    {
        using Scene scene = PrcReader.Read(Solids(scale, false, [Rectangle()]).Build(), cancellationToken: TestContext.Current.CancellationToken);

        SceneBounds bounds = scene.Meshes[0].Bounds;
        Assert.Equal(2f * factor, bounds.Max.X - bounds.Min.X, 0.01f * factor);
        Assert.Equal(3f * factor, bounds.Max.Y - bounds.Min.Y, 0.01f * factor);
    }

    [Fact]
    public void AnItemWithBothATessellationAndExactGeometryIsDrawnFromItsTessellation()
    {
        using Scene scene = PrcReader.Read(Solids(true, Rectangle()).Build(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal(3, scene.Indices.Length);                      // the writer's one triangle, not the rectangle
        Assert.True(PrcReader.IsComplete(scene));
    }

    [Fact]
    public void TwoItemsShowingTheSameBodyShareOneMesh()
    {
        PrcTestFile file = Solids(false, Rectangle());
        file.Occurrences.Add(new PrcTestFile.Occurrence("second", BiasedPart: 1, Translation: [5, 0, 0]));
        file.Occurrences.Add(new PrcTestFile.Occurrence("assembly", Children: [0, 1]));

        using Scene scene = PrcReader.Read(file.Build(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal(2, NodesWithAMesh(scene));
        Assert.Equal(6, scene.Indices.Length);
    }

    [Fact]
    public void AnUncompressedBodyIsDrawnAlongsideTheCompressedOnes()
    {
        // Phase B: an uncompressed body is drawn too (this one has no connex: nothing to draw, and nothing missing).
        using Scene scene = PrcReader.Read(Solids(false, Rectangle(), PrcTestFile.EmptyBrepBody()).Build(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, NodesWithAMesh(scene));
        Assert.True(PrcReader.IsComplete(scene));
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code is PrcReader.UncompressedBrepDiagnostic or PrcReader.BrepDiagnostic);
    }

    [Fact]
    public void ABodyThatDoesNotReadMakesTheSceneIncompleteAndTheRestIsDrawn()
    {
        PrcBitWriter broken = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TOPO_BrepDataCompress).Bits(0xFFFF, 16);
        PrcTestFile file = Solids(true, broken);                    // the part has a tessellation as well; add a second part that has the broken body only
        file.Parts.Add(new PrcTestFile.Part("part b", "solid b", 0, BiasedStyle: 1, Body: (0, 0)));
        file.Occurrences.Add(new PrcTestFile.Occurrence("b", BiasedPart: 2));
        file.Occurrences.Add(new PrcTestFile.Occurrence("assembly", Children: [0, 1]));

        using Scene scene = PrcReader.Read(file.Build(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, NodesWithAMesh(scene));                     // the tessellated one
        Assert.False(PrcReader.IsComplete(scene));
        Assert.Contains(scene.Diagnostics, d => d.Code == PrcReader.BrepDiagnostic && d.Severity == SceneDiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(5, 0)]                                              // a context the section does not have
    [InlineData(0, 9)]                                              // a body the context does not have
    public void AnItemWhoseBodyIndexIsPastTheSectionIsReportedNotThrown(int context, int body)
    {
        PrcTestFile file = Solids(false, Rectangle());
        file.Parts[0] = file.Parts[0] with { Body = ((uint)context, (uint)body) };

        using Scene scene = PrcReader.Read(file.Build(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, NodesWithAMesh(scene));
        Assert.False(PrcReader.IsComplete(scene));
        Assert.Contains(scene.Diagnostics, d => d.Code == PrcReader.BrepDiagnostic);
    }

    [Fact]
    public void TheBodyIsDrawnInItsItemsStyle()
    {
        using Scene scene = PrcReader.Read(Solids(false, Rectangle()).Build(), cancellationToken: TestContext.Current.CancellationToken);

        SceneNode node = scene.Nodes.ToArray().Single(n => n.Mesh >= 0);

        Assert.Equal(1.0f, scene.Materials[scene.Bindings[node.FirstBinding]].Diffuse.X);
        Assert.Equal(0.0f, scene.Materials[scene.Bindings[node.FirstBinding]].Diffuse.Y);
    }

    [Fact]
    public void TheDrawnBodyHasTheBodysPoints()
    {
        using Scene scene = PrcReader.Read(Solids(false, Rectangle(10)).Build(), cancellationToken: TestContext.Current.CancellationToken);

        ReadOnlySpan<float> p = scene.Positions;
        float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < p.Length; i += 3)
        {
            minX = Math.Min(minX, p[i]);
            maxX = Math.Max(maxX, p[i]);
            maxY = Math.Max(maxY, p[i + 1]);
        }

        Assert.Equal(10f, minX, 1e-3);
        Assert.Equal(12f, maxX, 1e-3);
        Assert.Equal(3f, maxY, 1e-3);
    }

    /// <summary>
    /// A body of one shell with two faces, in this order: an ana plane face (a 2 by 2 square at x = 5) and an iso plane face (a 2 by 3
    /// rectangle at x = 0). The file stores the iso face first, then says the kind of face at each place of the shell.
    /// </summary>
    private static PrcBitWriter SquareThenRectangle()
    {
        var w = new PrcBrepWriter(0.01, 5);
        w.BodyStart(isoVertices: 4, isoEdges: 4, singleConnex: true);
        var square = new PrcBrepWriter.AnaLoop(true,
            Line(BrepVertex.New(5, 0, 0), BrepVertex.New(7, 0, 0)), Line(BrepVertex.Ref(5), BrepVertex.New(7, 2, 0)),
            Line(BrepVertex.Ref(6), BrepVertex.New(5, 2, 0)), Line(BrepVertex.Ref(7), BrepVertex.Ref(4)));
        w.Shell(
            (f => f.AnaFace(8, PrcBrepWriter.PlaneNormal(0, 0, true), true, [square], null, null), false),
            (f => f.IsoFace(2, PrcBrepWriter.PlaneNormal(0, 0, true), true, true,
                Line(BrepVertex.New(0, 0, 0), BrepVertex.New(2, 0, 0)), Line(BrepVertex.Ref(1), BrepVertex.New(2, 3, 0)),
                third: null, fourth: null, common: BrepVertex.New(0, 3, 0)), true));
        return w.EndBody();
    }

    /// <summary>The solid of <see cref="SquareThenRectangle"/> in a red item, with green (style 2) and blue (style 3) to give its faces.</summary>
    private static PrcTestFile TwoFaces(params PrcTestFile.FaceGraphics[] faces)
    {
        var file = new PrcTestFile { ContextFaceGraphics = [faces] };
        file.Colors.AddRange([[1, 0, 0], [0, 1, 0], [0, 0, 1]]);
        file.Styles.AddRange([(1, -1), (4, -1), (7, -1)]);                // a colour is named by three times its index, plus one
        file.TopologicalContexts.Add([SquareThenRectangle()]);
        file.Parts.Add(new PrcTestFile.Part("part", "solid", 0, BiasedStyle: 1, Body: (0, 0)));
        file.Occurrences.Add(new PrcTestFile.Occurrence("occurrence", BiasedPart: 1));
        return file;
    }

    private static readonly Vector3 Red = new(1, 0, 0), Green = new(0, 1, 0), Blue = new(0, 0, 1);

    /// <summary>The colours the triangles of the square (x beyond 4) and of the rectangle are drawn in, each once.</summary>
    private static (Vector3[] Square, Vector3[] Rectangle) FaceColours(Scene scene)
    {
        SceneNode node = scene.Nodes.ToArray().Single(n => n.Mesh >= 0);
        SceneMesh mesh = scene.Meshes[node.Mesh];
        var square = new HashSet<Vector3>();
        var rectangle = new HashSet<Vector3>();
        for (int p = 0; p < mesh.PartCount; p++)
        {
            SceneMeshPart part = scene.Parts[mesh.FirstPart + p];
            Vector3 colour = scene.Materials[scene.Bindings[node.FirstBinding + part.ShadingId]].Diffuse;
            for (int i = part.IndexStart; i < part.IndexStart + part.IndexCount; i += 3)
            {
                float x = 0;
                for (int k = 0; k < 3; k++)
                {
                    x += scene.Positions[(mesh.VertexStart + (int)scene.Indices[i + k]) * 3] / 3;
                }

                (x > 4 ? square : rectangle).Add(colour);
            }
        }

        return ([.. square], [.. rectangle]);
    }

    [Fact]
    public void EachFaceIsDrawnInTheStyleTheExtraGeometryGivesItInTheOrderOfTheShell()
    {
        using Scene scene = PrcReader.Read(TwoFaces(PrcTestFile.FaceGraphics.Style(2), PrcTestFile.FaceGraphics.Style(3)).Build(),
            cancellationToken: TestContext.Current.CancellationToken);

        (Vector3[] square, Vector3[] rectangle) = FaceColours(scene);

        Assert.Equal([Green], square);                              // first in the shell, though stored second
        Assert.Equal([Blue], rectangle);
    }

    [Fact]
    public void AFaceThatUsesTheCurrentGraphicsHasTheLastGivenAndOneWithoutGraphicsHasItsItems()
    {
        using Scene current = PrcReader.Read(TwoFaces(PrcTestFile.FaceGraphics.Style(3), PrcTestFile.FaceGraphics.Current).Build(),
            cancellationToken: TestContext.Current.CancellationToken);
        using Scene none = PrcReader.Read(TwoFaces(PrcTestFile.FaceGraphics.None, PrcTestFile.FaceGraphics.Style(2)).Build(),
            cancellationToken: TestContext.Current.CancellationToken);

        (Vector3[] square, Vector3[] rectangle) = FaceColours(current);
        Assert.Equal([Blue], square);
        Assert.Equal([Blue], rectangle);

        (square, rectangle) = FaceColours(none);
        Assert.Equal([Red], square);
        Assert.Equal([Green], rectangle);
    }

    [Fact]
    public void FaceGraphicsThatDoNotCountTheFacesOfTheContextAreNotUsed()
    {
        using Scene scene = PrcReader.Read(TwoFaces(PrcTestFile.FaceGraphics.Style(2)).Build(), cancellationToken: TestContext.Current.CancellationToken);

        (Vector3[] square, Vector3[] rectangle) = FaceColours(scene);

        Assert.Equal([Red], square);
        Assert.Equal([Red], rectangle);
        Assert.True(PrcReader.IsComplete(scene));
    }
}
