using System.Buffers;
using System.Buffers.Binary;
using Caly.Rendering.Scene3D.Prc.Bits;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The bit-level values of PRC. The integer and double vectors come from prc-rs's test data (MIT).
/// </summary>
public class PrcBitReaderTests
{
    private static byte[] Vector(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Prc", name));

    [Fact]
    public void IntegersOfTheReferenceVectorReadBack()
    {
        // 66,002 times: i as an unsigned integer, i and -i as integers, i as 32 raw bits, bytes swapped.
        var reader = new PrcBitReader(Vector("read_ints.bin"));
        for (uint i = 0; i < 66002; i++)
        {
            Assert.Equal(i, reader.ReadUnsignedInteger());
            Assert.Equal((int)i, reader.ReadInteger());
            Assert.Equal(-(int)i, reader.ReadInteger());
            Assert.Equal(i, BinaryPrimitives.ReverseEndianness(reader.ReadBits(32)));
        }

        Assert.InRange(reader.BitsLeft, 0, 7);
    }

    [Fact]
    public void DoublesOfTheReferenceVectorReadBack()
    {
        // 6,002 times: i, then i * 1.15 and i * -1.11.
        var reader = new PrcBitReader(Vector("read_doubles.bin"));
        for (uint i = 0; i < 6002; i++)
        {
            Assert.Equal(i, reader.ReadUnsignedInteger());
            Assert.Equal(i * 1.15, reader.ReadDouble());
            Assert.Equal(i * -1.11, reader.ReadDouble());
        }

        Assert.InRange(reader.BitsLeft, 0, 7);
    }

    [Fact]
    public void BitsAreReadMostSignificantFirstAtAnyOffset()
    {
        byte[] data = [0b1011_0010, 0xFF, 0x00, 0xA5, 0x5A, 0x12, 0x34, 0x56, 0x78, 0x9A];
        var reader = new PrcBitReader(data);

        Assert.True(reader.ReadBoolean());
        Assert.False(reader.ReadBoolean());
        Assert.Equal(0b11u, reader.ReadBits(2));
        Assert.Equal(0u, reader.ReadBits(0));
        Assert.Equal(0b0010_1111u, reader.ReadBits(8));
        Assert.Equal(12, reader.BitPosition);
        Assert.Equal(0b1111_0000_0000_1010_0101_0101_1010_0001u, reader.ReadBits(32));

        // The last bytes, where no eight are left to load at once.
        reader.Seek(48);
        Assert.Equal(0x3456789Au, reader.ReadBits(32));
        Assert.Equal(0, reader.BitsLeft);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(255u)]
    [InlineData(256u)]
    [InlineData(0x00FFFFFFu)]
    [InlineData(0x01000000u)]
    [InlineData(uint.MaxValue)]
    public void UnsignedIntegersRoundTrip(uint value)
    {
        byte[] data = new PrcBitWriter().Bit(true).Unsigned(value).Bits(0b101, 3).ToArray();
        var reader = new PrcBitReader(data);

        Assert.True(reader.ReadBoolean());
        Assert.Equal(value, reader.ReadUnsignedInteger());
        Assert.Equal(0b101u, reader.ReadBits(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(-128)]
    [InlineData(-129)]
    [InlineData(-1239255)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void IntegersRoundTrip(int value)
    {
        var reader = new PrcBitReader(new PrcBitWriter().Integer(value).Bit(true).ToArray());

        Assert.Equal(value, reader.ReadInteger());
        Assert.True(reader.ReadBoolean());
    }

    [Fact]
    public void AnIntegerOfFiveBytesIsRefused()
    {
        var writer = new PrcBitWriter();
        for (int i = 0; i < 5; i++)
        {
            writer.Bit(true).Bits(1, 8);
        }

        byte[] data = writer.Bit(false).ToArray();

        var e = Assert.Throws<Scene3DException>(() => new PrcBitReader(data).ReadInteger());
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    [Fact]
    public void StringsAreUtf8WithLatin1AsTheFallbackAndCanBeSkipped()
    {
        byte[] data = new PrcBitWriter()
            .String("Pièce n°1 ✓")
            .String(null)
            .Bit(true).Bytes([0x50, 0xE9, 0x63, 0x65], withCount: true)   // "Péce" in Latin-1: not UTF-8
            .String("skipped")
            .String(new string('x', 1000))
            .Bits(0b11, 2)
            .ToArray();
        var reader = new PrcBitReader(data);

        Assert.Equal("Pièce n°1 ✓", reader.ReadString());
        Assert.Equal("", reader.ReadString());
        Assert.Equal("Péce", reader.ReadString());
        reader.SkipString();
        Assert.Equal(new string('x', 1000), reader.ReadString());
        Assert.Equal(0b11u, reader.ReadBits(2));
    }

    [Fact]
    public void TheSmallValuesReadAsTheFormatLaysThemOut()
    {
        byte[] data = new PrcBitWriter()
            .Bits(0x80, 8)                 // character: -128
            .Bits(0xFE, 8)                 // unsigned character
            .Short(0xBEEF)                 // low byte first
            .Bits(BinaryPrimitives.ReverseEndianness(BitConverter.SingleToUInt32Bits(1.5f)), 32)
            .Bits(13, 5).Bits(5000, 13)    // number of bits, then the number
            .SignedBits(-37, 7)            // sign, then six bits
            .SignedBits(0, 1)              // one bit: only the sign
            .Unsigned(11).Bits(0b10101010101, 11)   // user data: 11 bits
            .Bit(true)
            .ToArray();
        var reader = new PrcBitReader(data);

        Assert.Equal(-128, reader.ReadCharacter());
        Assert.Equal(0xFE, reader.ReadUnsignedCharacter());
        Assert.Equal(0xBEEF, reader.ReadUnsignedShort());
        Assert.Equal(1.5f, reader.ReadFloatAsBytes());
        Assert.Equal(5000u, reader.ReadNumberOfBitsThenUnsignedInteger());
        Assert.Equal(-37, reader.ReadIntegerWithVariableBitNumber(7));
        Assert.Equal(0, reader.ReadIntegerWithVariableBitNumber(1));
        reader.SkipUserData();
        Assert.True(reader.ReadBoolean());
    }

    [Fact]
    public void ReadingPastTheEndIsAlwaysTheSameError()
    {
        byte[] data = new PrcBitWriter().Unsigned(70000).Integer(-70000).String("abc").Bits(77, 9).ToArray();
        long total = data.Length * 8L;

        // Every prefix of the data, to the bit: either the values come out or the reader says "truncated".
        for (int length = 0; length < data.Length; length++)
        {
            byte[] cut = data[..length];
            try
            {
                var reader = new PrcBitReader(cut);
                reader.ReadUnsignedInteger();
                reader.ReadInteger();
                reader.ReadString();
                reader.ReadBits(9);
                Assert.Fail($"{length} of {data.Length} bytes were enough.");
            }
            catch (Scene3DException e)
            {
                Assert.Equal(Scene3DError.Truncated, e.Error);
            }
        }

        Assert.Throws<Scene3DException>(() => new PrcBitReader(data).Skip(total + 1));
        Assert.Throws<Scene3DException>(() => new PrcBitReader(data).Seek(total + 1));
        Assert.Throws<Scene3DException>(() => new PrcBitReader([]).ReadBoolean());
        Assert.Throws<Scene3DException>(() => new PrcBitReader([]).ReadDouble());
    }

    [Fact]
    public void ACountThatTheDataCannotHoldIsRefusedBeforeAnythingIsAllocated()
    {
        // A string of four billion bytes in a six-byte stream.
        byte[] data = new PrcBitWriter().Bit(true).Unsigned(uint.MaxValue).ToArray();

        var e = Assert.Throws<Scene3DException>(() => new PrcBitReader(data).ReadString());
        Assert.Equal(Scene3DError.Truncated, e.Error);
    }

    // ---- compressed arrays ----

    [Fact]
    public void APlainArrayIsACountAndItsElements()
    {
        byte[] data = new PrcBitWriter().Bit(false).Unsigned(3).Bits(5, 8).Bits(0xFF, 8).Bits(0, 8).Bit(true).ToArray();
        var reader = new PrcBitReader(data);

        int count = PrcHuffman.ReadElements(ref reader, true, 6, true, true, false, out short[] values);

        Assert.Equal(new short[] { 5, -1, 0 }, values.AsSpan(0, count).ToArray());
        Assert.True(reader.ReadBoolean());
        ArrayPool<short>.Shared.Return(values);
    }

    /// <summary>A Huffman block of three symbols with codes 0, 10 and 11, holding six elements.</summary>
    private static PrcBitWriter HuffmanSample(PrcBitWriter writer, bool withCompressedBit)
    {
        (byte[] words, uint bitsInLast) = new PrcHuffmanBlockWriter()
            .Value(3, 7)                 // leaves, in bits per element + 1
            .Value(2, 8)                 // longest code
            .Value(5, 6).Value(1, 2).TableCode("0")
            .Value(0b111110, 6).Value(2, 2).TableCode("10")      // -2 as a six-bit number
            .Value(31, 6).Value(2, 2).TableCode("11")
            .Value(6, 32)                // elements
            .Code("0").Code("10").Code("11").Code("0").Code("0").Code("11")
            .Finish();

        if (withCompressedBit)
        {
            writer.Bit(true);
        }

        return writer.Unsigned((uint)(words.Length / 4)).Bytes(words, withCount: false).Unsigned(bitsInLast);
    }

    [Fact]
    public void AHuffmanArrayDecodesItsSymbols()
    {
        byte[] data = HuffmanSample(new PrcBitWriter().Bits(0b101, 3), withCompressedBit: true).Bit(true).ToArray();
        var reader = new PrcBitReader(data);
        reader.ReadBits(3);

        int count = PrcHuffman.ReadElements(ref reader, true, 6, false, true, false, out short[] values);

        Assert.Equal(new short[] { 5, -2, 31, 5, 5, 31 }, values.AsSpan(0, count).ToArray());
        Assert.True(reader.ReadBoolean());
        ArrayPool<short>.Shared.Return(values);

        // Unsigned symbols, and skipping the same array.
        reader = new PrcBitReader(data);
        reader.ReadBits(3);
        count = PrcHuffman.ReadElements(ref reader, true, 6, false, false, false, out values);
        Assert.Equal(new short[] { 5, 62, 31, 5, 5, 31 }, values.AsSpan(0, count).ToArray());
        ArrayPool<short>.Shared.Return(values);

        reader = new PrcBitReader(data);
        reader.ReadBits(3);
        PrcHuffman.SkipElements(ref reader, true, false, false);
        Assert.True(reader.ReadBoolean());
        Assert.Equal(0, reader.BitsLeft >> 3);
    }

    [Fact]
    public void ATableOfOneSymbolNeedsNoBitsPerElement()
    {
        (byte[] words, uint bitsInLast) = new PrcHuffmanBlockWriter()
            .Value(1, 7).Value(1, 8)
            .Value(9, 6).Value(0, 1)     // one symbol, code of no length
            .Value(1000, 32)
            .Finish();
        byte[] data = new PrcBitWriter().Bit(true).Unsigned((uint)(words.Length / 4)).Bytes(words, false).Unsigned(bitsInLast).ToArray();
        var reader = new PrcBitReader(data);

        int count = PrcHuffman.ReadElements(ref reader, true, 6, true, true, false, out short[] values);

        Assert.Equal(1000, count);
        Assert.All(values.AsSpan(0, count).ToArray(), v => Assert.Equal(9, v));
        ArrayPool<short>.Shared.Return(values);
    }

    [Fact]
    public void AnIntegerArrayIsItsWidthsThenItsValues()
    {
        // Widths 5, 1, 8 (stored plainly), then -11 in five bits, 0 in one, 100 in eight.
        byte[] data = new PrcBitWriter()
            .Bit(false).Unsigned(3).Bits(5, 8).Bits(1, 8).Bits(8, 8)
            .SignedBits(-11, 5).SignedBits(0, 1).SignedBits(100, 8)
            .Bit(true)
            .ToArray();
        var reader = new PrcBitReader(data);

        int count = PrcHuffman.ReadIntegerArray(ref reader, out int[] values);

        Assert.Equal(new[] { -11, 0, 100 }, values.AsSpan(0, count).ToArray());
        Assert.True(reader.ReadBoolean());
        ArrayPool<int>.Shared.Return(values);
    }

    [Fact]
    public void AnIndexArrayStoresDifferencesOfWidthsAndOfValues()
    {
        // Width steps 4, +1, -3 give widths 4, 5, 2; value steps 7, +9, -1 give indices 7, 16, 15.
        byte[] data = new PrcBitWriter()
            .Bit(false).Unsigned(3).Bits(4, 8).Bits(1, 8).Bits(unchecked((byte)-3), 8)
            .SignedBits(7, 4).SignedBits(9, 5).SignedBits(-1, 2)
            .Bit(true)
            .ToArray();
        var reader = new PrcBitReader(data);

        int count = PrcHuffman.ReadIndiceArray(ref reader, true, true, out int[] values);

        Assert.Equal(new[] { 7, 16, 15 }, values.AsSpan(0, count).ToArray());
        Assert.True(reader.ReadBoolean());
        ArrayPool<int>.Shared.Return(values);
    }

    [Fact]
    public void BrokenHuffmanBlocksAreRefused()
    {
        static Scene3DException Read(byte[] data)
        {
            return Assert.Throws<Scene3DException>(() =>
            {
                var reader = new PrcBitReader(data);
                PrcHuffman.ReadElements(ref reader, true, 6, true, true, false, out _);
            });
        }

        // More elements than the block has bits.
        (byte[] words, uint bitsInLast) = new PrcHuffmanBlockWriter()
            .Value(2, 7).Value(1, 8).Value(1, 6).Value(1, 1).TableCode("0").Value(2, 6).Value(1, 1).TableCode("1")
            .Value(100000, 32).Code("0101")
            .Finish();
        Assert.Equal(Scene3DError.InvalidValue, Read(new PrcBitWriter().Bit(true).Unsigned((uint)(words.Length / 4)).Bytes(words, false).Unsigned(bitsInLast).ToArray()).Error);

        // Two symbols with the same code.
        (words, bitsInLast) = new PrcHuffmanBlockWriter()
            .Value(2, 7).Value(1, 8).Value(1, 6).Value(1, 1).TableCode("0").Value(2, 6).Value(1, 1).TableCode("0")
            .Value(1, 32).Code("0")
            .Finish();
        Assert.Equal(Scene3DError.InvalidValue, Read(new PrcBitWriter().Bit(true).Unsigned((uint)(words.Length / 4)).Bytes(words, false).Unsigned(bitsInLast).ToArray()).Error);

        // A block that claims more words than the stream holds.
        Assert.Equal(Scene3DError.Truncated, Read(new PrcBitWriter().Bit(true).Unsigned(1000).Bits(0, 32).ToArray()).Error);

        // A block cut inside its table, at every length.
        byte[] good = HuffmanSample(new PrcBitWriter(), withCompressedBit: true).ToArray();
        for (int length = 0; length < good.Length - 1; length++)
        {
            Read(good[..length]);
        }
    }
}
