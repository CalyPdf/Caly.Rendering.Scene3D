using System.Buffers;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>
/// Turns a compressed tessellation (<c>PRC_TYPE_TESS_3D_Compressed</c>) into a mesh: it is decoded
/// (<see cref="PrcCompressedDecoder"/>) and its triangles are given the material slots of their faces,
/// as <see cref="PrcMesher"/> does for a plain tessellation.
/// <para>
/// Styles are stored in the order the triangles were walked (ISO 14739-1, 7.8.9.6): one when the
/// first triangle of a face is met, or one for every triangle of a face that says it has several.
/// A style is an index into the file structure's styles plus one, as in a plain tessellation; behaviours
/// are per face.
/// </para>
/// </summary>
internal static class PrcCompressedMesher
{
    /// <summary>The behaviour of a face that has none stored: the bit that says it is shown.</summary>
    private const uint ShownByDefault = 1;

    /// <summary>
    /// The graphics of the triangles that have a style, each once, in the order they are met: entry
    /// <c>i</c> is material slot <c>i + 1</c> of the mesh (slot 0 is for triangles without a style).
    /// </summary>
    public static PrcFaceGraphics[] Slots(PrcTess3DCompressed tess)
    {
        int triangleCount = tess.TriangleCount;
        PrcFaceGraphics[] ofTriangles = ArrayPool<PrcFaceGraphics>.Shared.Rent(Math.Max(triangleCount, 1));
        try
        {
            TriangleGraphics(tess, ofTriangles);
            var seen = new HashSet<PrcFaceGraphics>();
            var slots = new List<PrcFaceGraphics>();
            for (int t = 0; t < triangleCount; t++)
            {
                if (ofTriangles[t].Style != 0 && seen.Add(ofTriangles[t]))
                {
                    slots.Add(ofTriangles[t]);
                }
            }

            return [.. slots];
        }
        finally
        {
            ArrayPool<PrcFaceGraphics>.Shared.Return(ofTriangles);
        }
    }

    /// <summary>The graphics of every triangle, in the order of the triangles.</summary>
    private static void TriangleGraphics(PrcTess3DCompressed tess, Span<PrcFaceGraphics> ofTriangles)
    {
        int[] faces = tess.TriangleFaces;
        ushort[] styles = tess.LineAttributes;
        byte[] behaviors = tess.Behaviors;
        PrcBits several = tess.IsMultipleLineAttributeOnFace;
        int faceCount = Math.Max(tess.FaceCount, 1);
        int[] styleOfFace = ArrayPool<int>.Shared.Rent(faceCount);
        try
        {
            styleOfFace.AsSpan(0, faceCount).Fill(-1);
            int cursor = 0;
            for (int t = 0; t < faces.Length; t++)
            {
                // A tessellation that says it has no faces still numbers them, and stores what is said per
                // face once: for all its triangles.
                int face = tess.HasFaces ? faces[t] : 0;
                if ((uint)face >= (uint)faceCount)
                {
                    ofTriangles[t] = default;
                    continue;
                }

                // A style missing from the array is no style: the triangle takes its owner's appearance.
                int style;
                if (face < several.Count && several[face])
                {
                    style = cursor < styles.Length ? styles[cursor++] : 0;
                }
                else
                {
                    if (styleOfFace[face] < 0)
                    {
                        styleOfFace[face] = cursor < styles.Length ? styles[cursor++] : 0;
                    }

                    style = styleOfFace[face];
                }

                // A tessellation without behaviours has nothing special to say of its faces: they are shown.
                ofTriangles[t] = new PrcFaceGraphics((uint)style, face < behaviors.Length ? behaviors[face] : ShownByDefault);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(styleOfFace);
        }
    }

    /// <summary>
    /// Adds the tessellation to the scene being built, as a mesh; false, with the reason, when it
    /// cannot be decoded (nothing is added then).
    /// </summary>
    /// <param name="builder">The scene being built.</param>
    /// <param name="tess">The tessellation.</param>
    /// <param name="name">The mesh's name, as an index of the builder's names.</param>
    /// <param name="mesh">The mesh.</param>
    /// <param name="reason">Why it could not be decoded.</param>
    /// <param name="cancellation">Stops the work.</param>
    /// <param name="hiddenSlots">
    /// Per material slot of <see cref="Slots"/> (entry <c>i</c> for slot <c>i + 1</c>), whether its
    /// triangles are left out of the mesh; empty for none.
    /// </param>
    public static bool TryAddMesh(SceneBuilder builder, PrcTess3DCompressed tess, int name, out PrcMesh mesh, out string? reason, CancellationToken cancellation = default, ReadOnlySpan<bool> hiddenSlots = default)
    {
        CheckLimits(builder.Limits, tess);

        PrcCompressedGeometry geometry;
        try
        {
            geometry = PrcCompressedDecoder.Decode(tess, cancellation);
        }
        catch (Scene3DException e) when (e.Error == Scene3DError.InvalidValue)
        {
            mesh = default;
            reason = e.Message;
            return false;
        }

        using (geometry)
        {
            reason = null;
            mesh = AddMesh(builder, tess, geometry, name, cancellation, hiddenSlots);
            return true;
        }
    }

    /// <summary>The most bytes decoding and meshing rent for one triangle (corner arrays, normals, texture, colours, working copies).</summary>
    private const long BytesPerTriangle = 96;

    /// <summary>The bytes decoding rents for one distinct point (three doubles).</summary>
    private const long BytesPerPoint = 24;

    /// <summary>
    /// Refuses a tessellation that alone would exceed the limits, from the counts the file gives, before the
    /// decoder rents anything: every stored point is a distinct vertex of the mesh, every triangle three indices.
    /// </summary>
    private static void CheckLimits(SceneLimits limits, PrcTess3DCompressed tess)
    {
        long triangles = tess.TriangleCount;
        long points = tess.Points.Length / 3;
        if (triangles * 3 > limits.MaxIndices)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {limits.MaxIndices} indices.");
        }

        if (points > limits.MaxVertices)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {limits.MaxVertices} vertices.");
        }

        if (triangles * BytesPerTriangle + points * BytesPerPoint > limits.MaxTotalBytes)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene needs more than {limits.MaxTotalBytes} bytes.");
        }
    }

    private static PrcMesh AddMesh(SceneBuilder builder, PrcTess3DCompressed tess, PrcCompressedGeometry geometry, int name, CancellationToken cancellation, ReadOnlySpan<bool> hiddenSlots)
    {
        int all = geometry.TriangleCount;
        if ((long)all * 3 > builder.Limits.MaxIndices)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {builder.Limits.MaxIndices} indices.");
        }

        PrcFaceGraphics[] slotStyles = Slots(tess);
        var slotOfStyle = new Dictionary<PrcFaceGraphics, int>(slotStyles.Length);
        for (int i = 0; i < slotStyles.Length; i++)
        {
            slotOfStyle.Add(slotStyles[i], i + 1);
        }

        PrcFaceGraphics[] ofTriangles = ArrayPool<PrcFaceGraphics>.Shared.Rent(Math.Max(all, 1));
        int[] cornerPoint = ArrayPool<int>.Shared.Rent(Math.Max(all * 3, 1));
        int[] cornerNormal = ArrayPool<int>.Shared.Rent(Math.Max(all * 3, 1));
        int[] cornerNone = ArrayPool<int>.Shared.Rent(Math.Max(all * 3, 1));
        bool textured = geometry.CornerTextures.Length > 0 && geometry.TextureCount > 0;
        int[] cornerTexture = textured ? ArrayPool<int>.Shared.Rent(Math.Max(all * 3, 1)) : cornerNone;
        bool colored = geometry.CornerColors.Length > 0;
        int[] cornerColor = colored ? ArrayPool<int>.Shared.Rent(Math.Max(all * 3, 1)) : cornerNone;
        int[] triangleSlot = ArrayPool<int>.Shared.Rent(Math.Max(all, 1));
        try
        {
            TriangleGraphics(tess, ofTriangles);

            // The triangles that are shown, with their slots.
            int triangleCount = 0;
            for (int t = 0; t < all; t++)
            {
                int slot = ofTriangles[t].Style != 0 && slotOfStyle.TryGetValue(ofTriangles[t], out int found) ? found : 0;
                if (slot > 0 && slot <= hiddenSlots.Length && hiddenSlots[slot - 1])
                {
                    continue;
                }

                geometry.Corners.AsSpan(t * 3, 3).CopyTo(cornerPoint.AsSpan(triangleCount * 3, 3));
                geometry.CornerNormals.AsSpan(t * 3, 3).CopyTo(cornerNormal.AsSpan(triangleCount * 3, 3));
                if (textured)
                {
                    geometry.CornerTextures.AsSpan(t * 3, 3).CopyTo(cornerTexture.AsSpan(triangleCount * 3, 3));
                }

                if (colored)
                {
                    geometry.CornerColors.AsSpan(t * 3, 3).CopyTo(cornerColor.AsSpan(triangleCount * 3, 3));
                }

                triangleSlot[triangleCount++] = slot;
            }

            if (triangleCount == 0 && !hiddenSlots.IsEmpty)
            {
                // Nothing of it is shown.
                return new PrcMesh(-1, slotStyles, 0, false);
            }

            cornerNone.AsSpan(0, triangleCount * 3).Fill(-1);
            if (geometry.TextureDropped)
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "prc-texture-coordinates-left-out", "A compressed mesh of the PRC model has texture coordinates that could not be told apart corner by corner; it is shown without them.");
            }

            if (geometry.ColorsDropped)
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "prc-point-colours-left-out", "A compressed mesh of the PRC model has colours on its points that do not come out even; it is shown without them.");
            }

            return PrcMesher.Emit(
                builder,
                name,
                SceneMeshFlags.HasNormals | (textured ? SceneMeshFlags.HasTexCoords : 0) | (colored ? SceneMeshFlags.HasColors : 0),
                slotStyles,
                geometry.Points.AsSpan(0, geometry.PointCount * 3),
                geometry.Normals.AsSpan(0, geometry.NormalCount * 3),
                textured ? geometry.TextureCoordinates.AsSpan(0, geometry.TextureCount * 2) : default,
                cornerPoint,
                cornerNormal,
                cornerTexture,
                cornerColor,
                triangleSlot,
                triangleCount,
                missingNormals: tess.MustRecalculateNormals,
                tess.MustRecalculateNormals ? tess.CreaseAngle : 0,
                anyColor: colored,
                cancellation);
        }
        finally
        {
            ArrayPool<PrcFaceGraphics>.Shared.Return(ofTriangles);
            ArrayPool<int>.Shared.Return(cornerPoint);
            ArrayPool<int>.Shared.Return(cornerNormal);
            ArrayPool<int>.Shared.Return(cornerNone);
            if (textured)
            {
                ArrayPool<int>.Shared.Return(cornerTexture);
            }

            if (colored)
            {
                ArrayPool<int>.Shared.Return(cornerColor);
            }

            ArrayPool<int>.Shared.Return(triangleSlot);
        }
    }
}
