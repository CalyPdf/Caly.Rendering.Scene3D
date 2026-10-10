using Caly.Rendering.Scene3D.Prc.Container;

namespace Caly.Rendering.Scene3D.Prc;

/// <summary>
/// Reads PRC (ISO 14739-1) into a <see cref="Scene"/>: the assembly tree with names and placements, the
/// tessellated geometry of its parts, colours, materials and textures.
/// <para>
/// Tessellations are turned into meshes whether stored plainly or in the format's compressed form.
/// A compressed one is decoded by redoing the writer's own arithmetic, and is checked by being used
/// up exactly; one that does not decode is left without geometry: the scene then has a diagnostic
/// with the code <see cref="CompressedTessellationDiagnostic"/>, and <see cref="IsComplete"/> is false.
/// </para>
/// <para>
/// A solid that has a tessellation is drawn from it. A solid that has none is drawn from its exact geometry, stored compressed
/// (<c>PRC_TYPE_TOPO_BrepDataCompress</c>) or not (<c>PRC_TYPE_TOPO_BrepData</c>): the body is cut into triangles at a fixed chord
/// tolerance chosen when the file is read. A body that does not read and an item that names a body the file does not have leave
/// parts out: the code is <see cref="BrepDiagnostic"/>, and <see cref="IsComplete"/> is false. A face that cannot be cut is dropped
/// alone and counted (<c>prc-brep-face-not-tessellated</c>), a face on a kind of surface not drawn likewise
/// (<c>prc-brep-surface-not-supported</c>).
/// </para>
/// </summary>
public static class PrcReader
{
    /// <summary>Code of the diagnostic of a scene in which parts are missing because their compressed mesh could not be decoded.</summary>
    public const string CompressedTessellationDiagnostic = "prc-compressed-tessellation";

    /// <summary>Code of the diagnostic of a scene in which solids are missing because their compressed exact geometry could not be read.</summary>
    public const string BrepDiagnostic = "prc-brep-not-decoded";

    /// <summary>
    /// Code of the diagnostic of a scene in which solids were missing because their exact geometry is stored in the uncompressed form.
    /// No longer said: uncompressed bodies are drawn since phase B. Kept for callers that test for it.
    /// </summary>
    public const string UncompressedBrepDiagnostic = "prc-brep-uncompressed-not-shown";

    /// <summary>Whether the data starts like a PRC file.</summary>
    public static bool IsPrc(ReadOnlySpan<byte> data) => PrcContainer.IsPrc(data);

    /// <summary>
    /// Whether a scene this reader built shows everything the file has to show: false when parts are
    /// missing because their compressed mesh or their exact geometry could not be decoded or is not shown yet.
    /// </summary>
    public static bool IsComplete(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        foreach (SceneDiagnostic diagnostic in scene.Diagnostics)
        {
            if (diagnostic.Code is CompressedTessellationDiagnostic or BrepDiagnostic or UncompressedBrepDiagnostic)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a PRC file.</summary>
    /// <param name="data">The whole file. Texture images of the scene may be slices of it, which the scene then keeps alive.</param>
    /// <param name="limits">Bounds on what the file may make the reader allocate; the defaults when null.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="Scene3DException">The data is not PRC, is damaged, or exceeds the limits.</exception>
    /// <exception cref="OperationCanceledException">The reading was cancelled.</exception>
    public static Scene Read(ReadOnlyMemory<byte> data, SceneLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= SceneLimits.Default;
        using var builder = new SceneBuilder(limits);
        try
        {
            PrcFile file;
            using (PrcContainer container = PrcContainer.Open(data, limits))
            {
                file = PrcFileReader.Read(container, limits, trace: null, cancellationToken, PrcGeometryReading.WhenNeeded);
            }

            PrcSceneBuilder.Build(file, builder, cancellationToken);
            return builder.Build();
        }
        catch (Scene3DException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException e)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, "The PRC file needs more memory than is available.", e);
        }
        catch (Exception e)
        {
            // Last line of defence so that callers have one exception type to handle. Reaching it is a
            // defect in the reader: the fuzz tests fail on an inner exception of another kind.
            throw new Scene3DException(Scene3DError.InvalidValue, "The PRC data could not be read.", e);
        }
    }
}
