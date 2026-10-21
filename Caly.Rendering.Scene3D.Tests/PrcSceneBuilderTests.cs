using System.Numerics;
using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// From what is kept of a PRC file to the scene: the assembly tree with its prototypes, the placement of
/// parts, and the inheritance of appearance down to the faces. The models are made in memory, one
/// feature at a time.
/// </summary>
public class PrcSceneBuilderTests
{
    private const ushort Shown = 0x0001, SonHeritShow = 0x0002, FatherHeritShow = 0x0004, SonHeritColor = 0x0008, FatherHeritColor = 0x0010,
        SonHeritTransparency = 0x0080, FatherHeritTransparency = 0x0100, Removed = 0x2000;

    private static PrcUniqueId Id(uint n) => new() { UniqueId0 = n, UniqueId1 = n + 1, UniqueId2 = n + 2, UniqueId3 = n + 3 };

    private static int _lastId;

    /// <summary>Every entity the tests make has an identifier of its own, as in a file.</summary>
    private static uint NextId() => (uint)Interlocked.Increment(ref _lastId);

    private static PrcRootPRCBaseWithGraphics Base(string name, uint style = 0, ushort behavior = Shown) => new()
    {
        Base = new PrcContentPRCRefBase { Base = new PrcContentPRCBase { EntityName = new PrcName { Name = name } }, NonpersistentId = 0, UniqueIdCad = 0, UniqueId = NextId() },
        GraphicsContent = new PrcGraphicsContent { BiasedLayerIndex = 0, BiasedIndexOfLineStyle = style, BehaviorBitField = behavior },
    };

    private static PrcAsmProductOccurrence Occurrence(string name, uint part = 0, uint[]? sons = null, uint prototype = 0, PrcUniqueId? prototypeIn = null,
        object? location = null, uint style = 0, ushort behavior = Shown, byte flags = 0, byte productBehavior = 0, double unit = 1, bool unitFromCad = true,
        PrcMiscEntityReference[]? references = null) => new()
    {
        Base = Base(name, style, behavior),
        ReferencesProductOccurrence = new PrcReferencesOfProductOccurrence
        {
            BiasedIndexPart = part,
            BiasedIndexPrototype = prototype,
            PrototypeInSameFileStructure = new PrcFileIdentifier { Flag = prototypeIn is null, UniqueId = prototypeIn ?? default },
            BiasedIndexExternalData = 0,
            ExternalDataInSameFileStructure = default,
            NumberOfChildProductOccurrences = (uint)(sons?.Length ?? 0),
            IndexChildOccurrence = sons ?? [],
        },
        ProductBehavior = (sbyte)productBehavior,
        ProductInformation = new PrcProductInformation { UnitFromCadFile = unitFromCad, Unit = unit, ProductInformationFlags = (sbyte)flags, ProductLoadStatus = 3 },
        Location = location,
        LocationType = 0,
        EntityReference = references ?? [],
        Markups = new PrcMarkupData { Markups = [] },
    };

    private static PrcRiRepresentationItem Solid(string name, uint tessellation, uint style = 0, ushort behavior = Shown, uint coordinateSystem = 0) => new()
    {
        Data = new PrcRiPolyBrepModel { ItemContent = new PrcRepresentationItemContent { Base = Base(name, style, behavior), BiasedIndexLocalCoordinateSystem = coordinateSystem, BiasedIndexTessellation = tessellation } },
        DataType = PrcType.PRC_TYPE_RI_PolyBrepModel,
    };

    private static PrcRiRepresentationItem Set(string name, uint style, ushort behavior, params PrcRiRepresentationItem[] items) => new()
    {
        Data = new PrcRiSet { ItemContent = new PrcRepresentationItemContent { Base = Base(name, style, behavior), BiasedIndexLocalCoordinateSystem = 0, BiasedIndexTessellation = 0 }, RepresentationItems = items },
        DataType = PrcType.PRC_TYPE_RI_Set,
    };

    private static PrcAsmPartDefinition Part(string name, params PrcRiRepresentationItem[] items) => new() { Base = Base(name), RepresentationItems = items };

    private static PrcAsmPartDefinition StyledPart(string name, uint style, ushort behavior, params PrcRiRepresentationItem[] items) => new() { Base = Base(name, style, behavior), RepresentationItems = items };

    /// <summary>A tessellation of one triangle per face; each face may name a style and a behaviour.</summary>
    private static PrcTess Mesh(params (uint Style, ushort Behavior)[] faces)
    {
        if (faces.Length == 0)
        {
            faces = [(0, 0)];
        }

        return new PrcTess
        {
            IdType = PrcType.PRC_TYPE_TESS_3D,
            Id = new PrcTess3D
            {
                TessellationCoordinates = new PrcContentBaseTessData { Coordinates = [0, 0, 0, 1, 0, 0, 0, 1, 0] },
                HasFaces = true,
                HasLoops = false,
                MustCalculateNormals = true,
                CreaseAngle = 0.5,
                NormalCoordinates = [],
                WireIndices = [],
                TriangulatedIndexArray = [.. Enumerable.Repeat(new uint[] { 0, 3, 6 }, faces.Length).SelectMany(x => x)],
                FaceTessellationData = [.. faces.Select((f, i) => new PrcTessFace
                {
                    LineAttributes = f.Style == 0 ? [] : [f.Style],
                    StartOfWireData = 0,
                    SizesWire = [],
                    UsedEntitiesFlag = 2,
                    StartTriangulated = (uint)(i * 6),
                    Triangulateddata = [1],
                    NumberOfTexturedCoordinateIndices = 0,
                    VertexColorData = null,
                    Behavior = f.Behavior,
                })],
                TextureCoordinates = [],
            },
        };
    }

    private static PrcTess Other(uint type) => new() { IdType = type, Id = null };

    /// <summary>A compressed tessellation of one triangle; with a neighbour announced that never comes, it cannot be decoded.</summary>
    private static PrcTess Compressed(bool decodable = true, ushort style = 0) => new()
    {
        IdType = PrcType.PRC_TYPE_TESS_3D_Compressed,
        Id = new PrcTess3DCompressed
        {
            IsCalculated = false,
            HasFaces = true,
            Tolerance = 0.5,
            HasOrigin = false,
            OriginX = 0,
            OriginY = 0,
            OriginZ = 0,
            Points = [0, 0, 0, 8, 0, 0, 0, 6, 0],
            EdgeStatus = [(byte)(decodable ? 0 : 2)],
            TriangleFaces = [0],
            FaceCount = 1,
            PointIsReference = new Caly.Rendering.Scene3D.Prc.Tessellation.PrcBits([0u], 3),
            PointReferences = [],
            MustRecalculateNormals = true,
            NormalIsReversed = new Caly.Rendering.Scene3D.Prc.Tessellation.PrcBits([0u], 1),
            LineAttributes = [style],
            Behaviors = [1],
        },
    };

    private static PrcGraphStyle Colour(uint biasedIndex, int transparency = -1) => new() { IsMaterial = false, BiasedColorIndex = biasedIndex, IsTransparency = transparency >= 0, Transparency = (sbyte)Math.Max(transparency, 0) };

    private static PrcGraphStyle MaterialStyle(uint biasedIndex) => new() { IsMaterial = true, BiasedColorIndex = biasedIndex, IsTransparency = false, Transparency = 0 };

    private static readonly PrcRgbColor[] Palette =
    [
        new() { Red = 1, Green = 0, Blue = 0 },
        new() { Red = 0, Green = 1, Blue = 0 },
        new() { Red = 0, Green = 0, Blue = 1 },
        new() { Red = 1, Green = 1, Blue = 0 },
    ];

    private static readonly Vector3 Red = new(1, 0, 0), Green = new(0, 1, 0), Blue = new(0, 0, 1), Yellow = new(1, 1, 0), Grey = new(0.75f);

    // Styles 1 to 4 are the palette's colours; 5 is blue at half transparency; 6 names no colour.
    private static readonly PrcGraphStyle[] PaletteStyles = [Colour(1), Colour(4), Colour(7), Colour(10), Colour(7, 128), Colour(0)];

    private static PrcFileStructureData Structure(PrcAsmProductOccurrence[] products, PrcAsmPartDefinition[]? parts = null, PrcTess[]? tessellations = null, uint id = 1,
        PrcGraphStyle[]? styles = null, PrcMaterial[]? materials = null, PrcGraphPicture[]? pictures = null, PrcGraphTextureDefinition[]? textures = null,
        PrcRiCoordinateSystem[]? coordinateSystems = null, ReadOnlyMemory<byte>[]? files = null) => new()
    {
        Id = Id(id),
        Files = files ?? [],
        Globals = new PrcFileStructureInternalGlobalData
        {
            Colors = Palette,
            Pictures = pictures ?? [],
            Textures = textures ?? [],
            Materials = materials ?? [],
            Styles = styles ?? PaletteStyles,
            RefCoords = coordinateSystems ?? [],
        },
        Tree = new PrcAsmFileStructureTree { Parts = parts ?? [], Products = products, InternalData = new PrcAsmFileStructure { IndexProductOccurrence = (uint)products.Length } },
        Tessellation = new PrcAsmFileStructureTessellation { Tess = tessellations ?? [Mesh()] },
    };

    private static PrcFile File(params PrcFileStructureData[] structures) => File(1, structures);

    private static PrcFile File(double unit, params PrcFileStructureData[] structures) => File(unit, true, structures);

    private static PrcFile File(double unit, bool unitFromCad, params PrcFileStructureData[] structures) => new()
    {
        FileStructures = structures,
        ModelFile = new PrcAsmModelFile
        {
            UnitsFromCadFile = unitFromCad,
            UnitsInMm = unit,
            ProductOccurrences = [new PrcProductOccurrenceReference { UniqueId = structures[^1].Id, RootIndex = (uint)structures[^1].Tree.Products.Length, ProductOccurrenceIsActive = true }],
        },
        Sections = [],
    };

    private static Scene Build(PrcFile file, SceneLimits? limits = null)
    {
        using var builder = new SceneBuilder(limits);
        PrcSceneBuilder.Build(file, builder, default);
        return builder.Build();
    }

    private static string Tree(Scene scene)
    {
        var lines = new List<string>();
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            SceneNode node = scene.Nodes[i];
            lines.Add($"{node.Parent}>{Plain(scene.GetName(node.Name))}{(node.Mesh >= 0 ? "#" + node.Mesh : "")}{(node.Visible ? "" : "(hidden)")}");
        }

        return string.Join(" ", lines);
    }

    /// <summary>The name of a node without the identifier PDF views know it by.</summary>
    internal static string Plain(string name)
    {
        int identifier = PrcNodeNames.IdentifierOf(name).Length;
        return identifier == 0 ? name : name[..^(identifier + 1)];
    }

    /// <summary>The material of a mesh node's slot.</summary>
    private static SceneMaterial MaterialOf(Scene scene, string nodeName, int slot = 0)
    {
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            SceneNode node = scene.Nodes[i];
            if (Plain(scene.GetName(node.Name)) == nodeName && node.Mesh >= 0)
            {
                Assert.True(slot < node.BindingCount, $"'{nodeName}' has {node.BindingCount} material slots.");
                return scene.Materials[scene.Bindings[node.FirstBinding + slot]];
            }
        }

        throw new Xunit.Sdk.XunitException($"No mesh node named '{nodeName}' in: {Tree(scene)}");
    }

    private static Vector3 OriginOf(Scene scene, string nodeName)
    {
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            if (Plain(scene.GetName(scene.Nodes[i].Name)) == nodeName)
            {
                return scene.GetWorldMatrix(i).Translation;
            }
        }

        throw new Xunit.Sdk.XunitException($"No node named '{nodeName}' in: {Tree(scene)}");
    }

    [Fact]
    public void AnOccurrenceWithAPartIsANodeAndItsItemsAreMeshNodes()
    {
        using Scene scene = Build(File(25.4, Structure([Occurrence("bracket", part: 1)], [Part("bracket part", Solid("body", 1))])));

        Assert.Equal("-1>bracket 0>body#0", Tree(scene));
        Assert.Equal(0.0254, scene.UnitScale, 9);
        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal(Grey, MaterialOf(scene, "body").Diffuse);    // nothing names a style: the default material
        Assert.Empty(scene.Diagnostics);
        Assert.True(PrcReader.IsComplete(scene));
    }

    [Fact]
    public void APartUsedTwiceIsOneMeshShownByTwoNodes()
    {
        var left = new PrcMiscCartesianTransformation { Transform = Translation(-2, 0, 0) };
        var right = new PrcMiscCartesianTransformation { Transform = Translation(2, 0, 5) };
        using Scene scene = Build(File(Structure(
            [Occurrence("left", part: 1, location: left), Occurrence("right", part: 1, location: right), Occurrence("assembly", sons: [0, 1])],
            [Part("plate part", Solid("plate", 1))])));

        Assert.Equal("-1>assembly 0>left 1>plate#0 0>right 3>plate#0", Tree(scene));
        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal(new Vector3(-2, 0, 0), scene.GetWorldMatrix(2).Translation);
        Assert.Equal(new Vector3(2, 0, 5), scene.GetWorldMatrix(4).Translation);
    }

    private static PrcTransformation3D Translation(double x, double y, double z) => new()
    {
        Behavior = 0x01,
        Translation = new PrcVector3D { X = x, Y = y, Z = z },
        NonOrthoMatrix = new PrcVector3D[3],
        Rotation = new PrcVector3D[2],
        NonUniformScale = default,
        Scale = 0,
        Homogeneous = new double[4],
    };

    /// <summary>A prototype is a template: an occurrence without part or sons of its own shows its prototype's.</summary>
    [Fact]
    public void APrototypeLendsItsPartAndItsSons()
    {
        // Occurrence 2 places prototype 1, which has a part and one son (0).
        using Scene same = Build(File(Structure(
            [Occurrence("bolt", part: 2), Occurrence("wheel definition", part: 1, sons: [0]), Occurrence("front wheel", prototype: 2), Occurrence("car", sons: [2])],
            [Part("rim part", Solid("rim", 1)), Part("bolt part", Solid("bolt body", 1))])));
        Assert.Equal("-1>car 0>front wheel 1>rim#0 1>bolt 3>bolt body#0", Tree(same));

        // The prototype in another file structure, named by that structure's identifier; itself a
        // placement of a third, in its own structure.
        PrcFileStructureData parts = Structure([Occurrence("rim file root", part: 1), Occurrence("rim instance", prototype: 1)], [Part("rim part", Solid("rim", 1))], id: 50);
        PrcFileStructureData assembly = Structure([Occurrence("front wheel", prototype: 2, prototypeIn: Id(50)), Occurrence("car", sons: [0])], id: 60);
        using Scene other = Build(File(parts, assembly));
        Assert.Equal("-1>car 0>front wheel 1>rim#0", Tree(other));

        // An occurrence without a name takes its prototype's; without that either, it is "node".
        using Scene named = Build(File(Structure([Occurrence("wheel", part: 1), Occurrence("", prototype: 1), Occurrence("car", sons: [1])], [Part("p", Solid("rim", 1))])));
        Assert.Equal("-1>car 0>wheel 1>rim#0", Tree(named));
        using Scene unnamed = Build(File(Structure([Occurrence("", part: 1), Occurrence("", prototype: 1), Occurrence("car", sons: [1])], [Part("p", Solid("rim", 1))])));
        Assert.Equal("-1>car 0>node 1>rim#0", Tree(unnamed));
    }

    /// <summary>
    /// A writer that copies the sons of a subassembly under each use of it leaves the copies without a
    /// location: where a son sits is said once, by the occurrence the copy has as its prototype.
    /// </summary>
    [Fact]
    public void AnOccurrenceWithoutALocationIsPlacedAsItsPrototype()
    {
        var up = new PrcMiscCartesianTransformation { Transform = Translation(0, 0, 9) };
        var aside = new PrcMiscCartesianTransformation { Transform = Translation(4, 0, 0) };
        var own = new PrcMiscCartesianTransformation { Transform = Translation(0, 7, 0) };
        using Scene scene = Build(File(Structure(
            [
                Occurrence("bolt definition", part: 1),                              // 0
                Occurrence("bolt in the wheel", prototype: 1, location: up),         // 1
                Occurrence("wheel definition", sons: [1]),                           // 2
                Occurrence("copied bolt", prototype: 2),                             // 3: no location of its own
                Occurrence("moved bolt", prototype: 2, location: own),               // 4: its own wins
                Occurrence("wheel", prototype: 3, sons: [3, 4], location: aside),    // 5
                Occurrence("car", sons: [5]),
            ],
            [Part("bolt part", Solid("bolt body", 1))])));

        Assert.Equal("-1>car 0>wheel 1>copied bolt 2>bolt body#0 1>moved bolt 4>bolt body#0", Tree(scene));
        Assert.Equal(new Vector3(4, 0, 0), OriginOf(scene, "wheel"));
        Assert.Equal(new Vector3(4, 0, 9), OriginOf(scene, "copied bolt"));
        Assert.Equal(new Vector3(4, 7, 0), OriginOf(scene, "moved bolt"));
    }

    [Fact]
    public void AnOccurrencesOwnSonsStandForItsPrototypes()
    {
        // The writer copied the prototype's sons under the occurrence: they are shown once, the
        // prototype's own are not added to them. The prototype's part still is.
        using Scene scene = Build(File(Structure(
            [
                Occurrence("definition bolt", part: 2), Occurrence("definition", part: 1, sons: [0]),
                Occurrence("copied bolt", part: 2), Occurrence("instance", prototype: 2, sons: [2]), Occurrence("car", sons: [3]),
            ],
            [Part("rim part", Solid("rim", 1)), Part("bolt part", Solid("bolt body", 1))])));

        Assert.Equal("-1>car 0>instance 1>rim#0 1>copied bolt 3>bolt body#0", Tree(scene));
    }

    [Fact]
    public void AFileStructureThatIsMissingLeavesAHole()
    {
        using Scene scene = Build(File(Structure([Occurrence("wheel", prototype: 1, prototypeIn: Id(999)), Occurrence("car", sons: [0])])));

        Assert.Equal("-1>car 0>wheel", Tree(scene));
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-missing-file-structure" && d.Severity == SceneDiagnosticSeverity.Warning);
    }

    /// <summary>Review focus: references that never end.</summary>
    [Fact]
    public void ReferencesInACircleAreRefused()
    {
        Scene3DError Fails(PrcFile file, SceneLimits? limits = null) => Assert.Throws<Scene3DException>(() => Build(file, limits)).Error;

        // Its own prototype; two that are each other's; a son that is its father.
        Assert.Equal(Scene3DError.InvalidValue, Fails(File(Structure([Occurrence("a", prototype: 1)]))));
        Assert.Equal(Scene3DError.InvalidValue, Fails(File(Structure([Occurrence("a", prototype: 2), Occurrence("b", prototype: 1), Occurrence("root", sons: [0])]))));
        Assert.Equal(Scene3DError.LimitExceeded, Fails(File(Structure([Occurrence("a", sons: [0])]))));

        // A tree that doubles at every level: bounded by the number of nodes.
        var doubling = new PrcAsmProductOccurrence[40];
        doubling[0] = Occurrence("leaf");
        for (uint i = 1; i < doubling.Length; i++)
        {
            doubling[i] = Occurrence("level", sons: [i - 1, i - 1]);
        }

        Assert.Equal(Scene3DError.LimitExceeded, Fails(File(Structure(doubling)), new SceneLimits { MaxNodes = 10_000 }));

        // Indices of things that are not there.
        Assert.Equal(Scene3DError.InvalidValue, Fails(File(Structure([Occurrence("a", sons: [7])]))));
        Assert.Equal(Scene3DError.InvalidValue, Fails(File(Structure([Occurrence("a", part: 3)], [Part("p")]))));
        Assert.Equal(Scene3DError.InvalidValue, Fails(File(Structure([Occurrence("a", part: 1)], [Part("p", Solid("s", 9))]))));
        Assert.Equal(Scene3DError.InvalidValue, Fails(File(Structure([Occurrence("a", prototype: 9)]))));
    }

    [Fact]
    public void ARootTheFileDoesNotHaveIsRefused()
    {
        PrcFile file = File(Structure([Occurrence("a")]));
        file.ModelFile.ProductOccurrences[0].UniqueId = Id(77);
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => Build(file)).Error);

        // Of several roots the active ones are shown.
        file = File(Structure([Occurrence("draft"), Occurrence("released")]));
        file.ModelFile.ProductOccurrences =
        [
            new PrcProductOccurrenceReference { UniqueId = Id(1), RootIndex = 1, ProductOccurrenceIsActive = false },
            new PrcProductOccurrenceReference { UniqueId = Id(1), RootIndex = 2, ProductOccurrenceIsActive = true },
        ];
        using Scene scene = Build(file);
        Assert.Equal("-1>released", Tree(scene));
    }

    /// <summary>A son's style replaces its father's: the nearest entity that names one gives the colour.</summary>
    [Fact]
    public void TheNearestStyleGivesTheColour()
    {
        using Scene scene = Build(File(Structure(
            [
                Occurrence("plain", part: 1), Occurrence("tinted", part: 1, style: 2), Occurrence("own", part: 2, style: 2),
                Occurrence("assembly", sons: [0, 1, 2], style: 1),
            ],
            [Part("p1", Solid("body", 1)), Part("p2", Solid("blue body", 1, style: 3))])));

        Assert.Equal("-1>assembly 0>plain 1>body#0 0>tinted 3>body#0 0>own 5>blue body#0", Tree(scene));
        SceneNode[] nodes = scene.Nodes.ToArray();
        Assert.Equal(Red, scene.Materials[scene.Bindings[nodes[2].FirstBinding]].Diffuse);     // from the assembly
        Assert.Equal(Green, scene.Materials[scene.Bindings[nodes[4].FirstBinding]].Diffuse);   // from its occurrence
        Assert.Equal(Blue, scene.Materials[scene.Bindings[nodes[6].FirstBinding]].Diffuse);    // its own
        Assert.Equal(Blue, scene.Materials[scene.Bindings[nodes[6].FirstBinding]].Ambient);

        // The same appearance is one material.
        Assert.Equal(4, scene.Materials.Length);
    }

    /// <summary>
    /// A father that says its colour is inherited keeps it against its sons, unless a son insists; of
    /// several such fathers the oldest wins (ISO 14739-1, GraphicsContent).
    /// </summary>
    [Fact]
    public void AFatherMayImposeItsColourAndASonMayInsist()
    {
        const ushort imposing = Shown | FatherHeritColor, insisting = Shown | SonHeritColor;
        using Scene scene = Build(File(Structure(
            [
                Occurrence("yields", part: 1), Occurrence("insists", part: 2), Occurrence("also imposes", part: 1, style: 4, behavior: imposing),
                Occurrence("highlighted", sons: [0, 1, 2], style: 1, behavior: imposing),
            ],
            [Part("p1", Solid("blue body", 1, style: 3)), Part("p2", Solid("green body", 1, style: 2, behavior: insisting))])));

        SceneNode[] nodes = scene.Nodes.ToArray();
        Assert.Equal("-1>highlighted 0>yields 1>blue body#0 0>insists 3>green body#0 0>also imposes 5>blue body#0", Tree(scene));
        Assert.Equal(Red, scene.Materials[scene.Bindings[nodes[2].FirstBinding]].Diffuse);
        Assert.Equal(Green, scene.Materials[scene.Bindings[nodes[4].FirstBinding]].Diffuse);
        Assert.Equal(Red, scene.Materials[scene.Bindings[nodes[6].FirstBinding]].Diffuse);
    }

    [Fact]
    public void FacesAreTheYoungestSons()
    {
        // A mesh of three faces: without style, yellow, and green that yields to an imposing father.
        PrcTess mesh = Mesh((0, 0), (4, Shown), (2, Shown));
        using Scene scene = Build(File(Structure(
            [Occurrence("free", part: 1), Occurrence("forced", part: 1, style: 3, behavior: Shown | FatherHeritColor), Occurrence("root", sons: [0, 1])],
            [Part("p", Solid("body", 1, style: 1))], [mesh])));

        Assert.Equal(Red, MaterialOf(scene, "body", 0).Diffuse);      // the item's own
        Assert.Equal(Yellow, MaterialOf(scene, "body", 1).Diffuse);
        Assert.Equal(Green, MaterialOf(scene, "body", 2).Diffuse);

        SceneNode forced = scene.Nodes[4];
        Assert.Equal(3, forced.BindingCount);
        Assert.All(Enumerable.Range(0, 3), slot => Assert.Equal(Blue, scene.Materials[scene.Bindings[forced.FirstBinding + slot]].Diffuse));

        // A face that insists keeps its colour.
        using Scene insisting = Build(File(Structure(
            [Occurrence("forced", part: 1, style: 3, behavior: Shown | FatherHeritColor)], [Part("p", Solid("body", 1))], [Mesh((4, Shown | SonHeritColor))])));
        Assert.Equal(Blue, MaterialOf(insisting, "body", 0).Diffuse);
        Assert.Equal(Yellow, MaterialOf(insisting, "body", 1).Diffuse);
    }

    /// <summary>The triangles a node draws: those of the parts of its mesh.</summary>
    private static int TrianglesOf(Scene scene, string nodeName)
    {
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            SceneNode node = scene.Nodes[i];
            if (Plain(scene.GetName(node.Name)) == nodeName && node.Kind == SceneNodeKind.Model)
            {
                if (node.Mesh < 0)
                {
                    return 0;
                }

                SceneMesh mesh = scene.Meshes[node.Mesh];
                int indices = 0;
                for (int part = 0; part < mesh.PartCount; part++)
                {
                    indices += scene.Parts[mesh.FirstPart + part].IndexCount;
                }

                return indices / 3;
            }
        }

        throw new Xunit.Sdk.XunitException($"No item named '{nodeName}' in: {Tree(scene)}");
    }

    /// <summary>
    /// A face has the behaviour bits of any entity, the one that says whether it is shown among them
    /// (ISO 14739-1, PRC_TYPE_TESS_Face). A face that is not shown is not drawn.
    /// </summary>
    [Fact]
    public void AFaceThatIsNotShownIsNotDrawn()
    {
        // Three faces of one triangle each: the second is not shown.
        using Scene scene = Build(File(Structure(
            [Occurrence("o", part: 1)], [Part("p", Solid("body", 1))], [Mesh((1, Shown), (2, 0), (3, Shown))])));

        Assert.Equal("-1>o 0>body#0", Tree(scene));
        Assert.Equal(2, TrianglesOf(scene, "body"));

        // The slots are the faces' still: the shown ones keep their colours.
        Assert.Equal(Red, MaterialOf(scene, "body", 1).Diffuse);
        Assert.Equal(Blue, MaterialOf(scene, "body", 3).Diffuse);

        // A face without a style has no behaviour to speak of (the bits are "not relevant" then).
        using Scene plain = Build(File(Structure(
            [Occurrence("o", part: 1)], [Part("p", Solid("body", 1))], [Mesh((0, 0), (1, Shown))])));
        Assert.Equal(2, TrianglesOf(plain, "body"));
    }

    /// <summary>
    /// Writers that do not use the bits leave them all clear, on faces that are plainly to be seen:
    /// every styled face of the Anark, NAVAir, Native-NX and OfficeBuilding samples (57,100 faces) has
    /// behaviour 0, and no sample mixes faces that say "shown" with faces that do not. So the bit
    /// counts only where the faces of a file structure use it: where at least one says "shown".
    /// </summary>
    [Fact]
    public void FacesThatAllLeaveTheirBitsClearAreDrawn()
    {
        using Scene scene = Build(File(Structure(
            [Occurrence("o", part: 1)], [Part("p", Solid("body", 1))], [Mesh((1, 0), (2, 0), (0, 0))])));

        Assert.Equal(3, TrianglesOf(scene, "body"));

        // Another tessellation of the same file structure decides it too.
        using Scene two = Build(File(Structure(
            [Occurrence("o", part: 1)], [Part("p", Solid("a", 1), Solid("b", 2))], [Mesh((1, 0), (2, 0)), Mesh((3, Shown))])));
        Assert.Equal(0, TrianglesOf(two, "a"));
        Assert.Equal(1, TrianglesOf(two, "b"));
    }

    /// <summary>
    /// Whether a face is shown is inherited like its colour: a father may impose "shown" on its sons.
    /// Two uses of one part may then draw different faces of the same tessellation.
    /// </summary>
    [Fact]
    public void AFatherMayImposeThatItsFacesAreShown()
    {
        using Scene scene = Build(File(Structure(
            [
                Occurrence("as it is", part: 1),
                Occurrence("all of it", part: 1, behavior: Shown | FatherHeritShow),
                Occurrence("assembly", sons: [0, 1]),
            ],
            [Part("p", Solid("body", 1))], [Mesh((1, Shown), (2, 0))])));

        Assert.Equal("-1>assembly 0>as it is 1>body#0 0>all of it 3>body#1", Tree(scene));
        Assert.Equal(2, scene.Meshes.Length);
        Assert.Equal(1, scene.Meshes[0].PartCount);
        Assert.Equal(2, scene.Meshes[1].PartCount);

        // A face that insists is not overruled.
        using Scene insisting = Build(File(Structure(
            [Occurrence("all of it", part: 1, behavior: Shown | FatherHeritShow)],
            [Part("p", Solid("body", 1))], [Mesh((1, Shown), (2, SonHeritShow))])));
        Assert.Equal(1, TrianglesOf(insisting, "body"));
    }

    /// <summary>A body that is not shown is not drawn, whatever its faces say; one whose faces are all hidden has nothing to draw.</summary>
    [Fact]
    public void AHiddenBodyStaysHiddenAndABodyWithoutShownFacesHasNoMesh()
    {
        using Scene scene = Build(File(Structure(
            [Occurrence("o", part: 1)],
            [Part("p", Solid("hidden body", 1, behavior: 0), Solid("faceless", 2))],
            [Mesh((1, Shown)), Mesh((1, 0), (2, 0)), Mesh((3, Shown))])));

        Assert.Equal("-1>o 0>hidden body#0(hidden) 0>faceless(hidden)", Tree(scene));
    }

    /// <summary>
    /// A PRC model has one unit of length: the first reliable one, from the model file down through the
    /// product occurrences (ISO 14739-1, 5.6). The unit of an occurrence is not a scale: writers put the
    /// factor between an inch part and a millimetre assembly into the part's placement (the Flashlight
    /// and PROSTEP engine samples: placements scaled by 25.4 and by 1/25.4).
    /// </summary>
    [Fact]
    public void TheUnitIsTheFirstReliableOneFromTheModelFileDown()
    {
        PrcFileStructureData Model() => Structure(
            [
                Occurrence("inch part", part: 1, unit: 25.4),
                Occurrence("centimetre part", part: 1, unit: 10),
                Occurrence("assembly", sons: [0, 1], unit: 3, unitFromCad: false),
            ],
            [Part("p", Solid("body", 1))]);

        // The model file's, when it is reliable: whatever the occurrences say.
        using Scene reliable = Build(File(1000, Model()));
        Assert.Equal(1, reliable.UnitScale, 9);
        Assert.Empty(reliable.Diagnostics);

        // Else the first reliable one on the way down.
        using Scene below = Build(File(1000, false, Model()));
        Assert.Equal(0.0254, below.UnitScale, 9);
        Assert.Empty(below.Diagnostics);

        // Nothing is scaled by it.
        Assert.Equal(Matrix4x4.Identity, below.GetWorldMatrix(2));
        Assert.Equal(Matrix4x4.Identity, below.GetWorldMatrix(4));

        // None is reliable: the model file's, and the scene says that it may not be right.
        using Scene none = Build(File(25.4, false, Structure([Occurrence("o", part: 1, unit: 10, unitFromCad: false)], [Part("p", Solid("body", 1))])));
        Assert.Equal(0.0254, none.UnitScale, 9);
        Assert.Equal(["prc-unit-not-reliable"], none.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void TransparencyIsInheritedOnItsOwn()
    {
        // The occurrence is half transparent blue; the item is red and says nothing of transparency.
        using Scene scene = Build(File(Structure([Occurrence("glass", part: 1, style: 5)], [Part("p", Solid("body", 1, style: 1), Solid("bare", 1))])));

        SceneMaterial body = MaterialOf(scene, "body");
        Assert.Equal(Red, body.Diffuse);
        Assert.Equal(128 / 255f, body.Opacity, 5);

        SceneMaterial bare = MaterialOf(scene, "bare");
        Assert.Equal(Blue, bare.Diffuse);
        Assert.Equal(128 / 255f, bare.Opacity, 5);
    }

    /// <summary>
    /// A style that names no colour and gives a transparency changes the transparency only: the colour stays what the fathers gave
    /// (the iPhone sample's assembly makes its phone see-through that way; read as a colour it turned the phone the default grey).
    /// </summary>
    [Fact]
    public void AStyleWithoutAColourLeavesTheColourAlone()
    {
        PrcGraphStyle[] styles = [Colour(7), Colour(0, 38)];
        using Scene scene = Build(File(Structure([Occurrence("o", part: 1, style: 1)], [Part("p", Solid("glass", 1, style: 2))], styles: styles)));

        SceneMaterial glass = MaterialOf(scene, "glass");
        Assert.Equal(Blue, glass.Diffuse);
        Assert.Equal(38 / 255f, glass.Opacity, 5);
    }

    /// <summary>
    /// Whether something is shown follows the same rule: the nearest entity says, unless a father's
    /// "hidden" is inherited.
    /// </summary>
    [Fact]
    public void WhatIsHiddenStaysInTheTreeAndIsNotDrawn()
    {
        using Scene scene = Build(File(Structure(
            [
                Occurrence("shown", part: 1), Occurrence("hidden item", part: 2),
                Occurrence("hidden branch", part: 1, behavior: FatherHeritShow),
                Occurrence("hidden, sons decide", part: 1, behavior: 0),
                Occurrence("root", sons: [0, 1, 2, 3]),
            ],
            [Part("p1", Solid("body", 1)), Part("p2", Solid("ghost", 1, behavior: 0))])));

        Assert.Equal("-1>root 0>shown 1>body#0 0>hidden item 3>ghost#0(hidden) 0>hidden branch 5>body#0(hidden) 0>hidden, sons decide 7>body#0", Tree(scene));
    }

    /// <summary>
    /// What an occurrence says of an entity under it (PRC_TYPE_MISC_EntityReference: "modifiers towards their nominal definition, which may
    /// include location, color, and visibility"): a style of the occurrence's own file structure, or that the entity is removed, for that
    /// use of the part only. Assemblies colour and cut away the items of their parts that way (the Tetra4D Turbine).
    /// </summary>
    private static PrcMiscEntityReference Reference(PrcRiRepresentationItem item, uint style = 0, ushort behavior = Shown, PrcUniqueId? inStructure = null,
        uint coordinateSystem = 0) => new()
    {
        ContentEntityReference = new PrcContentEntityReference
        {
            Base = Base("", style, behavior),
            IndexOfLocalCoordinateSystem = coordinateSystem,
            Flag = true,
            ReferenceData = new PrcReferenceData
            {
                DataType = PrcType.PRC_TYPE_MISC_ReferenceOnPRCBase,
                Data = new PrcMiscReferenceOnPRCBase
                {
                    TypeOfEntity = item.DataType,
                    Flag = inStructure is null,
                    DifferentUniqueId = inStructure ?? default,
                    UniqueId = ((PrcRiPolyBrepModel)item.Data!).ItemContent.Base.Base.UniqueId,
                },
            },
        },
    };

    [Fact]
    public void AnOccurrenceRecoloursOrRemovesTheItemsOfItsPart()
    {
        // The parts' file structure: style 1 is red there, and the blade's face says red too.
        PrcRiRepresentationItem blade = Solid("blade", 1), halfA = Solid("half A", 1), halfB = Solid("half B", 1);
        PrcFileStructureData parts = Structure(
            [Occurrence("blade definition", part: 1), Occurrence("cover definition", part: 2)],
            [Part("blade part", blade), Part("cover part", halfA, halfB)], [Mesh((1, Shown))], id: 50);

        // The assembly's own styles: 1 is green, 2 is yellow.
        PrcGraphStyle[] assemblyStyles = [Colour(4), Colour(10)];
        PrcFileStructureData assembly = Structure(
            [
                Occurrence("blade 1", prototype: 1, prototypeIn: Id(50), references: [Reference(blade, style: 1, behavior: Shown | SonHeritColor, inStructure: Id(50))]),
                Occurrence("blade 2", prototype: 1, prototypeIn: Id(50)),
                Occurrence("cover", prototype: 2, prototypeIn: Id(50), references: [Reference(halfA, behavior: FatherHeritShow | Removed, inStructure: Id(50))]),
                Occurrence("engine", sons: [0, 1, 2]),
            ],
            id: 60, styles: assemblyStyles);

        using Scene scene = Build(File(parts, assembly));

        // The removed half is gone from that use of the cover only.
        Assert.Equal("-1>engine 0>blade 1 1>blade#0 0>blade 2 3>blade#0 0>cover 5>half B#0", Tree(scene));

        // The recoloured blade takes the assembly's green on the item and over its own face's red; the other blade keeps its red face.
        SceneNode[] nodes = scene.Nodes.ToArray();
        Assert.All(Enumerable.Range(0, nodes[2].BindingCount), slot => Assert.Equal(Green, scene.Materials[scene.Bindings[nodes[2].FirstBinding + slot]].Diffuse));
        Assert.Equal(Red, scene.Materials[scene.Bindings[nodes[4].FirstBinding + 1]].Diffuse);

        // Of two occurrences that say something of the same item, the outer one (the assembly) has the last word.
        PrcFileStructureData outer = Structure(
            [
                Occurrence("blade 1", prototype: 1, prototypeIn: Id(50), references: [Reference(blade, style: 1, inStructure: Id(50))]),
                Occurrence("engine", sons: [0], references: [Reference(blade, style: 2, inStructure: Id(50))]),
            ],
            id: 60, styles: assemblyStyles);
        using Scene overridden = Build(File(parts, outer));
        Assert.Equal(Yellow, MaterialOf(overridden, "blade", 0).Diffuse);
    }

    /// <summary>An item of exact geometry that shows a tessellation: body <paramref name="body"/> of topological context <paramref name="context"/> (both counted from 1).</summary>
    private static PrcRiRepresentationItem BrepItem(string name, uint tessellation, uint context = 1, uint body = 1) => new()
    {
        Data = new PrcRiBrepModel
        {
            ItemContent = new PrcRepresentationItemContent { Base = Base(name), BiasedIndexLocalCoordinateSystem = 0, BiasedIndexTessellation = tessellation },
            ExactGeometry = true,
            IndexTopologicalContext = context,
            IndexBody = body,
        },
        DataType = PrcType.PRC_TYPE_RI_BrepModel,
    };

    /// <summary>What an occurrence says of face <paramref name="face"/> (counted from 0) of a body.</summary>
    private static PrcMiscEntityReference FaceReference(uint face, uint style = 0, ushort behavior = Shown, PrcUniqueId? inStructure = null, uint context = 1, uint body = 1,
        uint type = PrcType.PRC_TYPE_TOPO_Face) => new()
    {
        ContentEntityReference = new PrcContentEntityReference
        {
            Base = Base("", style, behavior),
            IndexOfLocalCoordinateSystem = 0,
            Flag = true,
            ReferenceData = new PrcReferenceData
            {
                DataType = PrcType.PRC_TYPE_MISC_ReferenceOnTopology,
                Data = new PrcMiscReferenceOnTopology
                {
                    TypeOfEntity = type,
                    Flag = true,
                    Data = new PrcAdditionalTargetData
                    {
                        Flag = inStructure is null,
                        UniqueId = inStructure ?? default,
                        IndexOfTopologicalIndex = context,
                        IndexOfBody = body,
                        NumberOfIndices = 1,
                        Indices = [face],
                    },
                },
            },
        },
    };

    /// <summary>The colours the triangles of the <paramref name="instance"/>-th mesh node are drawn in, with how many triangles each.</summary>
    private static string TriangleColours(Scene scene, int instance)
    {
        SceneNode node = scene.Nodes.ToArray().Where(n => n.Mesh >= 0).ElementAt(instance);
        SceneMesh mesh = scene.Meshes[node.Mesh];
        var colours = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (SceneMeshPart part in scene.Parts.Slice(mesh.FirstPart, mesh.PartCount))
        {
            Vector3 d = scene.Materials[scene.Bindings[node.FirstBinding + part.ShadingId]].Diffuse;
            string name = d == Red ? "red" : d == Green ? "green" : d == Yellow ? "yellow" : d == Blue ? "blue" : d == Grey ? "grey" : d.ToString();
            colours[name] = colours.GetValueOrDefault(name) + (part.IndexCount / 3);
        }

        return string.Join(" ", colours.Select(c => $"{c.Key}:{c.Value}"));
    }

    /// <summary>
    /// An occurrence colours single faces of the bodies of its parts too, or removes them (a topology reference: the body as its
    /// item names it, and the face's number in it, which is the face's number in the item's tessellation). That holds for that use
    /// of the part only, over the face's own style, and of two occurrences the outer one wins.
    /// </summary>
    [Fact]
    public void AnOccurrenceRecoloursOrRemovesSingleFacesOfItsParts()
    {
        // The parts' file structure: style 1 is red there; face 0 of the body says red, faces 1 and 2 nothing.
        PrcFileStructureData parts = Structure(
            [Occurrence("body definition", part: 1)],
            [Part("part", BrepItem("body", 1))], [Mesh((1, Shown), (0, 0), (0, 0))], id: 50);

        // The assembly's own styles: 1 is green, 2 is yellow.
        PrcGraphStyle[] assemblyStyles = [Colour(4), Colour(10)];
        PrcFileStructureData assembly = Structure(
            [
                Occurrence("body 1", prototype: 1, prototypeIn: Id(50), references: [FaceReference(0, style: 1, inStructure: Id(50)), FaceReference(2, style: 2, inStructure: Id(50))]),
                Occurrence("body 2", prototype: 1, prototypeIn: Id(50)),
                Occurrence("body 3", prototype: 1, prototypeIn: Id(50), references: [FaceReference(1, behavior: Removed, inStructure: Id(50))]),
                Occurrence("engine", sons: [0, 1, 2]),
            ],
            id: 60, styles: assemblyStyles);

        using Scene scene = Build(File(parts, assembly));
        Assert.Equal("green:1 grey:1 yellow:1", TriangleColours(scene, 0));
        Assert.Equal("grey:2 red:1", TriangleColours(scene, 1));
        Assert.Equal("grey:1 red:1", TriangleColours(scene, 2));
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "prc-face-reference-not-applied");

        // Of two occurrences that say something of the same face, the outer one (the assembly) has the last word.
        PrcFileStructureData outer = Structure(
            [
                Occurrence("body 1", prototype: 1, prototypeIn: Id(50), references: [FaceReference(0, style: 1, inStructure: Id(50))]),
                Occurrence("engine", sons: [0], references: [FaceReference(0, style: 2, inStructure: Id(50))]),
            ],
            id: 60, styles: assemblyStyles);
        using Scene overridden = Build(File(parts, outer));
        Assert.Equal("grey:2 yellow:1", TriangleColours(overridden, 0));
    }

    [Fact]
    public void AnOccurrenceRecoloursAFaceOfACompressedMesh()
    {
        PrcFileStructureData parts = Structure([Occurrence("definition", part: 1)], [Part("part", BrepItem("body", 1))], [Compressed(style: 1)], id: 50);
        PrcFileStructureData assembly = Structure(
            [Occurrence("body 1", prototype: 1, prototypeIn: Id(50), references: [FaceReference(0, style: 2, inStructure: Id(50))]), Occurrence("root", sons: [0])],
            id: 60, styles: [Colour(4), Colour(10)]);

        using Scene scene = Build(File(parts, assembly));
        Assert.Equal("yellow:1", TriangleColours(scene, 0));
    }

    [Fact]
    public void WhatAnOccurrenceSaysOfOtherTopologyIsReportedNotApplied()
    {
        PrcRiRepresentationItem body = Solid("body", 1);
        PrcMiscEntityReference edge = FaceReference(0, style: 1, type: PrcType.PRC_TYPE_TOPO_Edge);

        using Scene scene = Build(File(Structure([Occurrence("root", part: 1, references: [edge])], [Part("p", body)])));
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-topology-reference-not-applied");
    }

    /// <summary>
    /// An occurrence may place an item of its part elsewhere: its reference names a coordinate system of the occurrence's own file
    /// structure (counted from 1), which is then the item's placement in its part, in place of the item's own, for that use of the
    /// part only (PROSTEP's Camera sample lines its display icons up that way). A style given with it is applied too.
    /// </summary>
    [Fact]
    public void AnOccurrenceMovesAnItemOfItsPart()
    {
        PrcRiCoordinateSystem System(double x, double y, double z) => new()
        {
            Transform = new PrcMiscCartesianTransformation { Transform = Translation(x, y, z) },
            TransformType = PrcType.PRC_TYPE_MISC_CartesianTransformation,
        };

        PrcRiRepresentationItem moved = Solid("moved", 1, coordinateSystem: 1);
        PrcFileStructureData parts = Structure([Occurrence("definition", part: 1)], [Part("part", moved)], id: 50, coordinateSystems: [System(0, 0, 7)]);
        PrcFileStructureData assembly = Structure(
            [
                Occurrence("use 1", prototype: 1, prototypeIn: Id(50), references: [Reference(moved, style: 1, inStructure: Id(50), coordinateSystem: 2)]),
                Occurrence("use 2", prototype: 1, prototypeIn: Id(50)),
                Occurrence("root", sons: [0, 1]),
            ],
            id: 60, styles: [Colour(4)], coordinateSystems: [System(9, 9, 9), System(5, 0, 0)]);

        using Scene scene = Build(File(parts, assembly));
        int[] instances = [.. Enumerable.Range(0, scene.Nodes.Length).Where(i => Plain(scene.GetName(scene.Nodes[i].Name)) == "moved")];
        Assert.Equal(2, instances.Length);
        Assert.Equal(new Vector3(5, 0, 0), Vector3.Transform(Vector3.Zero, scene.GetWorldMatrix(instances[0])));
        Assert.Equal(new Vector3(0, 0, 7), Vector3.Transform(Vector3.Zero, scene.GetWorldMatrix(instances[1])));
        Assert.Equal(Green, MaterialOf(scene, "moved", 0).Diffuse);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "prc-reference-placement-not-applied");
    }

    [Fact]
    public void WhatIsRemovedOrSuppressedIsNotThere()
    {
        using Scene scene = Build(File(Structure(
            [
                Occurrence("kept", part: 1), Occurrence("removed", part: 1, behavior: Shown | Removed), Occurrence("suppressed", part: 1, productBehavior: 1),
                Occurrence("root", sons: [0, 1, 2]),
            ],
            [Part("p", Solid("body", 1), Solid("removed body", 1, behavior: Shown | Removed), Set("removed set", 0, Shown | Removed, Solid("inside", 1)))])));

        // What is removed is gone; what is suppressed is there, hidden, for a view to name.
        Assert.Equal("-1>root 0>kept 1>body#0 0>suppressed 3>body#0(hidden)", Tree(scene));
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-suppressed-occurrence");
    }

    /// <summary>
    /// Configurations are alternatives of one product: only the default one is shown. The others, and
    /// occurrences that are views, are in the scene (PDF views name them) but hidden.
    /// </summary>
    [Fact]
    public void OfSeveralConfigurationsTheDefaultIsShown()
    {
        const byte configuration = 0x08, isDefault = 0x01, view = 0x10, container = 0x04;
        using Scene scene = Build(File(Structure(
            [
                Occurrence("left-hand drive", part: 1, flags: configuration), Occurrence("right-hand drive", part: 1, flags: configuration | isDefault),
                Occurrence("exploded view", part: 1, flags: view), Occurrence("car", sons: [0, 1, 2], flags: container),
            ],
            [Part("p", Solid("body", 1))])));

        Assert.Equal("-1>car 0>left-hand drive 1>body#0(hidden) 0>right-hand drive 3>body#0 0>exploded view 5>body#0(hidden)", Tree(scene));
        Assert.Equal(2, Assert.Single(scene.Diagnostics, d => d.Code == "prc-alternative-hidden").Count);

        // Without a default, the first; a single configuration is simply shown.
        using Scene first = Build(File(Structure([Occurrence("a", part: 1, flags: configuration), Occurrence("b", part: 1, flags: configuration), Occurrence("car", sons: [0, 1])], [Part("p", Solid("body", 1))])));
        Assert.Equal("-1>car 0>a 1>body#0 0>b 3>body#0(hidden)", Tree(first));
        using Scene single = Build(File(Structure([Occurrence("only", part: 1, flags: configuration), Occurrence("car", sons: [0])], [Part("p", Solid("body", 1))])));
        Assert.Equal("-1>car 0>only 1>body#0", Tree(single));
    }

    [Fact]
    public void ASetOfItemsIsAGroup()
    {
        using Scene scene = Build(File(Structure([Occurrence("part", part: 1)], [Part("p", Set("shell", 2, Shown, Solid("inner", 1), Solid("outer", 1, style: 1)))])));

        Assert.Equal("-1>part 0>shell 1>inner#0 1>outer#0", Tree(scene));
        Assert.Equal(Green, MaterialOf(scene, "inner").Diffuse);
        Assert.Equal(Red, MaterialOf(scene, "outer").Diffuse);
    }

    /// <summary>
    /// What is missing is said: a mesh in the compressed form the reader does not turn into geometry
    /// makes the scene incomplete; exact geometry without a mesh, wires and markup do not.
    /// </summary>
    [Fact]
    public void ItemsWithoutAMeshAreNodesWithoutOne()
    {
        var curve = new PrcRiRepresentationItem { Data = null, DataType = PrcType.PRC_TYPE_RI_Curve };
        using Scene scene = Build(File(Structure(
            [Occurrence("part", part: 1)],
            [Part("p", Solid("meshed", 1), Solid("compressed", 2), Solid("exact only", 0), Solid("wire", 3), curve)],
            [Mesh(), Compressed(decodable: false), Other(PrcType.PRC_TYPE_TESS_3D_Wire)])));

        Assert.Equal("-1>part 0>meshed#0 0>compressed 0>exact only 0>wire", Tree(scene));
        Assert.False(PrcReader.IsComplete(scene));
        Assert.Equal(1, Assert.Single(scene.Diagnostics, d => d.Code == PrcReader.CompressedTessellationDiagnostic).Count);
        Assert.Equal(1, Assert.Single(scene.Diagnostics, d => d.Code == "prc-exact-geometry-only").Count);
        Assert.Equal(2, Assert.Single(scene.Diagnostics, d => d.Code == "prc-item-not-shown").Count);

        using Scene whole = Build(File(Structure([Occurrence("part", part: 1)], [Part("p", Solid("exact only", 0), curve)])));
        Assert.True(PrcReader.IsComplete(whole));
        Assert.Equal(0, whole.Meshes.Length);
    }

    [Fact]
    public void ACompressedTessellationIsDecodedIntoAMesh()
    {
        using Scene scene = Build(File(Structure(
            [Occurrence("part", part: 1)],
            [Part("p", Solid("compressed", 1), Solid("again", 1))],
            [Compressed()])));

        // One mesh of one triangle, shown by both items; nothing is missing.
        Assert.Equal("-1>part 0>compressed#0 0>again#0", Tree(scene));
        Assert.True(PrcReader.IsComplete(scene));
        Assert.Equal(3, scene.Indices.Length);
        Assert.Equal(3, scene.Meshes[0].VertexCount);
    }

    [Fact]
    public void ACompressedTessellationWithColoursOnItsPointsGivesAMeshWithColours()
    {
        PrcTess tess = Compressed();
        var compressed = (PrcTess3DCompressed)tess.Id!;
        compressed.IsPointColorOnFace = new Caly.Rendering.Scene3D.Prc.Tessellation.PrcBits([0x80000000u], 1);
        compressed.PointColors = [0, 255, 0, 0, 0, 0, 0, 255, 0, 0, 1, 0, 0, 255, 128];
        using Scene scene = Build(File(Structure(
            [Occurrence("part", part: 1)],
            [Part("p", Solid("compressed", 1))],
            [tess])));

        // Red, green, and a half transparent blue: one a corner of the triangle.
        Assert.Equal(SceneMeshFlags.HasNormals | SceneMeshFlags.HasColors, scene.Meshes[0].Flags);
        Assert.Equal([0x80FF0000u, 0xFF0000FFu, 0xFF00FF00u], scene.Colors.ToArray().Order().ToArray());
    }

    [Fact]
    public void ACompressedTessellationThatDoesNotDecodeIsSaidOnce()
    {
        using Scene scene = Build(File(Structure(
            [Occurrence("part", part: 1)],
            [Part("p", Solid("compressed", 1), Solid("again", 1))],
            [Compressed(decodable: false)])));

        Assert.Equal("-1>part 0>compressed 0>again", Tree(scene));
        Assert.False(PrcReader.IsComplete(scene));
        Assert.Equal(1, Assert.Single(scene.Diagnostics, d => d.Code == PrcReader.CompressedTessellationDiagnostic).Count);
    }

    /// <summary>
    /// A file whose compressed faces say they are shown and whose plain faces leave that bit clear
    /// (the OfficeBuilding sample): the plain ones are not taken at their word for it.
    /// </summary>
    [Fact]
    public void CompressedFacesThatSayTheyAreShownDoNotHideThePlainOnes()
    {
        PrcGraphStyle[] styles = [Colour(10)];
        using Scene scene = Build(File(Structure(
            [Occurrence("o", part: 1)],
            [Part("p", Solid("plain", 1), Solid("compressed", 2))],
            [Mesh((1, 0)), Compressed(style: 1)],
            styles: styles)));

        Assert.Equal("-1>o 0>plain#0 0>compressed#1", Tree(scene));
        Assert.All(scene.Nodes.ToArray(), node => Assert.True(node.Visible));
    }

    [Fact]
    public void TheFacesOfACompressedTessellationHaveTheirStyles()
    {
        PrcGraphStyle[] styles = [Colour(10)];
        using Scene scene = Build(File(Structure([Occurrence("o", part: 1)], [Part("p", Solid("styled", 1))], [Compressed(style: 1)], styles: styles)));

        // Slot 0 for triangles without a style, slot 1 for the face's style: the triangle is in slot 1.
        Assert.Equal(Yellow, MaterialOf(scene, "styled", slot: 1).Diffuse);
        Assert.Equal(1, scene.Meshes[0].PartCount);
    }

    /// <summary>
    /// A tessellation that says it has no faces still numbers its triangles' faces (558 of the samples do),
    /// and stores one style: it is the style of every triangle, not of the first number only.
    /// </summary>
    [Fact]
    public void WithoutFacesTheOneStyleOfACompressedTessellationIsForEveryTriangle()
    {
        PrcTess tess = Compressed(style: 1);
        var compressed = (PrcTess3DCompressed)tess.Id!;
        compressed.HasFaces = false;
        compressed.Points = [0, 0, 0, 8, 0, 0, 0, 6, 0, 0, -6, 0];
        compressed.EdgeStatus = [2, 0];
        compressed.TriangleFaces = [0, 1];
        compressed.FaceCount = 2;
        compressed.PointIsReference = new Caly.Rendering.Scene3D.Prc.Tessellation.PrcBits([0u], 4);
        compressed.NormalIsReversed = new Caly.Rendering.Scene3D.Prc.Tessellation.PrcBits([0u], 2);
        PrcGraphStyle[] styles = [Colour(10)];
        using Scene scene = Build(File(Structure([Occurrence("o", part: 1)], [Part("p", Solid("styled", 1))], [tess], styles: styles)));

        // Both triangles are in the slot of the style: one part of six indices.
        Assert.Equal(Yellow, MaterialOf(scene, "styled", slot: 1).Diffuse);
        Assert.Equal(1, scene.Meshes[0].PartCount);
        Assert.Equal(6, scene.Indices.Length);
    }

    /// <summary>A colour's index counts the numbers of the colours, three each, plus one (issue #816).</summary>
    [Fact]
    public void ColoursAreIndexedByTheirNumbers()
    {
        PrcGraphStyle[] styles = [Colour(10), Colour(13), Colour(0)];
        using Scene scene = Build(File(Structure([Occurrence("o", part: 1)], [Part("p", Solid("fourth", 1, style: 1), Solid("beyond", 1, style: 2), Solid("none", 1, style: 3), Solid("no style", 1, style: 9))], styles: styles)));

        Assert.Equal(Yellow, MaterialOf(scene, "fourth").Diffuse);
        Assert.Equal(Grey, MaterialOf(scene, "beyond").Diffuse);
        Assert.Equal(Grey, MaterialOf(scene, "none").Diffuse);
        Assert.Equal(Grey, MaterialOf(scene, "no style").Diffuse);
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-missing-colour");
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-missing-style");
    }

    /// <summary>
    /// A writer leaves the four alphas of its materials at 0 and says in the style that they are opaque
    /// (17 payloads of the second sample set, and documents whose model then drew as nothing at all): an
    /// alpha of 0 everywhere is an alpha not given.
    /// </summary>
    [Fact]
    public void AMaterialWhoseAlphasAreAllZeroIsNotTransparent()
    {
        var unset = new PrcGraphMaterial { BiasedAmbientIndex = 1, BiasedDiffuseIndex = 4, BiasedEmissiveIndex = 0, BiasedSpecularIndex = 10, Shininess = 0.4, AmbientAlpha = 0, DiffuseAlpha = 0, EmissiveAlpha = 0, SpecularAlpha = 0 };
        var clear = new PrcGraphMaterial { BiasedAmbientIndex = 1, BiasedDiffuseIndex = 4, BiasedEmissiveIndex = 0, BiasedSpecularIndex = 10, Shininess = 0.4, AmbientAlpha = 1, DiffuseAlpha = 0, EmissiveAlpha = 1, SpecularAlpha = 1 };
        PrcMaterial[] materials = [new() { Id = unset, IdType = PrcType.PRC_TYPE_GRAPH_Material }, new() { Id = clear, IdType = PrcType.PRC_TYPE_GRAPH_Material }];
        PrcGraphStyle[] styles = [MaterialStyle(1), MaterialStyle(2)];

        using Scene scene = Build(File(Structure(
            [Occurrence("o", part: 1)],
            [Part("p", Solid("unset", 1, style: 1), Solid("clear", 1, style: 2))],
            styles: styles, materials: materials)));

        Assert.Equal(1f, MaterialOf(scene, "unset").Opacity);

        // A diffuse alpha of 0 among alphas that are given is what it says.
        Assert.Equal(0f, MaterialOf(scene, "clear").Opacity);
    }

    /// <summary>Materials and textures applied on them are entries of one array (issue #818).</summary>
    [Fact]
    public void AStyleMayNameAMaterialOrATextureOnOne()
    {
        var steel = new PrcGraphMaterial { BiasedAmbientIndex = 1, BiasedDiffuseIndex = 4, BiasedEmissiveIndex = 0, BiasedSpecularIndex = 10, Shininess = 0.4, AmbientAlpha = 1, DiffuseAlpha = 0.5, EmissiveAlpha = 1, SpecularAlpha = 1 };
        var black = new PrcGraphMaterial { BiasedAmbientIndex = 0, BiasedDiffuseIndex = 0, BiasedEmissiveIndex = 0, BiasedSpecularIndex = 7, Shininess = 64, AmbientAlpha = 1, DiffuseAlpha = 1, EmissiveAlpha = 1, SpecularAlpha = 1 };
        var label = new PrcGraphTextureApplication { BiasedMaterialGenericIndex = 2, BiasedTextureDefinitionIndex = 1, BiasedNextTextureIndex = 0, BiasedUvCoordinatesIndex = 1 };
        var raw = new PrcGraphTextureApplication { BiasedMaterialGenericIndex = 2, BiasedTextureDefinitionIndex = 2, BiasedNextTextureIndex = 0, BiasedUvCoordinatesIndex = 1 };
        PrcMaterial[] materials =
        [
            new() { Id = steel, IdType = PrcType.PRC_TYPE_GRAPH_Material }, new() { Id = black, IdType = PrcType.PRC_TYPE_GRAPH_Material },
            new() { Id = label, IdType = PrcType.PRC_TYPE_GRAPH_TextureApplication }, new() { Id = raw, IdType = PrcType.PRC_TYPE_GRAPH_TextureApplication },
        ];
        byte[] png = PngHeader(64, 32, colourType: 6);
        PrcGraphPicture[] pictures =
        [
            new() { Format = 0, BiasedUncompressedFileIndex = 1, PixelWidth = 0, PixelHeight = 0 },
            new() { Format = 2, BiasedUncompressedFileIndex = 1, PixelWidth = 4, PixelHeight = 4 },
        ];
        PrcGraphTextureDefinition[] textures =
        [
            new() { BiasedPictureIndex = 1, TextureApplicationMode = 0, AlphaTestReference = 0, TextureWrappingModeS = 1, TextureWrappingModeT = 1, TextureTransformation = null, TextureFunction = 2, TextureMappingType = 1, TextureMappingOperator = 0 },
            new() { BiasedPictureIndex = 2, TextureApplicationMode = 0, AlphaTestReference = 0, TextureWrappingModeS = 1, TextureWrappingModeT = 1, TextureTransformation = null, TextureFunction = 2, TextureMappingType = 1, TextureMappingOperator = 0 },
        ];
        PrcGraphStyle[] styles = [MaterialStyle(1), MaterialStyle(3), MaterialStyle(4), MaterialStyle(9)];

        using Scene scene = Build(File(Structure(
            [Occurrence("o", part: 1)],
            [Part("p", Solid("steel", 1, style: 1), Solid("labelled", 1, style: 2), Solid("labelled again", 1, style: 2), Solid("raw pixels", 1, style: 3), Solid("no such material", 1, style: 4))],
            styles: styles, materials: materials, pictures: pictures, textures: textures, files: [png])));

        SceneMaterial m = MaterialOf(scene, "steel");
        Assert.Equal((Red, Green, Yellow, Vector3.Zero), (m.Ambient, m.Diffuse, m.Specular, m.Emissive));
        Assert.Equal(0.4f, m.Reflectivity);
        Assert.Equal(0.5f, m.Opacity);
        Assert.Equal(0, m.LayerCount);

        // The picture is the colour of a textured surface; the material under it gives the highlight.
        m = MaterialOf(scene, "labelled");
        Assert.Equal((Vector3.One, Vector3.One, Blue), (m.Ambient, m.Diffuse, m.Specular));
        Assert.Equal(0.5f, m.Reflectivity);                           // an exponent of 64 of 128
        Assert.Equal(1, m.LayerCount);
        SceneTexture texture = scene.Textures[scene.TextureLayers[m.FirstLayer].Texture];
        Assert.Equal((64, 32, SceneTextureChannels.Rgba), (texture.Width, texture.Height, texture.Channels));
        Assert.Equal(SceneImageEncoding.Png, texture.Images[0].Encoding);
        Assert.Equal(png, texture.Images[0].Data.ToArray());
        Assert.Single(scene.Textures);

        // Pixels that are not a PNG or JPEG file are not carried: the material stays, without texture.
        m = MaterialOf(scene, "raw pixels");
        Assert.Equal(0, m.LayerCount);
        Assert.Equal(Blue, m.Specular);
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-picture-not-carried");

        Assert.Equal(Grey, MaterialOf(scene, "no such material").Diffuse);
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-missing-material");
    }

    /// <summary>
    /// A texture definition may place its picture on the surface's coordinates: scaled, turned and moved (the
    /// Shoe's sole grain is a small picture that has to repeat across a large atlas of coordinates).
    /// </summary>
    [Fact]
    public void ATextureDefinitionTransformsTheTextureCoordinates()
    {
        PrcMaterial[] materials = Enumerable.Range(1, 5)
            .Select(i => new PrcMaterial { Id = new PrcGraphTextureApplication { BiasedMaterialGenericIndex = 0, BiasedTextureDefinitionIndex = (uint)i, BiasedNextTextureIndex = 0, BiasedUvCoordinatesIndex = 1 }, IdType = PrcType.PRC_TYPE_GRAPH_TextureApplication })
            .ToArray();
        byte[] png = PngHeader(8, 8, colourType: 6);
        PrcGraphPicture[] pictures = [new() { Format = 0, BiasedUncompressedFileIndex = 1, PixelWidth = 0, PixelHeight = 0 }];

        static PrcGraphTextureDefinition Definition(PrcGraphTextureTransformation? transformation) => new()
        {
            BiasedPictureIndex = 1, TextureApplicationMode = 0, AlphaTestReference = 0, TextureWrappingModeS = 1, TextureWrappingModeT = 1, TextureTransformation = transformation, TextureFunction = 2, TextureMappingType = 1, TextureMappingOperator = 0,
        };

        static PrcTransformation2D Transform(sbyte behavior, PrcVector2D? translation = null, double scale = 1, PrcVector2D? nonUniform = null, PrcVector2D? rotation = null) => new()
        {
            Behavior = behavior, Translation = translation, NonOrthoMatrix = [], Rotation = rotation, NonUniformScale = nonUniform, Scale = scale, Homogeneous = [],
        };

        PrcGraphTextureDefinition[] textures =
        [
            Definition(null),
            Definition(new PrcGraphTextureTransformation { InvertS = false, InvertT = false, Transform = Transform(1 | 8, translation: new PrcVector2D { X = 0.25, Y = 0.5 }, scale: 8) }),
            Definition(new PrcGraphTextureTransformation { InvertS = false, InvertT = false, Transform = Transform(16, nonUniform: new PrcVector2D { X = 4, Y = 2 }) }),
            Definition(new PrcGraphTextureTransformation { InvertS = false, InvertT = false, Transform = Transform(2, rotation: new PrcVector2D { X = 0, Y = 1 }) }),
            Definition(new PrcGraphTextureTransformation { InvertS = false, InvertT = true, Transform = null }),
        ];

        using Scene scene = Build(File(Structure(
            [Occurrence("o", part: 1)],
            [Part("p", Solid("none", 1, style: 1), Solid("uniform", 1, style: 2), Solid("separate", 1, style: 3), Solid("turned", 1, style: 4), Solid("mirrored", 1, style: 5))],
            styles: [MaterialStyle(1), MaterialStyle(2), MaterialStyle(3), MaterialStyle(4), MaterialStyle(5)], materials: materials, pictures: pictures, textures: textures, files: [png])));

        // A coordinate (u, v) goes to (u, v) × the matrix of the scene, which is the format's transposed.
        Matrix4x4 Layer(string name) => scene.TextureLayers[MaterialOf(scene, name).FirstLayer].Transform;
        Assert.Equal(Matrix4x4.Identity, Layer("none"));
        Assert.Equal(new Vector2(0.25f + 8, 0.5f + 16), Vector2.Transform(new Vector2(1, 2), Layer("uniform")));
        Assert.Equal(new Vector2(4, 4), Vector2.Transform(new Vector2(1, 2), Layer("separate")));
        Assert.Equal(new Vector2(-2, 1), Vector2.Transform(new Vector2(1, 2), Layer("turned")));
        Assert.Equal(new Vector2(1, -1), Vector2.Transform(new Vector2(1, 2), Layer("mirrored")));
    }

    /// <summary>
    /// How a texture definition sets its picture on the surface: repeating or clamped (the format's own numbers
    /// count from 0: unknown, repeat, clamp to border, clamp, clamp to edge, mirrored repeat); the mapping and
    /// the function it asks for are said when they are not what the reader does.
    /// </summary>
    [Fact]
    public void ATextureDefinitionSaysHowItIsRepeatedMappedAndCombined()
    {
        PrcMaterial[] materials = Enumerable.Range(1, 8)
            .Select(i => new PrcMaterial { Id = new PrcGraphTextureApplication { BiasedMaterialGenericIndex = 0, BiasedTextureDefinitionIndex = (uint)i, BiasedNextTextureIndex = 0, BiasedUvCoordinatesIndex = 1 }, IdType = PrcType.PRC_TYPE_GRAPH_TextureApplication })
            .ToArray();
        byte[] png = PngHeader(8, 8, colourType: 6);
        PrcGraphPicture[] pictures = [new() { Format = 0, BiasedUncompressedFileIndex = 1, PixelWidth = 0, PixelHeight = 0 }];

        static PrcGraphTextureDefinition Definition(int s = 1, int t = 1, int mapping = 1, int op = 0, int function = 2) => new()
        {
            BiasedPictureIndex = 1, TextureApplicationMode = 0, AlphaTestReference = 0, TextureWrappingModeS = s, TextureWrappingModeT = t,
            TextureTransformation = null, TextureFunction = function, TextureMappingType = mapping, TextureMappingOperator = op,
        };

        PrcGraphTextureDefinition[] textures =
        [
            Definition(),                                  // repeat both ways, the stored coordinates, modulate
            Definition(s: 4, t: 3),                        // clamped both ways
            Definition(s: 1, t: 2),                        // repeat along S, clamped (to the border) along T
            Definition(s: 0, t: 0, function: 0),           // unknown: the application chooses
            Definition(mapping: 3, op: 1, function: 1),    // the operator mapping every sample file has: its coordinates are stored
            Definition(mapping: 3, op: 4),                 // a cubic projection: not done
            Definition(mapping: 2),                        // coordinates taken from the surface: not done
            Definition(function: 3),                       // blend: not done
        ];

        using Scene scene = Build(File(Structure(
            [Occurrence("o", part: 1)],
            [Part("p", Solid("repeat", 1, style: 1), Solid("clamp", 1, style: 2), Solid("mixed", 1, style: 3), Solid("unknown", 1, style: 4), Solid("operator", 1, style: 5), Solid("cubic", 1, style: 6), Solid("surface", 1, style: 7), Solid("blend", 1, style: 8))],
            styles: Enumerable.Range(1, 8).Select(i => MaterialStyle((uint)i)).ToArray(), materials: materials, pictures: pictures, textures: textures, files: [png])));

        byte Repeat(string name) => scene.TextureLayers[MaterialOf(scene, name).FirstLayer].Repeat;
        Assert.Equal((byte)3, Repeat("repeat"));
        Assert.Equal((byte)0, Repeat("clamp"));
        Assert.Equal((byte)1, Repeat("mixed"));
        Assert.Equal((byte)3, Repeat("unknown"));
        Assert.Equal((byte)3, Repeat("operator"));

        // The first five ask for nothing the reader does not do; of the next three, the two projections are one
        // thing said and the blend another (a diagnostic is reported once).
        SceneDiagnostic[] said = scene.Diagnostics.ToArray().Where(d => d.Code == "prc-texture-mapping-not-applied" || d.Code == "prc-texture-function-not-applied").ToArray();
        Assert.Single(said, d => d.Code == "prc-texture-mapping-not-applied");
        Assert.Single(said, d => d.Code == "prc-texture-function-not-applied");
        Assert.Equal(2, said.Length);
        Assert.All(said, d => Assert.Equal(SceneDiagnosticSeverity.Info, d.Severity));
    }

    internal static byte[] PngHeader(int width, int height, byte colourType)
    {
        byte[] file = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 0, 0, 0, 0, 0, 8, colourType, 0, 0, 0, 1, 2, 3, 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(20), height);
        return file;
    }

    /// <summary>
    /// Placements: a translation, axes (the third is their cross product, turned for a mirror) and
    /// scales, or the sixteen numbers of a matrix stored column by column.
    /// </summary>
    [Fact]
    public void PlacementsBecomeTheScenesMatrices()
    {
        // The x axis turned to +y and the y axis to -x (a quarter turn about z), doubled, moved to (10, 20, 30).
        var turn = new PrcMiscCartesianTransformation
        {
            Transform = new PrcTransformation3D
            {
                Behavior = 0x01 | 0x02 | 0x08,
                Translation = new PrcVector3D { X = 10, Y = 20, Z = 30 },
                NonOrthoMatrix = new PrcVector3D[3],
                Rotation = [new PrcVector3D { X = 0, Y = 1, Z = 0 }, new PrcVector3D { X = -1, Y = 0, Z = 0 }],
                NonUniformScale = default,
                Scale = 2,
                Homogeneous = new double[4],
            },
        };
        var mirrored = new PrcMiscCartesianTransformation
        {
            Transform = new PrcTransformation3D
            {
                Behavior = 0x02 | 0x04 | 0x10,
                Translation = default,
                NonOrthoMatrix = new PrcVector3D[3],
                Rotation = [new PrcVector3D { X = 1, Y = 0, Z = 0 }, new PrcVector3D { X = 0, Y = 1, Z = 0 }],
                NonUniformScale = new PrcVector3D { X = 2, Y = 3, Z = 4 },
                Scale = 0,
                Homogeneous = new double[4],
            },
        };

        // The same quarter turn and move as a matrix: columns (0,1,0,0), (-1,0,0,0), (0,0,1,0), (10,20,30,1).
        var matrix = new PrcMiscGeneralTransformation { GeneralTransform = [0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 10, 20, 30, 1] };
        var system = new PrcRiCoordinateSystem { Transform = new PrcMiscCartesianTransformation { Transform = Translation(0, 0, 7) }, TransformType = PrcType.PRC_TYPE_MISC_CartesianTransformation };

        using Scene scene = Build(File(Structure(
            [Occurrence("turned", location: turn), Occurrence("mirrored", location: mirrored), Occurrence("matrix", location: matrix), Occurrence("item", part: 1), Occurrence("root", sons: [0, 1, 2, 3])],
            [Part("p", Solid("placed", 1, coordinateSystem: 1), Solid("lost", 1, coordinateSystem: 5))], coordinateSystems: [system])));

        Vector3 At(string name, Vector3 point)
        {
            for (int i = 0; i < scene.Nodes.Length; i++)
            {
                if (Plain(scene.GetName(scene.Nodes[i].Name)) == name)
                {
                    return Vector3.Transform(point, scene.GetWorldMatrix(i));
                }
            }

            throw new Xunit.Sdk.XunitException(name);
        }

        Assert.Equal(new Vector3(10, 22, 30), At("turned", Vector3.UnitX));
        Assert.Equal(new Vector3(8, 20, 30), At("turned", Vector3.UnitY));
        Assert.Equal(new Vector3(10, 20, 32), At("turned", Vector3.UnitZ));
        Assert.Equal(new Vector3(2, 3, -4), At("mirrored", Vector3.One));
        Assert.Equal(new Vector3(10, 21, 30), At("matrix", Vector3.UnitX));
        Assert.Equal(new Vector3(9, 20, 30), At("matrix", Vector3.UnitY));
        Assert.Equal(new Vector3(0, 0, 7), OriginOf(scene, "placed"));
        Assert.Equal(Vector3.Zero, OriginOf(scene, "lost"));
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-missing-coordinate-system");
    }

    [Fact]
    public void APlacementThatIsNotNumbersIsIgnoredAndSaid()
    {
        var broken = new PrcMiscGeneralTransformation { GeneralTransform = [1, 0, 0, 0, 0, double.NaN, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1] };
        using Scene scene = Build(File(Structure([Occurrence("o", part: 1, location: broken)], [Part("p", Solid("body", 1))])));

        Assert.Equal(Matrix4x4.Identity, scene.Nodes[0].Local);
        Assert.Contains(scene.Diagnostics, d => d.Code == "prc-non-finite-transform");
    }
}
