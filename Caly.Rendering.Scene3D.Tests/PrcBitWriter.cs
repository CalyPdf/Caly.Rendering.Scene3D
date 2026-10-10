using System.Text;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Writes the bit stream of a PRC section, for tests: the counterpart of the reader's primitives, as far
/// as synthetic entities need them. Most significant bit first.
/// </summary>
internal sealed class PrcBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitsInLast;

    public long BitLength => _bytes.Count == 0 ? 0 : (_bytes.Count - 1) * 8L + (_bitsInLast == 0 ? 8 : _bitsInLast);

    public PrcBitWriter Bit(bool value)
    {
        if (_bitsInLast == 0)
        {
            _bytes.Add(0);
        }

        if (value)
        {
            _bytes[^1] |= (byte)(0x80 >> _bitsInLast);
        }

        _bitsInLast = (_bitsInLast + 1) & 7;
        return this;
    }

    public PrcBitWriter Bits(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            Bit(((value >> i) & 1) != 0);
        }

        return this;
    }

    public PrcBitWriter Unsigned(uint value)
    {
        while (value != 0)
        {
            Bit(true).Bits(value & 0xFF, 8);
            value >>= 8;
        }

        return Bit(false);
    }

    public PrcBitWriter Integer(int value)
    {
        if (value == 0)
        {
            return Bit(false);
        }

        while (true)
        {
            int low = value & 0xFF;
            Bit(true).Bits((uint)low, 8);
            value >>= 8;
            if ((value == 0 && (low & 0x80) == 0) || (value == -1 && (low & 0x80) != 0))
            {
                return Bit(false);
            }
        }
    }

    public PrcBitWriter String(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Bit(false);
        }

        return Bit(true).Bytes(Encoding.UTF8.GetBytes(value), withCount: true);
    }

    public PrcBitWriter Bytes(ReadOnlySpan<byte> bytes, bool withCount)
    {
        if (withCount)
        {
            Unsigned((uint)bytes.Length);
        }

        foreach (byte b in bytes)
        {
            Bits(b, 8);
        }

        return this;
    }

    /// <summary>A double, in the plainest of the encodings the format allows: exponent, sign, every mantissa byte given.</summary>
    public PrcBitWriter Double(double value)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        if (bits == 0)
        {
            return Bits(0b01, 2);
        }

        if (!Caly.Rendering.Scene3D.Prc.Bits.PrcDoubles.TryFindExponent(bits & 0x7FF0000000000000, out uint code, out int length))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The exponent has no code.");
        }

        Bits(code, length).Bit((bits >> 63) != 0);
        if ((bits & 0x000FFFFFFFFFFFFF) == 0)
        {
            return Bit(false);
        }

        Bit(true).Bits((uint)(bits >> 48) & 0xF, 4);
        for (int i = 5; i >= 0; i--)
        {
            Bit(true).Bits((uint)(bits >> (8 * i)) & 0xFF, 8);
        }

        return this;
    }

    /// <summary>
    /// A compressed entity type (ISO 14739-1 draft 7.9.20.5.2): a flag, then for a curve two bits (line 0, circle 1, B-spline 2;
    /// ellipse 12 and composite 13 as 3 followed by two more bits), for anything else four bits.
    /// </summary>
    public PrcBitWriter CompressedEntityType(bool isCurve, byte value)
    {
        Bit(isCurve);
        if (!isCurve)
        {
            return Bits(value, 4);
        }

        return value switch
        {
            0 or 1 or 2 => Bits(value, 2),
            12 => Bits(3, 2).Bits(0, 2),
            13 => Bits(3, 2).Bits(1, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Not a curve type."),
        };
    }

    /// <summary>Five bits for a width, then the number in that many bits (none for 0).</summary>
    public PrcBitWriter NumberOfBitsThenUnsigned(uint value)
    {
        int width = value == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(value);
        return Bits((uint)width, 5).Bits(value, width);
    }

    /// <summary>A sign bit, then the value over the tolerance, rounded to the nearest, in <paramref name="bitCount"/> − 1 bits.</summary>
    public PrcBitWriter DoubleWithVariableBitNumber(double value, int bitCount, double tolerance)
    {
        Bit(value < 0);
        if (bitCount <= 1)
        {
            return this;
        }

        uint magnitude = (uint)Math.Round(Math.Abs(value) / tolerance);
        return Bits(magnitude, bitCount - 1);
    }

    /// <summary>
    /// A compressed point at the given tolerance (the caller passes the point tolerance, a hundredth of the body's): six
    /// bits for the width, then three numbers of that width, or three plain doubles when the width would pass 30.
    /// </summary>
    public PrcBitWriter CompressedPoint(double x, double y, double z, double tolerance)
    {
        double largest = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
        uint magnitude = (uint)Math.Round(largest / tolerance);
        int width = magnitude == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(magnitude) + 1;
        Bits((uint)width, 6);
        if (width == 0)
        {
            return this;
        }

        if (width > 30)
        {
            return Double(x).Double(y).Double(z);
        }

        return DoubleWithVariableBitNumber(x, width, tolerance).DoubleWithVariableBitNumber(y, width, tolerance).DoubleWithVariableBitNumber(z, width, tolerance);
    }

    public PrcBitWriter Short(ushort value) => Bits((uint)(value & 0xFF), 8).Bits((uint)(value >> 8), 8);

    public PrcBitWriter SignedBits(int value, int bitCount) => Bit(value < 0).Bits((uint)Math.Abs(value), Math.Max(0, bitCount - 1));

    /// <summary>The bits another writer holds, appended as they are.</summary>
    public PrcBitWriter Append(PrcBitWriter other)
    {
        long bits = other.BitLength;
        for (long i = 0; i < bits; i++)
        {
            Bit((other._bytes[(int)(i / 8)] & (0x80 >> (int)(i % 8))) != 0);
        }

        return this;
    }

    public byte[] ToArray() => [.. _bytes];
}

/// <summary>Writes a Huffman block: bits from the least significant end of each byte, padded to whole 32-bit words.</summary>
internal sealed class PrcHuffmanBlockWriter
{
    private readonly List<byte> _bytes = [];
    private long _bits;

    public PrcHuffmanBlockWriter Value(uint value, int count)
    {
        for (int i = 0; i < count; i++, _bits++)
        {
            if ((_bits & 7) == 0)
            {
                _bytes.Add(0);
            }

            if (((value >> i) & 1) != 0)
            {
                _bytes[^1] |= (byte)(1 << (int)(_bits & 7));
            }
        }

        return this;
    }

    /// <summary>A code as the stream carries it: its first bit is the one the decoder walks first.</summary>
    public PrcHuffmanBlockWriter Code(string bits)
    {
        foreach (char c in bits)
        {
            Value(c == '1' ? 1u : 0u, 1);
        }

        return this;
    }

    /// <summary>A code in the table: stored so that its first walked bit is its most significant.</summary>
    public PrcHuffmanBlockWriter TableCode(string bits)
    {
        uint value = 0;
        foreach (char c in bits)
        {
            value = (value << 1) | (c == '1' ? 1u : 0u);
        }

        return Value(value, bits.Length);
    }

    public long BitLength => _bits;

    /// <summary>The block as whole words, and how many bits of the last word are used.</summary>
    public (byte[] Words, uint BitsInLastWord) Finish()
    {
        var words = new List<byte>(_bytes);
        while (words.Count % 4 != 0)
        {
            words.Add(0);
        }

        long used = _bits - (words.Count - 4) * 8L;
        return ([.. words], (uint)used);
    }
}
