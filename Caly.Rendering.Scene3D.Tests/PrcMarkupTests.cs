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
        // A text (entity 14) needs a font; a symbol (9) a picture.
        using Scene scene = Read(new PrcTestFile.MarkupTessellation()
            .Other(14, [4, 2], 0).Color(Red).Polyline(0, 0, 0, 1, 0, 0).Other(9, [0, 0, 0], 0));

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
