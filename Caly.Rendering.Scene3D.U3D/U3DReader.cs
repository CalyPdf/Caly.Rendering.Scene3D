using Caly.Rendering.Scene3D.U3D.Internal;

namespace Caly.Rendering.Scene3D.U3D;

/// <summary>
/// Reads Universal 3D (ECMA-363) files into a <see cref="Scene"/>.
/// <para>
/// Covered: the scene graph (group, model, light and view nodes), lights, cameras, materials, shaders,
/// textures (carried as their encoded images), meshes at full progressive detail, point and line sets,
/// node and skeletal animation, and Acrobat 3D's vendor mesh (<c>RHAdobeMeshResource</c>).
/// Subdivision, CLOD-level and glyph modifiers, other extensions and file references are skipped and
/// listed in <see cref="Scene.Diagnostics"/>. External files and URLs are never opened.
/// </para>
/// </summary>
public static class U3DReader
{
    /// <summary>Whether <paramref name="data"/> starts with the U3D file signature.</summary>
    public static bool IsU3D(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 && data[0] == (byte)'U' && data[1] == (byte)'3' && data[2] == (byte)'D' && data[3] == 0;
    }

    /// <summary>
    /// Reads a U3D file.
    /// </summary>
    /// <param name="data">
    /// The whole file. The returned scene may keep references into this memory (texture images), so it
    /// must not be modified while the scene is in use.
    /// </param>
    /// <param name="limits">Bounds on what the reader will allocate; the defaults when omitted.</param>
    /// <param name="cancellationToken">Checked between blocks and inside large ones.</param>
    /// <returns>The scene. Dispose it to return its pooled buffers.</returns>
    /// <exception cref="Scene3DException">The data is not a U3D file the reader can turn into a scene.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static Scene Read(ReadOnlyMemory<byte> data, SceneLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= SceneLimits.Default;
        using var builder = new SceneBuilder(limits);

        U3DDocument? document = null;
        try
        {
            document = new U3DDocument(data, limits, cancellationToken);
            document.Load();
            document.BuildScene(builder);
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
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Last line of defence so that callers have one exception type to handle. Reaching it is a
            // defect in the reader: the fuzz tests fail on an inner exception.
            throw new Scene3DException(Scene3DError.InvalidValue, "The U3D data could not be read.", e);
        }
        finally
        {
            // The histograms of the last block read go back to their pool.
            document?.Release();
        }
    }
}
