using System.Numerics;
using Caly.Rendering.Scene3D.Prc;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Markups (notes, dimensions) of an occurrence: their tessellation, which is drawing entities
/// (polylines, triangles, colours) in the model's own space, becomes hidden nodes named as the views of a
/// PDF name them, so that a view can show them.
/// </summary>
public class PrcMarkupTests
{
    private const uint Red = 0, Blue = 3;                       // colours of the test file: 3 times their index
    private const uint RedBytes = 0xFF0000FF, BlueBytes = 0xFFFF0000;

    private static Scene Read(PrcTestFile.MarkupTessellation markup, int extraDoubles = 0, uint tessellation = 2)
    {
        PrcTestFile file = PrcTestFile.Simple();
        file.Tessellations.Add(markup.Write(extraDoubles));
        file.Occurrences[^1] = file.Occurrences[^1] with { Markups = [new PrcTestFile.Markup("Note", tessellation)] };
        return PrcReader.Read(file.Build());
    }

    private static int[] NodesOf(Scene scene, string prefix)
    {
        var found = new List<int>();
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            if (scene.GetName(scene.Nodes[i].Name).StartsWith(prefix, StringComparison.Ordinal))
            {
                found.Add(i);
            }
        }

        return [.. found];
    }

    private static (SceneTopology Topology, float[] Positions, uint[] Colors, uint[] Indices) MeshOf(Scene scene, int node)
    {
        SceneMesh mesh = scene.Meshes[scene.Nodes[node].Mesh];
        SceneMeshPart[] parts = scene.Parts.Slice(mesh.FirstPart, mesh.PartCount).ToArray();
        return (
            mesh.Topology,
            scene.Positions.Slice(mesh.VertexStart * 3, mesh.VertexCount * 3).ToArray(),
            mesh.ColorStart < 0 ? [] : scene.Colors.Slice(mesh.ColorStart, mesh.VertexCount).ToArray(),
            parts.SelectMany(p => scene.Indices.Slice(p.IndexStart, p.IndexCount).ToArray()).ToArray());
    }

    [Fact]
    public void AMarkupBecomesHiddenNodesNamedAsViewsNameIt()
    {
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Color(Red).Polyline(0, 0, 0, 1, 0, 0, 1, 1, 0)
            .Color(Blue).Triangles(0, 0, 1, 1, 0, 1, 0, 1, 1));

        SceneInvariants.Check(scene);
        int[] nodes = NodesOf(scene, "Note.");
        Assert.Equal(2, nodes.Length);

        // Both carry the markup's name, so that a view that names it shows both; the occurrence they
        // are under is the root, and its identifier ends the name (as for the items of a part).
        string occurrence = scene.GetName(scene.Nodes[0].Name);
        Assert.Equal("assembly.", occurrence[..9]);
        Assert.All(nodes, n =>
        {
            Assert.Equal(scene.GetName(scene.Nodes[nodes[0]].Name), scene.GetName(scene.Nodes[n].Name));
            Assert.Equal(0, scene.Nodes[n].Parent);
            Assert.False(scene.Nodes[n].Visible);
            Assert.EndsWith(PrcNodeNames.IdentifierOf(occurrence).ToString(), scene.GetName(scene.Nodes[n].Name));
        });

        var lines = MeshOf(scene, nodes[0]);
        Assert.Equal(SceneTopology.Lines, lines.Topology);
        Assert.Equal([0f, 0, 0, 1, 0, 0, 1, 1, 0], lines.Positions);
        Assert.Equal([0u, 1, 1, 2], lines.Indices);
        Assert.Equal([RedBytes, RedBytes, RedBytes], lines.Colors);

        var triangles = MeshOf(scene, nodes[1]);
        Assert.Equal(SceneTopology.Triangles, triangles.Topology);
        Assert.Equal([0f, 0, 1, 1, 0, 1, 0, 1, 1], triangles.Positions);
        Assert.Equal([0u, 1, 2], triangles.Indices);
        Assert.Equal([BlueBytes, BlueBytes, BlueBytes], triangles.Colors);

        // Drawn in their own colours, unlit.
        SceneMaterial material = scene.Materials[scene.Bindings[scene.Nodes[nodes[0]].FirstBinding]];
        Assert.Equal(SceneMaterialFlags.UseVertexColor, material.Flags);
        Assert.Equal(Vector3.One, material.Diffuse);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Severity != SceneDiagnosticSeverity.Info);
    }

    /// <summary>
    /// A part may have markups of its own (the Tetra4D LandingGear's notes and dimensions are all its part's): they show wherever
    /// the part is used, as nodes under each occurrence that shows it, named with that occurrence's identifier as views name them.
    /// </summary>
    [Fact]
    public void AMarkupOfAPartIsUnderEachOccurrenceThatShowsThePart()
    {
        PrcTestFile file = PrcTestFile.Simple();
        file.Tessellations.Add(new PrcTestFile.MarkupTessellation().Color(Red).Polyline(0, 0, 0, 1, 0, 0).Write(0));
        file.Parts[0] = file.Parts[0] with { Markups = [new PrcTestFile.Markup("Spec", 2)] };
        using Scene scene = PrcReader.Read(file.Build(), cancellationToken: TestContext.Current.CancellationToken);

        SceneInvariants.Check(scene);
        int[] nodes = NodesOf(scene, "Spec.");
        Assert.Equal(2, nodes.Length);
        Assert.All(nodes, n =>
        {
            string occurrence = scene.GetName(scene.Nodes[scene.Nodes[n].Parent].Name);
            Assert.Matches("^(left|right)\\.", occurrence);
            Assert.EndsWith(PrcNodeNames.IdentifierOf(occurrence).ToString(), scene.GetName(scene.Nodes[n].Name));
            Assert.False(scene.Nodes[n].Visible);
        });
        Assert.NotEqual(scene.Nodes[nodes[0]].Parent, scene.Nodes[nodes[1]].Parent);
    }

    [Fact]
    public void ThePartsOfTheModelAreNotChangedByItsMarkups()
    {
        using Scene without = PrcReader.Read(PrcTestFile.Simple().Build(), cancellationToken: TestContext.Current.CancellationToken);
        using Scene with = Read(new PrcTestFile.MarkupTessellation().Color(Red).Polyline(0, 0, 0, 1, 0, 0));

        Assert.Equal(without.Nodes.Length + 1, with.Nodes.Length);
        Assert.Equal(without.Indices.Length + 2, with.Indices.Length);
        Assert.True(PrcReader.IsComplete(with));
    }

    [Fact]
    public void EachPolylineIsOneConnectedRunOfSegmentsInItsColour()
    {
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Color(Red).Polyline(0, 0, 0, 1, 0, 0)
            .Color(Blue).Polyline(0, 1, 0, 1, 1, 0, 2, 1, 0));

        var lines = MeshOf(scene, NodesOf(scene, "Note.")[0]);
        Assert.Equal(SceneTopology.Lines, lines.Topology);
        Assert.Equal([0u, 1, 2, 3, 3, 4], lines.Indices);
        Assert.Equal([RedBytes, RedBytes, BlueBytes, BlueBytes, BlueBytes], lines.Colors);
        Assert.Single(NodesOf(scene, "Note."));
    }

    [Fact]
    public void ATriangleListHasWholeTrianglesOnly()
    {
        // Writers give the list one point more than whole triangles (every sample of the corpus does).
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Color(Red).Triangles(0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 5, 5, 5));

        var triangles = MeshOf(scene, NodesOf(scene, "Note.")[0]);
        Assert.Equal(SceneTopology.Triangles, triangles.Topology);
        Assert.Equal(6, triangles.Indices.Length);
        Assert.Equal(6, triangles.Positions.Length / 3);
    }

    [Fact]
    public void AQuadIsTwoTriangles()
    {
        using Scene scene = Read(new PrcTestFile.MarkupTessellation().Color(Blue).Quads(0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0));

        var quad = MeshOf(scene, NodesOf(scene, "Note.")[0]);
        Assert.Equal(SceneTopology.Triangles, quad.Topology);
        Assert.Equal([0u, 1, 2, 0, 2, 3], quad.Indices);
        Assert.Equal([BlueBytes, BlueBytes, BlueBytes, BlueBytes], quad.Colors);
    }

    [Fact]
    public void WithoutAColourThePolylineIsDark()
    {
        using Scene scene = Read(new PrcTestFile.MarkupTessellation().Polyline(0, 0, 0, 1, 0, 0));

        Assert.Equal([0xFF202020u, 0xFF202020u], MeshOf(scene, NodesOf(scene, "Note.")[0]).Colors);
    }

    [Fact]
    public void AColourThatIsNotInTheFileIsTheDarkOne()
    {
        using Scene scene = Read(new PrcTestFile.MarkupTessellation().Color(300).Polyline(0, 0, 0, 1, 0, 0));

        Assert.Equal([0xFF202020u, 0xFF202020u], MeshOf(scene, NodesOf(scene, "Note.")[0]).Colors);
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-missing-colour");
    }

    [Fact]
    public void WhatCannotBeDrawnIsSaidAndTheRestIsStillShown()
    {
        // A symbol (9) needs a picture.
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Color(Red).Polyline(0, 0, 0, 1, 0, 0).Other(9, [0, 0, 0], 0));

        Assert.Single(NodesOf(scene, "Note."));
        SceneDiagnostic said = Assert.Single(scene.Diagnostics, d => d.Code == "prc-markup-entity-not-shown");
        Assert.Equal(SceneDiagnosticSeverity.Info, said.Severity);
        Assert.True(PrcReader.IsComplete(scene));
    }

    [Fact]
    public void ABlockThatPlacesWhatItHoldsIsLeftOutWholeAndWhatFollowsIsStillRight()
    {
        // A face-view block (6) turns what it holds to the screen: not done, so it is not drawn, and the
        // doubles it holds are not those of the polyline after it.
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Color(Red)
            .Block(6, [1, 2, 3], inside => inside.Polyline(9, 9, 9, 8, 8, 8))
            .Color(Blue).Polyline(0, 0, 0, 1, 0, 0));

        int node = Assert.Single(NodesOf(scene, "Note."));
        var lines = MeshOf(scene, node);
        Assert.Equal([0f, 0, 0, 1, 0, 0], lines.Positions);
        Assert.Equal([BlueBytes, BlueBytes], lines.Colors);
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-markup-entity-not-shown");
    }

    /// <summary>
    /// A matrix block multiplies what it holds by its matrix (16 numbers, column after column), after any matrix around it; what
    /// follows the block is placed as before it.
    /// </summary>
    [Fact]
    public void AMatrixBlockPlacesWhatItHolds()
    {
        double[] scaleTwoMoveTen = [2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 2, 0, 10, 0, 0, 1];
        double[] moveUpOne = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 1, 0, 1];
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Color(Red)
            .Matrix(scaleTwoMoveTen, inside => inside.Polyline(0, 0, 0, 1, 0, 0).Matrix(moveUpOne, deeper => deeper.Polyline(0, 0, 0, 1, 0, 0)))
            .Polyline(5, 5, 5, 6, 5, 5));

        int node = Assert.Single(NodesOf(scene, "Note."));
        var lines = MeshOf(scene, node);
        Assert.Equal([10f, 0, 0, 12, 0, 0, 10, 2, 0, 12, 2, 0, 5, 5, 5, 6, 5, 5], lines.Positions);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "prc-markup-entity-not-shown");
    }

    /// <summary>
    /// A text is drawn as strokes in the colour that holds, filling its box: as wide as it says, and its capitals as high as it says,
    /// on the line y = 0 of the matrix that places it (the Tetra4D LandingGear's notes).
    /// </summary>
    [Fact]
    public void ATextIsDrawnAsStrokesFillingItsBox()
    {
        double[] place = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 100, 50, 7, 1];
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Color(Blue).Font(1)
            .Matrix(place, inside => inside.Text("HI", 20, 6)));

        SceneInvariants.Check(scene);
        int node = Assert.Single(NodesOf(scene, "Note."));
        var lines = MeshOf(scene, node);
        Assert.Equal(SceneTopology.Lines, lines.Topology);
        Assert.All(lines.Colors, c => Assert.Equal(BlueBytes, c));

        float[] x = [.. lines.Positions.Where((_, i) => i % 3 == 0)], y = [.. lines.Positions.Where((_, i) => i % 3 == 1)], z = [.. lines.Positions.Where((_, i) => i % 3 == 2)];
        Assert.Equal(100f, x.Min(), 3);
        Assert.InRange(x.Max(), 100f + 14, 100f + 20);
        Assert.Equal(50f, y.Min(), 3);
        Assert.Equal(56f, y.Max(), 3);
        Assert.All(z, v => Assert.Equal(7f, v));
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "prc-markup-entity-not-shown");
    }

    /// <summary>
    /// What a block's start says it holds is not to be trusted: a sample's frame-draw block says 69 numbers where the tessellation
    /// has 54, all its entities take. The entities are walked, those left out too, each taking the numbers it says it takes.
    /// </summary>
    [Fact]
    public void TheNumbersOfABlockAreThoseOfWhatItHolds()
    {
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Block(7, [1, 2, 3], inside => inside.Polyline(9, 9, 9, 8, 8, 8), claimedDoubles: 40)
            .Color(Blue).Polyline(0, 0, 0, 1, 0, 0));

        var lines = MeshOf(scene, Assert.Single(NodesOf(scene, "Note.")));
        Assert.Equal([0f, 0, 0, 1, 0, 0], lines.Positions);
        Assert.Equal([BlueBytes, BlueBytes], lines.Colors);
    }

    [Fact]
    public void ATextNamingAStringThatIsNotThereIsNotDrawn()
    {
        using Scene scene = Read(new PrcTestFile.MarkupTessellation().Color(Red).Polyline(0, 0, 0, 1, 0, 0).Other(14, [4, 2], 5));
        var lines = MeshOf(scene, Assert.Single(NodesOf(scene, "Note.")));
        Assert.Equal([0f, 0, 0, 1, 0, 0], lines.Positions);
    }

    [Fact]
    public void AMarkupWithNothingToDrawHasNoNode()
    {
        using Scene scene = Read(new PrcTestFile.MarkupTessellation().Color(Red));

        Assert.Empty(NodesOf(scene, "Note."));
    }

    [Fact]
    public void AMarkupNamingNoTessellationHasNoNodeAndIsSaid()
    {
        using Scene none = Read(new PrcTestFile.MarkupTessellation().Polyline(0, 0, 0, 1, 0, 0), tessellation: 0);
        using Scene missing = Read(new PrcTestFile.MarkupTessellation().Polyline(0, 0, 0, 1, 0, 0), tessellation: 9);

        Assert.Empty(NodesOf(none, "Note."));
        Assert.Empty(NodesOf(missing, "Note."));
        Assert.Contains(missing.Diagnostics, d => d.Code == "prc-missing-tessellation");
    }

    [Fact]
    public void AMarkupWhoseTessellationIsOfAnotherKindHasNoNode()
    {
        // Tessellation 1 is the plate's triangle.
        using Scene scene = Read(new PrcTestFile.MarkupTessellation().Polyline(0, 0, 0, 1, 0, 0), tessellation: 1);

        Assert.Empty(NodesOf(scene, "Note."));
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-missing-tessellation");
    }

    [Fact]
    public void CodesThatAskForMoreDoublesThanThereAreAreRefused()
    {
        // One point's worth of the polyline's doubles is missing.
        var markup = new PrcTestFile.MarkupTessellation().Polyline(0, 0, 0, 1, 0, 0);

        Scene3DException e = Assert.Throws<Scene3DException>(() => Read(markup, extraDoubles: -3));
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }
}
