using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// <see cref="SceneLimits"/> stop a compressed tessellation before the decoder allocates for it: the proof is
/// that a tessellation which would otherwise be refused as undecodable is refused as too large instead.
/// </summary>
public class PrcCompressedLimitTests
{
    /// <summary>
    /// A tessellation of <paramref name="triangles"/> triangles whose data cannot be decoded (its neighbour
    /// flags are not followed by the points they need): decoding it throws <see cref="Scene3DError.InvalidValue"/>.
    /// </summary>
    private static PrcTess3DCompressed Undecodable(int triangles, int storedPoints) => new()
    {
        IsCalculated = false,
        HasFaces = false,
        Tolerance = 0.5,
        HasOrigin = true,
        OriginX = 0,
        OriginY = 0,
        OriginZ = 0,
        Points = new int[storedPoints * 3],
        EdgeStatus = new byte[triangles],
        TriangleFaces = new int[triangles],
        FaceCount = 1,
        PointIsReference = new PrcBits([], 0),
        PointReferences = [],
        MustRecalculateNormals = true,
        NormalIsReversed = new PrcBits(new uint[(triangles + 31) / 32], triangles),
        LineAttributes = [0],
    };

    private static Scene3DError? TryAdd(PrcTess3DCompressed tess, SceneLimits limits)
    {
        using var builder = new SceneBuilder(limits);
        int name = builder.AddName("mesh");
        try
        {
            PrcCompressedMesher.TryAddMesh(builder, tess, name, out _, out _);
        }
        catch (Scene3DException e)
        {
            return e.Error;
        }

        return null;
    }

    [Fact]
    public void WithoutLimitsInTheWayItIsTheDataThatIsRefused()
    {
        Assert.Null(TryAdd(Undecodable(1000, 10), new SceneLimits()));
    }

    [Fact]
    public void MaxIndicesStopsATessellationBeforeItIsDecoded()
    {
        Assert.Equal(Scene3DError.LimitExceeded, TryAdd(Undecodable(1000, 10), new SceneLimits { MaxIndices = 2999 }));
        Assert.Null(TryAdd(Undecodable(1000, 10), new SceneLimits { MaxIndices = 3000 }));
    }

    [Fact]
    public void MaxVerticesStopsATessellationBeforeItIsDecoded()
    {
        Assert.Equal(Scene3DError.LimitExceeded, TryAdd(Undecodable(1000, 600), new SceneLimits { MaxVertices = 599 }));
        Assert.Null(TryAdd(Undecodable(1000, 600), new SceneLimits { MaxVertices = 600 }));
    }

    [Fact]
    public void MaxTotalBytesStopsATessellationBeforeItIsDecoded()
    {
        Assert.Equal(Scene3DError.LimitExceeded, TryAdd(Undecodable(1000, 600), new SceneLimits { MaxTotalBytes = 1000 }));
        Assert.Null(TryAdd(Undecodable(1000, 600), new SceneLimits { MaxTotalBytes = 1L << 30 }));
    }
}
