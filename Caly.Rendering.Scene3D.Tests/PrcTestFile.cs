using Caly.Rendering.Scene3D.Prc;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Writes a small but whole PRC file, bit by bit, for tests that need one in the repository: one file
/// structure with colours and styles, parts of one mesh item each, product occurrences that place them,
/// plain tessellations, and the model file. Every field is written in the order the format has it, so
/// the file is also a description of the format's layout as the reader understands it.
/// </summary>
internal sealed class PrcTestFile
{
    public sealed record Occurrence(string Name, uint BiasedPart = 0, uint[]? Children = null, double[]? Translation = null, uint BiasedStyle = 0, ushort Behavior = 1, uint BiasedPrototype = 0, Markup[]? Markups = null);

    /// <summary>A markup (a note, a dimension) of an occurrence, drawn by the markup tessellation <paramref name="BiasedTessellation"/> (see <see cref="MarkupTessellation"/>).</summary>
    public sealed record Markup(string Name, uint BiasedTessellation);

    /// <summary>
    /// A part with one item. With <paramref name="Body"/> the item is a solid (<c>PRC_TYPE_RI_BrepModel</c>) whose exact
    /// geometry is that body of that topological context; without, a mesh item (<c>PRC_TYPE_RI_PolyBrepModel</c>).
    /// </summary>
    public sealed record Part(string Name, string ItemName, uint BiasedTessellation, uint BiasedStyle = 0, ushort Behavior = 1, (uint Context, uint Body)? Body = null);

    public uint AuthoringVersion { get; init; } = 8137;

    public double UnitsInMm { get; init; } = 1;

    /// <summary>Colours, three numbers each.</summary>
    public List<double[]> Colors { get; } = [];

    /// <summary>Plain-colour styles: the colour's index as the file has it (3n + 1), and a transparency or -1.</summary>
    public List<(uint BiasedColor, int Transparency)> Styles { get; } = [];

    public List<Part> Parts { get; } = [];

    /// <summary>Sons before fathers; the last one is the root.</summary>
    public List<Occurrence> Occurrences { get; } = [];

    /// <summary>Tessellations, each already written (see <see cref="Tessellation"/>).</summary>
    public List<PrcBitWriter> Tessellations { get; } = [];

    /// <summary>The topological contexts of the exact-geometry section, each with its bodies already written (see <see cref="EmptyBrepBody"/>).</summary>
    public List<List<PrcBitWriter>> TopologicalContexts { get; } = [];

    /// <summary>The scale every topological context gives its bodies (the context's <c>has_scale</c> and <c>scale</c>); none by default.</summary>
    public double? ContextScale { get; init; }

    /// <summary>Text written as the user data of the tree's entities: 0 bits by default.</summary>
    public uint UserDataBits { get; init; }

    /// <summary>
    /// An uncompressed solid body without connex entities (<c>PRC_TYPE_TOPO_BrepData</c>); when its behaviour says it has a
    /// bounding box (bit 0 or 1), the six numbers of one follow (PDF Association issue #715).
    /// </summary>
    public static PrcBitWriter EmptyBrepBody(byte behavior = 0)
    {
        var w = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TOPO_BrepData).Bit(false).Bits(behavior, 8).Unsigned(0);
        if ((behavior & 3) != 0)
        {
            for (int i = 0; i < 6; i++)
            {
                w.Double(i < 3 ? -1 : 1);
            }
        }

        return w;
    }

    /// <summary>The smallest file with something to show: a root, a part under it, a triangle.</summary>
    public static PrcTestFile Simple()
    {
        var file = new PrcTestFile();
        file.Colors.Add([1, 0, 0]);
        file.Colors.Add([0, 0, 1]);
        file.Styles.Add((1, -1));
        file.Styles.Add((4, 128));
        file.Tessellations.Add(Tessellation(file.AuthoringVersion, [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 3, 6]));
        file.Parts.Add(new Part("plate part", "plate", 1, BiasedStyle: 1));
        file.Occurrences.Add(new Occurrence("left", BiasedPart: 1, Translation: [-2, 0, 0]));
        file.Occurrences.Add(new Occurrence("right", BiasedPart: 1, Translation: [2, 0, 0], BiasedStyle: 2));
        file.Occurrences.Add(new Occurrence("assembly", Children: [0, 1]));
        return file;
    }

    /// <summary>
    /// A plain tessellation of triangles in the plane z = 0: coordinates, then point indices (already
    /// multiplied by three). A file of version 7047 or later says that normals are to be calculated and
    /// stores none; an older one cannot say so, and stores the normal +z with every point.
    /// </summary>
    public static PrcBitWriter Tessellation(uint version, double[] coordinates, uint[] pointIndices)
    {
        var w = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_3D);
        w.Bit(false).Unsigned((uint)coordinates.Length);
        foreach (double value in coordinates)
        {
            w.Double(value);
        }

        w.Bit(true);                                         // has faces
        if (version >= 7039)
        {
            w.Bit(false);                                    // has loops
        }

        if (version >= 7047)
        {
            w.Bit(true).Bits(0, 8).Double(0.5);              // normals to be calculated: flags, crease angle
        }

        bool storedNormals = version < 7047;
        if (storedNormals)
        {
            w.Unsigned(3).Double(0).Double(0).Double(1);
        }
        else
        {
            w.Unsigned(0);
        }

        w.Unsigned(0);                                       // wire indices
        w.Unsigned((uint)pointIndices.Length * (storedNormals ? 2u : 1u));
        foreach (uint index in pointIndices)
        {
            if (storedNormals)
            {
                w.Unsigned(0);
            }

            w.Unsigned(index);
        }

        w.Unsigned(1);                                       // one face
        w.Unsigned(PrcType.PRC_TYPE_TESS_Face).Unsigned(0);  // no line attributes
        w.Unsigned(0).Unsigned(0);                           // wire start, no wire sizes
        w.Unsigned(0x0002).Unsigned(0).Unsigned(1).Unsigned((uint)pointIndices.Length / 3);
        w.Unsigned(0).Bit(false);                            // no texture coordinate indices, no vertex colours
        return w.Unsigned(0);                                // texture coordinates
    }

    public byte[] Build()
    {
        var container = new PrcTestContainer { AuthoringVersion = AuthoringVersion, ModelFile = ModelFile() };
        container.Structures.Add(new PrcTestContainer.Structure { Globals = Globals(), Tree = Tree(), Tessellation = TessellationSection(), Geometry = GeometrySection() });
        return container.Build();
    }

    /// <summary>The sections, inflated, as the file has them: for tests that damage one before packing.</summary>
    public (byte[] Globals, byte[] Tree, byte[] Tessellation, byte[] ModelFile) Sections() => (Globals(), Tree(), TessellationSection(), ModelFile());

    /// <summary>The exact-geometry section: a name, then the topological contexts with their bodies.</summary>
    public byte[] GeometrySection()
    {
        var w = new PrcBitWriter();
        Base(w.Unsigned(PrcType.PRC_TYPE_ASM_FileStructureGeometry), "geometry").Unsigned((uint)TopologicalContexts.Count);
        foreach (List<PrcBitWriter> bodies in TopologicalContexts)
        {
            Base(w.Unsigned(PrcType.PRC_TYPE_TOPO_Context), "context");
            w.Bits(0, 8).Double(0.001).Double(0.001).Bit(false);                                                // behaviour, granularity, tolerance, no thickness
            w.Bit(ContextScale is not null);
            if (ContextScale is { } scale)
            {
                w.Double(scale);
            }

            w.Unsigned((uint)bodies.Count);
            foreach (PrcBitWriter body in bodies)
            {
                w.Append(body);
            }
        }

        return w.Unsigned(0).ToArray();                                                                          // user data
    }

    private static PrcBitWriter Base(PrcBitWriter w, string name) => w.Unsigned(0).Bit(false).String(name);   // no attributes; a name

    // Entities that can be referred to have an identifier that is theirs alone in the file structure:
    // styles count from 1, the entities of the tree from FirstTreeIdentifier, in the order written.
    private uint _nextIdentifier;

    /// <summary>The identifier of the first entity of the tree (the first part); its item has the next, then the next part.</summary>
    public const uint FirstTreeIdentifier = 100;

    /// <summary>The identifier of an occurrence, by its place in <see cref="Occurrences"/>.</summary>
    public uint OccurrenceIdentifier(int index) => FirstTreeIdentifier + (uint)(Parts.Count * 2 + index);

    private PrcBitWriter RefBase(PrcBitWriter w, string name) => Base(w, name).Unsigned(0).Unsigned(0).Unsigned(_nextIdentifier++);

    private PrcBitWriter Graphics(PrcBitWriter w, string name, uint biasedStyle, ushort behavior) =>
        RefBase(w, name).Bit(false).Unsigned(0).Unsigned(biasedStyle).Short(behavior);                           // not "as before"; no layer

    private PrcBitWriter UserData(PrcBitWriter w) => w.Unsigned(UserDataBits).Bits(0, (int)UserDataBits);

    private byte[] Globals()
    {
        _nextIdentifier = 1;
        var w = new PrcBitWriter().Unsigned(0);                                                                  // no schemas
        Base(w.Unsigned(PrcType.PRC_TYPE_ASM_FileStructureGlobals), "globals").Unsigned(0);                      // no other file structures
        w.Double(0.01).Double(0.5);                                                                              // tessellation chord and angle
        w.String(null).Unsigned(0);                                                                              // markup fonts
        w.Unsigned((uint)Colors.Count);
        foreach (double[] color in Colors)
        {
            w.Double(color[0]).Double(color[1]).Double(color[2]);
        }

        w.Unsigned(0).Unsigned(0).Unsigned(0).Unsigned(0);                                                       // pictures, textures, materials, line patterns
        w.Unsigned((uint)Styles.Count);
        foreach ((uint color, int transparency) in Styles)
        {
            RefBase(w.Unsigned(PrcType.PRC_TYPE_GRAPH_Style), "style").Double(0.1).Bit(false).Unsigned(0).Bit(false).Unsigned(color);
            w.Bit(transparency >= 0);
            if (transparency >= 0)
            {
                w.Bits((uint)transparency, 8);
            }

            w.Bit(false).Bit(false).Bit(false);                                                                  // rendering parameters
        }

        w.Unsigned(0).Unsigned(0);                                                                               // fill patterns, coordinate systems
        return w.Unsigned(0).ToArray();                                                                          // user data
    }

    private byte[] Tree()
    {
        _nextIdentifier = FirstTreeIdentifier;
        var w = new PrcBitWriter();
        Base(w.Unsigned(PrcType.PRC_TYPE_ASM_FileStructureTree), "tree");
        w.Unsigned((uint)Parts.Count);
        foreach (Part part in Parts)
        {
            Graphics(w.Unsigned(PrcType.PRC_TYPE_ASM_PartDefinition), part.Name, 0, 1);
            for (int i = 0; i < 6; i++)
            {
                w.Double(0);                                                                                     // bounding box
            }

            w.Unsigned(1);
            if (part.Body is { } body)
            {
                Graphics(w.Unsigned(PrcType.PRC_TYPE_RI_BrepModel), part.ItemName, part.BiasedStyle, part.Behavior);
                w.Unsigned(0).Unsigned(part.BiasedTessellation);                                                 // no coordinate system
                w.Bit(true).Unsigned(body.Context + 1).Unsigned(body.Body + 1).Bit(true);                                // exact geometry: where (as the files have it, from 1); closed
            }
            else
            {
                Graphics(w.Unsigned(PrcType.PRC_TYPE_RI_PolyBrepModel), part.ItemName, part.BiasedStyle, part.Behavior);
                w.Unsigned(0).Unsigned(part.BiasedTessellation).Bit(true);                                       // no coordinate system; closed
            }

            UserData(w);
            w.Unsigned(0).Unsigned(0).Unsigned(0).Unsigned(0);                                                   // markup
            w.Unsigned(0);                                                                                       // views
            UserData(w);
        }

        w.Unsigned((uint)Occurrences.Count);
        foreach (Occurrence occurrence in Occurrences)
        {
            Graphics(w.Unsigned(PrcType.PRC_TYPE_ASM_ProductOccurrence), occurrence.Name, occurrence.BiasedStyle, occurrence.Behavior);
            w.Unsigned(occurrence.BiasedPart).Unsigned(occurrence.BiasedPrototype);
            if (occurrence.BiasedPrototype != 0)
            {
                w.Bit(true);                                                                                     // in this file structure
            }

            w.Unsigned(0);                                                                                       // no external data
            uint[] children = occurrence.Children ?? [];
            w.Unsigned((uint)children.Length);
            foreach (uint child in children)
            {
                w.Unsigned(child);
            }

            w.Bits(0, 8);                                                                                        // product behaviour
            w.Bit(true).Double(1).Bits(0, 8).Integer(3);                                                         // unit from the CAD file, 1 mm; flags; loaded
            w.Bit(occurrence.Translation is not null);
            if (occurrence.Translation is { } t)
            {
                w.Unsigned(PrcType.PRC_TYPE_MISC_CartesianTransformation).Bits(0x01, 8).Double(t[0]).Double(t[1]).Double(t[2]);
            }

            w.Unsigned(0);                                                                                       // entity references
            WriteMarkups(w, occurrence.Markups ?? []);
            w.Unsigned(0).Bit(false).Unsigned(0).Unsigned(0);                                                    // views, no filter, display filters, scene parameters
            UserData(w);
        }

        Base(w.Unsigned(PrcType.PRC_TYPE_ASM_FileStructure), "internal").Unsigned(100).Unsigned((uint)Occurrences.Count);
        return UserData(w).ToArray();
    }

    private void WriteMarkups(PrcBitWriter w, Markup[] markups)
    {
        w.Unsigned(0).Unsigned(0);                                                                               // linked items, leaders
        w.Unsigned((uint)markups.Length);
        foreach (Markup markup in markups)
        {
            Graphics(w.Unsigned(PrcType.PRC_TYPE_MKP_Markup), markup.Name, 0, 1);
            w.Unsigned(0).Unsigned(0);                                                                           // type, subtype
            w.Unsigned(0).Unsigned(0);                                                                           // linked items, leaders
            w.Unsigned(markup.BiasedTessellation);
            UserData(w);
        }

        w.Unsigned(0);                                                                                           // annotation entities
    }

    /// <summary>
    /// A markup tessellation (<c>PRC_TYPE_TESS_Markup</c>): drawing entities one after the other, each
    /// with its codes and its doubles in the coordinates (ISO 14739-1, 7.8.8).
    /// </summary>
    public sealed class MarkupTessellation
    {
        private const uint ExtraData = 0x04000000;

        private readonly List<uint> _codes = [];
        private readonly List<double> _coordinates = [];

        /// <summary>A colour that holds until the next one; <paramref name="identifier"/> is as the file has it, three times the colour's index.</summary>
        public MarkupTessellation Color(uint identifier) => Entity(ExtraData | (11u << 21), [], identifier);

        public MarkupTessellation Polyline(params double[] points) => Entity(0, points);

        public MarkupTessellation Triangles(params double[] points) => Entity(ExtraData | (2u << 21), points);

        public MarkupTessellation Quads(params double[] points) => Entity(ExtraData | (3u << 21), points);

        /// <summary>An entity of another kind (<paramref name="type"/> as in the table of the standard), with its inner codes and doubles.</summary>
        public MarkupTessellation Other(uint type, double[] doubles, params uint[] inner) => Entity(ExtraData | (type << 21), doubles, inner);

        /// <summary>
        /// A block of entities (a face-view, frame-draw or fixed-size mode: <paramref name="type"/> 6, 7 or 8):
        /// its start, which has the number of entities inside and the doubles of the block, the entities, and its end.
        /// </summary>
        public MarkupTessellation Block(uint type, double[] origin, Action<MarkupTessellation> inside)
        {
            var content = new MarkupTessellation();
            inside(content);
            uint start = ExtraData | (type << 21);
            _codes.Add(start | (uint)(content._codes.Count / 2));
            _codes.Add((uint)(origin.Length + content._coordinates.Count));
            _coordinates.AddRange(origin);
            _coordinates.AddRange(content._coordinates);
            _codes.AddRange(content._codes);
            _codes.Add(start);
            _codes.Add(0);
            return this;
        }

        private MarkupTessellation Entity(uint first, double[] doubles, params uint[] inner)
        {
            _codes.Add(first | (uint)inner.Length);
            _codes.Add((uint)doubles.Length);
            _codes.AddRange(inner);
            _coordinates.AddRange(doubles);
            return this;
        }

        /// <summary>
        /// The entity as the tessellation section holds it. <paramref name="extraDoubles"/> are coordinates no
        /// entity uses; when negative, that many of the entities' doubles are missing.
        /// </summary>
        public PrcBitWriter Write(int extraDoubles = 0)
        {
            var w = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_Markup);
            int count = _coordinates.Count + extraDoubles;
            w.Bit(false).Unsigned((uint)count);
            for (int i = 0; i < count; i++)
            {
                w.Double(i < _coordinates.Count ? _coordinates[i] : 0);
            }

            w.Unsigned((uint)_codes.Count);
            foreach (uint code in _codes)
            {
                w.Unsigned(code);
            }

            w.Unsigned(0);                                                                                       // no text strings
            return w.String("label").Bits(0, 8);                                                                 // label, behaviour
        }
    }

    private byte[] TessellationSection()
    {
        var w = new PrcBitWriter();
        Base(w.Unsigned(PrcType.PRC_TYPE_ASM_FileStructureTessellation), "tessellations").Unsigned((uint)Tessellations.Count);
        foreach (PrcBitWriter tessellation in Tessellations)
        {
            w.Append(tessellation);
        }

        return w.Unsigned(0).ToArray();
    }

    private byte[] ModelFile()
    {
        var w = new PrcBitWriter().Unsigned(0);                                                                  // no schemas
        Base(w.Unsigned(PrcType.PRC_TYPE_ASM_ModelFile), "model").Bit(true).Double(UnitsInMm);
        w.Unsigned(1).Unsigned(1).Unsigned(2).Unsigned(3).Unsigned(4).Unsigned((uint)Occurrences.Count).Bit(true);   // the root: structure 1-2-3-4, its last occurrence, active
        w.Unsigned(0);                                                                                           // index of the file structure
        return w.Unsigned(0).ToArray();
    }
}
