using System.Buffers;
using Caly.Rendering.Scene3D.Internal;

namespace Caly.Rendering.Scene3D.U3D.Internal;

/// <summary>
/// Line sets and point sets (ECMA-363 9.6.2 and 9.6.3): a declaration, then continuation blocks that add
/// one position at a time, each with the lines (or points) that end there.
/// <para>
/// The standard leaves the predictions to the reader's imagination; what is here is what the Universal 3D
/// Sample Software does (CIFXLineSetDecoder, CIFXPointSetDecoder), its oddities included, since that is
/// what wrote the files: a line's colour is predicted as the SUM of the colours used at the split position
/// times their number, a point's as the colour of the point numbered like the split position, made a unit
/// vector; the signs of a colour difference are read in the context of a normal's.
/// </para>
/// </summary>
internal sealed partial class U3DDocument
{
    private sealed class SetDeclaration
    {
        public required string Name { get; init; }

        /// <summary>Line segments, two ends each; otherwise points.</summary>
        public required bool Lines { get; init; }

        public required uint PrimitiveCount { get; init; }

        public required uint PositionCount { get; init; }

        public required uint TexCoordCount { get; init; }

        public required MeshShading[] Shadings { get; init; }

        public required float PositionQuant { get; init; }

        public required float DiffuseQuant { get; init; }

        public int SceneMesh { get; set; } = NotBuilt;
    }

    private readonly Dictionary<string, SetDeclaration> _sets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<U3DBlock>> _setContinuations = new(StringComparer.Ordinal);

    private void ReadSetDeclaration(U3DBlock block, bool lines)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        reader.ReadU32(); // chain index
        reader.ReadU32(); // reserved
        uint primitiveCount = reader.ReadU32();
        uint positionCount = reader.ReadU32();
        reader.ReadU32(); // normal count
        reader.ReadU32(); // diffuse colour count
        reader.ReadU32(); // specular colour count
        uint texCoordCount = reader.ReadU32();

        uint shadingCount = reader.ReadU32();
        if (shadingCount > (uint)(reader.RemainingBytes / MinimumShadingBytes))
        {
            throw Truncated($"U3D {(lines ? "line" : "point")} set '{name}' declares {shadingCount} shading descriptions, more than its block can hold.");
        }

        var shadings = new MeshShading[shadingCount];
        for (int i = 0; i < shadings.Length; i++)
        {
            uint shadingAttributes = reader.ReadU32();
            uint layers = reader.ReadU32();
            if (layers > MaxTextureLayers)
            {
                throw Invalid($"U3D {(lines ? "line" : "point")} set '{name}' declares {layers} texture layers for one shading; the format has {MaxTextureLayers}.");
            }

            for (uint layer = 0; layer < layers; layer++)
            {
                reader.ReadU32(); // texture coordinate dimensions
            }

            reader.ReadU32(); // original shading id
            shadings[i] = new MeshShading((shadingAttributes & ShadingHasDiffuse) != 0, (shadingAttributes & ShadingHasSpecular) != 0, (int)layers);
        }

        reader.ReadU32(); // position quality factor
        reader.ReadU32(); // normal quality factor
        reader.ReadU32(); // texture coordinate quality factor
        float positionQuant = reader.ReadF32();
        reader.ReadF32(); // normal inverse quant
        reader.ReadF32(); // texture coordinate inverse quant
        float diffuseQuant = reader.ReadF32();

        // The specular inverse quant, three reserved values and the skeleton follow: nothing shown.
        _sets[name] = new SetDeclaration
        {
            Name = name,
            Lines = lines,
            PrimitiveCount = primitiveCount,
            PositionCount = positionCount,
            TexCoordCount = texCoordCount,
            Shadings = shadings,
            PositionQuant = positionQuant,
            DiffuseQuant = diffuseQuant
        };
    }

    /// <summary>Notes where a set's positions and primitives are; they are decoded if a node of the scene uses the set.</summary>
    private void ReadSetContinuation(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        if (!_setContinuations.TryGetValue(name, out List<U3DBlock>? blocks))
        {
            _setContinuations[name] = blocks = [];
        }

        blocks.Add(block);
    }

    private int GetSet(SceneBuilder builder, SetDeclaration declaration)
    {
        if (declaration.SceneMesh == NotBuilt)
        {
            declaration.SceneMesh = BuildSet(builder, declaration);
        }

        return declaration.SceneMesh;
    }

    private int BuildSet(SceneBuilder builder, SetDeclaration declaration)
    {
        string what = declaration.Lines ? "line" : "point";
        if (!_setContinuations.TryGetValue(declaration.Name, out List<U3DBlock>? blocks) || declaration.PrimitiveCount == 0 || declaration.PositionCount == 0)
        {
            return -1;
        }

        int ends = declaration.Lines ? 2 : 1;
        if (declaration.PositionCount > Math.Min(_limits.MaxVertices, int.MaxValue / 4) || declaration.PrimitiveCount * (long)ends > Math.Min(_limits.MaxIndices, int.MaxValue / 2))
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D {what} set '{declaration.Name}' declares {declaration.PositionCount} positions and {declaration.PrimitiveCount} {what}s, more than the limits allow.");
        }

        // A position costs at least five coded values and a primitive three, a few bits each at the very
        // least: what the blocks hold bounds what is allocated for a file that declares more than it has.
        long bits = 0;
        foreach (U3DBlock block in blocks)
        {
            bits += OpenBlock(block).RemainingBytes * 8L;
        }

        int maxPositions = (int)Math.Min(declaration.PositionCount, bits);
        int maxPrimitives = (int)Math.Min(declaration.PrimitiveCount, bits);
        MeshShading[] shadings = declaration.Shadings;

        float[] positions = ArrayPool<float>.Shared.Rent(Math.Max(maxPositions * 3, 3));
        int[] endPosition = ArrayPool<int>.Shared.Rent(Math.Max(maxPrimitives * ends, 2));
        int[] endColor = ArrayPool<int>.Shared.Rent(Math.Max(maxPrimitives * ends, 2));
        int[] shadingOf = ArrayPool<int>.Shared.Rent(Math.Max(maxPrimitives, 1));

        // Lines only: the ends that meet at each position, as lists threaded through nextEnd.
        int[] firstEnd = declaration.Lines ? ArrayPool<int>.Shared.Rent(Math.Max(maxPositions, 1)) : [];
        int[] nextEnd = declaration.Lines ? ArrayPool<int>.Shared.Rent(Math.Max(maxPrimitives * 2, 2)) : [];
        float[] colors = ArrayPool<float>.Shared.Rent(64);
        int positionCount = 0, primitiveCount = 0, colorCount = 0;
        bool anyColor = false;
        try
        {
            if (declaration.Lines)
            {
                firstEnd.AsSpan(0, Math.Max(maxPositions, 1)).Fill(-1);
            }

            Span<float> predicted = stackalloc float[4];
            foreach (U3DBlock block in blocks)
            {
                U3DBitReader reader = OpenBlock(block);
                reader.SkipString();
                reader.ReadU32(); // chain index
                uint start = reader.ReadU32(), end = reader.ReadU32();
                if (start != (uint)positionCount || end < start)
                {
                    // A block that does not carry on where the last one stopped cannot be placed.
                    builder.Report(SceneDiagnosticSeverity.Warning, "set-continuation-out-of-order", $"A block of U3D {what} set '{declaration.Name}' starts at position {start}, where {positionCount} was expected; it and what follows are left out.");
                    break;
                }

                if (end > (uint)maxPositions)
                {
                    throw Invalid($"U3D {what} set '{declaration.Name}' has a block ending at position {end}; the set declares {declaration.PositionCount}, or its blocks cannot hold as many.");
                }

                for (int current = (int)start; current < (int)end; current++)
                {
                    if ((current & (CancellationInterval - 1)) == 0)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                    }

                    uint split = reader.ReadCompressedU32(U3DConstants.StaticFull + (uint)Math.Max(current, 1));
                    byte signs = reader.ReadCompressedU8(U3DConstants.ContextPositionDiffSigns);
                    uint dx = reader.ReadCompressedU32(U3DConstants.ContextPositionDiffMagX);
                    uint dy = reader.ReadCompressedU32(U3DConstants.ContextPositionDiffMagY);
                    uint dz = reader.ReadCompressedU32(U3DConstants.ContextPositionDiffMagZ);
                    float px = 0, py = 0, pz = 0;
                    if (current > 0)
                    {
                        if (split >= (uint)current)
                        {
                            throw Invalid($"U3D {what} set '{declaration.Name}' places position {current} from position {split}, which does not exist yet.");
                        }

                        px = positions[split * 3];
                        py = positions[(split * 3) + 1];
                        pz = positions[(split * 3) + 2];
                    }

                    float quant = declaration.PositionQuant;
                    positions[current * 3] = px + ((signs & 1) != 0 ? -quant * dx : quant * dx);
                    positions[(current * 3) + 1] = py + ((signs & 2) != 0 ? -quant * dy : quant * dy);
                    positions[(current * 3) + 2] = pz + ((signs & 4) != 0 ? -quant * dz : quant * dz);
                    positionCount = current + 1;

                    // Normals: read past. Lines and points are drawn without light.
                    uint localNormals = reader.ReadCompressedU32(U3DConstants.ContextNumLocalNormals);
                    if (localNormals > (uint)reader.RemainingBytes * 8L + 64)
                    {
                        throw Truncated($"U3D {what} set '{declaration.Name}' gives position {current} {localNormals} normals, more than its block can hold.");
                    }

                    for (uint n = 0; n < localNormals; n++)
                    {
                        reader.ReadCompressedU8(U3DConstants.ContextNormalDiffSigns);
                        reader.ReadCompressedU32(U3DConstants.ContextNormalDiffMagX);
                        reader.ReadCompressedU32(U3DConstants.ContextNormalDiffMagY);
                        reader.ReadCompressedU32(U3DConstants.ContextNormalDiffMagZ);
                    }

                    uint local = reader.ReadCompressedU32(U3DConstants.ContextNumNewFaces);
                    if (local > (uint)(maxPrimitives - primitiveCount))
                    {
                        throw Invalid($"U3D {what} set '{declaration.Name}' has more {what}s than it declares, or than its blocks can hold.");
                    }

                    int firstOfUpdate = primitiveCount;
                    for (uint i = 0; i < local; i++)
                    {
                        uint shadingId = reader.ReadCompressedU32(U3DConstants.ContextSetShadingId);
                        if (shadingId >= (uint)shadings.Length)
                        {
                            throw Invalid($"U3D {what} set '{declaration.Name}' has a {what} with shading {shadingId}; the set declares {shadings.Length}.");
                        }

                        MeshShading shading = shadings[shadingId];
                        int at = primitiveCount * ends;
                        if (declaration.Lines)
                        {
                            uint other = reader.ReadCompressedU32(U3DConstants.StaticFull + (uint)current);
                            if (other >= (uint)current)
                            {
                                throw Invalid($"U3D line set '{declaration.Name}' joins position {current} to position {other}, which does not exist yet.");
                            }

                            endPosition[at] = (int)other;
                            endPosition[at + 1] = current;
                        }
                        else
                        {
                            endPosition[at] = current;
                        }

                        for (int e = 0; e < ends; e++)
                        {
                            reader.ReadCompressedU32(U3DConstants.ContextNormalLocalIndex);

                            int color = -1;
                            if (shading.Diffuse)
                            {
                                if (reader.ReadCompressedU8(U3DConstants.ContextVertColorDupType) == 0)
                                {
                                    byte colorSigns = reader.ReadCompressedU8(U3DConstants.ContextNormalDiffSigns);
                                    uint dr = reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagR);
                                    uint dg = reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagG);
                                    uint db = reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagB);
                                    uint da = reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagA);

                                    predicted.Clear();
                                    if (current > 0 && colorCount > 0 && primitiveCount > 0)
                                    {
                                        if (declaration.Lines)
                                        {
                                            PredictLineColor(predicted, firstEnd[split], nextEnd, endColor, colors);
                                        }
                                        else if (split < (uint)primitiveCount && endColor[split] >= 0)
                                        {
                                            PredictPointColor(predicted, colors.AsSpan(endColor[split] * 4, 4));
                                        }
                                    }

                                    if ((colorCount + 1) * 4 > colors.Length)
                                    {
                                        float[] larger = ArrayPool<float>.Shared.Rent(colors.Length * 2);
                                        colors.AsSpan(0, colorCount * 4).CopyTo(larger);
                                        ArrayPool<float>.Shared.Return(colors);
                                        colors = larger;
                                    }

                                    float colorQuant = declaration.DiffuseQuant;
                                    Span<float> rgba = colors.AsSpan(colorCount * 4, 4);
                                    rgba[0] = predicted[0] + ((colorSigns & 1) != 0 ? -colorQuant * dr : colorQuant * dr);
                                    rgba[1] = predicted[1] + ((colorSigns & 2) != 0 ? -colorQuant * dg : colorQuant * dg);
                                    rgba[2] = predicted[2] + ((colorSigns & 4) != 0 ? -colorQuant * db : colorQuant * db);
                                    rgba[3] = predicted[3] + ((colorSigns & 8) != 0 ? -colorQuant * da : colorQuant * da);
                                    colorCount++;
                                }

                                // New, or the one added last once more.
                                color = colorCount - 1;
                                anyColor |= color >= 0;
                            }

                            if (shading.Specular && reader.ReadCompressedU8(U3DConstants.ContextVertColorDupType) == 0)
                            {
                                reader.ReadCompressedU8(U3DConstants.ContextNormalDiffSigns);
                                reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagR);
                                reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagG);
                                reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagB);
                                reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagA);
                            }

                            if (declaration.TexCoordCount > 0)
                            {
                                for (int layer = 0; layer < shading.Layers; layer++)
                                {
                                    if (reader.ReadCompressedU8(U3DConstants.ContextTexCoordDupType) == 0)
                                    {
                                        reader.ReadCompressedU8(U3DConstants.ContextTexCoordDiffSigns);
                                        reader.ReadCompressedU32(U3DConstants.ContextTexCoordDiffMagU);
                                        reader.ReadCompressedU32(U3DConstants.ContextTexCoordDiffMagV);
                                        reader.ReadCompressedU32(U3DConstants.ContextTexCoordDiffMagS);
                                        reader.ReadCompressedU32(U3DConstants.ContextTexCoordDiffMagT);
                                    }
                                }
                            }

                            endColor[at + e] = color;
                        }

                        shadingOf[primitiveCount++] = (int)shadingId;
                    }

                    // The lines of this position take part in predictions from the next position on.
                    if (declaration.Lines)
                    {
                        for (int line = firstOfUpdate; line < primitiveCount; line++)
                        {
                            for (int e = 0; e < 2; e++)
                            {
                                int position = endPosition[(line * 2) + e];
                                nextEnd[(line * 2) + e] = firstEnd[position];
                                firstEnd[position] = (line * 2) + e;
                            }
                        }
                    }
                }
            }

            if (primitiveCount == 0)
            {
                return -1;
            }

            SceneMeshFlags flags = anyColor ? SceneMeshFlags.HasColors : SceneMeshFlags.None;
            var unifier = new VertexUnifier(positionCount, _limits.MaxVertices);
            try
            {
                builder.BeginMesh(builder.AddName(declaration.Name), flags, declaration.Lines ? SceneTopology.Lines : SceneTopology.Points);
                Span<uint> indices = builder.AllocateIndices(primitiveCount * ends);
                bool grouped = true;
                for (int i = 0; i < primitiveCount; i++)
                {
                    grouped &= i == 0 || shadingOf[i] >= shadingOf[i - 1];
                    for (int e = 0; e < ends; e++)
                    {
                        indices[(i * ends) + e] = (uint)unifier.GetOrAdd(endPosition[(i * ends) + e], -1, anyColor ? endColor[(i * ends) + e] : -1, -1);
                    }
                }

                int nonFinite = FillVertices(builder.AllocateVertices(unifier.Count), unifier.Tuples, positions.AsSpan(0, positionCount * 3), default, colors.AsSpan(0, colorCount * 4), default);
                AddParts(builder, indices, shadings.Length > 1 ? shadingOf.AsSpan(0, primitiveCount) : default, shadings.Length, grouped, ends);
                int mesh = builder.EndMesh();
                if (nonFinite > 0)
                {
                    builder.Report(SceneDiagnosticSeverity.Warning, "non-finite-geometry", $"U3D {what} set '{declaration.Name}' has {nonFinite} vertices with a NaN or infinite coordinate, set to zero.");
                }

                return mesh;
            }
            finally
            {
                unifier.Dispose();
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(positions);
            ArrayPool<int>.Shared.Return(endPosition);
            ArrayPool<int>.Shared.Return(endColor);
            ArrayPool<int>.Shared.Return(shadingOf);
            ArrayPool<float>.Shared.Return(colors);
            if (declaration.Lines)
            {
                ArrayPool<int>.Shared.Return(firstEnd);
                ArrayPool<int>.Shared.Return(nextEnd);
            }
        }
    }

    /// <summary>
    /// The colours the lines met so far use at the split position, added up, and the sum multiplied by
    /// their number (not divided: the sample software's arithmetic, kept because it wrote the files).
    /// </summary>
    private static void PredictLineColor(Span<float> predicted, int end, ReadOnlySpan<int> nextEnd, ReadOnlySpan<int> endColor, ReadOnlySpan<float> colors)
    {
        int count = 0;
        for (; end >= 0; end = nextEnd[end])
        {
            int color = endColor[end];
            if (color < 0)
            {
                continue;
            }

            count++;
            for (int k = 0; k < 4; k++)
            {
                predicted[k] += colors[(color * 4) + k];
            }
        }

        for (int k = 0; k < 4; k++)
        {
            predicted[k] *= count;
        }
    }

    /// <summary>The colour of the point numbered like the split position, its red, green and blue made a unit vector.</summary>
    private static void PredictPointColor(Span<float> predicted, ReadOnlySpan<float> color)
    {
        float length = MathF.Sqrt((color[0] * color[0]) + (color[1] * color[1]) + (color[2] * color[2]));
        if (length > 0)
        {
            predicted[0] = color[0] / length;
            predicted[1] = color[1] / length;
            predicted[2] = color[2] / length;
        }

        predicted[3] = color[3];
    }
}
