using System.Numerics;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Model;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Prc;

/// <summary>
/// Builds the scene of a PRC file from what was kept of it.
/// <para>
/// The assembly is a tree of product occurrences that starts in the model file. An occurrence places
/// (location) what it refers to: its own sons, its own part, or those of its prototype, an occurrence
/// that serves as a template, possibly in another file structure. Every way from the root to an
/// occurrence becomes a node; a part's tessellation becomes one mesh, shown by a node wherever the part
/// is used (ISO 14739-1, 5.1 and PRC_TYPE_ASM_ProductOccurrence).
/// </para>
/// <para>
/// Appearance is inherited down that tree, entity by entity, to the faces: each may name a style, and
/// behaviour bits say whether a father's or a son's wins (PRC_TYPE_ROOT_PRCBaseWithGraphics).
/// </para>
/// </summary>
internal sealed class PrcSceneBuilder
{
    // Behaviour bits of an entity's graphics.
    private const int Show = 0x0001, SonHeritShow = 0x0002, FatherHeritShow = 0x0004, SonHeritColor = 0x0008, FatherHeritColor = 0x0010,
        SonHeritTransparency = 0x0080, FatherHeritTransparency = 0x0100, Removed = 0x2000;

    // PRC_PRODUCT_BEHAVIOUR and PRCProductFlag.
    private const int Suppressed = 0x01;
    private const int FlagDefault = 0x01, FlagConfiguration = 0x08, FlagView = 0x10;

    /// <summary>Most occurrences a prototype may lead through before it names its part or its sons.</summary>
    private const int MaxReferenceChain = 64;

    private readonly PrcFile _file;
    private readonly SceneBuilder _builder;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<MeshKey, PrcMesh> _meshes = [];
    private readonly Dictionary<(int Structure, int Context, int Body), PrcMesh> _brepMeshes = [];
    private readonly Dictionary<PrcTopoContext, PrcBrepDataConverter.Tables?> _brepTables = new(ReferenceEqualityComparer.Instance);
    private int _brepFaces, _brepDropped, _brepUnsupported;
    private bool _brepCapped;
    private readonly Dictionary<(int Structure, int Tessellation), PrcFaceGraphics[]> _slots = [];
    private readonly HashSet<(int Structure, int Tessellation)> _undecoded = [];
    private bool?[]? _facesSay;
    private double? _unit;
    private readonly Dictionary<MaterialKey, int> _materials = [];
    private readonly Dictionary<(int Structure, int Picture), int> _textures = [];
    private readonly int _emptyName;
    private int _markupMaterial = -1;

    // What the occurrences on the way down say of entities under them, and the bodies they say something of single faces of (how many faces).
    private readonly Dictionary<Target, EntityOverride> _overrides = [];
    private readonly Dictionary<(int Structure, uint Context, uint Body), int> _faceOverrideBodies = [];

    private PrcSceneBuilder(PrcFile file, SceneBuilder builder, CancellationToken cancellation)
    {
        _file = file;
        _builder = builder;
        _cancellation = cancellation;
        _emptyName = builder.AddName("");
    }

    /// <summary>What decides the look of triangles, as it stands at one entity of the tree.</summary>
    private struct Appearance
    {
        // The style that gives the colour or material, and the file structure whose globals it indexes.
        public int ColorStructure;
        public uint ColorStyle;
        public bool ColorForced;

        // The style that gives the transparency.
        public int TransparencyStructure;
        public uint TransparencyStyle;
        public bool TransparencyForced;

        public bool Shown;
        public bool ShowForced;

        // Under a suppressed occurrence, or a configuration that is not the default one: there to be
        // named by a view, not drawn otherwise.
        public bool Withheld;
    }

    private readonly record struct MaterialKey(int ColorStructure, uint ColorStyle, int TransparencyStructure, uint TransparencyStyle, bool VertexColors);

    private readonly record struct OccurrenceId(int Structure, int Index);

    /// <summary>The graphics an occurrence gives an entity under it: a style of the occurrence's file structure, and behaviour bits.</summary>
    /// <remarks><paramref name="Placement"/>: a coordinate system of that file structure, as its index plus one (0 for none), that places an item in its part.</remarks>
    private readonly record struct EntityOverride(int StyleStructure, uint Style, int Behavior, uint Placement = 0);

    /// <summary>
    /// An entity an occurrence says something of, in file structure <paramref name="Structure"/>: a representation item, by its type
    /// and unique identifier (<paramref name="Body"/> and <paramref name="Face"/> 0), or a face (<c>PRC_TYPE_TOPO_Face</c>), by the
    /// topological context (<paramref name="Id"/>) and body as an item names them (from 1), and its number in the body plus one.
    /// </summary>
    private readonly record struct Target(int Structure, uint Type, uint Id, uint Body, uint Face)
    {
        public static Target Item(int structure, uint type, uint uniqueId) => new(structure, type, uniqueId, 0, 0);

        public static Target FaceOf(int structure, uint context, uint body, uint face) => new(structure, PrcType.PRC_TYPE_TOPO_Face, context, body, face + 1);

        public bool IsFace => Face != 0;
    }

    /// <summary>
    /// A tessellation as one of the meshes made of it: <paramref name="Hidden"/> says which of its material slots are left out ("" for none),
    /// <paramref name="Faces"/> which faces have slots of their own for what an occurrence says of them ("" for none).
    /// </summary>
    private readonly record struct MeshKey(int Structure, int Tessellation, string Hidden, string Faces);

    public static void Build(PrcFile file, SceneBuilder builder, CancellationToken cancellation)
    {
        new PrcSceneBuilder(file, builder, cancellation).Build();
    }

    private void Build()
    {
        // Material 0 is what anything without a style of its own shows: a neutral grey.
        _builder.AddMaterial(DefaultMaterial());

        // One unit of length for the whole model: the model file's when it is reliable, else that of
        // the first product occurrence, on the way down, that has a reliable one (ISO 14739-1, 5.6).
        if (_file.ModelFile.UnitsFromCadFile && IsUnit(_file.ModelFile.UnitsInMm))
        {
            _unit = _file.ModelFile.UnitsInMm;
        }

        PrcProductOccurrenceReference[] roots = _file.ModelFile.ProductOccurrences;
        bool anyActive = roots.Any(r => r.ProductOccurrenceIsActive);
        var start = new Appearance { Shown = true };
        foreach (PrcProductOccurrenceReference root in roots)
        {
            if (anyActive && !root.ProductOccurrenceIsActive)
            {
                Note("prc-inactive-root", "A root of the PRC model that is not active is not shown.");
                continue;
            }

            int structure = FindStructure(root.UniqueId);
            if (structure < 0 || root.RootIndex == 0)
            {
                throw Invalid("The PRC model file names a root that is not in the file.");
            }

            AddOccurrence(new OccurrenceId(structure, (int)(root.RootIndex - 1)), -1, start, 0);
        }

        ReportBrepTotals();
        if (_unit is null)
        {
            Note("prc-unit-not-reliable", "The PRC model does not say reliably what its unit of length is; lengths taken from it may be wrong.");
            _unit = IsUnit(_file.ModelFile.UnitsInMm) ? _file.ModelFile.UnitsInMm : 1000;
        }

        _builder.UnitScale = _unit.Value / 1000;
    }

    private static bool IsUnit(double millimetres) => double.IsFinite(millimetres) && millimetres > 0;

    private int FindStructure(PrcUniqueId id)
    {
        PrcFileStructureData[] structures = _file.FileStructures;
        for (int i = 0; i < structures.Length; i++)
        {
            PrcUniqueId other = structures[i].Id;
            if (other.UniqueId0 == id.UniqueId0 && other.UniqueId1 == id.UniqueId1 && other.UniqueId2 == id.UniqueId2 && other.UniqueId3 == id.UniqueId3)
            {
                return i;
            }
        }

        return -1;
    }

    private PrcAsmProductOccurrence Occurrence(OccurrenceId id)
    {
        PrcAsmProductOccurrence[] products = _file.FileStructures[id.Structure].Tree.Products;
        if ((uint)id.Index >= (uint)products.Length)
        {
            throw Invalid($"A PRC product occurrence refers to occurrence {id.Index} of a file structure that has {products.Length}.");
        }

        return products[id.Index];
    }

    private void AddOccurrence(OccurrenceId id, int parent, Appearance appearance, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (depth > _builder.Limits.MaxNestingDepth)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The PRC assembly is more than {_builder.Limits.MaxNestingDepth} levels deep.");
        }

        PrcAsmProductOccurrence occurrence = Occurrence(id);
        PrcGraphicsContent graphics = occurrence.Base.GraphicsContent;
        if ((graphics.BehaviorBitField & Removed) != 0)
        {
            return;
        }

        if (((byte)occurrence.ProductBehavior & Suppressed) != 0 && !appearance.Withheld)
        {
            Note("prc-suppressed-occurrence", "A suppressed part of the PRC assembly is in the scene, hidden.");
            appearance.Withheld = true;
        }

        Apply(ref appearance, id.Structure, graphics.BiasedIndexOfLineStyle, graphics.BehaviorBitField, show: true);
        List<Target>? overridden = AddOverrides(occurrence, id.Structure, null);

        // What the occurrence shows: its own part and sons, else those of its prototype (or of its
        // external data), through as many prototypes as it takes. The occurrences on that way add
        // their graphics as the sons they stand for, and the first that has a name or a location lends
        // it to an occurrence that has none: the copies a writer makes of a subassembly's sons carry
        // neither, their prototypes do.
        string name = occurrence.Base.Base.Base.EntityName.Name;
        object? location = occurrence.Location;
        OccurrenceId? partOwner = null;
        OccurrenceId? sonsOwner = null;
        OccurrenceId current = id;
        for (int step = 0; ; step++)
        {
            PrcAsmProductOccurrence o = Occurrence(current);
            PrcReferencesOfProductOccurrence references = o.ReferencesProductOccurrence;

            // The unit of an occurrence is no scale: where a part in inches sits in an assembly in
            // millimetres, its placement has the factor.
            if (_unit is null && o.ProductInformation.UnitFromCadFile && IsUnit(o.ProductInformation.Unit))
            {
                _unit = o.ProductInformation.Unit;
            }

            if (step > 0)
            {
                PrcGraphicsContent g = o.Base.GraphicsContent;
                if ((g.BehaviorBitField & Removed) != 0)
                {
                    break;
                }

                Apply(ref appearance, current.Structure, g.BiasedIndexOfLineStyle, g.BehaviorBitField, show: true);
                overridden = AddOverrides(o, current.Structure, overridden);
                if (name.Length == 0)
                {
                    name = o.Base.Base.Base.EntityName.Name;
                }

                location ??= o.Location;
            }

            if (partOwner is null && references.BiasedIndexPart != 0)
            {
                partOwner = current;
            }

            if (sonsOwner is null && references.IndexChildOccurrence.Length != 0)
            {
                sonsOwner = current;
            }

            if (partOwner is not null && sonsOwner is not null && location is not null && name.Length != 0)
            {
                break;
            }

            OccurrenceId next;
            if (references.BiasedIndexPrototype != 0)
            {
                if (!TryFollow(current, references.BiasedIndexPrototype, references.PrototypeInSameFileStructure, out next))
                {
                    break;
                }
            }
            else if (references.BiasedIndexExternalData != 0)
            {
                if (!TryFollow(current, references.BiasedIndexExternalData, references.ExternalDataInSameFileStructure, out next))
                {
                    break;
                }
            }
            else
            {
                break;
            }

            if (step >= MaxReferenceChain)
            {
                throw Invalid("The prototypes of a PRC product occurrence refer to each other without end.");
            }

            current = next;
        }

        if (!PrcTransforms.TryGetMatrix(location, out Matrix4x4 local))
        {
            Warn("prc-non-finite-transform", "A placement in the PRC assembly has values that are not numbers; it was ignored.");
        }

        // The node is called as PDF viewers call it (see PrcNodeNames): an occurrence that has no
        // name, nor a prototype with one, is "node", and its reference is then given twice.
        string reference = PrcNodeNames.Reference(_file.FileStructures[id.Structure].Id, PrcType.PRC_TYPE_ASM_ProductOccurrence, occurrence.Base.Base.UniqueId);
        int node = _builder.AddNode(new SceneNode
        {
            Parent = parent,
            Local = local,
            Kind = SceneNodeKind.Group,
            Mesh = -1,
            Light = -1,
            Camera = -1,
            Name = _builder.AddName(name.Length == 0 ? PrcNodeNames.Join(PrcNodeNames.UnnamedOccurrence, reference + reference) : PrcNodeNames.Join(name, reference)),
            Visible = true,
        });

        if (partOwner is { } owner)
        {
            PrcFileStructureData structure = _file.FileStructures[owner.Structure];
            uint partIndex = Occurrence(owner).ReferencesProductOccurrence.BiasedIndexPart - 1;
            if (partIndex >= (uint)structure.Tree.Parts.Length)
            {
                throw Invalid($"A PRC product occurrence refers to part {partIndex} of a file structure that has {structure.Tree.Parts.Length}.");
            }

            PrcAsmPartDefinition part = structure.Tree.Parts[partIndex];
            PrcGraphicsContent g = part.Base.GraphicsContent;
            if ((g.BehaviorBitField & Removed) == 0)
            {
                Appearance ofPart = appearance;
                Apply(ref ofPart, owner.Structure, g.BiasedIndexOfLineStyle, g.BehaviorBitField, show: true);
                AddItems(part.RepresentationItems, owner.Structure, node, ofPart, depth + 1, reference);

                // A part's own notes and dimensions show wherever it is used, named with the occurrence as its items are (the
                // Tetra4D LandingGear's are all its part's).
                AddMarkups(part.Markups.Markups, owner.Structure, node, reference);
            }
        }

        AddMarkups(occurrence.Markups.Markups, id.Structure, node, reference);

        if (sonsOwner is { } sons)
        {
            uint[] children = Occurrence(sons).ReferencesProductOccurrence.IndexChildOccurrence;

            // Configurations are alternatives: of several, the default one is shown. The others, and
            // occurrences that are views of the product, are in the scene but hidden: a view of the PDF
            // may name them.
            int chosen = -1;
            int configurations = 0;
            for (int i = 0; i < children.Length; i++)
            {
                int flags = (byte)Occurrence(new OccurrenceId(sons.Structure, (int)children[i])).ProductInformation.ProductInformationFlags;
                if ((flags & FlagConfiguration) != 0)
                {
                    configurations++;
                    if (chosen < 0 || ((flags & FlagDefault) != 0 && !IsDefault(children[chosen])))
                    {
                        chosen = i;
                    }
                }
            }

            for (int i = 0; i < children.Length; i++)
            {
                var child = new OccurrenceId(sons.Structure, (int)children[i]);
                int flags = (byte)Occurrence(child).ProductInformation.ProductInformationFlags;
                Appearance ofChild = appearance;
                if (!appearance.Withheld && ((flags & FlagView) != 0 || ((flags & FlagConfiguration) != 0 && configurations > 1 && i != chosen)))
                {
                    Note("prc-alternative-hidden", "A configuration or view of the PRC assembly other than the default one is in the scene, hidden.");
                    ofChild.Withheld = true;
                }

                AddOccurrence(child, node, ofChild, depth + 1);
            }

            bool IsDefault(uint index) => ((byte)Occurrence(new OccurrenceId(sons.Structure, (int)index)).ProductInformation.ProductInformationFlags & FlagDefault) != 0;
        }

        // What it said of the entities under it holds below it only (an exception ends the whole build).
        foreach (Target key in overridden ?? [])
        {
            _overrides.Remove(key);
            if (key.IsFace)
            {
                var body = (key.Structure, key.Id, key.Body);
                int left = _faceOverrideBodies[body] - 1;
                if (left == 0)
                {
                    _faceOverrideBodies.Remove(body);
                }
                else
                {
                    _faceOverrideBodies[body] = left;
                }
            }
        }
    }

    /// <summary>
    /// Takes what an occurrence says of entities under it (its <c>entity_reference</c>: ISO 14739-1, "modifiers towards their nominal
    /// definition, which may include location, color, and visibility") for the occurrences and items below it. Assemblies colour the
    /// items of their parts that way, and cut them away (the Tetra4D Turbine: its blades, bearings and hoses are coloured, and half its
    /// cover and most of its housing removed, by the assembly only). It may also say something of single faces of the bodies of its
    /// parts (ISO 14739-1, 7.4.7: the body's topological context and number, and the face's number in the body), as nine samples of
    /// the corpus colour theirs. Of two occurrences that say something of the same entity the outer one wins: the assembly has the
    /// last word over its subassemblies. Returns the keys it added, for the caller to take back.
    /// </summary>
    private List<Target>? AddOverrides(PrcAsmProductOccurrence occurrence, int structure, List<Target>? added)
    {
        foreach (PrcMiscEntityReference reference in occurrence.EntityReference)
        {
            PrcContentEntityReference content = reference.ContentEntityReference;
            PrcGraphicsContent graphics = content.Base.GraphicsContent;
            bool says = graphics.BiasedIndexOfLineStyle != 0 || (graphics.BehaviorBitField & Removed) != 0;
            uint placement = 0;
            Target key;
            switch (content.ReferenceData?.Data)
            {
                case PrcMiscReferenceOnTopology { TypeOfEntity: PrcType.PRC_TYPE_TOPO_Face, Data: { Indices.Length: 1 } face }:
                    if (content.IndexOfLocalCoordinateSystem != 0)
                    {
                        // A face is drawn with its body: it cannot be moved apart from it.
                        Note("prc-topology-reference-not-applied", "What the PRC assembly says of edges, vertices, or faces it does not locate or moves, in its parts is not applied.");
                    }

                    if (!says)
                    {
                        continue;
                    }

                    key = Target.FaceOf(face.Flag ? structure : FindStructure(face.UniqueId), face.IndexOfTopologicalIndex, face.IndexOfBody, face.Indices[0]);
                    break;

                case PrcMiscReferenceOnTopology:
                    // Edges and vertices are there for markups to point at; a face without its body cannot be found.
                    Note("prc-topology-reference-not-applied", "What the PRC assembly says of edges, vertices, or faces it does not locate or moves, in its parts is not applied.");
                    continue;

                case PrcMiscReferenceOnPRCBase target:
                    // The coordinate system counts from 1, as an item's own does (a reference of the PROSTEP Camera sample names the
                    // last of its 239 systems by 239), though ISO 14739-1 7.4.10.1 says "-1 if none".
                    placement = content.IndexOfLocalCoordinateSystem;
                    if (!says && placement == 0)
                    {
                        continue;
                    }

                    key = Target.Item(target.Flag ? structure : FindStructure(target.DifferentUniqueId), target.TypeOfEntity, target.UniqueId);
                    break;

                default:
                    continue;
            }

            if (key.Structure >= 0 && _overrides.TryAdd(key, new EntityOverride(structure, graphics.BiasedIndexOfLineStyle, graphics.BehaviorBitField, placement)))
            {
                added ??= [];
                added.Add(key);
                if (key.IsFace)
                {
                    var body = (key.Structure, key.Id, key.Body);
                    _faceOverrideBodies[body] = _faceOverrideBodies.GetValueOrDefault(body) + 1;
                }
            }
        }

        return added;
    }

    /// <summary>
    /// The markups of an occurrence (notes, dimensions): each becomes nodes, one for its lines and one for its
    /// triangles, that carry the name a view of a PDF gives the markup. They are hidden: a view that names the
    /// markup shows them, as the notes of one view of a model are not those of another.
    /// </summary>
    private void AddMarkups(PrcMkpMarkup[] markups, int structure, int parent, string occurrence)
    {
        if (markups.Length == 0)
        {
            return;
        }

        PrcFileStructureData data = _file.FileStructures[structure];
        foreach (PrcMkpMarkup markup in markups)
        {
            _cancellation.ThrowIfCancellationRequested();
            if ((markup.Base.GraphicsContent.BehaviorBitField & Removed) != 0 || markup.BiasedIndexTessellation == 0)
            {
                continue;
            }

            PrcTess[] tessellations = data.Tessellation.Tess;
            uint index = markup.BiasedIndexTessellation - 1;
            if (index >= (uint)tessellations.Length || tessellations[index].Id is not PrcTessMarkup drawing)
            {
                Warn("prc-missing-tessellation", "A markup of the PRC model names a tessellation that is not there, or is not a markup's; it is not shown.");
                continue;
            }

            string name = PrcNodeNames.Join(markup.Base.Base.Base.EntityName.Name, PrcNodeNames.Reference(data.Id, PrcType.PRC_TYPE_MKP_Markup, markup.Base.Base.UniqueId) + occurrence);
            PrcMarkupMeshes meshes = PrcMarkupMesher.Build(_builder, drawing, data.Globals.Colors, name, _cancellation);
            if (meshes.EntitiesNotShown)
            {
                Note("prc-markup-entity-not-shown", "Text, symbols and other entities of a markup of the PRC model that need a font, a picture or the screen are not shown.");
            }

            if (meshes.MissingColors)
            {
                Warn("prc-missing-colour", "A markup of the PRC model names a colour that does not exist; the default one is used.");
            }

            int binding = -1;
            foreach (int mesh in (ReadOnlySpan<int>)[meshes.Lines, meshes.Triangles])
            {
                if (mesh < 0)
                {
                    continue;
                }

                binding = binding < 0 ? _builder.AddBinding(MarkupMaterial()) : binding;
                _builder.AddNode(new SceneNode
                {
                    Parent = parent,
                    Local = Matrix4x4.Identity,
                    Kind = SceneNodeKind.Model,
                    Mesh = mesh,
                    Light = -1,
                    Camera = -1,
                    FirstBinding = binding,
                    BindingCount = 1,
                    Name = _builder.AddName(name),
                    Visible = false,
                });
            }
        }
    }

    /// <summary>What markups are drawn with: their own colours, unlit.</summary>
    private int MarkupMaterial()
    {
        if (_markupMaterial < 0)
        {
            _markupMaterial = _builder.AddMaterial(new SceneMaterial
            {
                Ambient = Vector3.One,
                Diffuse = Vector3.One,
                Opacity = 1,
                Flags = SceneMaterialFlags.UseVertexColor,
                Name = _emptyName,
            });
        }

        return _markupMaterial;
    }

    /// <summary>The occurrence a prototype or external-data reference names: in the same file structure, or in the one whose identifier it gives.</summary>
    private bool TryFollow(OccurrenceId from, uint biasedIndex, PrcFileIdentifier file, out OccurrenceId target)
    {
        int structure = file.Flag ? from.Structure : FindStructure(file.UniqueId);
        target = new OccurrenceId(structure, (int)(biasedIndex - 1));
        if (structure < 0)
        {
            Warn("prc-missing-file-structure", "A part of the PRC assembly is in a file structure that the file does not contain; it is missing.");
            return false;
        }

        if (target == from)
        {
            throw Invalid("A PRC product occurrence is its own prototype.");
        }

        return true;
    }

    /// <summary>The representation items of a part, or of a set of items: each that has a tessellation becomes a node showing its mesh.</summary>
    private void AddItems(PrcRiRepresentationItem[] items, int structure, int parent, Appearance appearance, int depth, string occurrence)
    {
        if (depth > _builder.Limits.MaxNestingDepth)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The PRC assembly is more than {_builder.Limits.MaxNestingDepth} levels deep.");
        }

        foreach (PrcRiRepresentationItem item in items)
        {
            _cancellation.ThrowIfCancellationRequested();
            switch (item.Data)
            {
                case PrcRiBrepModel solid:
                    AddItem(solid.ItemContent, item.DataType, null, structure, parent, appearance, depth, occurrence, solid);
                    break;
                case PrcRiPolyBrepModel mesh:
                    AddItem(mesh.ItemContent, item.DataType, null, structure, parent, appearance, depth, occurrence);
                    break;
                case PrcRiSet set:
                    AddItem(set.ItemContent, item.DataType, set.RepresentationItems, structure, parent, appearance, depth, occurrence);
                    break;
                default:
                    // Curves, wires, points, planes, coordinate systems: construction geometry, not drawn.
                    Note("prc-item-not-shown", "Curves, points and other items of the PRC model that are not surfaces are not shown.");
                    break;
            }
        }
    }

    private void AddItem(PrcRepresentationItemContent content, uint type, PrcRiRepresentationItem[]? members, int structure, int parent, Appearance appearance, int depth, string occurrence, PrcRiBrepModel? solid = null)
    {
        PrcGraphicsContent graphics = content.Base.GraphicsContent;
        if ((graphics.BehaviorBitField & Removed) != 0)
        {
            return;
        }

        Apply(ref appearance, structure, graphics.BiasedIndexOfLineStyle, graphics.BehaviorBitField, show: true);
        Matrix4x4 local;
        if (_overrides.Count != 0 && _overrides.TryGetValue(Target.Item(structure, type, content.Base.Base.UniqueId), out EntityOverride over))
        {
            if ((over.Behavior & Removed) != 0)
            {
                return;
            }

            // An occurrence's style is applied over the item's own, and then holds for the item's faces and members: the assembly's
            // appearance of a part overrides the part's (the Turbine's parts give every face their own grey). Its coordinate system,
            // one of the occurrence's file structure, places the item in its part instead of the item's own.
            ApplyOverride(ref appearance, over);
            local = over.Placement != 0 ? Placement(over.StyleStructure, over.Placement) : Placement(structure, content.BiasedIndexLocalCoordinateSystem);
        }
        else
        {
            local = Placement(structure, content.BiasedIndexLocalCoordinateSystem);
        }

        PrcFileStructureData data = _file.FileStructures[structure];

        var node = new SceneNode
        {
            Parent = parent,
            Local = local,
            Kind = members is null ? SceneNodeKind.Model : SceneNodeKind.Group,
            Mesh = -1,
            Light = -1,
            Camera = -1,
            Name = _builder.AddName(PrcNodeNames.Join(content.Base.Base.Base.EntityName.Name, PrcNodeNames.Reference(data.Id, type, content.Base.Base.UniqueId) + occurrence)),
            Visible = members is not null || (appearance.Shown && !appearance.Withheld),
        };

        if (members is not null)
        {
            AddItems(members, structure, _builder.AddNode(node), appearance, depth + 1, occurrence);
            return;
        }

        if (content.BiasedIndexTessellation == 0)
        {
            if (solid is { ExactGeometry: true })
            {
                if (_faceOverrideBodies.ContainsKey((structure, solid.IndexTopologicalContext, solid.IndexBody)))
                {
                    Note("prc-face-reference-not-applied", "What the PRC assembly says of single faces of solids drawn from their exact geometry is not applied.");
                }

                AddBrep(solid, node, data, structure, appearance);
                return;
            }

            Note("prc-exact-geometry-only", "A part of the PRC model has exact geometry only, without a mesh to show.");
            _builder.AddNode(node);
            return;
        }

        int tessellationIndex = (int)(content.BiasedIndexTessellation - 1);
        PrcTess[] tessellations = data.Tessellation.Tess;
        if ((uint)tessellationIndex >= (uint)tessellations.Length)
        {
            throw Invalid($"An item of the PRC model refers to tessellation {tessellationIndex} of a file structure that has {tessellations.Length}.");
        }

        PrcTess tessellation = tessellations[tessellationIndex];
        PrcTess3D? plain = tessellation.Id as PrcTess3D;
        PrcTess3DCompressed? compressed = tessellation.Id as PrcTess3DCompressed;
        if (plain is null && compressed is null)
        {
            Note("prc-item-not-shown", "Curves, points and other items of the PRC model that are not surfaces are not shown.");
            _builder.AddNode(node);
            return;
        }

        if (_undecoded.Contains((structure, tessellationIndex)))
        {
            // Said once already, for another item that shows the same tessellation.
            _builder.AddNode(node);
            return;
        }

        // The faces that have a style are the youngest sons of this item, one material slot each. A
        // face may say that it is not shown: it is then left out of the mesh this item draws, which
        // is the tessellation's own mesh unless a face is left out. The faces an occurrence says
        // something of have slots of their own: a face's number in its body is its number in the
        // tessellation of the body's item (the faces of a tessellation are those of its body in the
        // order of the body's shells, and the faces the corpus's references colour together touch
        // each other more often read so than by the order the body stores them in).
        List<EntityOverride>? said = null;
        int[]? faceOverrides = solid is not null && _faceOverrideBodies.Count != 0
            ? FaceOverrides(structure, solid, plain?.FaceTessellationData.Length ?? compressed!.FaceCount, out said)
            : null;
        PrcFaceGraphics[]? slots;
        if (faceOverrides is not null)
        {
            slots = plain is not null ? PrcMesher.Slots(plain, faceOverrides) : PrcCompressedMesher.Slots(compressed!, faceOverrides);
        }
        else if (!_slots.TryGetValue((structure, tessellationIndex), out slots))
        {
            slots = plain is not null ? PrcMesher.Slots(plain) : PrcCompressedMesher.Slots(compressed!);
            _slots.Add((structure, tessellationIndex), slots);
        }

        bool facesSay = FacesSayWhetherTheyAreShown(structure, compressed is not null);
        var ofFaces = new Appearance[slots.Length];
        bool[]? hidden = null;
        for (int i = 0; i < slots.Length; i++)
        {
            // A face that names no style says nothing of itself (it would have no slot of its own): only the occurrence's word counts.
            ofFaces[i] = appearance;
            if (slots[i].Style != 0)
            {
                Apply(ref ofFaces[i], structure, slots[i].Style, (int)slots[i].Behavior, show: facesSay);
            }

            bool removed = false;
            if (slots[i].Override != 0)
            {
                EntityOverride ofFace = said![slots[i].Override - 1];
                removed = (ofFace.Behavior & Removed) != 0;
                ApplyOverride(ref ofFaces[i], ofFace);
            }

            if (removed || (!ofFaces[i].Shown && appearance.Shown))
            {
                hidden ??= new bool[slots.Length];
                hidden[i] = true;
            }
        }

        var key = new MeshKey(structure, tessellationIndex, hidden is null ? "" : string.Concat(hidden.Select(h => h ? '1' : '0')), Signature(faceOverrides));
        if (!_meshes.TryGetValue(key, out PrcMesh mesh))
        {
            if (plain is not null)
            {
                mesh = PrcMesher.AddMesh(_builder, plain, node.Name, _cancellation, hidden, faceOverrides);
            }
            else if (!PrcCompressedMesher.TryAddMesh(_builder, compressed!, node.Name, out mesh, out _, _cancellation, hidden, faceOverrides))
            {
                // A model is shown whole or said to be incomplete: the reader's caller decides.
                _undecoded.Add((structure, tessellationIndex));
                _builder.Report(SceneDiagnosticSeverity.Warning, PrcReader.CompressedTessellationDiagnostic, "A part of the PRC model is a compressed mesh that could not be decoded; it is missing.");
                _builder.AddNode(node);
                return;
            }

            _meshes.Add(key, mesh);
        }

        if (mesh.Mesh < 0)
        {
            // None of its faces is shown.
            node.Visible = false;
            _builder.AddNode(node);
            return;
        }

        // Slot 0 is for the faces without a style of their own.
        node.Mesh = mesh.Mesh;
        node.BindingCount = slots.Length + 1;
        node.FirstBinding = _builder.AddBinding(Material(appearance, mesh.HasColors));
        foreach (Appearance ofFace in ofFaces)
        {
            _builder.AddBinding(Material(ofFace, mesh.HasColors));
        }

        _builder.AddNode(node);
    }

    /// <summary>The placement a coordinate system of a file structure gives (its index plus one; 0 for none): identity when there is none.</summary>
    private Matrix4x4 Placement(int structure, uint biasedIndex)
    {
        Matrix4x4 local = Matrix4x4.Identity;
        if (biasedIndex != 0)
        {
            PrcRiCoordinateSystem[] systems = _file.FileStructures[structure].Globals.RefCoords;
            if (biasedIndex - 1 >= (uint)systems.Length)
            {
                Warn("prc-missing-coordinate-system", "An item of the PRC model names a coordinate system that does not exist; it is placed without one.");
            }
            else if (!PrcTransforms.TryGetMatrix(systems[biasedIndex - 1].Transform, out local))
            {
                Warn("prc-non-finite-transform", "A placement in the PRC assembly has values that are not numbers; it was ignored.");
                local = Matrix4x4.Identity;
            }
        }

        return local;
    }

    /// <summary>
    /// What the occurrences above an item say of the faces of its body, by the face's number in the item's tessellation of
    /// <paramref name="faceCount"/> faces: each different thing said once in <paramref name="said"/>, and per face its number there
    /// plus one (0 for nothing). Null when nothing is said of them.
    /// </summary>
    private int[]? FaceOverrides(int structure, PrcRiBrepModel solid, int faceCount, out List<EntityOverride>? said)
    {
        said = null;
        if (!_faceOverrideBodies.TryGetValue((structure, solid.IndexTopologicalContext, solid.IndexBody), out int left))
        {
            return null;
        }

        int[]? faces = null;
        for (int f = 0; f < faceCount && left > 0; f++)
        {
            if (_overrides.TryGetValue(Target.FaceOf(structure, solid.IndexTopologicalContext, solid.IndexBody, (uint)f), out EntityOverride over))
            {
                said ??= [];
                int number = said.IndexOf(over);
                if (number < 0)
                {
                    said.Add(over);
                    number = said.Count - 1;
                }

                faces ??= new int[faceCount];
                faces[f] = number + 1;
                left--;
            }
        }

        return faces;
    }

    /// <summary>Which faces have which of the things said of them, for a mesh key: the faces nothing is said of are left out.</summary>
    private static string Signature(int[]? faceOverrides)
    {
        if (faceOverrides is null)
        {
            return "";
        }

        var text = new System.Text.StringBuilder();
        for (int f = 0; f < faceOverrides.Length; f++)
        {
            if (faceOverrides[f] != 0)
            {
                text.Append(f).Append('=').Append(faceOverrides[f]).Append(',');
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// A solid that has no tessellation: drawn from the exact geometry of its body, compressed or not (an uncompressed one is turned
    /// into the model of a compressed one first). What cannot be shown is said once per kind, and the scene is then incomplete.
    /// </summary>
    private void AddBrep(PrcRiBrepModel solid, SceneNode node, PrcFileStructureData data, int structure, Appearance appearance)
    {
        // The indices count from 1 in real files (the LandingGear sample: context 1, body 1 of one and one), though issue #532 says zero-based.
        PrcTopoContext[]? contexts = data.ExactGeometry?.TopoContexts;
        uint contextIndex = solid.IndexTopologicalContext - 1, bodyIndex = solid.IndexBody - 1;
        if (contexts is null || contextIndex >= (uint)contexts.Length || bodyIndex >= (uint)contexts[contextIndex].Bodies.Length)
        {
            // The section could not be read, or the item names a body it does not have.
            Warn(PrcReader.BrepDiagnostic, "A solid of the PRC model has exact geometry that could not be read; it is missing.");
            _builder.AddNode(node);
            return;
        }

        PrcTopoBody body = contexts[contextIndex].Bodies[bodyIndex];
        if (body.Id is not (PrcTopoBrepDataCompress or PrcTopoBrepData))
        {
            Note("prc-item-not-shown", "Curves, points and other items of the PRC model that are not surfaces are not shown.");
            _builder.AddNode(node);
            return;
        }

        var key = (structure, (int)solid.IndexTopologicalContext, (int)solid.IndexBody);
        if (!_brepMeshes.TryGetValue(key, out PrcMesh mesh))
        {
            // An uncompressed body is first turned into the model a compressed one reads into, against the numbers of its context.
            if (body.Id is not PrcTopoBrepDataCompress brep)
            {
                if (!TryConvert((PrcTopoBrepData)body.Id, contexts[contextIndex], out brep))
                {
                    Warn(PrcReader.BrepDiagnostic, "A solid of the PRC model has exact geometry that could not be read; it is missing.");
                    _builder.AddNode(node);
                    return;
                }
            }

            // A context's scale takes the coordinates of its bodies to the file's (1000 on 3000 bodies of the corpus, 39.37 on 6, an
            // unusable 0 on 11: the sample whose parts are 0.19 across and are placed a hundred apart).
            PrcTopoContext context = contexts[contextIndex];
            double scale = context.HasScale && double.IsFinite(context.Scale) && context.Scale > 0 ? context.Scale : 1;
            // The colours of single faces are in the extra geometry; a body converted from an uncompressed one has none read.
            PrcFaceGraphics[]? faceGraphics = body.Id is PrcTopoBrepDataCompress
                ? PrcFaceStyles.Of(data.ExtraGeometry, contexts, (int)contextIndex, (int)bodyIndex)
                : null;
            mesh = PrcBrepTessellator.AddMesh(_builder, brep, node.Name, out PrcBrepTessellation result, scale, _cancellation, faceGraphics);
            _brepFaces += result.Faces;
            _brepDropped += result.FacesDropped;
            _brepUnsupported += result.FacesUnsupported;
            _brepCapped |= result.Capped;
            _brepMeshes.Add(key, mesh);
        }

        if (mesh.Mesh < 0)
        {
            node.Visible = false;
            _builder.AddNode(node);
            return;
        }

        // Slot 0 is for the faces without a style of their own; a face's style is applied over the item's as a tessellation's is.
        node.Mesh = mesh.Mesh;
        node.BindingCount = mesh.Slots.Length + 1;
        node.FirstBinding = _builder.AddBinding(Material(appearance, mesh.HasColors));
        foreach (PrcFaceGraphics slot in mesh.Slots)
        {
            Appearance ofFace = appearance;
            Apply(ref ofFace, structure, slot.Style, (int)slot.Behavior, show: false);
            _builder.AddBinding(Material(ofFace, mesh.HasColors));
        }

        _builder.AddNode(node);
    }

    /// <summary>An uncompressed body as the body model; false when its references do not hold together.</summary>
    private bool TryConvert(PrcTopoBrepData data, PrcTopoContext context, out PrcTopoBrepDataCompress body)
    {
        body = null!;
        if (!_brepTables.TryGetValue(context, out PrcBrepDataConverter.Tables? tables))
        {
            try
            {
                tables = PrcBrepDataConverter.Index(context);
            }
            catch (Scene3DException)
            {
                tables = null;
            }

            _brepTables.Add(context, tables);
        }

        return tables is not null && PrcBrepDataConverter.TryConvert(data, tables, context.Tolerance, out body, out _);
    }

    /// <summary>Says how many faces of the solids could not be cut, and whether a body was cut coarser than the tolerance asks.</summary>
    private void ReportBrepTotals()
    {
        if (_brepDropped > 0)
        {
            Warn("prc-brep-face-not-tessellated", $"{_brepDropped} of {_brepFaces} faces of the solids of the PRC model could not be cut into triangles; they are missing.");
        }

        if (_brepUnsupported > 0)
        {
            Note("prc-brep-surface-not-supported", $"{_brepUnsupported} faces of the solids of the PRC model lie on a kind of surface that is not read; they are missing.");
        }

        if (_brepCapped)
        {
            Note("prc-brep-tessellation-capped", "A solid of the PRC model was cut into fewer triangles than its tolerance asks, to keep the size of the mesh bounded.");
        }
    }

    /// <summary>
    /// Whether the faces of a file structure use the bit that says a face is shown: whether any face
    /// with a style has it set. Writers that do not use the behaviour bits of faces leave them all
    /// clear, on faces that are to be seen (every face of the Anark, NAVAir, Native-NX and
    /// OfficeBuilding samples); taken at their word such a model would be empty.
    /// </summary>
    /// <remarks>
    /// Plain and compressed tessellations are asked apart: a writer may set the bit on the faces of
    /// one kind and leave it clear on those of the other (the OfficeBuilding sample does).
    /// </remarks>
    private bool FacesSayWhetherTheyAreShown(int structure, bool ofCompressed)
    {
        int slot = (structure * 2) + (ofCompressed ? 1 : 0);
        _facesSay ??= new bool?[_file.FileStructures.Length * 2];
        if (_facesSay[slot] is { } known)
        {
            return known;
        }

        bool any = false;
        foreach (PrcTess tess in _file.FileStructures[structure].Tessellation.Tess)
        {
            if (!ofCompressed && tess.Id is PrcTess3D plain)
            {
                foreach (PrcTessFace face in plain.FaceTessellationData)
                {
                    if (face.LineAttributes.Length != 0 && (face.Behavior & Show) != 0)
                    {
                        any = true;
                        break;
                    }
                }
            }
            else if (ofCompressed && tess.Id is PrcTess3DCompressed compressed && compressed.LineAttributes.AsSpan().IndexOfAnyExcept((ushort)0) >= 0)
            {
                foreach (byte behavior in compressed.Behaviors)
                {
                    if ((behavior & Show) != 0)
                    {
                        any = true;
                        break;
                    }
                }
            }

            if (any)
            {
                break;
            }
        }

        _facesSay[slot] = any;
        return any;
    }

    /// <summary>
    /// Adds the graphics of an entity to what its fathers gave. A son's value replaces a father's,
    /// unless the father's entity said that it is inherited (FatherHerit) and the son's does not insist
    /// (SonHerit): of several fathers that say so the oldest wins (ISO 14739-1, GraphicsContent).
    /// </summary>
    private void Apply(ref Appearance appearance, int structure, uint style, int behavior, bool show)
    {
        if (style != 0)
        {
            if (HasColor(structure, style) && (!appearance.ColorForced || (behavior & SonHeritColor) != 0))
            {
                appearance.ColorStructure = structure;
                appearance.ColorStyle = style;
                appearance.ColorForced = (behavior & (FatherHeritColor | SonHeritColor)) == FatherHeritColor;
            }

            if (HasTransparency(structure, style) && (!appearance.TransparencyForced || (behavior & SonHeritTransparency) != 0))
            {
                appearance.TransparencyStructure = structure;
                appearance.TransparencyStyle = style;
                appearance.TransparencyForced = (behavior & (FatherHeritTransparency | SonHeritTransparency)) == FatherHeritTransparency;
            }
        }

        if (show && (!appearance.ShowForced || (behavior & SonHeritShow) != 0))
        {
            appearance.Shown = (behavior & Show) != 0;
            appearance.ShowForced = (behavior & (FatherHeritShow | SonHeritShow)) == FatherHeritShow;
        }
    }

    /// <summary>
    /// What an occurrence says of an entity under it, over what the entity has: its style is applied with the occurrence's behaviour,
    /// and then holds for what is under the entity, as a father's that is inherited would.
    /// </summary>
    private void ApplyOverride(ref Appearance appearance, EntityOverride over)
    {
        bool colour = over.Style != 0 && HasColor(over.StyleStructure, over.Style) && (!appearance.ColorForced || (over.Behavior & SonHeritColor) != 0);
        bool transparency = over.Style != 0 && HasTransparency(over.StyleStructure, over.Style) && (!appearance.TransparencyForced || (over.Behavior & SonHeritTransparency) != 0);
        Apply(ref appearance, over.StyleStructure, over.Style, over.Behavior, show: false);
        appearance.ColorForced |= colour;
        appearance.TransparencyForced |= transparency;
    }

    /// <summary>
    /// Whether a style gives a colour or a material: one that names neither says only how transparent it is, and leaves the colour to
    /// the fathers. A style that is not there counts as one (it is reported when the material is made).
    /// </summary>
    private bool HasColor(int structure, uint style)
    {
        PrcGraphStyle[] styles = _file.FileStructures[structure].Globals.Styles;
        return style - 1 >= (uint)styles.Length || styles[style - 1].BiasedColorIndex != 0;
    }

    private bool HasTransparency(int structure, uint style)
    {
        PrcGraphStyle[] styles = _file.FileStructures[structure].Globals.Styles;
        return style - 1 < (uint)styles.Length && styles[style - 1].IsTransparency;
    }

    /// <summary>
    /// What a part has when its style gives a colour and nothing else (most parts of Tetra4D's exports), or
    /// no style at all. The standard says nothing of how such a part shines; the pictures the exporters
    /// drew of their models give it a white highlight about half as bright as the light, and tight enough
    /// that a black part stays black (SpinFire's MIL-STD wheel).
    /// </summary>
    private static SceneMaterial DefaultMaterial() => new()
    {
        Ambient = new Vector3(0.75f),
        Diffuse = new Vector3(0.75f),
        Specular = new Vector3(0.5f),
        Opacity = 1,
        Reflectivity = 0.3f,
        Flags = SceneMaterialFlags.Lit,
    };

    private int Material(in Appearance appearance, bool vertexColors)
    {
        if (appearance.ColorStyle == 0 && appearance.TransparencyStyle == 0 && !vertexColors)
        {
            return 0;
        }

        var key = new MaterialKey(appearance.ColorStructure, appearance.ColorStyle, appearance.TransparencyStructure, appearance.TransparencyStyle, vertexColors);
        if (_materials.TryGetValue(key, out int index))
        {
            return index;
        }

        SceneMaterial material = DefaultMaterial();
        material.Name = _emptyName;
        if (vertexColors)
        {
            material.Flags |= SceneMaterialFlags.UseVertexColor;
        }

        if (appearance.ColorStyle != 0)
        {
            FillFromStyle(ref material, appearance.ColorStructure, appearance.ColorStyle);
        }

        if (appearance.TransparencyStyle != 0)
        {
            // 0 is transparent, 255 opaque.
            PrcGraphStyle style = _file.FileStructures[appearance.TransparencyStructure].Globals.Styles[appearance.TransparencyStyle - 1];
            material.Opacity *= (byte)style.Transparency / 255f;
        }

        index = _builder.AddMaterial(material);
        _materials.Add(key, index);
        return index;
    }

    private void FillFromStyle(ref SceneMaterial material, int structure, uint biasedStyle)
    {
        PrcFileStructureInternalGlobalData globals = _file.FileStructures[structure].Globals;
        if (biasedStyle - 1 >= (uint)globals.Styles.Length)
        {
            Warn("prc-missing-style", "An entity of the PRC model names a style that does not exist; the default appearance is used.");
            return;
        }

        PrcGraphStyle style = globals.Styles[biasedStyle - 1];
        if (!style.IsMaterial)
        {
            if (TryGetColor(globals, style.BiasedColorIndex, out Vector3 color))
            {
                material.Ambient = color;
                material.Diffuse = color;
            }

            return;
        }

        if (style.BiasedColorIndex == 0)
        {
            return;
        }

        if (style.BiasedColorIndex - 1 >= (uint)globals.Materials.Length)
        {
            Warn("prc-missing-material", "A style of the PRC model names a material that does not exist; the default appearance is used.");
            return;
        }

        // The materials are one array of two kinds of entries (PDF Association issue #818): a material,
        // or a texture applied on one.
        object? entry = globals.Materials[style.BiasedColorIndex - 1].Id;
        if (entry is PrcGraphTextureApplication application)
        {
            uint generic = application.BiasedMaterialGenericIndex;
            if (generic != 0 && generic - 1 < (uint)globals.Materials.Length && globals.Materials[generic - 1].Id is PrcGraphMaterial under)
            {
                FillFromMaterial(ref material, globals, under);
            }

            int texture = Texture(structure, application, out Matrix4x4 uvTransform, out PrcGraphTextureDefinition? definition);
            if (texture >= 0)
            {
                // The picture is the surface's colour: the material under it gives the highlight, not a
                // tint (in files it is often black, which would blot the picture out).
                material.Ambient = Vector3.One;
                material.Diffuse = Vector3.One;
                material.FirstLayer = _builder.AddTextureLayer(new SceneTextureLayer
                {
                    Texture = texture,
                    Intensity = 1,
                    BlendConstant = 1,
                    BlendFunction = SceneBlendFunction.Multiply,
                    Repeat = RepeatOf(definition),
                    Transform = uvTransform,
                });
                material.LayerCount = 1;
            }

            if (definition is not null)
            {
                NoteWhatIsNotApplied(definition);
            }

            if (application.BiasedNextTextureIndex != 0)
            {
                Note("prc-further-textures", "A material of the PRC model applies several textures; only the first is used.");
            }
        }
        else if (entry is PrcGraphMaterial plain)
        {
            FillFromMaterial(ref material, globals, plain);
        }
    }

    private void FillFromMaterial(ref SceneMaterial material, PrcFileStructureInternalGlobalData globals, PrcGraphMaterial source)
    {
        if (TryGetColor(globals, source.BiasedAmbientIndex, out Vector3 ambient))
        {
            material.Ambient = ambient;
        }

        if (TryGetColor(globals, source.BiasedDiffuseIndex, out Vector3 diffuse))
        {
            material.Diffuse = diffuse;
        }

        material.Emissive = TryGetColor(globals, source.BiasedEmissiveIndex, out Vector3 emissive) ? emissive : Vector3.Zero;
        if (TryGetColor(globals, source.BiasedSpecularIndex, out Vector3 specular))
        {
            material.Specular = specular;
        }

        // Shininess and alpha as OpenGL has them: the exponent runs to 128; files also give 0 to 1.
        double shininess = source.Shininess;
        if (double.IsFinite(shininess) && shininess >= 0)
        {
            material.Reflectivity = (float)Math.Min(shininess > 1 ? shininess / 128 : shininess, 1);
        }

        // Four alphas of 0 are alphas not given: there are writers that leave them so and say in the
        // style that the surface is opaque, and read as written their models are drawn as nothing.
        double alpha = source.DiffuseAlpha;
        bool given = source.AmbientAlpha != 0 || alpha != 0 || source.EmissiveAlpha != 0 || source.SpecularAlpha != 0;
        if (given && double.IsFinite(alpha))
        {
            material.Opacity = (float)Math.Clamp(alpha, 0, 1);
        }
    }

    /// <summary>
    /// A colour by its index as the file gives it: plus one, and counted in the three numbers of each
    /// colour rather than in colours (PDF Association issue #816), so colour <c>n</c> is index <c>3n + 1</c>.
    /// </summary>
    private bool TryGetColor(PrcFileStructureInternalGlobalData globals, uint biasedIndex, out Vector3 color)
    {
        color = default;
        if (biasedIndex == 0)
        {
            return false;
        }

        uint index = (biasedIndex - 1) / 3;
        if (index >= (uint)globals.Colors.Length)
        {
            Warn("prc-missing-colour", "A style of the PRC model names a colour that does not exist; the default appearance is used.");
            return false;
        }

        PrcRgbColor c = globals.Colors[index];
        color = new Vector3(Channel(c.Red), Channel(c.Green), Channel(c.Blue));
        return true;

        static float Channel(double value) => double.IsFinite(value) ? (float)Math.Clamp(value, 0, 1) : 0;
    }

    /// <summary>The scene's texture for a texture application, or -1 when its picture cannot be carried.</summary>
    private int Texture(int structure, PrcGraphTextureApplication application, out Matrix4x4 uvTransform, out PrcGraphTextureDefinition? textureDefinition)
    {
        uvTransform = Matrix4x4.Identity;
        textureDefinition = null;
        PrcFileStructureData data = _file.FileStructures[structure];
        uint definition = application.BiasedTextureDefinitionIndex;
        if (definition == 0 || definition - 1 >= (uint)data.Globals.Textures.Length)
        {
            return -1;
        }

        textureDefinition = data.Globals.Textures[definition - 1];
        uvTransform = PrcTransforms.TextureMatrix(textureDefinition.TextureTransformation);

        uint biasedPicture = data.Globals.Textures[definition - 1].BiasedPictureIndex;
        if (biasedPicture == 0 || biasedPicture - 1 >= (uint)data.Globals.Pictures.Length)
        {
            Warn("prc-missing-picture", "A texture of the PRC model names a picture that does not exist; it is not applied.");
            return -1;
        }

        int pictureIndex = (int)(biasedPicture - 1);
        if (_textures.TryGetValue((structure, pictureIndex), out int texture))
        {
            return texture;
        }

        texture = -1;
        PrcGraphPicture picture = data.Globals.Pictures[pictureIndex];
        uint file = picture.BiasedUncompressedFileIndex;
        if (file == 0 || file - 1 >= (uint)data.Files.Length)
        {
            Warn("prc-missing-picture", "A texture of the PRC model names a picture that does not exist; it is not applied.");
        }
        else if (!PrcPictures.TryDescribe(picture.Format, data.Files[file - 1].Span, out SceneImageEncoding encoding, out SceneTextureChannels channels, out int width, out int height))
        {
            Warn("prc-picture-not-carried", "A texture of the PRC model is a picture in a form that is not carried (only PNG and JPEG are); it is not applied.");
        }
        else
        {
            texture = _builder.AddTexture(new SceneTexture("", width, height, channels, [new SceneTextureImage(encoding, channels, data.Files[file - 1])]));
        }

        _textures.Add((structure, pictureIndex), texture);
        return texture;
    }

    // The format's wrapping modes count from 0 (the stored values of every sample file agree): unknown,
    // repeat, clamp to border, clamp, clamp to edge, mirrored repeat. Unknown leaves the choice to the
    // application, which repeats; a mirrored repeat is drawn as a plain one.
    private static byte RepeatOf(PrcGraphTextureDefinition? definition)
    {
        return definition is null ? (byte)3 : (byte)(Repeats(definition.TextureWrappingModeS) | (Repeats(definition.TextureWrappingModeT) << 1));

        static int Repeats(int mode) => mode is 0 or 1 or 5 ? 1 : mode is >= 2 and <= 4 ? 0 : 1;
    }

    // Texture mapping types (from 0): the application's choice, the coordinates stored with the tessellation,
    // the surface's own parameters, an operator (with the operator's own number: 0 and 1 are "unknown", which
    // every sample file writes, and its coordinates are the stored ones). Functions (from 0): unknown, modulate,
    // replace, blend, decal. A replace is drawn as the lit surface colour, which is what the samples' own viewer
    // shows for them.
    private void NoteWhatIsNotApplied(PrcGraphTextureDefinition definition)
    {
        if (definition.TextureMappingType == 2 || (definition.TextureMappingType == 3 && definition.TextureMappingOperator > 1))
        {
            Note("prc-texture-mapping-not-applied", "A texture of the PRC model asks for coordinates taken from the surface or made by a projection; the coordinates stored with its mesh are used.");
        }

        if (definition.TextureFunction > 2)
        {
            Note("prc-texture-function-not-applied", "A texture of the PRC model is blended or applied as a decal; it is applied as the surface colour.");
        }
    }

    private void Note(string code, string message) => _builder.Report(SceneDiagnosticSeverity.Info, code, message);

    private void Warn(string code, string message) => _builder.Report(SceneDiagnosticSeverity.Warning, code, message);

    private static Scene3DException Invalid(string message) => new(Scene3DError.InvalidValue, message);
}
