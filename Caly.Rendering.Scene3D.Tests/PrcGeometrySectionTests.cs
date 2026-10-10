using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>The exact-geometry section: when it is inflated, how far it is read, what of it is kept.</summary>
public sealed class PrcGeometrySectionTests
{
    private static PrcTestFile FileWithASolid(byte behavior = 0, uint biasedTessellation = 0, bool addTessellation = false)
    {
        var file = new PrcTestFile();
        file.Colors.Add([1, 0, 0]);
        file.Styles.Add((1, -1));
        if (addTessellation)
        {
            file.Tessellations.Add(PrcTestFile.Tessellation(file.AuthoringVersion, [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 3, 6]));
        }

        file.TopologicalContexts.Add([PrcTestFile.EmptyBrepBody(behavior)]);
        file.Parts.Add(new PrcTestFile.Part("solid part", "solid", biasedTessellation, BiasedStyle: 1, Body: (0, 0)));
        file.Occurrences.Add(new PrcTestFile.Occurrence("root", BiasedPart: 1));
        return file;
    }

    private static PrcFile Read(byte[] bytes, PrcGeometryReading geometry, out long inflated)
    {
        using PrcContainer container = PrcContainer.Open(bytes);
        PrcFile file = PrcFileReader.Read(container, geometry: geometry);
        inflated = container.InflatedBytes;
        return file;
    }

    [Fact]
    public void TheGeometrySectionIsNotInflatedWhenEveryShownItemHasATessellation()
    {
        byte[] bytes = FileWithASolid(biasedTessellation: 1, addTessellation: true).Build();

        PrcFile never = Read(bytes, PrcGeometryReading.Never, out long neverInflated);
        PrcFile whenNeeded = Read(bytes, PrcGeometryReading.WhenNeeded, out long whenNeededInflated);

        Assert.Null(never.FileStructures[0].ExactGeometry);
        Assert.Null(whenNeeded.FileStructures[0].ExactGeometry);
        Assert.Equal(neverInflated, whenNeededInflated);
        Assert.DoesNotContain(whenNeeded.Sections, s => s.Name == "Geometry");
    }

    [Fact]
    public void TheGeometrySectionIsReadForAnItemWithExactGeometryAndNoTessellation()
    {
        byte[] bytes = FileWithASolid().Build();

        PrcFile never = Read(bytes, PrcGeometryReading.Never, out long neverInflated);
        PrcFile file = Read(bytes, PrcGeometryReading.WhenNeeded, out long inflated);

        Assert.Null(never.FileStructures[0].ExactGeometry);
        PrcFileStructureExactGeometry exact = Assert.IsType<PrcFileStructureExactGeometry>(file.FileStructures[0].ExactGeometry);
        PrcTopoContext context = Assert.Single(exact.TopoContexts);
        PrcTopoBody body = Assert.Single(context.Bodies);
        Assert.Equal(PrcType.PRC_TYPE_TOPO_BrepData, body.IdType);
        Assert.IsType<PrcTopoBrepData>(body.Id);                   // kept since phase B (an empty body here)
        Assert.True(inflated > neverInflated);
    }

    [Fact]
    public void AlwaysReadsItEvenForAFileWhoseItemsAllHaveATessellation()
    {
        byte[] bytes = FileWithASolid(biasedTessellation: 1, addTessellation: true).Build();

        PrcFile file = Read(bytes, PrcGeometryReading.Always, out _);

        Assert.NotNull(file.FileStructures[0].ExactGeometry);
    }

    [Fact]
    public void AnUncompressedBodyIsWalkedToTheEndOfTheSection()
    {
        PrcFile file = Read(FileWithASolid().Build(), PrcGeometryReading.WhenNeeded, out _);

        PrcSectionRead section = Assert.Single(file.Sections, s => s.Name == "Geometry");
        Assert.InRange(section.RemainingBits, 0, 7);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ABoundingBoxFollowsABodyOnlyWhenItsBehaviourSaysEvaluatedOrPrecise(byte behavior)
    {
        // The body stores a box exactly when bit 0 or bit 1 is set (issue #715); a reader that gets it wrong ends out of step.
        var file = FileWithASolid(behavior);
        file.TopologicalContexts[0].Add(PrcTestFile.EmptyBrepBody(behavior));

        PrcFile read = Read(file.Build(), PrcGeometryReading.WhenNeeded, out _);

        Assert.Equal(2, read.FileStructures[0].ExactGeometry!.TopoContexts[0].Bodies.Length);
        PrcSectionRead section = Assert.Single(read.Sections, s => s.Name == "Geometry");
        Assert.InRange(section.RemainingBits, 0, 7);
    }

    [Fact]
    public void ABrepModelKeepsWhereItsBodyIs()
    {
        var file = FileWithASolid();
        file.TopologicalContexts.Add([PrcTestFile.EmptyBrepBody(), PrcTestFile.EmptyBrepBody()]);
        file.Parts[0] = file.Parts[0] with { Body = (1, 1) };

        PrcFile read = Read(file.Build(), PrcGeometryReading.WhenNeeded, out _);

        PrcRiBrepModel model = Assert.IsType<PrcRiBrepModel>(read.FileStructures[0].Tree.Parts[0].RepresentationItems[0].Data);
        Assert.True(model.ExactGeometry);
        Assert.Equal(2u, model.IndexTopologicalContext);   // counted from 1 in the file
        Assert.Equal(2u, model.IndexBody);
        Assert.Equal(0u, model.ItemContent.BiasedIndexTessellation);
    }

    private static PrcRepresentationItemContent Content(uint tessellation) =>
        new() { Base = default, BiasedIndexLocalCoordinateSystem = 0, BiasedIndexTessellation = tessellation };

    private static PrcRiRepresentationItem Solid(uint tessellation, bool exact) => new()
    {
        DataType = PrcType.PRC_TYPE_RI_BrepModel,
        Data = new PrcRiBrepModel { ItemContent = Content(tessellation), ExactGeometry = exact, IndexTopologicalContext = 0, IndexBody = 0 },
    };

    private static PrcRiRepresentationItem Set(params PrcRiRepresentationItem[] members) => new()
    {
        DataType = PrcType.PRC_TYPE_RI_Set,
        Data = new PrcRiSet { ItemContent = Content(0), RepresentationItems = members },
    };

    [Fact]
    public void ASolidWithExactGeometryAndNoTessellationNeedsTheSection()
    {
        Assert.True(PrcFileReader.NeedsExactGeometry([Solid(0, exact: true)]));
    }

    [Fact]
    public void ASolidWithATessellationOrWithoutExactGeometryDoesNot()
    {
        Assert.False(PrcFileReader.NeedsExactGeometry([Solid(3, exact: true)]));
        Assert.False(PrcFileReader.NeedsExactGeometry([Solid(0, exact: false)]));
        Assert.False(PrcFileReader.NeedsExactGeometry([]));
    }

    [Fact]
    public void ASolidInsideASetNeedsTheSectionToo()
    {
        Assert.True(PrcFileReader.NeedsExactGeometry([Set(Solid(2, exact: true), Set(Solid(0, exact: true)))]));
        Assert.False(PrcFileReader.NeedsExactGeometry([Set(Solid(2, exact: true), Set(Solid(5, exact: true)))]));
    }
}
