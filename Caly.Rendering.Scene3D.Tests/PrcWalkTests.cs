using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Getting past what is not kept: compressed tessellations, wires, and the whole of every payload of
/// the corpus. (That the walk is the right one is checked outside the suite, entity by entity against
/// prc-rs; here, that it ends where each section ends.)
/// </summary>
public class PrcWalkTests(ITestOutputHelper output)
{
    private static PrcContext Context(uint authoringVersion = 8137) => new(authoringVersion, 1);

    private sealed class Names : PrcTrace
    {
        public List<string> Seen { get; } = [];

        public override void Entity(string name, long bitPosition, int depth) => Seen.Add(name);
    }

    /// <summary>An array of small values stored plainly: a bit saying so, the count, a byte each.</summary>
    private static PrcBitWriter Characters(PrcBitWriter w, params int[] values)
    {
        w.Bit(false).Unsigned((uint)values.Length);
        foreach (int value in values)
        {
            w.Bits((uint)(value & 0xFF), 8);
        }

        return w;
    }

    private static PrcBitWriter Shorts(PrcBitWriter w, params ushort[] values)
    {
        w.Bit(false).Unsigned((uint)values.Length);
        foreach (ushort value in values)
        {
            w.Short(value);
        }

        return w;
    }

    /// <summary>A compressed tessellation of two triangles on faces 0 and 2 (three faces), five points of which two are references.</summary>
    internal static PrcBitWriter Compressed(uint version, bool hasFaces, bool recalculateNormals, bool texture)
    {
        var w = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_3D_Compressed).Bit(true).Bit(hasFaces).Double(0.001);
        if (version >= 7031)
        {
            // The origin (1, 0, -1): floats as their bytes, least significant first.
            w.Bits(0x0000803F, 32).Bits(0, 32).Bits(0x000080BF, 32);
        }

        // Points: three integers of 5, 3 and 9 bits.
        Characters(w, 5, 3, 9).SignedBits(-7, 5).SignedBits(2, 3).SignedBits(100, 9);

        // Edge status, two per... whatever: it is only read past.
        Characters(w, 1, 0, 3, 2, 1, 0);

        // Triangle to face: 0, then 2. Widths as steps (2, then +1), values as steps (0, then +2).
        Characters(w, 2, 1).SignedBits(0, 2).SignedBits(2, 3);

        // Five points, the second and the fifth are references; two references, stored plainly (no more than three).
        w.Unsigned(5).Bits(0b01001, 5);
        w.Unsigned(2).Bits(3, 8).Bits(0, 8).SignedBits(1, 3).SignedBits(-1, 3);

        w.Bit(recalculateNormals);
        if (recalculateNormals)
        {
            w.Bits(0b10, 2).Double(0.7).Bits(0, 8);        // reversed per triangle, crease angle, flags
        }
        else
        {
            w.Bits(10, 8).Unsigned(7).Bits(0b1010101, 7);  // angle bits, normal data
            Shorts(w, 1, 2, 3);
            w.Bits(0b101, 3);                              // planar, per face
        }

        // Point colours: per face (or once), then five characters per coloured vertex.
        w.Bit(true).Bits(0b110, hasFaces ? 3 : 1);
        Characters(w, 0, 255, 0, 0, 0);

        // Several line attributes: per face (or once). Then the line attributes.
        w.Bit(true).Bits(0, hasFaces ? 3 : 1);
        Shorts(w, 4, 5);

        w.Bit(!texture);
        if (texture)
        {
            w.Unsigned(1).Bits(0xDEADBEEF, 32).Unsigned(17);          // one word of binary data, bits used of the last
            w.Unsigned(2).Bits(3, 5).Bits(0b101, 3).Bits(0, 5);       // two references: three bits, then none
            w.Double(0.5).Unsigned(2).Bits(0, 32).Bits(0x0000003F, 32);   // two coordinates: 0 and 0.5
            w.Bit(false).Bits(0b011, 3);                              // not all faces have a texture: which
        }

        w.Bit(true);
        Characters(w, 1, 1, 1);                            // behaviours
        return w;
    }

    [Theory]
    [InlineData(8137u, true, false, false)]
    [InlineData(8137u, true, true, false)]
    [InlineData(8137u, false, false, true)]
    [InlineData(8137u, true, true, true)]
    [InlineData(7030u, true, false, true)]
    [InlineData(7031u, false, true, false)]
    public void ACompressedTessellationIsReadPast(uint version, bool hasFaces, bool recalculateNormals, bool texture)
    {
        PrcBitWriter data = Compressed(version, hasFaces, recalculateNormals, texture);
        var r = new PrcBitReader([.. data.ToArray(), 0xFF, 0xFF, 0xFF, 0xFF]);
        var trace = new Names();
        PrcContext ctx = Context(version);
        ctx.Trace = trace;

        PrcTess tess = PrcReaders.ReadTess(ref r, ctx);

        Assert.Equal(data.BitLength, r.BitPosition);
        Assert.Equal(PrcType.PRC_TYPE_TESS_3D_Compressed, tess.IdType);
        Assert.IsType<PrcTess3DCompressed>(tess.Id);
        Assert.Equal(["PRC_TYPE_TESS", "PRC_TYPE_TESS_3D_Compressed", "CompressedIntegerArray", "CharacterArray", "CompressedIndiceArray", "UncompressedBoolArray", "CompressedIndiceArrayWithoutBit"], trace.Seen.Take(7));
        Assert.Equal(texture, trace.Seen.Contains("CompressedTextureParameter"));
    }

    private static PrcTess3DCompressed ReadCompressed(uint version, bool hasFaces, bool recalculateNormals, bool texture, SceneLimits? limits = null)
    {
        var r = new PrcBitReader([.. Compressed(version, hasFaces, recalculateNormals, texture).ToArray(), 0xFF, 0xFF, 0xFF, 0xFF]);
        return PrcReaders.ReadTess3DCompressed(ref r, new PrcContext(version, 1, limits));
    }

    /// <summary>What the file holds of a compressed tessellation is kept as it is, for the decoder.</summary>
    [Fact]
    public void ACompressedTessellationIsKeptWhole()
    {
        PrcTess3DCompressed tess = ReadCompressed(8137, hasFaces: true, recalculateNormals: false, texture: true);

        Assert.True(tess.IsCalculated);
        Assert.True(tess.HasFaces);
        Assert.Equal(0.001, tess.Tolerance);
        Assert.True(tess.HasOrigin);
        Assert.Equal((1f, 0f, -1f), (tess.OriginX, tess.OriginY, tess.OriginZ));
        Assert.Equal([-7, 2, 100], tess.Points);
        Assert.Equal([1, 0, 3, 2, 1, 0], tess.EdgeStatus);
        Assert.Equal([0, 2], tess.TriangleFaces);
        Assert.Equal(2, tess.TriangleCount);
        Assert.Equal(3, tess.FaceCount);

        // Five points met, the second and the fifth are references.
        Assert.Equal(5, tess.PointIsReference.Count);
        Assert.Equal([false, true, false, false, true], Enumerable.Range(0, 5).Select(i => tess.PointIsReference[i]));
        Assert.Equal(2, tess.PointIsReference.CountSet());
        Assert.Equal([1, 0], tess.PointReferences);

        Assert.False(tess.MustRecalculateNormals);
        Assert.Equal(10, tess.NormalAngleBits);
        Assert.Equal(7, tess.NormalBits.Count);
        Assert.Equal([true, false, true, false, true, false, true], Enumerable.Range(0, 7).Select(i => tess.NormalBits[i]));
        Assert.Equal([1, 2, 3], tess.NormalAngles);
        Assert.Equal([true, false, true], Enumerable.Range(0, 3).Select(i => tess.IsFacePlanar[i]));

        Assert.Equal([true, true, false], Enumerable.Range(0, 3).Select(i => tess.IsPointColorOnFace[i]));
        Assert.Equal([0, 255, 0, 0, 0], tess.PointColors);
        Assert.Equal(3, tess.IsMultipleLineAttributeOnFace.Count);
        Assert.Equal(0, tess.IsMultipleLineAttributeOnFace.CountSet());
        Assert.Equal([4, 5], tess.LineAttributes);
        Assert.Equal([1, 1, 1], tess.Behaviors);

        // The texture parameters: one word of bits of which 17 count, two references, two coordinates.
        Assert.NotNull(tess.Texture);
        Assert.Equal(17, tess.Texture.Bits.Count);
        // The first byte written is 0xDE: its bits from the lowest.
        Assert.Equal(new[] { false, true, true, true, true, false, true, true }, Enumerable.Range(0, 8).Select(i => tess.Texture.Bits[i]));
        Assert.Equal([5u, 0u], tess.Texture.References);
        Assert.Equal([0f, 0.5f], tess.Texture.Parameters);
        Assert.Equal([false, true, true], Enumerable.Range(0, 3).Select(i => tess.FaceHasTexture[i]));
    }

    [Fact]
    public void ACompressedTessellationThatAsksForItsNormalsKeepsWhatTheyNeed()
    {
        PrcTess3DCompressed tess = ReadCompressed(8137, hasFaces: false, recalculateNormals: true, texture: false);

        Assert.True(tess.MustRecalculateNormals);
        Assert.Equal(2, tess.NormalIsReversed.Count);
        Assert.Equal([true, false], Enumerable.Range(0, 2).Select(i => tess.NormalIsReversed[i]));
        Assert.Equal(0.7, tess.CreaseAngle);
        Assert.Equal(0, tess.NormalBits.Count);
        Assert.Empty(tess.NormalAngles);
        Assert.Null(tess.Texture);

        // Without faces the per-face arrays have one entry.
        Assert.Equal(1, tess.IsPointColorOnFace.Count);
        Assert.Equal(1, tess.IsMultipleLineAttributeOnFace.Count);
    }

    /// <summary>Review focus: a file older than version 7031 has no origin; the tessellation is read all the same.</summary>
    [Fact]
    public void ACompressedTessellationOfAnOldFileHasNoOrigin()
    {
        PrcTess3DCompressed tess = ReadCompressed(7030, hasFaces: true, recalculateNormals: false, texture: false);

        Assert.False(tess.HasOrigin);
        Assert.Equal([-7, 2, 100], tess.Points);
        Assert.Equal([0, 2], tess.TriangleFaces);
    }

    [Fact]
    public void WhatACompressedTessellationKeepsCountsAgainstTheLimit()
    {
        Scene3DException e = Assert.Throws<Scene3DException>(() => ReadCompressed(8137, true, false, true, new SceneLimits { MaxTotalBytes = 16 }));
        Assert.Equal(Scene3DError.LimitExceeded, e.Error);
    }

    [Fact]
    public void ACompressedTessellationCutAnywhereIsTruncation()
    {
        byte[] bytes = Compressed(8137, hasFaces: true, recalculateNormals: false, texture: true).ToArray();
        for (int length = 0; length < bytes.Length - 1; length++)
        {
            byte[] cut = bytes[..length];
            Scene3DException e = Assert.Throws<Scene3DException>(() =>
            {
                var r = new PrcBitReader(cut);
                PrcReaders.ReadTess3DCompressed(ref r, Context());
            });
            Assert.Equal(Scene3DError.Truncated, e.Error);
        }
    }

    /// <summary>Review focus: sizes of arrays that follow from other arrays, not from the bits left.</summary>
    [Fact]
    public void ACompressedTessellationWithImpossibleSizesFailsCleanly()
    {
        // A triangle on face two thousand million: every per-face array would be that long.
        var w = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_3D_Compressed).Bit(true).Bit(true).Double(0.001).Bits(0, 32).Bits(0, 32).Bits(0, 32);
        Characters(w);
        Characters(w);
        Characters(w, 32).SignedBits(2_000_000_000, 32);
        w.Unsigned(4_000_000_000).Bits(0xFFFFFFFF, 32);

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(w.ToArray());
            PrcReaders.ReadTess3DCompressed(ref r, Context());
        });
        Assert.Equal(Scene3DError.Truncated, e.Error);
    }

    /// <summary>
    /// The primitives of a face, kind after kind from the lowest bit of its flags: the example of the ISO
    /// text has 5 triangles, two fans of 5 and 7 points and a strip of 11.
    /// </summary>
    [Fact]
    public void ThePointsOfAFaceAreCountedByKind()
    {
        Assert.Equal(5 * 3 + 5 + 7 + 11, PrcFaceLayout.PointCount(0x0002 | 0x0004 | 0x0008, [5, 2, 5, 7, 1, 11]));
        Assert.Equal(3, PrcFaceLayout.PointCount(0x0002, [1]));
        Assert.Equal(0, PrcFaceLayout.PointCount(0, []));
        Assert.Equal(6 + 9, PrcFaceLayout.PointCount(0x0002 | 0x0200, [2, 3]));

        // A fan with one normal carries a flag on its number of points.
        Assert.Equal(6, PrcFaceLayout.PointCount(0x0040, [1, 6 | 0x40000000]));

        // Fewer sizes than the flags ask for; a reserved kind.
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => PrcFaceLayout.PointCount(0x0004, [3, 4, 5])).Error);
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => PrcFaceLayout.PointCount(0x0006, [3])).Error);
        Assert.Equal(Scene3DError.Unsupported, Assert.Throws<Scene3DException>(() => PrcFaceLayout.PointCount(0x0001, [3])).Error);
    }

    /// <summary>
    /// A wire tessellation's colours: one per point of each edge, or per segment; an edge that closes
    /// has one point more than it stores; without indices the coordinates are one edge.
    /// </summary>
    [Theory]
    [InlineData(false, false, 3 + 2)]
    [InlineData(true, false, 2 + 1)]
    [InlineData(false, true, 3 + 3)]
    [InlineData(true, true, 2 + 2)]
    public void AWiresColoursAreCountedFromItsEdges(bool perSegment, bool secondEdgeCloses, int colours)
    {
        PrcBitWriter w = Wire(perSegment, secondEdgeCloses, colours);
        var r = new PrcBitReader([.. w.ToArray(), 0xFF, 0xFF, 0xFF, 0xFF]);
        PrcContext ctx = Context();
        PrcReaders.ReadTess3DWire(ref r, ctx);

        Assert.Equal(w.BitLength, r.BitPosition);
        Assert.False(ctx.InsideWire);
    }

    /// <summary>A wire of two edges, of three and of two points, with a colour per point or segment.</summary>
    internal static PrcBitWriter Wire(bool perSegment, bool secondEdgeCloses, int colours)
    {
        var w = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_3D_Wire).Bit(false).Unsigned(0);
        w.Unsigned(7).Unsigned(3).Unsigned(0).Unsigned(3).Unsigned(6).Unsigned(secondEdgeCloses ? 2u | 0x10000000 : 2).Unsigned(9).Unsigned(12);
        w.Bit(true);                                       // vertex colours
        w.Bit(false).Bit(perSegment).Bit(false);           // RGB, per point or segment, not optimised
        w.Bits(0x102030, 24);
        for (int i = 1; i < colours; i++)
        {
            w.Bit(i % 2 == 0);
            if (i % 2 != 0)
            {
                w.Bits(0x405060, 24);
            }
        }

        return w;
    }

    [Fact]
    public void AWireWithoutIndicesIsOneEdgeThroughItsCoordinates()
    {
        var w = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_3D_Wire).Bit(false).Unsigned(12);
        for (int i = 0; i < 12; i++)
        {
            w.Double(i);
        }

        w.Unsigned(0).Bit(true).Bit(true).Bit(false).Bit(false).Bits(0x10203040, 32).Bit(true).Bit(true).Bit(true);   // RGBA, four points

        var r = new PrcBitReader([.. w.ToArray(), 0xFF, 0xFF, 0xFF, 0xFF]);
        PrcReaders.ReadTess3DWire(ref r, Context());
        Assert.Equal(w.BitLength, r.BitPosition);
    }

    [Fact]
    public void AWireEdgeLongerThanItsIndicesIsRefused()
    {
        var w = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_3D_Wire).Bit(false).Unsigned(0)
            .Unsigned(2).Unsigned(5).Unsigned(0).Bit(true).Bit(false).Bit(false).Bit(false).Bits(0, 32).Bits(0, 32);

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(w.ToArray());
            PrcReaders.ReadTess3DWire(ref r, Context());
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    /// <summary>
    /// Every payload of the corpus (SCENE3D_PRC_CORPUS; nothing to do without it) is walked from its
    /// first section to its last, each ending in its last byte.
    /// </summary>
    [Fact]
    public void EveryPayloadOfTheCorpusIsWalkedToTheEndOfEverySection()
    {
        long entities = 0;
        foreach (string path in PrcContainerTests.CorpusFiles())
        {
            byte[] file = File.ReadAllBytes(path);
            using PrcContainer container = PrcContainer.Open(file);
            var counter = new Counter();
            PrcFile read = PrcFileReader.Read(container, trace: counter, cancellation: TestContext.Current.CancellationToken);

            Assert.Equal(container.FileStructureCount, read.FileStructures.Length);
            Assert.Equal(container.FileStructureCount * 3 + 1, read.Sections.Length);
            foreach (PrcSectionRead section in read.Sections)
            {
                // The last byte is padded; a section that ends on a byte has one byte of padding more.
                Assert.True(section.RemainingBits is >= 0 and <= 8, $"{Path.GetFileName(path)}: {section.Name} of structure {section.FileStructure} leaves {section.RemainingBits} of {section.TotalBits} bits.");
            }

            int plain = 0, compressed = 0, wire = 0, markup = 0, otherVersion = 0;
            for (int i = 0; i < container.FileStructureCount; i++)
            {
                otherVersion += container[i].AuthoringVersion == container.AuthoringVersion ? 0 : 1;
                foreach (PrcTess tess in read.FileStructures[i].Tessellation.Tess)
                {
                    switch (tess.IdType)
                    {
                        case PrcType.PRC_TYPE_TESS_3D: plain++; Assert.IsType<PrcTess3D>(tess.Id); break;
                        case PrcType.PRC_TYPE_TESS_3D_Compressed: compressed++; break;
                        case PrcType.PRC_TYPE_TESS_3D_Wire: wire++; break;
                        case PrcType.PRC_TYPE_TESS_Markup: markup++; break;
                        default: Assert.Fail($"Tessellation of type {tess.IdType}."); break;
                    }
                }
            }

            entities += counter.Count;
            output.WriteLine($"{Path.GetFileName(path)}: {counter.Count} entities; tessellations: {plain} plain, {compressed} compressed, {wire} wire, {markup} markup; {otherVersion} of {container.FileStructureCount} structures written by another version than the file's ({container.AuthoringVersion})");
        }

        output.WriteLine($"{entities} entities in all");
    }

    private sealed class Counter : PrcTrace
    {
        public long Count { get; private set; }

        public override void Entity(string name, long bitPosition, int depth) => Count++;
    }
}
