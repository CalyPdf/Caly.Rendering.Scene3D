using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Damaged compressed tessellations: seeds that cover each branch of the decoder (calculated normals,
/// stored normals, a planar face, texture coordinates, colours on points) are mutated field by field with a
/// seeded generator, and every result must be either a deliberate <see cref="Scene3DException"/> or a
/// geometry whose indices all lie inside its own arrays and whose mesh the scene builder takes. Nothing
/// else (an index out of range, an unbounded loop, a crash) is allowed. The damaged copies of real
/// tessellations are in <c>PrcCompressedCorpusTests</c>.
/// </summary>
public class PrcCompressedFuzzTests
{
    /// <summary>Mutations tried per seed; SCENE3D_FUZZ_MUTATIONS raises it for a deeper run.</summary>
    private static readonly int Mutations = int.TryParse(Environment.GetEnvironmentVariable("SCENE3D_FUZZ_MUTATIONS"), out int n) && n > 0 ? n * 20 : 3000;

    private static readonly SceneLimits Limits = SceneLimits.Default with { MaxTotalBytes = 64L << 20, MaxVertices = 1_000_000, MaxIndices = 3_000_000 };

    private static readonly int[] Extremes = [0, 1, -1, 2, 255, 1023, short.MaxValue, int.MaxValue, int.MinValue, 1 << 20];

    public static TheoryData<string> SeedNames() => ["calculated", "calculated-references", "textured", "coloured", "stored", "stored-planar", "stored-reversed"];

    private static PrcTess3DCompressed Seed(string name) => name switch
    {
        "calculated" => PrcCompressedDecoderTests.Calculated("300", "00000", [], [0, 0, 0, 8, 0, 0, 0, 6, 0, 0, -4, 0, 0, -4, 0], "000"),
        "calculated-references" => PrcCompressedDecoderTests.Calculated("300", "00001", [3], [0, 0, 0, 8, 0, 0, 0, 6, 0, 0, -4, 0], "000"),
        "textured" => PrcCompressedDecoderTests.Textured("000100110", [0, 0, 1], 6),
        "coloured" => PrcCompressedDecoderTests.Coloured(6),
        "stored" => PrcCompressedDecoderTests.Stored("000000000000", [0, 1023, 0, 1023, 0, 1023]),
        "stored-planar" => PrcCompressedDecoderTests.Stored("0000", [0, 1023], planar: true),
        "stored-reversed" => PrcCompressedDecoderTests.Stored("010001000100", [0, 1023, 0, 1023, 0, 1023]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static PrcBits Flip(PrcBits bits, Random random)
    {
        int count = bits.Count;
        var words = new uint[(count + 31) / 32];
        for (int i = 0; i < count; i++)
        {
            if (bits[i])
            {
                words[i >> 5] |= 0x80000000u >> (i & 31);
            }
        }

        switch (random.Next(3))
        {
            case 0 when count > 0:
                words[random.Next(count) >> 5] ^= 0x80000000u >> random.Next(32);
                return new PrcBits(words, count);
            case 1 when count > 0:
                return new PrcBits(words, random.Next(count)); // cut short
            default:
                Array.Resize(ref words, words.Length + 2);
                return new PrcBits(words, count + random.Next(1, 60)); // padded with zero bits
        }
    }

    private static T[] Resize<T>(T[] source, Random random)
    {
        int length = random.Next(3) switch { 0 => Math.Max(0, source.Length - random.Next(1, 4)), 1 => source.Length + random.Next(1, 4), _ => 0 };
        var result = new T[length];
        Array.Copy(source, result, Math.Min(length, source.Length));
        return result;
    }

    private static void Mutate(PrcTess3DCompressed t, Random random)
    {
        switch (random.Next(14))
        {
            case 0:
                t.Points = t.Points.Length > 0 ? Poke(t.Points, random) : t.Points;
                break;
            case 1:
                t.Points = Resize(t.Points, random);
                break;
            case 2:
                t.EdgeStatus = t.EdgeStatus.Length > 0 ? Poke(t.EdgeStatus, random, v => (byte)v) : t.EdgeStatus;
                break;
            case 3:
                t.EdgeStatus = Resize(t.EdgeStatus, random);
                break;
            case 4:
                t.PointIsReference = Flip(t.PointIsReference, random);
                break;
            case 5:
                t.PointReferences = t.PointReferences.Length > 0 ? Poke(t.PointReferences, random) : [Extremes[random.Next(Extremes.Length)]];
                break;
            case 6:
                t.NormalIsReversed = Flip(t.NormalIsReversed, random);
                break;
            case 7:
                t.NormalBits = Flip(t.NormalBits, random);
                break;
            case 8:
                t.NormalAngles = t.NormalAngles.Length > 0 ? Poke(t.NormalAngles, random, v => (ushort)v) : t.NormalAngles;
                break;
            case 9:
                t.NormalAngleBits = random.Next(-2, 20);
                break;
            case 10:
                t.Tolerance = random.Next(5) switch { 0 => 0, 1 => -1, 2 => double.NaN, 3 => double.PositiveInfinity, _ => 1e300 };
                break;
            case 11:
                t.TriangleFaces = Resize(t.TriangleFaces, random);
                break;
            case 12:
                t.PointColors = Resize(t.PointColors, random);
                break;
            default:
                if (t.Texture is { } texture)
                {
                    t.Texture = new PrcCompressedTexture { Bits = Flip(texture.Bits, random), References = Resize(texture.References, random), Parameters = Resize(texture.Parameters, random) };
                }
                else
                {
                    t.FaceCount = random.Next(-1, 6);
                }

                break;
        }
    }

    private static int[] Poke(int[] source, Random random) => Poke(source, random, v => v);

    private static T[] Poke<T>(T[] source, Random random, Func<int, T> convert)
    {
        T[] copy = (T[])source.Clone();
        copy[random.Next(copy.Length)] = convert(random.Next(3) == 0 ? Extremes[random.Next(Extremes.Length)] : random.Next(-16, 17));
        return copy;
    }

    /// <summary>Everything the geometry says points inside what it has.</summary>
    private static void AssertConsistent(PrcCompressedGeometry g)
    {
        Assert.InRange(g.TriangleCount, 0, g.Corners.Length / 3);
        for (int i = 0; i < g.TriangleCount * 3; i++)
        {
            Assert.InRange(g.Corners[i], 0, g.PointCount - 1);
            if (g.CornerNormals.Length > 0)
            {
                Assert.InRange(g.CornerNormals[i], -1, g.NormalCount - 1);
            }

            if (g.CornerTextures.Length > 0)
            {
                Assert.InRange(g.CornerTextures[i], -1, g.TextureCount - 1);
            }
        }
    }

    [Theory]
    [MemberData(nameof(SeedNames))]
    public void DamagedTessellationsAreRefusedOrConsistent(string seedName)
    {
        int refused = 0, decoded = 0;
        for (int mutation = 0; mutation < Mutations; mutation++)
        {
            var random = new Random(unchecked((seedName.Length * 131 + seedName[^1]) * 100003 + mutation));
            PrcTess3DCompressed tess = Seed(seedName);
            int rounds = 1 + random.Next(3);
            for (int round = 0; round < rounds; round++)
            {
                Mutate(tess, random);
            }

            try
            {
                using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);
                AssertConsistent(geometry);
                decoded++;
            }
            catch (Scene3DException e)
            {
                Assert.True(e.Error is Scene3DError.InvalidValue or Scene3DError.LimitExceeded or Scene3DError.Truncated, $"{seedName} #{mutation}: {e.Error}: {e.Message}");
                refused++;
            }

            // The whole way to a mesh, with limits that a hostile file could reach.
            try
            {
                using var builder = new SceneBuilder(Limits);
                int name = builder.AddName("mesh");
                PrcCompressedMesher.TryAddMesh(builder, tess, name, out _, out _, TestContext.Current.CancellationToken);
            }
            catch (Scene3DException)
            {
            }
        }

        // The seed itself decodes, and the mutations are not all refused or all accepted: both paths ran.
        using (PrcCompressedGeometry seed = PrcCompressedDecoder.Decode(Seed(seedName), TestContext.Current.CancellationToken))
        {
            AssertConsistent(seed);
        }

        Assert.True(refused > 0, $"{seedName}: no mutation was refused");
        Assert.True(decoded > 0, $"{seedName}: every mutation was refused");
    }
}
