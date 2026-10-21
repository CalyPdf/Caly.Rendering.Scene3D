using System.Numerics;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>The meshes made of one markup tessellation; -1 where it has nothing of that kind.</summary>
/// <param name="Lines">A mesh of line segments: the polylines and the texts.</param>
/// <param name="Triangles">A mesh of triangles: the triangle lists and quads.</param>
/// <param name="EntitiesNotShown">Whether entities of a kind that is not drawn (symbols, patterns, pictures, blocks that turn what they hold to the screen) were left out.</param>
/// <param name="MissingColors">Whether an entity named a colour that the file does not have.</param>
internal readonly record struct PrcMarkupMeshes(int Lines, int Triangles, bool EntitiesNotShown, bool MissingColors);

/// <summary>
/// Turns a markup tessellation (<c>PRC_TYPE_TESS_Markup</c>, ISO 14739-1, 7.8.8) into meshes: the notes,
/// dimensions and frames of a model, as polylines, triangles and texts.
/// <para>
/// A markup is a list of entities. Each has two codes: its type with the number of inner codes it has,
/// and the number of doubles it takes from the coordinates (the entities use them one after the other).
/// Colours (type 11) hold until the next one; the colour is named by three times its index in the
/// globals' colours, as styles name them. A text (type 14) names one of the tessellation's strings and
/// gives the box it fills, a width and a height: it is drawn in a stroke font of this library
/// (<see cref="PrcStrokeFont"/>) stretched to that box, its capitals as high as the box, on its
/// baseline y = 0 (the file names its font by family and size only; the samples' notes are such texts).
/// Symbols, patterns and pictures are not drawn.
/// </para>
/// <para>
/// Blocks are a start, entities and an end. The start takes its own doubles (a matrix, a point); what it says the whole block
/// holds is not used (it can be wrong), the entities inside take their own.
/// A matrix block multiplies what it holds by its matrix (16 numbers, column after column), after the
/// matrix of any block around it: it is how texts are placed. Face-view, frame-draw and fixed-size blocks
/// place what they hold on the screen, which is not done: the whole block is left out.
/// </para>
/// </summary>
internal static class PrcMarkupMesher
{
    // The first code of an entity.
    private const uint IsMatrix = 0x08000000, IsExtraData = 0x04000000, ExtraDataType = 0x3E00000, InnerMask = 0xFFFFF;

    // Types of entities that are not polylines (the 'extra data' type of the first code).
    private const uint TrianglesType = 2, QuadsType = 3, FaceView = 6, FrameDraw = 7, FixedSize = 8, ColorType = 11, LineStipple = 12, FontType = 13, TextType = 14,
        LineWidth = 17;

    /// <summary>What a markup is drawn in when it names no colour: a dark grey, as annotations usually are.</summary>
    internal const uint DefaultColor = 0xFF202020;

    private enum Kind { Polyline, Triangles, Quads, Text, Color, MatrixBlock, ScreenBlock, Ignored, NotShown }

    /// <summary>
    /// An entity that is drawn, or a colour: where its codes start, where its doubles are, and the matrix that places it
    /// (an index of the matrices of the blocks; -1 for none).
    /// </summary>
    private readonly record struct Entity(Kind Kind, int Code, int Offset, int Doubles, int Transform);

    /// <summary>Builds the meshes; returns them as -1 where the markup has none.</summary>
    /// <exception cref="Scene3DException">The codes ask for more than the tessellation has.</exception>
    public static PrcMarkupMeshes Build(SceneBuilder builder, PrcTessMarkup tessellation, PrcRgbColor[] colors, string name, CancellationToken cancellation)
    {
        double[] coordinates = tessellation.TessellationCoordinates.Coordinates;
        uint[] codes = tessellation.CodeNumbers;
        string[] strings = tessellation.TextStrings;

        // What the codes say, checked against what there is.
        var entities = new List<Entity>();
        var transforms = new List<Matrix4x4>();
        var open = new Stack<int>();
        int transform = -1;
        bool notShown = false;
        long offset = 0;
        int leftOut = 0;
        long lineVertices = 0, lineIndices = 0, triangleVertices = 0, triangleIndices = 0;
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
            bool block = kind is Kind.MatrixBlock or Kind.ScreenBlock;

            // The inner codes of a block's start count the entities in the block, which follow on their own.
            long next = i + 2L + (block ? 0 : first & InnerMask);
            if (next > codes.Length)
            {
                throw Invalid("A markup of the PRC model ends in the middle of an entity.");
            }

            int at = i;
            i = (int)next;

            if (block)
            {
                if (!startsBlock)
                {
                    // The end of a block: a left-out one, or a matrix block, after which the matrix before it holds again.
                    if (leftOut > 0)
                    {
                        leftOut--;
                    }
                    else if (open.TryPop(out int previous))
                    {
                        transform = previous;
                    }

                    continue;
                }

                // A block's own numbers: a matrix's 16, the point a screen block is drawn at. What its start says it holds in all
                // is not used: a sample's frame-draw block says 69 where its tessellation has 54, which its entities take.
                int own = kind == Kind.MatrixBlock ? 16 : 3;
                if (offset + own > coordinates.Length)
                {
                    throw Invalid($"The entities of a markup of the PRC model use {offset + own} numbers, and it has {coordinates.Length}.");
                }

                if (leftOut == 0 && kind == Kind.MatrixBlock)
                {
                    Matrix4x4 matrix = MatrixAt(coordinates.AsSpan((int)offset, 16));
                    transforms.Add(transform < 0 ? matrix : matrix * transforms[transform]);
                    open.Push(transform);
                    transform = transforms.Count - 1;
                }
                else
                {
                    // A block that places what it holds on the screen, and all inside it: left out, its entities walked past.
                    leftOut++;
                    notShown = true;
                }

                offset += own;
                continue;
            }

            offset += doubles;
            if (offset > coordinates.Length)
            {
                throw Invalid($"The entities of a markup of the PRC model use {offset} numbers, and it has {coordinates.Length}.");
            }

            if (leftOut > 0)
            {
                continue;
            }

            switch (kind)
            {
                case Kind.Polyline:
                    if (doubles < 6)
                    {
                        continue;
                    }

                    lineVertices += doubles / 3;
                    lineIndices += ((doubles / 3) - 1) * 2;
                    break;
                case Kind.Triangles:
                    if (doubles < 9)
                    {
                        continue;
                    }

                    triangleVertices += doubles / 9 * 3;
                    triangleIndices += doubles / 9 * 3;
                    break;
                case Kind.Quads:
                    if (doubles < 12)
                    {
                        continue;
                    }

                    triangleVertices += doubles / 12 * 4;
                    triangleIndices += doubles / 12 * 6;
                    break;
                case Kind.Text:
                    if (TextOf(codes, at, strings) is not { Length: > 0 } text || doubles < 2)
                    {
                        continue;
                    }

                    foreach (char c in text)
                    {
                        foreach (float[] stroke in PrcStrokeFont.Of(c).Strokes)
                        {
                            lineVertices += stroke.Length / 2;
                            lineIndices += ((stroke.Length / 2) - 1) * 2;
                        }
                    }

                    break;
                case Kind.Color:
                    break;
                case Kind.NotShown:
                    notShown = true;
                    continue;
                default:
                    continue;
            }

            entities.Add(new Entity(kind, at, (int)(offset - doubles), (int)doubles, transform));
        }

        long most = builder.Limits.MaxIndices;
        if (lineIndices > most || triangleIndices > most)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {most} indices.");
        }

        bool missingColors = false;
        var drawing = new Drawing(entities, transforms, codes, coordinates, strings, colors);
        int lines = lineIndices > 0 ? Fill(builder, SceneTopology.Lines, (int)lineVertices, (int)lineIndices, name, drawing, ref missingColors) : -1;
        int triangles = triangleIndices > 0 ? Fill(builder, SceneTopology.Triangles, (int)triangleVertices, (int)triangleIndices, name, drawing, ref missingColors) : -1;
        return new PrcMarkupMeshes(lines, triangles, notShown, missingColors);
    }

    /// <summary>What a markup's entities are drawn from.</summary>
    private sealed record Drawing(List<Entity> Entities, List<Matrix4x4> Transforms, uint[] Codes, double[] Coordinates, string[] Strings, PrcRgbColor[] Colors);

    /// <summary>One kind of mesh: the vertices and indices of the entities of that kind, each in the colour that holds at it.</summary>
    private static int Fill(SceneBuilder builder, SceneTopology topology, int vertexCount, int indexCount, string name, Drawing drawing, ref bool missingColors)
    {
        builder.BeginMesh(builder.AddName(name), SceneMeshFlags.HasColors, topology);
        SceneVertexSpans vertices = builder.AllocateVertices(vertexCount);
        Span<uint> indices = builder.AllocateIndices(indexCount);
        uint color = DefaultColor;
        int vertex = 0, index = 0;
        foreach (Entity entity in drawing.Entities)
        {
            ReadOnlySpan<double> own = drawing.Coordinates.AsSpan(entity.Offset, entity.Doubles);
            Matrix4x4 place = entity.Transform < 0 ? Matrix4x4.Identity : drawing.Transforms[entity.Transform];
            switch (entity.Kind)
            {
                case Kind.Color:
                    color = ColorOf(drawing.Codes, entity.Code, drawing.Colors, ref missingColors);
                    break;

                case Kind.Polyline when topology == SceneTopology.Lines:
                    int points = own.Length / 3;
                    for (int p = 0; p < points; p++)
                    {
                        Put(vertices, vertex + p, Point(own.Slice(p * 3, 3)), place, color);
                        if (p > 0)
                        {
                            indices[index++] = (uint)(vertex + p - 1);
                            indices[index++] = (uint)(vertex + p);
                        }
                    }

                    vertex += points;
                    break;

                case Kind.Text when topology == SceneTopology.Lines:
                    string text = TextOf(drawing.Codes, entity.Code, drawing.Strings)!;
                    AddText(text, Finite(own[0]), Finite(own[1]), place, color, vertices, indices, ref vertex, ref index);
                    break;

                case Kind.Triangles when topology == SceneTopology.Triangles:
                    // A list of triangles holds a point more than whole triangles in every sample: it is dropped.
                    int corners = own.Length / 9 * 3;
                    for (int c = 0; c < corners; c++)
                    {
                        Put(vertices, vertex + c, Point(own.Slice(c * 3, 3)), place, color);
                        indices[index++] = (uint)(vertex + c);
                    }

                    vertex += corners;
                    break;

                case Kind.Quads when topology == SceneTopology.Triangles:
                    for (int q = 0, quads = own.Length / 12; q < quads; q++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            Put(vertices, vertex + c, Point(own.Slice((q * 12) + (c * 3), 3)), place, color);
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

    /// <summary>
    /// A text as strokes: its glyphs side by side from x = 0, stretched to <paramref name="width"/> (the font's own proportions when
    /// the file gives none), their capitals <paramref name="height"/> high above the baseline y = 0.
    /// </summary>
    private static void AddText(string text, float width, float height, Matrix4x4 place, uint color, in SceneVertexSpans vertices, Span<uint> indices, ref int vertex, ref int index)
    {
        float sy = height / PrcStrokeFont.CapHeight;
        float natural = PrcStrokeFont.Width(text);
        float sx = width > 0 && natural > 0 ? width / natural : sy;
        float pen = 0;
        foreach (char c in text)
        {
            PrcStrokeFont.Glyph glyph = PrcStrokeFont.Of(c);
            foreach (float[] stroke in glyph.Strokes)
            {
                int points = stroke.Length / 2;
                for (int p = 0; p < points; p++)
                {
                    Put(vertices, vertex + p, new Vector3((pen + stroke[p * 2]) * sx, stroke[(p * 2) + 1] * sy, 0), place, color);
                    if (p > 0)
                    {
                        indices[index++] = (uint)(vertex + p - 1);
                        indices[index++] = (uint)(vertex + p);
                    }
                }

                vertex += points;
            }

            pen += glyph.Advance;
        }
    }

    /// <summary>The string a text entity names by its first inner code; null when it names none that is there.</summary>
    private static string? TextOf(uint[] codes, int at, string[] strings) =>
        (codes[at] & InnerMask) >= 1 && codes[at + 2] < (uint)strings.Length ? strings[codes[at + 2]] : null;

    /// <summary>A matrix as a block stores it: column after column, the translation in the last; as a row-vector matrix.</summary>
    private static Matrix4x4 MatrixAt(ReadOnlySpan<double> m) => new(
        Finite(m[0]), Finite(m[1]), Finite(m[2]), Finite(m[3]),
        Finite(m[4]), Finite(m[5]), Finite(m[6]), Finite(m[7]),
        Finite(m[8]), Finite(m[9]), Finite(m[10]), Finite(m[11]),
        Finite(m[12]), Finite(m[13]), Finite(m[14]), Finite(m[15]));

    private static Vector3 Point(ReadOnlySpan<double> point) => new(Finite(point[0]), Finite(point[1]), Finite(point[2]));

    private static float Finite(double value) => double.IsFinite(value) ? (float)value : 0f;

    private static void Put(in SceneVertexSpans vertices, int vertex, Vector3 point, in Matrix4x4 place, uint color)
    {
        Vector3 p = Vector3.Transform(point, place);
        vertices.Positions[vertex * 3] = float.IsFinite(p.X) ? p.X : 0f;
        vertices.Positions[(vertex * 3) + 1] = float.IsFinite(p.Y) ? p.Y : 0f;
        vertices.Positions[(vertex * 3) + 2] = float.IsFinite(p.Z) ? p.Z : 0f;
        vertices.Colors[vertex] = color;
    }

    private static Kind KindOf(uint first, out bool startsBlock)
    {
        startsBlock = false;
        if ((first & IsMatrix) != 0)
        {
            // A block ends where an entity of its kind holds nothing.
            startsBlock = (first & InnerMask) != 0;
            return Kind.MatrixBlock;
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
            case TextType:
                return Kind.Text;
            case FontType:
            case LineStipple:
            case LineWidth:
                return Kind.Ignored;
            case FaceView:
            case FrameDraw:
            case FixedSize:
                startsBlock = (first & InnerMask) != 0;
                return Kind.ScreenBlock;
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
