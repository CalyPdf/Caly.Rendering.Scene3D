using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Caly.Rendering.Scene3D.U3D.Internal;

/// <summary>
/// Reads the data section of one U3D block: plain values and arithmetic-coded symbols share one bit stream
/// (ECMA-363 clause 10, Annex A.3.2).
/// <para>
/// Bits are consumed least-significant first within each byte. While the arithmetic coder is in its
/// initial state (it returns there after every uncompressed byte, and leaves it only once a compressed
/// symbol narrows the interval) an uncompressed value is simply the next bits of the stream, so it is read
/// directly, and whole arrays are block-copied when the position is byte-aligned. That covers the vertex
/// arrays of a base mesh, which precede its first compressed symbol.
/// </para>
/// </summary>
internal ref struct U3DBitReader
{
    private const uint HalfMask = 0x8000;
    private const uint NotHalfMask = 0x7FFF;

    /// <summary>Bits a legitimately coded stream may be read beyond its data (look-ahead and flush).</summary>
    private const long SlackBits = 32;

    private readonly ReadOnlySpan<byte> _data;
    private readonly U3DContexts _contexts;
    private readonly bool _noCompression;
    private readonly long _bitLength;

    private long _bitPosition;
    private uint _high;
    private uint _low;
    private uint _underflow;

    public U3DBitReader(ReadOnlySpan<byte> data, U3DContexts contexts, bool noCompression)
    {
        _data = data;
        _contexts = contexts;
        _noCompression = noCompression;
        _bitLength = data.Length * 8L;
        _bitPosition = 0;
        _high = 0xFFFF;
        _low = 0;
        _underflow = 0;
        ForceSymbolPath = false;
    }

    /// <summary>Test hook: decode every uncompressed byte as a symbol, never directly.</summary>
    internal bool ForceSymbolPath { get; init; }

    /// <summary>Whole bytes consumed so far (rounded up).</summary>
    public readonly int BytePosition => (int)((_bitPosition + 7) >> 3);

    /// <summary>Bytes not yet consumed (rounded down, never negative).</summary>
    public readonly int RemainingBytes => (int)Math.Max(0, (_bitLength - _bitPosition) >> 3);

    private readonly bool IsDirect => !ForceSymbolPath && _high == 0xFFFF && _low == 0 && _underflow == 0;

    public byte ReadU8()
    {
        if (IsDirect)
        {
            return (byte)TakeBits(8);
        }

        uint symbol = ReadSymbol(U3DConstants.Context8) - 1;
        return (byte)ReverseBits8(symbol);
    }

    public ushort ReadU16()
    {
        if (IsDirect)
        {
            return (ushort)TakeBits(16);
        }

        uint low = ReadU8();
        return (ushort)(low | ((uint)ReadU8() << 8));
    }

    public uint ReadU32()
    {
        if (IsDirect)
        {
            return TakeBits(32);
        }

        uint low = ReadU16();
        return low | ((uint)ReadU16() << 16);
    }

    public ulong ReadU64()
    {
        ulong low = ReadU32();
        return low | ((ulong)ReadU32() << 32);
    }

    public int ReadI32() => (int)ReadU32();

    public float ReadF32() => BitConverter.UInt32BitsToSingle(ReadU32());

    /// <summary>Reads <c>destination.Length</c> floats; a block copy when the stream allows it.</summary>
    public void ReadF32(scoped Span<float> destination)
    {
        if (IsDirect && (_bitPosition & 7) == 0 && BitConverter.IsLittleEndian)
        {
            long bytes = destination.Length * 4L;
            if (_bitPosition + bytes * 8 > _bitLength)
            {
                ThrowTruncated();
            }

            _data.Slice((int)(_bitPosition >> 3), (int)bytes).CopyTo(MemoryMarshal.AsBytes(destination));
            _bitPosition += bytes * 8;
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadF32();
        }
    }

    /// <summary>
    /// Takes the next <paramref name="count"/> floats as a view of the block itself, without copying, when
    /// the stream allows it (the same condition as the block copy of <see cref="ReadF32(Span{float})"/>).
    /// Returns false, having consumed nothing, when they have to be decoded.
    /// </summary>
    public bool TryTakeF32(int count, out ReadOnlySpan<float> values)
    {
        if (IsDirect && (_bitPosition & 7) == 0 && BitConverter.IsLittleEndian)
        {
            long bytes = count * 4L;
            if (_bitPosition + bytes * 8 > _bitLength)
            {
                ThrowTruncated();
            }

            values = MemoryMarshal.Cast<byte, float>(_data.Slice((int)(_bitPosition >> 3), (int)bytes));
            _bitPosition += bytes * 8;
            return true;
        }

        values = default;
        return false;
    }

    /// <summary>Reads a string: a 16-bit byte count followed by UTF-8 bytes.</summary>
    public string ReadString(int maxLength)
    {
        int length = ReadU16();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > maxLength)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"A name of {length} bytes exceeds the limit of {maxLength}.");
        }

        if (IsDirect && (_bitPosition & 7) == 0)
        {
            if (_bitPosition + length * 8L > _bitLength)
            {
                ThrowTruncated();
            }

            string direct = Encoding.UTF8.GetString(_data.Slice((int)(_bitPosition >> 3), length));
            _bitPosition += length * 8L;
            return direct;
        }

        Span<byte> bytes = length <= 256 ? stackalloc byte[length] : new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = ReadU8();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Skips a string without materialising it.</summary>
    public void SkipString()
    {
        int length = ReadU16();
        for (int i = 0; i < length; i++)
        {
            ReadU8();
        }
    }

    public uint ReadCompressedU32(uint context)
    {
        if (_noCompression || context == U3DConstants.Context8 || context >= U3DConstants.MaxRange)
        {
            return ReadU32();
        }

        uint symbol = ReadSymbol(context);
        if (symbol != 0)
        {
            return symbol - 1;
        }

        // Escape: the value follows uncompressed and joins the histogram.
        uint value = ReadU32();
        _contexts.AddSymbol(context, value + 1);
        return value;
    }

    public ushort ReadCompressedU16(uint context)
    {
        if (_noCompression || context == U3DConstants.Context8 || context >= U3DConstants.MaxRange)
        {
            return ReadU16();
        }

        uint symbol = ReadSymbol(context);
        if (symbol != 0)
        {
            return (ushort)(symbol - 1);
        }

        ushort value = ReadU16();
        _contexts.AddSymbol(context, value + 1u);
        return value;
    }

    public byte ReadCompressedU8(uint context)
    {
        if (_noCompression || context == U3DConstants.Context8 || context >= U3DConstants.MaxRange)
        {
            return ReadU8();
        }

        uint symbol = ReadSymbol(context);
        if (symbol != 0)
        {
            return (byte)(symbol - 1);
        }

        byte value = ReadU8();
        _contexts.AddSymbol(context, value + 1u);
        return value;
    }

    /// <summary>
    /// Decodes one symbol. Symbol 0 of a dynamic context is the escape; a static context of N symbols
    /// yields 1..N; the byte context yields 1..256.
    /// </summary>
    private uint ReadSymbol(uint context)
    {
        // The 16-bit code word: the next bit, then, after any underflow bits, the following fifteen.
        uint code = PeekBits(_bitPosition, 1) << 15;
        code |= ReverseBits16(PeekBits(_bitPosition + 1 + _underflow, 15)) >> 1;

        uint low = _low, high = _high;
        uint range = high + 1 - low;
        if (range == 0 || code < low || code > high)
        {
            ThrowCorrupt();
        }

        bool dynamic = U3DContexts.IsDynamic(context);
        uint total = dynamic
            ? _contexts.GetTotalSymbolFrequency(context)
            : context == U3DConstants.Context8 ? 256u : context - U3DConstants.StaticFull;

        uint codeCumulative = (total * (1 + code - low) - 1) / range;

        uint symbol, cumulative, frequency;
        if (dynamic)
        {
            symbol = _contexts.GetSymbolFromFrequency(context, codeCumulative);
            cumulative = _contexts.GetCumulativeSymbolFrequency(context, symbol);
            frequency = _contexts.GetSymbolFrequency(context, symbol);
            if (frequency == 0)
            {
                ThrowCorrupt();
            }
        }
        else
        {
            // Equally likely symbols: the cumulative frequency is the symbol itself.
            symbol = codeCumulative + 1;
            cumulative = codeCumulative;
            frequency = 1;
        }

        high = low - 1 + range * (cumulative + frequency) / total;
        low += range * cumulative / total;

        if (dynamic)
        {
            _contexts.AddSymbol(context, symbol);
        }

        // Shift out the leading bits the interval's ends now share: zeros come in under low, ones under
        // high. Annex A does this a bit at a time; the number of shared leading bits is counted at once.
        // (Ends that are equal share all sixteen.)
        int bitCount = BitOperations.LeadingZeroCount((low ^ high) << 16 | 0x8000);
        low = (low << bitCount) & 0xFFFF;
        high = ((high << bitCount) | ((1u << bitCount) - 1)) & 0xFFFF;

        uint savedLow = HalfMask & low, savedHigh = HalfMask & high;
        if (bitCount > 0)
        {
            bitCount += (int)_underflow;
            _underflow = 0;
        }

        // Underflow: the ends straddle the midpoint (low 01…, high 10…). Every further position where low
        // has a one and high a zero is dropped, the bits below moving up; again counted at once. At most
        // fifteen: the zero shifted in under low ends the run.
        int underflow = BitOperations.LeadingZeroCount(~((low & ~high) << 17));
        low = ((low & NotHalfMask) << underflow) & NotHalfMask;
        high = (((high & NotHalfMask) << underflow) | ((1u << underflow) - 1)) & NotHalfMask;

        _underflow += (uint)underflow;
        _low = low | savedLow;
        _high = high | savedHigh;

        _bitPosition += bitCount;
        if (_bitPosition > _bitLength + SlackBits)
        {
            ThrowTruncated();
        }

        return symbol;
    }

    /// <summary>Consumes <paramref name="count"/> bits (at most 32) that must lie within the data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint TakeBits(int count)
    {
        if (_bitPosition + count > _bitLength)
        {
            ThrowTruncated();
        }

        uint value = PeekBits(_bitPosition, count);
        _bitPosition += count;
        return value;
    }

    /// <summary>
    /// The <paramref name="count"/> bits (at most 32) starting at <paramref name="bitPosition"/>, first bit
    /// in the least significant position. Bits beyond the data read as zero.
    /// </summary>
    private readonly uint PeekBits(long bitPosition, int count)
    {
        long byteIndex = bitPosition >> 3;
        int shift = (int)(bitPosition & 7);

        ulong window;
        if (byteIndex + 8 <= _data.Length)
        {
            window = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice((int)byteIndex));
        }
        else
        {
            window = 0;
            for (int i = 0; i < 8 && byteIndex + i < _data.Length; i++)
            {
                window |= (ulong)_data[(int)(byteIndex + i)] << (i * 8);
            }
        }

        ulong mask = count == 32 ? 0xFFFFFFFFul : (1ul << count) - 1;
        return (uint)((window >> shift) & mask);
    }

    private static uint ReverseBits8(uint value)
    {
        value = ((value >> 1) & 0x55) | ((value & 0x55) << 1);
        value = ((value >> 2) & 0x33) | ((value & 0x33) << 2);
        return ((value >> 4) & 0x0F) | ((value & 0x0F) << 4);
    }

    private static uint ReverseBits16(uint value)
    {
        value = ((value >> 1) & 0x5555) | ((value & 0x5555) << 1);
        value = ((value >> 2) & 0x3333) | ((value & 0x3333) << 2);
        value = ((value >> 4) & 0x0F0F) | ((value & 0x0F0F) << 4);
        return ((value >> 8) & 0x00FF) | ((value & 0x00FF) << 8);
    }

    private static void ThrowTruncated()
    {
        throw new Scene3DException(Scene3DError.Truncated, "The U3D block ends before its content is complete.");
    }

    private static void ThrowCorrupt()
    {
        throw new Scene3DException(Scene3DError.InvalidValue, "The U3D block contains corrupt arithmetic-coded data.");
    }
}
