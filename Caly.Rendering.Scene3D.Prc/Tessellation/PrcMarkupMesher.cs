using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>The meshes made of one markup tessellation; -1 where it has nothing of that kind.</summary>
/// <param name="Lines">A mesh of line segments: the polylines.</param>
/// <param name="Triangles">A mesh of triangles: the triangle lists and quads.</param>
/// <param name="EntitiesNotShown">Whether entities of a kind that is not drawn (text, symbols, patterns, pictures, blocks that move or scale what they hold) were left out.</param>
/// <param name="MissingColors">Whether an entity named a colour that the file does not have.</param>
internal readonly record struct PrcMarkupMeshes(int Lines, int Triangles, bool EntitiesNotShown, bool MissingColors);

/// <summary>
/// Turns a markup tessellation (<c>PRC_TYPE_TESS_Markup</c>, ISO 14739-1, 7.8.8) into meshes: the notes,
/// dimensions and frames of a model, as the polylines and the triangles a writer has already drawn them
/// with (text in the samples is a polyline per outline and a list of triangles for the fill).
/// <para>
/// A markup is a list of entities. Each has two codes: its type with the number of inner codes it has,
/// and the number of doubles it takes from the coordinates (the entities use them one after the other).
/// Colours (type 11) hold until the next one; the colour is named by three times its index in the
/// globals' colours, as styles name them. Entities that need a font, a picture or a viewer's screen
/// (text, symbols, patterns) are not drawn. Blocks (a matrix, face-view, frame-draw or fixed-size
/// mode: a start, entities, an end) change how what they hold is placed, which is not done: the
/// whole block is left out. Its start gives the doubles of all it holds, so the entities inside take
/// none of their own.
/// </para>
/// </summary>
internal static class PrcMarkupMesher
{
    // The first code of an entity.
    private const uint IsMatrix = 0x08000000, IsExtraData = 0x04000000, ExtraDataType = 0x3E00000, InnerMask = 0xFFFFF;

    // Types of entities that are not polylines (the 'extra data' type of the first code).
    private const uint TrianglesType = 2, QuadsType = 3, FaceView = 6, FrameDraw = 7, FixedSize = 8, ColorType = 11, LineStipple = 12, LineWidth = 17;

    /// <summary>What a markup is drawn in when it names no colour: a dark grey, as annotations usually are.</summary>
    internal const uint DefaultColor = 0xFF202020;

    private enum Kind { Polyline, Triangles, Quads, Color, Block, Ignored, NotShown }

    /// <summary>An entity that is drawn, or a colour: where its codes start and where its doubles are.</summary>
    private readonly record struct Entity(Kind Kind, int Code, int Offset, int Doubles);

    /// <summary>Builds the meshes; returns them as -1 where the markup has none.</summary>
    /// <exception cref="Scene3DException">The codes ask for more than the tessellation has.</exception>
    public static PrcMarkupMeshes Build(SceneBuilder builder, PrcTessMarkup tessellation, PrcRgbColor[] colors, string name, CancellationToken cancellation)
    {
        double[] coordinates = tessellation.TessellationCoordinates.Coordinates;
        uint[] codes = tessellation.CodeNumbers;

        // What the codes say, checked against what there is.
        var entities = new List<Entity>();
        bool notShown = false;
        long offset = 0;
        int inBlock = 0;
        int lineVertices = 0, lineIndices = 0, triangleVertices = 0, triangleIndices = 0;
        for (int i = 0; i < codes.Length;)
        {
            cancellation.ThrowIfCancellationRequested();
            if (i + 1 >= codes.Length)
            {
                throw Invalid("A markup of the PRC model ends in the middle of an entity.");
            }

            uint first = codes[i];
            uint doubles = codes[i + 1];
            Kind kind = KindOf(first, out bool startsBlock);

            // The inner codes of a block's start count the entities in the block, which follow on their own.
            long next = i + 2L + (kind == Kind.Block ? 0 : first & InnerMask);
            if (next > codes.Length)
            {
                throw Invalid("A markup of the PRC model ends in the middle of an entity.");
            }

            int at = i;
            i = (int)next;

            if (kind == Kind.Block)
            {
                if (startsBlock && inBlock == 0)
                {
                    offset += doubles;
                    notShown = true;
                }

                inBlock = startsBlock ? inBlock + 1 : Math.Max(0, inBlock - 1);
                continue;
            }

            if (inBlock > 0)
            {
                continue;
            }

            offset += doubles;
            if (offset > coordinates.Length)
            {
                throw Invalid($"The entities of a markup of the PRC model use {offset} numbers, and it has {coordinates.Length}.");
            }

            switch (kind)
            {
                case Kind.Polyline:
                    if (doubles < 6)
                    {
                        continue;
                    }

                    lineVertices += (int)(doubles / 3);
                    lineIndices += ((int)(doubles / 3) - 1) * 2;
                    break;
                case Kind.Triangles:
                    if (doubles < 9)
                    {
                        continue;
                    }

                    triangleVertices += (int)(doubles / 9) * 3;
                    triangleIndices += (int)(doubles / 9) * 3;
                    break;
                case Kind.Quads:
                    if (doubles < 12)
                    {
                        continue;
                    }

                    triangleVertices += (int)(doubles / 12) * 4;
                    triangleIndices += (int)(doubles / 12) * 6;
                    break;
                case Kind.Color:
                    break;
                case Kind.NotShown:
                    notShown = true;
                    continue;
                default:
                    continue;
            }

            entities.Add(new Entity(kind, at, (int)(offset - doubles), (int)doubles));
        }

        bool missingColors = false;
        int lines = lineIndices > 0 ? Fill(builder, SceneTopology.Lines, lineVertices, lineIndices, name, entities, codes, coordinates, colors, ref missingColors) : -1;
        int triangles = triangleIndices > 0 ? Fill(builder, SceneTopology.Triangles, triangleVertices, triangleIndices, name, entities, codes, coordinates, colors, ref missingColors) : -1;
        return new PrcMarkupMeshes(lines, triangles, notShown, missingColors);
    }

    /// <summary>One kind of mesh: the vertices and indices of the entities of that kind, each in the colour that holds at it.</summary>
    private static int Fill(SceneBuilder builder, SceneTopology topology, int vertexCount, int indexCount, string name, List<Entity> entities, uint[] codes, double[] coordinates, PrcRgbColor[] colors, ref bool missingColors)
    {
        builder.BeginMesh(builder.AddName(name), SceneMeshFlags.HasColors, topology);
        SceneVertexSpans vertices = builder.AllocateVertices(vertexCount);
        Span<uint> indices = builder.AllocateIndices(indexCount);
        uint color = DefaultColor;
        int vertex = 0, index = 0;
        foreach (Entity entity in entities)
        {
            ReadOnlySpan<double> own = coordinates.AsSpan(entity.Offset, entity.Doubles);
            switch (entity.Kind)
            {
                case Kind.Color:
                    color = ColorOf(codes, entity.Code, colors, ref missingColors);
                    break;

                case Kind.Polyline when topology == SceneTopology.Lines:
                    int points = own.Length / 3;
                    for (int p = 0; p < points; p++)
                    {
                        Put(vertices, vertex + p, own.Slice(p * 3, 3), color);
                        if (p > 0)
                        {
                            indices[index++] = (uint)(vertex + p - 1);
                            indices[index++] = (uint)(vertex + p);
                        }
                    }

                    vertex += points;
                    break;

                case Kind.Triangles when topology == SceneTopology.Triangles:
                    // A list of triangles holds a point more than whole triangles in every sample: it is dropped.
                    int corners = own.Length / 9 * 3;
                    for (int c = 0; c < corners; c++)
                    {
                        Put(vertices, vertex + c, own.Slice(c * 3, 3), color);
                        indices[index++] = (uint)(vertex + c);
                    }

                    vertex += corners;
                    break;

                case Kind.Quads when topology == SceneTopology.Triangles:
                    for (int q = 0, quads = own.Length / 12; q < quads; q++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            Put(vertices, vertex + c, own.Slice((q * 12) + (c * 3), 3), color);
                        }

                        foreach (int corner in (ReadOnlySpan<int>)[0, 1, 2, 0, 2, 3])
                        {
                            indices[index++] = (uint)(vertex + corner);
                        }

                        vertex += 4;
                    }

                    break;
            }
        }

        builder.AddPart(indexCount, 0);
        return builder.EndMesh();
    }

    private static void Put(in SceneVertexSpans vertices, int vertex, ReadOnlySpan<double> point, uint color)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            double value = point[axis];
            vertices.Positions[(vertex * 3) + axis] = double.IsFinite(value) ? (float)value : 0f;
        }

        vertices.Colors[vertex] = color;
    }

    private static Kind KindOf(uint first, out bool startsBlock)
    {
        startsBlock = false;
        if ((first & IsMatrix) != 0)
        {
            // A block ends where an entity of its kind holds nothing.
            startsBlock = (first & InnerMask) != 0;
            return Kind.Block;
        }

        if ((first & IsExtraData) == 0)
        {
            return Kind.Polyline;
        }

        switch ((first & ExtraDataType) >> 21)
        {
            case TrianglesType:
                return Kind.Triangles;
            case QuadsType:
                return Kind.Quads;
            case ColorType:
                return Kind.Color;
            case LineStipple:
            case LineWidth:
                return Kind.Ignored;
            case FaceView:
            case FrameDraw:
            case FixedSize:
                startsBlock = (first & InnerMask) != 0;
                return Kind.Block;
            default:
                return Kind.NotShown;
        }
    }

    /// <summary>The colour a colour entity names: its first inner code is three times the colour's index.</summary>
    private static uint ColorOf(uint[] codes, int at, PrcRgbColor[] colors, ref bool missingColors)
    {
        if ((codes[at] & InnerMask) < 1)
        {
            missingColors = true;
            return DefaultColor;
        }

        uint index = codes[at + 2] / 3;
        if (index >= (uint)colors.Length)
        {
            missingColors = true;
            return DefaultColor;
        }

        PrcRgbColor c = colors[index];
        return Channel(c.Red) | (Channel(c.Green) << 8) | (Channel(c.Blue) << 16) | 0xFF000000;

        static uint Channel(double value) => double.IsFinite(value) ? (uint)Math.Round(Math.Clamp(value, 0, 1) * 255) : 0;
    }

    private static Scene3DException Invalid(string message) => new(Scene3DError.InvalidValue, message);
}
