using System.Buffers.Binary;
using System.Numerics;
using Caly.Rendering.Scene3D.U3D.Internal;

namespace Caly.Rendering.Scene3D.Tests;

public class U3DBitReaderTests
{
    private static U3DBitReader Reader(ReadOnlySpan<byte> block, bool forceSymbolPath = false, bool noCompression = false)
    {
        return new U3DBitReader(block, new U3DContexts(), noCompression) { ForceSymbolPath = forceSymbolPath };
    }

    [Fact]
    public void UncompressedValuesAreTheLittleEndianBytes()
    {
        byte[] data = [0x34, 0x12, 0x78, 0x56, 0x34, 0x12, 0x00, 0x00, 0x80, 0x3F, 0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01, 0xFF];

        foreach (bool symbolPath in new[] { false, true })
        {
            U3DBitReader r = Reader(data, symbolPath);
            Assert.Equal((ushort)0x1234, r.ReadU16());
            Assert.Equal(0x12345678u, r.ReadU32());
            Assert.Equal(1f, r.ReadF32());
            Assert.Equal(0x0123456789ABCDEFul, r.ReadU64());
            Assert.Equal((byte)0xFF, r.ReadU8());
            Assert.Equal(data.Length, r.BytePosition);
        }
    }

    [Fact]
    public void StringsAreUtf8WithA16BitLength()
    {
        byte[] data = [0x05, 0x00, (byte)'B', (byte)'o', (byte)'x', 0xC3, 0xA9, 0x00, 0x00];

        foreach (bool symbolPath in new[] { false, true })
        {
            U3DBitReader r = Reader(data, symbolPath);
            Assert.Equal("Boxé", r.ReadString(100));
            Assert.Equal("", r.ReadString(100));
        }
    }

    [Fact]
    public void StringLongerThanTheLimitIsRejected()
    {
        byte[] data = [0x05, 0x00, 1, 2, 3, 4, 5];

        var e = Assert.Throws<Scene3DException>(() =>
        {
            U3DBitReader r = Reader(data);
            r.ReadString(4);
        });

        Assert.Equal(Scene3DError.LimitExceeded, e.Error);
    }

    [Fact]
    public void ReadingPastTheEndIsTruncation()
    {
        byte[] data = [1, 2, 3];

        foreach (bool symbolPath in new[] { false, true })
        {
            var e = Assert.Throws<Scene3DException>(() =>
            {
                U3DBitReader r = Reader(data, symbolPath);
                r.ReadU32();
                r.ReadU32();
                r.ReadU32();
            });

            Assert.Equal(Scene3DError.Truncated, e.Error);
        }
    }

    /// <summary>
    /// The byte-aligned fast path must return exactly what decoding symbol by symbol returns. Every block of
    /// every sample scene is read as a run of 32-bit words both ways.
    /// </summary>
    [Fact]
    public void FastPathEqualsSymbolPathOnEveryBlock()
    {
        int blocks = 0, words = 0;
        foreach (string name in TestFiles.SceneNames)
        {
            byte[] file = TestFiles.Scene(name + ".u3d");
            foreach (RawBlocks.Block block in RawBlocks.Enumerate(file))
            {
                ReadOnlySpan<byte> data = RawBlocks.Data(file, block);
                int count = Math.Min(data.Length / 4, 4096);
                U3DBitReader fast = Reader(data);
                U3DBitReader slow = Reader(data, forceSymbolPath: true);
                for (int i = 0; i < count; i++)
                {
                    uint expected = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
                    Assert.Equal(expected, fast.ReadU32());
                    Assert.Equal(expected, slow.ReadU32());
                }

                blocks++;
                words += count;
            }
        }

        Assert.True(blocks > 1000 && words > 100_000, $"only {blocks} blocks / {words} words were compared");
    }

    [Fact]
    public void BulkFloatReadMatchesSingleReads()
    {
        byte[] file = TestFiles.Scene("plane64x64_base.u3d");
        RawBlocks.Block block = RawBlocks.Enumerate(file).Single(b => b.Type == RawBlocks.BaseMesh);
        ReadOnlySpan<byte> data = RawBlocks.Data(file, block);

        U3DBitReader single = Reader(data, forceSymbolPath: true);
        U3DBitReader bulk = Reader(data);
        string name = single.ReadString(1000);
        Assert.Equal(name, bulk.ReadString(1000));

        var expected = new float[3000];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = single.ReadF32();
        }

        var actual = new float[3000];
        bulk.ReadF32(actual);

        Assert.Equal(expected, actual);
        Assert.Equal(single.BytePosition, bulk.BytePosition);
    }

    /// <summary>
    /// End to end over a real base mesh block: uncompressed counts and positions, then the arithmetic-coded
    /// faces (a dynamic context for the shading id, static ranges for the indices). The triangles must be
    /// those of box_base.idtf.
    /// </summary>
    [Fact]
    public void DecodesTheFacesOfBoxBase()
    {
        byte[] file = TestFiles.Scene("box_base.u3d");
        RawBlocks.Block block = RawBlocks.Enumerate(file).Single(b => b.Type == RawBlocks.BaseMesh);

        foreach (bool symbolPath in new[] { false, true })
        {
            U3DBitReader r = Reader(RawBlocks.Data(file, block), symbolPath);
            r.ReadString(1000);
            r.ReadU32(); // chain index
            uint faceCount = r.ReadU32(), positionCount = r.ReadU32(), normalCount = r.ReadU32();
            uint diffuseCount = r.ReadU32(), specularCount = r.ReadU32(), texCoordCount = r.ReadU32();
            Assert.Equal((12u, 8u, 0u, 0u, 0u), (faceCount, positionCount, diffuseCount, specularCount, texCoordCount));

            var positions = new Vector3[positionCount];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = new Vector3(r.ReadF32(), r.ReadF32(), r.ReadF32());
            }

            for (uint i = 0; i < normalCount * 3; i++)
            {
                r.ReadF32();
            }

            var triangles = new List<string>();
            for (int f = 0; f < faceCount; f++)
            {
                Assert.Equal(0u, r.ReadCompressedU32(U3DConstants.ContextBaseShadingId));
                var corners = new Vector3[3];
                for (int c = 0; c < 3; c++)
                {
                    uint p = r.ReadCompressedU32(U3DConstants.StaticFull + positionCount);
                    Assert.True(p < positionCount);
                    corners[c] = positions[p];
                    Assert.True(r.ReadCompressedU32(U3DConstants.StaticFull + normalCount) < normalCount);
                }

                triangles.Add(Key(corners[0], corners[1], corners[2]));
            }

            // MODEL_POSITION_LIST and MESH_FACE_POSITION_LIST of box_base.idtf
            Vector3[] idtfPositions =
            [
                new(-20, -20, 0), new(20, -20, 0), new(-20, 20, 0), new(20, 20, 0),
                new(-20, -20, 40), new(20, -20, 40), new(-20, 20, 40), new(20, 20, 40)
            ];
            int[][] idtfFaces =
            [
                [0, 2, 3], [3, 1, 0], [4, 5, 7], [7, 6, 4], [0, 1, 5], [5, 4, 0],
                [1, 3, 7], [7, 5, 1], [3, 2, 6], [6, 7, 3], [2, 0, 4], [4, 6, 2]
            ];
            List<string> expected = idtfFaces.Select(f => Key(idtfPositions[f[0]], idtfPositions[f[1]], idtfPositions[f[2]])).ToList();

            expected.Sort(StringComparer.Ordinal);
            triangles.Sort(StringComparer.Ordinal);
            Assert.Equal(expected, triangles);
        }
    }

    /// <summary>
    /// A static context with an empty range (exactly 0x400) is a dynamic context in the reference decoder;
    /// treating it as static divides by zero. The first read through it is an escape followed by the value.
    /// </summary>
    [Fact]
    public void ZeroRangeContextIsDynamic()
    {
        // Hand-assembled: escape symbol (0) in a fresh dynamic context costs no bits beyond the code look-ahead,
        // then the value follows as a plain U32.
        byte[] data = [0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        U3DBitReader r = Reader(data);
        uint value = r.ReadCompressedU32(U3DConstants.StaticFull);

        Assert.Equal(0x2Au, value);
    }

    [Fact]
    public void NoCompressionModeReadsPlainValues()
    {
        byte[] data = [0x07, 0x00, 0x00, 0x00, 0x09, 0x00, 0x05];

        U3DBitReader r = Reader(data, noCompression: true);

        Assert.Equal(7u, r.ReadCompressedU32(U3DConstants.StaticFull + 100));
        Assert.Equal((ushort)9, r.ReadCompressedU16(U3DConstants.ContextBaseShadingId));
        Assert.Equal((byte)5, r.ReadCompressedU8(U3DConstants.ContextBaseShadingId));
    }

    [Fact]
    public void CorruptCodedDataIsReportedNotCrashed()
    {
        var random = new Random(12345);
        for (int round = 0; round < 200; round++)
        {
            byte[] data = new byte[64];
            random.NextBytes(data);
            try
            {
                U3DBitReader r = Reader(data);
                for (int i = 0; i < 200; i++)
                {
                    r.ReadCompressedU32(1 + (uint)(i % 3));
                    r.ReadCompressedU32(U3DConstants.StaticFull + (uint)(i % 7));
                    r.ReadCompressedU8(5);
                }
            }
            catch (Scene3DException)
            {
                // expected for garbage
            }
        }
    }

    private static string Key(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3[] v = [a, b, c];
        int first = 0;
        for (int i = 1; i < 3; i++)
        {
            if ((v[i].X, v[i].Y, v[i].Z).CompareTo((v[first].X, v[first].Y, v[first].Z)) < 0)
            {
                first = i;
            }
        }

        return string.Join('|', Enumerable.Range(0, 3).Select(i => v[(first + i) % 3]).Select(p => $"{p.X:R},{p.Y:R},{p.Z:R}"));
    }
}
