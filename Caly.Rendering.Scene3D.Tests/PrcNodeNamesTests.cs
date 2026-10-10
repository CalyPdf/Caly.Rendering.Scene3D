using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The names of the nodes of a PRC scene are the names the views of a PDF use: a display name, a dot
/// and an identifier of the entity. The identifiers below are those of a real document's views (a
/// model written by NX), for the file structure and entity numbers its PRC file has.
/// </summary>
public class PrcNodeNamesTests
{
    private static readonly PrcUniqueId Part = new() { UniqueId0 = 0xea3031fb, UniqueId1 = 0x5c1713ef, UniqueId2 = 1, UniqueId3 = 0x52699598 };
    private static readonly PrcUniqueId Assembly = new() { UniqueId0 = 0xcbbe6b9e, UniqueId1 = 0x8554fd29, UniqueId2 = 2, UniqueId3 = 0x5269959a };

    [Fact]
    public void AReferenceIsTheFileStructureTheTypeAndTheEntityAsTheFormatWritesIntegers()
    {
        // The occurrences 1 and 2 of the assembly: "UCL0004-1_NX_-_633951.cf5a...2808" and "UCL0004-1_NX_-.cf5a...2810".
        Assert.Equal("cf5af7dcb4a7fb54c2a04cd656d3524da02808", PrcNodeNames.Reference(Assembly, PrcType.PRC_TYPE_ASM_ProductOccurrence, 1));
        Assert.Equal("cf5af7dcb4a7fb54c2a04cd656d3524da02810", PrcNodeNames.Reference(Assembly, PrcType.PRC_TYPE_ASM_ProductOccurrence, 2));

        // The solid of the part, item 2 of its file structure; its node is named by this and its occurrence.
        Assert.Equal("fdcc661ea7be2717ae202cc656d3527a1020", PrcNodeNames.Reference(Part, PrcType.PRC_TYPE_RI_BrepModel, 2));

        // A markup of the assembly, entity 0x117: padded with one bit to a whole byte.
        Assert.Equal("cf5af7dcb4a7fb54c2a04cd656d3527da028bc04", PrcNodeNames.Reference(Assembly, PrcType.PRC_TYPE_MKP_Markup, 0x117));

        // Zero is a single clear bit.
        Assert.Equal("00", PrcNodeNames.Reference(default, 0, 0));
    }

    [Theory]
    [InlineData("UCL0004-1_NX_-_633951.cf5af7dcb4a7fb54c2a04cd656d3524da02808", "cf5af7dcb4a7fb54c2a04cd656d3524da02808")]
    [InlineData("0.25 Ball:1.9cfb3510757fbf268a272c2707f5514da02948", "9cfb3510757fbf268a272c2707f5514da02948")]
    [InlineData("Text.29.96f523d3479ef525fea0289eb6f7527da02ed404", "96f523d3479ef525fea0289eb6f7527da02ed404")]
    [InlineData(".00", "00")]
    [InlineData("plain name", "")]
    [InlineData("drawing.dwg", "")]
    [InlineData("version 1.5", "")]            // an odd number of digits is no bytes
    [InlineData("part.ABCD", "")]              // identifiers are lower case
    [InlineData("ends with a dot.", "")]
    [InlineData("", "")]
    public void TheIdentifierIsWhatFollowsTheLastDot(string name, string identifier)
    {
        Assert.Equal(identifier, PrcNodeNames.IdentifierOf(name).ToString());
    }

    [Fact]
    public void TheNodesOfASceneCarryTheirIdentifiers()
    {
        PrcTestFile file = PrcTestFile.Simple();
        using Scene scene = PrcReader.Read(file.Build(), cancellationToken: TestContext.Current.CancellationToken);

        // The file structure of the test file is 1-2-3-4. An occurrence is named by its reference; the
        // item of the part by its own and that of the occurrence it is shown under.
        var structure = new PrcUniqueId { UniqueId0 = 1, UniqueId1 = 2, UniqueId2 = 3, UniqueId3 = 4 };
        string Occurrence(int index) => PrcNodeNames.Reference(structure, PrcType.PRC_TYPE_ASM_ProductOccurrence, file.OccurrenceIdentifier(index));
        string item = PrcNodeNames.Reference(structure, PrcType.PRC_TYPE_RI_PolyBrepModel, PrcTestFile.FirstTreeIdentifier + 1);
        string[] names = [.. Enumerable.Range(0, scene.Nodes.Length).Select(i => scene.GetName(scene.Nodes[i].Name))];

        Assert.Equal(["assembly." + Occurrence(2), "left." + Occurrence(0), "plate." + item + Occurrence(0), "right." + Occurrence(1), "plate." + item + Occurrence(1)], names);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    /// <summary>
    /// Viewers hang folders of their own under an occurrence ("PRODUCT VIEWS", "3D PMI") and name them
    /// after it, with its identifier: 99 such entries in the views of the sample documents, each with
    /// a visibility and a transform of its own. They are not the occurrence.
    /// </summary>
    [Fact]
    public void AFolderUnderAnOccurrenceIsNotTheOccurrence()
    {
        PrcTestFile file = PrcTestFile.Simple();
        using Scene scene = PrcReader.Read(file.Build(), cancellationToken: TestContext.Current.CancellationToken);
        string assembly = scene.GetName(scene.Nodes[0].Name);
        string identifier = PrcNodeNames.IdentifierOf(assembly).ToString();
        Assert.Equal("assembly." + identifier, assembly);

        var view = new SceneView
        {
            NodeOverrides =
            [
                new SceneNodeOverride("assembly.PRODUCT VIEWS." + identifier, false, 1, System.Numerics.Matrix4x4.Identity),
                new SceneNodeOverride("assembly.3D PMI." + identifier, false, 1, System.Numerics.Matrix4x4.Identity),
            ],
        };
        Assert.Same(view, PrcNodeNames.Resolve(scene, view));

        // Another text altogether is the node under a name a viewer made up for it.
        var renamed = new SceneView { NodeOverrides = [new SceneNodeOverride("Baugruppe." + identifier, false, null, null), view.NodeOverrides[0]] };
        Assert.Equal([assembly, view.NodeOverrides[0].Name], PrcNodeNames.Resolve(scene, renamed).NodeOverrides.Select(n => n.Name));
    }

    [Fact]
    public void AViewIsGivenTheNamesOfTheScene()
    {
        using Scene scene = PrcReader.Read(PrcFuzzTests.Seed("old"), cancellationToken: TestContext.Current.CancellationToken);
        string[] names = [.. Enumerable.Range(0, scene.Nodes.Length).Select(i => scene.GetName(scene.Nodes[i].Name))];
        string sheet = names.First(n => n.StartsWith("sheet.", StringComparison.Ordinal));
        string identifier = PrcNodeNames.IdentifierOf(sheet).ToString();

        var view = new SceneView
        {
            Name = "Exploded",
            Background = new System.Numerics.Vector3(1, 0, 0),
            Lighting = SceneLighting.Day,
            NodeOverrides =
            [
                new SceneNodeOverride("Sheet Metal Body." + identifier, false, 0.5f, null),
                new SceneNodeOverride(sheet, true, null, null),
                new SceneNodeOverride("gone." + PrcNodeNames.Reference(default, 310, 77), false, null, null),
                new SceneNodeOverride("no identifier", false, null, null),
            ],
        };

        SceneView resolved = PrcNodeNames.Resolve(scene, view);

        Assert.NotSame(view, resolved);
        Assert.Equal((view.Name, view.Background, view.Lighting), (resolved.Name, resolved.Background, resolved.Lighting));
        Assert.Equal([sheet, sheet, view.NodeOverrides[2].Name, "no identifier"], resolved.NodeOverrides.Select(n => n.Name));
        Assert.Equal((false, 0.5f), (resolved.NodeOverrides[0].Visible, resolved.NodeOverrides[0].Opacity));

        // A view that already uses the scene's names, or names nothing, is returned as it is.
        var exact = new SceneView { NodeOverrides = [new SceneNodeOverride(sheet, false, null, null)] };
        Assert.Same(exact, PrcNodeNames.Resolve(scene, exact));
        var empty = new SceneView();
        Assert.Same(empty, PrcNodeNames.Resolve(scene, empty));
    }
}
