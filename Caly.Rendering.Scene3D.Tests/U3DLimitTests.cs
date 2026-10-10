using System.Numerics;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The limits the reader consults that other test classes do not already cover (nodes and nesting:
/// U3DSceneGraphTests, U3DBlockWalkerTests; vertices, indices, meshes: U3DMeshTests; texture bytes:
/// U3DTextureTests), cancellation, and what reading allocates.
/// </summary>
public class U3DLimitTests(ITestOutputHelper output)
{
    private static Scene3DError ErrorOf(byte[] file, SceneLimits limits)
    {
        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(file, limits));
        Assert.Null(e.InnerException);
        return e.Error;
    }

    [Fact]
    public void NamesAreBoundedByTheStringLimit()
    {
        byte[] file = new TestU3D().GroupNode(new string('n', 40), ("", Matrix4x4.Identity)).ToArray();

        U3DReader.Read(file, SceneLimits.Default with { MaxStringLength = 40 }, TestContext.Current.CancellationToken).Dispose();
        Assert.Equal(Scene3DError.LimitExceeded, ErrorOf(file, SceneLimits.Default with { MaxStringLength = 39 }));
    }

    [Fact]
    public void MaterialsAreBoundedByTheLimit()
    {
        byte[] file = new TestU3D().Shader("A", "").Shader("B", "").ToArray();

        // The default material and the two shaders.
        U3DReader.Read(file, SceneLimits.Default with { MaxMaterials = 3 }, TestContext.Current.CancellationToken).Dispose();
        Assert.Equal(Scene3DError.LimitExceeded, ErrorOf(file, SceneLimits.Default with { MaxMaterials = 2 }));
    }

    [Fact]
    public void TexturesAreBoundedByTheLimit()
    {
        byte[] file = new TestU3D()
            .TextureDeclaration("A", 1, 1, 0x0E)
            .TextureDeclaration("B", 1, 1, 0x0E)
            .ToArray();

        U3DReader.Read(file, SceneLimits.Default with { MaxTextures = 2 }, TestContext.Current.CancellationToken).Dispose();
        Assert.Equal(Scene3DError.LimitExceeded, ErrorOf(file, SceneLimits.Default with { MaxTextures = 1 }));
    }

    [Fact]
    public void TheSceneIsBoundedByTheTotalByteLimit()
    {
        byte[] file = TestFiles.Scene("plane64x64_base.u3d");

        long needed;
        using (Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken))
        {
            needed = scene.GeometryBytes;
        }

        Assert.Equal(Scene3DError.LimitExceeded, ErrorOf(file, SceneLimits.Default with { MaxTotalBytes = needed / 2 }));
    }

    [Theory]
    [InlineData("box_base")]
    [InlineData("plane64x64_base")]
    [InlineData("shading_alpha")]
    public void APreCancelledReadStops(string name)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var e = Assert.ThrowsAny<OperationCanceledException>(() => U3DReader.Read(TestFiles.Scene(name + ".u3d"), null, cancellation.Token));
        Assert.Equal(cancellation.Token, e.CancellationToken);
    }

    /// <summary>
    /// Progressive detail is a mesh built up position by position before the scene has any of it: what it
    /// holds meanwhile is under the same limit of bytes as the scene.
    /// </summary>
    [Fact]
    public void ProgressiveDetailIsHeldWithinTheLimitOfBytes()
    {
        byte[] file = TestFiles.Scene("plane100x100.u3d");

        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(file, SceneLimits.Default with { MaxTotalBytes = 64 * 1024 }, TestContext.Current.CancellationToken));

        Assert.Equal(Scene3DError.LimitExceeded, e.Error);
        Assert.Contains("progressive detail", e.Message);
    }

    /// <summary>
    /// Reading allocates the scene's pooled buffers and little else: once the pools are warm, a read of a
    /// 4096-vertex mesh costs a small fixed amount of garbage, nothing per vertex or face.
    /// </summary>
    [Fact]
    public void ReadingAllocatesLittleBesidesTheScenesBuffers()
    {
        byte[] file = TestFiles.Scene("plane64x64_base.u3d");

        long geometryBytes = 0;
        for (int i = 0; i < 3; i++)
        {
            using Scene warm = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);
            geometryBytes = warm.GeometryBytes;
        }

        // The least of several reads: the shared array pool is also used (and trimmed) by tests running beside
        // this one, which now and then costs a read its warm buffers; the steady state is what is claimed.
        long allocated = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            scene.Dispose();
        }

        output.WriteLine($"file {file.Length} bytes, scene geometry {geometryBytes} bytes, allocated {allocated} bytes");
        Assert.True(allocated < Math.Max(geometryBytes / 10, 32 * 1024), $"allocated {allocated} bytes for {geometryBytes} bytes of geometry");
    }
}
