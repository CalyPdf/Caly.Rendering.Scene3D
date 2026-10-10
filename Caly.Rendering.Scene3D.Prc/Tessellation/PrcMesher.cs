using System.Buffers;
using System.Numerics;
using Caly.Rendering.Scene3D.Internal;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>A tessellation as a mesh of a scene.</summary>
/// <param name="Mesh">The mesh's index in the scene being built; -1 when every triangle was left out.</param>
/// <param name="Slots">
/// The graphics of each material slot of the mesh but the first, as the faces give them. Slot 0 is for
/// the triangles whose faces name no style: they take the appearance of whatever shows the tessellation.
/// </param>
/// <param name="TriangleCount">Number of triangles.</param>
/// <param name="HasColors">Whether the mesh has a colour per vertex.</param>
internal readonly record struct PrcMesh(int Mesh, PrcFaceGraphics[] Slots, int TriangleCount, bool HasColors);

/// <summary>The graphics a face gives its triangles.</summary>
/// <param name="Style">The line style: its index in the file structure's styles, plus one.</param>
/// <param name="Behavior">The behaviour bits (as in the graphics of an entity), which say whether the style yields to the owner's and whether the face is shown.</param>
internal readonly record struct PrcFaceGraphics(uint Style, uint Behavior);

/// <summary>
/// Turns a plain tessellation (<c>PRC_TYPE_TESS_3D</c>) into a mesh: the triangles of every primitive of
/// every face, with vertices that carry position, normal, texture coordinate and colour together.
/// <para>
/// In the file a triangle corner is several indices (a normal, texture coordinates, a point), each into
/// its own array and multiplied by the size of that array's elements: 3 for points and normals, 2 for
/// texture coordinates (ISO 14739-1, 7.8.5; PDF Association issue #810). Normals are either stored or
/// to be calculated: then triangles that meet at a point at less than the crease angle share a normal
/// there.
/// </para>
/// </summary>
internal static class PrcMesher
{
    /// <summary>Most groups of triangles with a normal of their own at one point, when normals are calculated.</summary>
    private const int MaxNormalsPerPoint = 16;

    /// <summary>
    /// The graphics of the faces that have a style, each once, in the order of the faces: entry
    /// <c>i</c> is material slot <c>i + 1</c> of the mesh (slot 0 is for faces without a style).
    /// </summary>
    public static PrcFaceGraphics[] Slots(PrcTess3D tess)
    {
        var seen = new HashSet<PrcFaceGraphics>();
        var slots = new List<PrcFaceGraphics>();
        foreach (PrcTessFace face in tess.FaceTessellationData)
        {
            // One style for the whole face, or one per primitive (ISO 14739-1, 7.8.6.1).
            ReadOnlySpan<uint> styles = face.LineAttributes;
            int primitives = styles.Length <= 1 ? Math.Min(styles.Length, 1) : PrimitiveCount(face, styles.Length);
            for (int entity = 0; entity < primitives; entity++)
            {
                var graphics = new PrcFaceGraphics(styles[entity], face.Behavior);
                if (graphics.Style != 0 && seen.Add(graphics))
                {
                    slots.Add(graphics);
                }
            }
        }

        return [.. slots];
    }

    /// <summary>How many primitives a face has, counted no further than <paramref name="most"/>.</summary>
    private static int PrimitiveCount(PrcTessFace face, int most)
    {
        int count = 0;
        foreach (PrcPrimitive _ in PrcFaceLayout.Primitives(face.UsedEntitiesFlag, face.Triangulateddata, 0, normalsStored: false, TextureIndices(face)))
        {
            if (++count == most)
            {
                break;
            }
        }

        return count;
    }

    /// <summary>Adds the tessellation to the scene being built, as a mesh.</summary>
    /// <param name="builder">The scene being built.</param>
    /// <param name="tess">The tessellation.</param>
    /// <param name="name">The mesh's name, as an index of the builder's names.</param>
    /// <param name="cancellation">Stops the work.</param>
    /// <param name="hiddenSlots">
    /// Per material slot of <see cref="Slots"/> (entry <c>i</c> for slot <c>i + 1</c>), whether its
    /// triangles are left out of the mesh; empty for none.
    /// </param>
    public static PrcMesh AddMesh(SceneBuilder builder, PrcTess3D tess, int name, CancellationToken cancellation = default, ReadOnlySpan<bool> hiddenSlots = default)
    {
        ReadOnlySpan<double> coordinates = tess.TessellationCoordinates.Coordinates;
        ReadOnlySpan<double> normalCoordinates = tess.NormalCoordinates;
        ReadOnlySpan<double> textureCoordinates = tess.TextureCoordinates;
        ReadOnlySpan<uint> indices = tess.TriangulatedIndexArray;
        int pointCount = coordinates.Length / 3;
        int normalCount = normalCoordinates.Length / 3;
        int textureCount = textureCoordinates.Length / 2;

        // Normal indices are in the file unless normals are to be calculated (ISO 14739-1, 7.8.5.1).
        bool normalsStored = !tess.MustCalculateNormals;
        long[] faceStarts = FaceStarts(tess);

        // First pass: how many triangles, and whether every primitive lies within the index array.
        // Faces share the array without overlapping, so together they cannot use more indices than it has:
        // that bounds the work by the size of the data, whatever the faces claim.
        PrcFaceGraphics[] slotStyles = Slots(tess);
        var slotOfStyle = new Dictionary<PrcFaceGraphics, int>(slotStyles.Length);
        for (int i = 0; i < slotStyles.Length; i++)
        {
            slotOfStyle.Add(slotStyles[i], i + 1);
        }

        long triangleTotal = 0;
        long indicesUsed = 0;
        bool anyTexture = false, anyColor = false;
        for (int f = 0; f < tess.FaceTessellationData.Length; f++)
        {
            PrcTessFace face = tess.FaceTessellationData[f];
            int entity = 0;
            foreach (PrcPrimitive primitive in PrcFaceLayout.Primitives(face.UsedEntitiesFlag, face.Triangulateddata, faceStarts[f], normalsStored, TextureIndices(face)))
            {
                if (primitive.Start + primitive.IndexCount > indices.Length)
                {
                    throw Invalid("A face of a PRC tessellation refers to indices past the end of the tessellation's index array.");
                }

                indicesUsed += primitive.IndexCount;
                if (indicesUsed > indices.Length)
                {
                    throw Invalid("The faces of a PRC tessellation use more indices than the tessellation has.");
                }

                if (IsHidden(SlotOf(face, entity++, slotOfStyle), hiddenSlots))
                {
                    continue;
                }

                triangleTotal += primitive.TriangleCount;
                anyTexture |= primitive.TextureIndices > 0;
            }

            anyColor |= face.VertexColorData is { Colors.Length: > 0 };
        }

        if (triangleTotal * 3 > builder.Limits.MaxIndices)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {builder.Limits.MaxIndices} indices.");
        }

        int triangleCount = (int)triangleTotal;
        int cornerCount = triangleCount * 3;
        if (triangleCount == 0 && !hiddenSlots.IsEmpty)
        {
            // Nothing of it is shown.
            return new PrcMesh(-1, slotStyles, 0, anyColor);
        }

        bool useTexture = anyTexture && textureCount > 0;
        SceneMeshFlags flags = SceneMeshFlags.HasNormals | (useTexture ? SceneMeshFlags.HasTexCoords : 0) | (anyColor ? SceneMeshFlags.HasColors : 0);

        int[] cornerPoint = ArrayPool<int>.Shared.Rent(Math.Max(cornerCount, 1));
        int[] cornerNormal = ArrayPool<int>.Shared.Rent(Math.Max(cornerCount, 1));
        int[] cornerTexture = ArrayPool<int>.Shared.Rent(Math.Max(cornerCount, 1));
        int[] cornerColor = ArrayPool<int>.Shared.Rent(Math.Max(cornerCount, 1));
        int[] triangleSlot = ArrayPool<int>.Shared.Rent(Math.Max(triangleCount, 1));
        try
        {
            // Second pass: the corners of every triangle, each as indices into the tessellation's arrays.
            int corner = 0;
            int triangle = 0;
            bool missingNormals = !normalsStored;
            for (int f = 0; f < tess.FaceTessellationData.Length; f++)
            {
                PrcTessFace face = tess.FaceTessellationData[f];
                cancellation.ThrowIfCancellationRequested();
                ReadOnlySpan<uint> colors = face.VertexColorData is { } vertexColors ? vertexColors.Colors : default;
                int entity = 0;
                int pointOfFace = 0;
                foreach (PrcPrimitive primitive in PrcFaceLayout.Primitives(face.UsedEntitiesFlag, face.Triangulateddata, faceStarts[f], normalsStored, TextureIndices(face)))
                {
                    int slot = SlotOf(face, entity++, slotOfStyle);
                    if (IsHidden(slot, hiddenSlots))
                    {
                        // Its points still count: the face's vertex colours are in the order of all of them.
                        pointOfFace += primitive.PointCount;
                        continue;
                    }

                    int sharedNormal = primitive.SharedNormal < 0 ? -1 : Scaled(indices[(int)primitive.SharedNormal], 3, normalCount, "normal");
                    for (int t = 0; t < primitive.TriangleCount; t++)
                    {
                        // The three points of the triangle, in the order that keeps its front: a strip
                        // turns the other way at every second triangle.
                        int a, b, c;
                        switch (primitive.Shape)
                        {
                            case PrcPrimitiveShape.Fan:
                                (a, b, c) = (0, t + 1, t + 2);
                                break;
                            case PrcPrimitiveShape.Strip:
                                (a, b, c) = (t & 1) == 0 ? (t, t + 1, t + 2) : (t + 1, t, t + 2);
                                break;
                            default:
                                (a, b, c) = (0, 1, 2);
                                break;
                        }

                        triangleSlot[triangle++] = slot;
                        for (int k = 0; k < 3; k++)
                        {
                            int point = k == 0 ? a : k == 1 ? b : c;
                            int group = (int)(primitive.FirstPoint + (long)point * primitive.Stride);
                            int at = group;
                            int normal = sharedNormal;
                            if (primitive.NormalPerPoint)
                            {
                                // A normal of a whole triangle sits with the triangle's first point.
                                int normalAt = primitive.NormalPerTriangle ? (int)(primitive.FirstPoint + (long)t * primitive.Stride) : group;
                                normal = Scaled(indices[normalAt], 3, normalCount, "normal");
                                at++;
                            }

                            // Of several sets of texture coordinates the first is used. An index with its sign bit set (-2, and
                            // other small negative numbers, in some writers' files) names none: the corner has no coordinates.
                            int texture = primitive.TextureIndices > 0 && useTexture && !IsNegative(indices[at]) ? Scaled(indices[at], 2, textureCount, "texture coordinate") : -1;
                            at += primitive.TextureIndices;

                            cornerPoint[corner] = Scaled(indices[at], 3, pointCount, "point");
                            cornerNormal[corner] = normal;
                            cornerTexture[corner] = texture;
                            int colorAt = pointOfFace + point;
                            cornerColor[corner] = colorAt < colors.Length ? (int)colors[colorAt] : -1;
                            missingNormals |= normal < 0;
                            corner++;
                        }
                    }

                    pointOfFace += primitive.PointCount;
                }
            }

            return Emit(builder, name, flags, slotStyles, coordinates, normalCoordinates, textureCoordinates, cornerPoint, cornerNormal, cornerTexture, cornerColor, triangleSlot, triangleCount, missingNormals, tess.MustCalculateNormals ? tess.CreaseAngle : 0, anyColor, cancellation);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(cornerPoint);
            ArrayPool<int>.Shared.Return(cornerNormal);
            ArrayPool<int>.Shared.Return(cornerTexture);
            ArrayPool<int>.Shared.Return(cornerColor);
            ArrayPool<int>.Shared.Return(triangleSlot);
        }
    }

    /// <summary>
    /// Makes the mesh of triangles given corner by corner as indices into arrays of points, normals,
    /// texture coordinates and colours: corners that agree on every index are one vertex, corners
    /// without a normal are given a calculated one, and the triangles are grouped by material slot.
    /// </summary>
    /// <remarks>
    /// A corner's normal index is -1 for a normal to calculate (the array is then written to);
    /// <c>creaseAngle</c> is the angle within which triangles share a calculated normal at a point.
    /// </remarks>
    internal static PrcMesh Emit(
        SceneBuilder builder,
        int name,
        SceneMeshFlags flags,
        PrcFaceGraphics[] slotStyles,
        ReadOnlySpan<double> coordinates,
        ReadOnlySpan<double> normalCoordinates,
        ReadOnlySpan<double> textureCoordinates,
        int[] cornerPoint,
        int[] cornerNormal,
        int[] cornerTexture,
        int[] cornerColor,
        int[] triangleSlot,
        int triangleCount,
        bool missingNormals,
        double creaseAngle,
        bool anyColor,
        CancellationToken cancellation)
    {
        int cornerCount = triangleCount * 3;
        int pointCount = coordinates.Length / 3;
        int normalCount = normalCoordinates.Length / 3;
        float[]? calculated = null;
        var unifier = new VertexUnifier(Math.Max(pointCount, normalCount), builder.Limits.MaxVertices);
        try
        {
            if (missingNormals)
            {
                calculated = CalculateNormals(coordinates, cornerPoint.AsSpan(0, cornerCount), cornerNormal.AsSpan(0, cornerCount), normalCount, pointCount, creaseAngle, cancellation);
            }

            // Corners that agree on every index are one vertex; triangles are grouped by material slot.
            builder.BeginMesh(name, flags);
            Span<uint> output = builder.AllocateIndices(cornerCount);
            int slotCount = slotStyles.Length + 1;
            Span<int> slotStart = slotCount <= 64 ? stackalloc int[slotCount + 1] : new int[slotCount + 1];
            slotStart.Clear();
            for (int t = 0; t < triangleCount; t++)
            {
                slotStart[triangleSlot[t] + 1] += 3;
            }

            for (int s = 0; s < slotCount; s++)
            {
                slotStart[s + 1] += slotStart[s];
            }

            Span<int> slotCursor = slotCount <= 64 ? stackalloc int[slotCount] : new int[slotCount];
            slotStart[..slotCount].CopyTo(slotCursor);
            for (int t = 0; t < triangleCount; t++)
            {
                ref int cursor = ref slotCursor[triangleSlot[t]];
                for (int k = 0; k < 3; k++)
                {
                    int c = t * 3 + k;
                    output[cursor++] = (uint)unifier.GetOrAdd(cornerPoint[c], cornerNormal[c], cornerColor[c], cornerTexture[c]);
                }
            }

            int nonFinite = FillVertices(builder.AllocateVertices(unifier.Count), unifier.Tuples, coordinates, normalCoordinates, calculated, normalCount, textureCoordinates);
            for (int s = 0; s < slotCount; s++)
            {
                if (slotStart[s + 1] > slotStart[s])
                {
                    builder.AddPart(slotStart[s + 1] - slotStart[s], s);
                }
            }

            int mesh = builder.EndMesh();
            if (nonFinite > 0)
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "prc-non-finite-vertex", $"A PRC tessellation has {nonFinite} vertices with values that are not numbers; they were set to zero.");
            }

            return new PrcMesh(mesh, slotStyles, triangleCount, anyColor);
        }
        finally
        {
            unifier.Dispose();
            if (calculated is not null)
            {
                ArrayPool<float>.Shared.Return(calculated);
            }
        }
    }

    /// <summary>The material slot of a primitive of a face: one style for the whole face, or one per primitive (ISO 14739-1, 7.8.6.1).</summary>
    private static int SlotOf(PrcTessFace face, int entity, Dictionary<PrcFaceGraphics, int> slotOfStyle)
    {
        uint[] styles = face.LineAttributes;
        uint style = styles.Length == 0 ? 0 : styles.Length == 1 ? styles[0] : entity < styles.Length ? styles[entity] : 0;
        return style != 0 && slotOfStyle.TryGetValue(new PrcFaceGraphics(style, face.Behavior), out int slot) ? slot : 0;
    }

    private static bool IsHidden(int slot, ReadOnlySpan<bool> hiddenSlots) => slot > 0 && slot <= hiddenSlots.Length && hiddenSlots[slot - 1];

    private static int TextureIndices(PrcTessFace face) => (int)Math.Min(face.NumberOfTexturedCoordinateIndices, 1 << 20);

    /// <summary>
    /// Where each face's indices start in the tessellation's index array. A face says so itself, but
    /// "as if normal indices were stored" (ISO 14739-1, 7.8.5.1): when normals are to be calculated
    /// the array has no normal indices and every face starts earlier than it says. The faces then
    /// follow each other in the order of the starts they give.
    /// </summary>
    internal static long[] FaceStarts(PrcTess3D tess)
    {
        PrcTessFace[] faces = tess.FaceTessellationData;
        var starts = new long[faces.Length];
        if (!tess.MustCalculateNormals)
        {
            for (int f = 0; f < faces.Length; f++)
            {
                starts[f] = faces[f].StartTriangulated;
            }

            return starts;
        }

        // Usually the faces are listed in that order already.
        var order = new int[faces.Length];
        bool sorted = true;
        for (int f = 0; f < faces.Length; f++)
        {
            order[f] = f;
            sorted &= f == 0 || faces[f - 1].StartTriangulated <= faces[f].StartTriangulated;
        }

        if (!sorted)
        {
            Array.Sort(order, (x, y) => faces[x].StartTriangulated != faces[y].StartTriangulated
                ? faces[x].StartTriangulated.CompareTo(faces[y].StartTriangulated)
                : x.CompareTo(y));
        }

        long cursor = 0;
        long limit = tess.TriangulatedIndexArray.Length;
        foreach (int f in order)
        {
            starts[f] = cursor;

            // Past the end of the array there is nothing to count: the face is refused where it is used.
            foreach (PrcPrimitive primitive in PrcFaceLayout.Primitives(faces[f].UsedEntitiesFlag, faces[f].Triangulateddata, 0, normalsStored: false, TextureIndices(faces[f])))
            {
                cursor += primitive.IndexCount;
                if (cursor > limit)
                {
                    break;
                }
            }
        }

        return starts;
    }

    private static bool IsNegative(uint index) => (index & 0x80000000u) != 0;

    /// <summary>An index of the file, which counts the values of an array, as an index of its elements.</summary>
    private static int Scaled(uint index, uint size, int count, string what)
    {
        uint element = index / size;
        if (index % size != 0 || element >= (uint)count)
        {
            throw Invalid($"A PRC tessellation has a {what} index ({index}) that is not one of its {count} {what}s.");
        }

        return (int)element;
    }

    /// <summary>
    /// Gives a normal to every corner that has none. At each point the triangles are sorted into groups
    /// whose normals are within the crease angle of the group's first; a group shares one normal, the
    /// sum of its triangles' normals (larger triangles count more). A corner's normal index becomes
    /// <paramref name="normalCount"/> plus the number of its group; the result holds three floats per group.
    /// </summary>
    private static float[] CalculateNormals(ReadOnlySpan<double> coordinates, ReadOnlySpan<int> cornerPoint, Span<int> cornerNormal, int normalCount, int pointCount, double creaseAngle, CancellationToken cancellation)
    {
        int cornerCount = cornerPoint.Length;
        int triangleCount = cornerCount / 3;

        // An angle the file may give in degrees: nothing sensible is above a half turn in radians.
        double angle = double.IsFinite(creaseAngle) && creaseAngle > 0 ? creaseAngle : 0;
        if (angle > Math.PI)
        {
            angle *= Math.PI / 180;
        }

        float cosine = (float)Math.Cos(Math.Min(angle, Math.PI));

        float[] triangleNormals = ArrayPool<float>.Shared.Rent(Math.Max(cornerCount, 1));   // area-weighted, three per triangle
        float[] unitNormals = ArrayPool<float>.Shared.Rent(Math.Max(cornerCount, 1));
        int[] first = ArrayPool<int>.Shared.Rent(pointCount + 2);
        int[] cornersOfPoint = ArrayPool<int>.Shared.Rent(Math.Max(cornerCount, 1));
        float[] result = ArrayPool<float>.Shared.Rent(Math.Max(cornerCount, 1) * 3);
        try
        {
            for (int t = 0; t < triangleCount; t++)
            {
                Vector3 p0 = Point(coordinates, cornerPoint[t * 3]);
                Vector3 n = Vector3.Cross(Point(coordinates, cornerPoint[t * 3 + 1]) - p0, Point(coordinates, cornerPoint[t * 3 + 2]) - p0);
                float length = n.Length();
                Vector3 unit = length > 0 && float.IsFinite(length) ? n / length : Vector3.Zero;
                if (unit == Vector3.Zero)
                {
                    n = Vector3.Zero;
                }

                n.CopyTo(triangleNormals.AsSpan(t * 3, 3));
                unit.CopyTo(unitNormals.AsSpan(t * 3, 3));
            }

            // The corners at each point, by a counting sort.
            Span<int> start = first.AsSpan(0, pointCount + 2);
            start.Clear();
            for (int c = 0; c < cornerCount; c++)
            {
                start[cornerPoint[c] + 2]++;
            }

            for (int p = 0; p < pointCount; p++)
            {
                start[p + 2] += start[p + 1];
            }

            for (int c = 0; c < cornerCount; c++)
            {
                cornersOfPoint[start[cornerPoint[c] + 1]++] = c;
            }

            int groups = 0;
            Span<Vector3> leader = stackalloc Vector3[MaxNormalsPerPoint];
            Span<Vector3> sum = stackalloc Vector3[MaxNormalsPerPoint];
            Span<int> groupId = stackalloc int[MaxNormalsPerPoint];
            for (int p = 0; p < pointCount; p++)
            {
                if ((p & 0xFFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                int from = start[p], to = start[p + 1];
                int local = 0;
                for (int i = from; i < to; i++)
                {
                    int c = cornersOfPoint[i];
                    if (cornerNormal[c] >= 0)
                    {
                        continue;
                    }

                    int t = c / 3;
                    var unit = new Vector3(unitNormals.AsSpan(t * 3, 3));
                    int best = -1;
                    float bestDot = -2;
                    for (int g = 0; g < local; g++)
                    {
                        float dot = Vector3.Dot(leader[g], unit);
                        if (dot > bestDot)
                        {
                            bestDot = dot;
                            best = g;
                        }
                    }

                    // A new group, unless the triangle is within the crease angle of one (or there are
                    // already as many groups as a point is given).
                    if (best < 0 || (bestDot < cosine && local < MaxNormalsPerPoint))
                    {
                        best = local++;
                        leader[best] = unit;
                        sum[best] = Vector3.Zero;
                        groupId[best] = groups++;
                    }

                    sum[best] += new Vector3(triangleNormals.AsSpan(t * 3, 3));
                    cornerNormal[c] = normalCount + groupId[best];
                }

                for (int g = 0; g < local; g++)
                {
                    float length = sum[g].Length();
                    Vector3 normal = length > 0 && float.IsFinite(length) ? sum[g] / length : leader[g];
                    normal.CopyTo(result.AsSpan(groupId[g] * 3, 3));
                }
            }

            return result;
        }
        catch
        {
            ArrayPool<float>.Shared.Return(result);
            throw;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(triangleNormals);
            ArrayPool<float>.Shared.Return(unitNormals);
            ArrayPool<int>.Shared.Return(first);
            ArrayPool<int>.Shared.Return(cornersOfPoint);
        }
    }

    private static Vector3 Point(ReadOnlySpan<double> coordinates, int point) =>
        new((float)coordinates[point * 3], (float)coordinates[point * 3 + 1], (float)coordinates[point * 3 + 2]);

    private static int FillVertices(SceneVertexSpans vertices, ReadOnlySpan<int> tuples, ReadOnlySpan<double> coordinates, ReadOnlySpan<double> normalCoordinates, float[]? calculated, int normalCount, ReadOnlySpan<double> textureCoordinates)
    {
        int count = tuples.Length / VertexUnifier.Stride;
        int nonFinite = 0;
        for (int v = 0; v < count; v++)
        {
            ReadOnlySpan<int> tuple = tuples.Slice(v * VertexUnifier.Stride, VertexUnifier.Stride);
            bool finite = Copy(coordinates.Slice(tuple[0] * 3, 3), vertices.Positions.Slice(v * 3, 3));

            Span<float> normal = vertices.Normals.Slice(v * 3, 3);
            if (tuple[1] < 0)
            {
                normal.Clear();
            }
            else if (tuple[1] < normalCount)
            {
                finite &= Copy(normalCoordinates.Slice(tuple[1] * 3, 3), normal);
            }
            else
            {
                calculated.AsSpan((tuple[1] - normalCount) * 3, 3).CopyTo(normal);
            }

            if (!vertices.Colors.IsEmpty)
            {
                // A point without a colour is white: it shows its material unchanged.
                vertices.Colors[v] = (uint)tuple[2];
            }

            if (!vertices.TexCoords.IsEmpty)
            {
                Span<float> uv = vertices.TexCoords.Slice(v * 2, 2);
                if (tuple[3] < 0)
                {
                    uv.Clear();
                }
                else
                {
                    finite &= Copy(textureCoordinates.Slice(tuple[3] * 2, 2), uv);
                }
            }

            nonFinite += finite ? 0 : 1;
        }

        return nonFinite;
    }

    private static bool Copy(ReadOnlySpan<double> source, Span<float> destination)
    {
        bool finite = true;
        for (int i = 0; i < source.Length; i++)
        {
            float value = (float)source[i];
            finite &= float.IsFinite(value);
            destination[i] = value;
        }

        if (!finite)
        {
            destination.Clear();
        }

        return finite;
    }

    private static Scene3DException Invalid(string message) => new(Scene3DError.InvalidValue, message);
}
