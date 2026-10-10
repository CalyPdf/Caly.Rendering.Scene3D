using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Caly.Rendering.Scene3D.Prc.Bits;

/// <summary>
/// Reads the bit stream of a PRC section: most significant bit first, nothing aligned, every value in
/// one of the format's own encodings. An entity has no length field; its extent is known only by reading
/// it, so every read checks the end of the data and fails with <see cref="Scene3DException"/>.
/// <para>
/// The encodings are those of ISO 14739-1 as real files use them; the bit-level details follow prc-rs
/// (MIT, see THIRD-PARTY-NOTICES.md).
/// </para>
/// </summary>
internal ref struct PrcBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private long _position;

    public PrcBitReader(ReadOnlySpan<byte> data, int maxStringLength = int.MaxValue)
    {
        _data = data;
        _position = 0;
        MaxStringLength = maxStringLength;
    }

    /// <summary>Longest string, in bytes, that is built; a longer one fails the read (it can still be read past).</summary>
    public int MaxStringLength { get; }

    /// <summary>Bits read so far.</summary>
    public readonly long BitPosition => _position;

    /// <summary>Bits in the data.</summary>
    public readonly long BitLength => (long)_data.Length * 8;

    /// <summary>Bits not yet read.</summary>
    public readonly long BitsLeft => BitLength - _position;

    /// <summary>Moves to a bit position, to read again what was peeked.</summary>
    public void Seek(long bitPosition)
    {
        if ((ulong)bitPosition > (ulong)BitLength)
        {
            ThrowTruncated();
        }

        _position = bitPosition;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBoolean()
    {
        long position = _position;
        int index = (int)(position >> 3);
        if ((uint)index >= (uint)_data.Length)
        {
            ThrowTruncated();
        }

        _position = position + 1;
        return ((_data[index] >> (7 - (int)(position & 7))) & 1) != 0;
    }

    /// <summary>Reads up to 32 bits as an unsigned number, first bit most significant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        long position = _position;
        if ((uint)count > 32 || BitLength - position < count)
        {
            ThrowTruncated();
        }

        int index = (int)(position >> 3);
        int offset = (int)(position & 7);
        ulong window;
        if (index + 8 <= _data.Length)
        {
            window = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(index));
        }
        else
        {
            // Near the end: the bytes that exist, left-aligned.
            window = 0;
            int available = _data.Length - index;
            for (int i = 0; i < available; i++)
            {
                window |= (ulong)_data[index + i] << (56 - 8 * i);
            }
        }

        _position = position + count;
        return (uint)((window << offset) >> (64 - count));
    }

    /// <summary>
    /// The format's unsigned integer: up to four bytes, least significant first, each announced by a
    /// set bit; a clear bit ends it.
    /// </summary>
    public uint ReadUnsignedInteger()
    {
        uint value = 0;
        int i = 0;
        while (ReadBoolean() && i < 4)
        {
            value |= ReadBits(8) << (8 * i);
            i++;
        }

        return value;
    }

    /// <summary>The format's signed integer: as the unsigned one, sign-extended from its last byte.</summary>
    public int ReadInteger()
    {
        int value = 0;
        int count = 0;
        while (ReadBoolean())
        {
            if (count == 4)
            {
                throw new Scene3DException(Scene3DError.InvalidValue, "A PRC integer has more than four bytes.");
            }

            value |= (int)ReadBits(8) << (8 * count);
            count++;
        }

        if (count is > 0 and < 4)
        {
            int shift = (4 - count) * 8;
            value = (value << shift) >> shift;
        }

        return value;
    }

    public sbyte ReadCharacter() => (sbyte)ReadBits(8);

    public byte ReadUnsignedCharacter() => (byte)ReadBits(8);

    public ushort ReadUnsignedShort()
    {
        uint low = ReadBits(8);
        return (ushort)(low | (ReadBits(8) << 8));
    }

    /// <summary>A single-precision number stored as its four bytes, least significant first.</summary>
    public float ReadFloatAsBytes()
    {
        return BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReverseEndianness(ReadBits(32)));
    }

    public double ReadDouble() => PrcDoubles.Read(ref this);

    /// <summary>A number of the given width, first bit most significant.</summary>
    public uint ReadUnsignedIntegerWithVariableBitNumber(int bitCount)
    {
        if ((uint)bitCount > 32)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A PRC value claims {bitCount} bits.");
        }

        return ReadBits(bitCount);
    }

    /// <summary>A sign bit, then the magnitude in one bit fewer than the given width.</summary>
    public int ReadIntegerWithVariableBitNumber(int bitCount)
    {
        if ((uint)bitCount > 32)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A PRC value claims {bitCount} bits.");
        }

        bool negative = ReadBoolean();
        uint magnitude = bitCount <= 1 ? 0 : ReadBits(bitCount - 1);
        return negative ? -(int)magnitude : (int)magnitude;
    }

    /// <summary>
    /// A compressed entity type: a flag; for a curve two bits (line 0, circle 1, B-spline 2, or 3 and two more bits: ellipse 0,
    /// composite 1); for anything else four bits (0 – 14).
    /// </summary>
    public PrcCompressedEntity ReadCompressedEntityType()
    {
        if (!ReadBoolean())
        {
            uint face = ReadBits(4);
            if (face > 14)
            {
                throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC face type of {face} is not one the format has.");
            }

            return new PrcCompressedEntity(false, (byte)face);
        }

        uint curve = ReadBits(2);
        if (curve < 3)
        {
            return new PrcCompressedEntity(true, (byte)curve);
        }

        uint rare = ReadBits(2);
        return rare switch
        {
            0 => new PrcCompressedEntity(true, 12),
            1 => new PrcCompressedEntity(true, 13),
            _ => throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC curve type of 3/{rare} is not one the format has."),
        };
    }

    /// <summary>
    /// A sign bit, then, for a width above one bit, <paramref name="bitCount"/> − 1 bits of magnitude, first bit most significant,
    /// in multiples of <paramref name="tolerance"/>. The width must be between 1 and 32.
    /// </summary>
    public double ReadDoubleWithVariableBitNumber(int bitCount, double tolerance)
    {
        if ((uint)(bitCount - 1) >= 32)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A PRC value claims {bitCount} bits.");
        }

        bool negative = ReadBoolean();
        if (bitCount == 1)
        {
            return negative ? -0.0 : 0.0;
        }

        double magnitude = ReadBits(bitCount - 1) * tolerance;
        return negative ? -magnitude : magnitude;
    }

    /// <summary>Three variable-bit numbers of the same width and tolerance.</summary>
    public PrcVector ReadPoint3DWithVariableBitNumber(int bitCount, double tolerance)
    {
        double x = ReadDoubleWithVariableBitNumber(bitCount, tolerance);
        double y = ReadDoubleWithVariableBitNumber(bitCount, tolerance);
        double z = ReadDoubleWithVariableBitNumber(bitCount, tolerance);
        return new PrcVector(x, y, z);
    }

    /// <summary>
    /// A compressed point: six bits for the width; none → the origin; up to 30 → three numbers of that width at
    /// <paramref name="tolerance"/> (a hundredth of the body's, which the caller passes); more → three plain doubles
    /// (PDF Association issue #706). A tolerance that is not a positive number is refused.
    /// </summary>
    public PrcVector ReadCompressedPoint(double tolerance)
    {
        if (!(tolerance > 0) || double.IsInfinity(tolerance))
        {
            throw new Scene3DException(Scene3DError.InvalidValue, "A compressed PRC body has a tolerance that is not a positive number.");
        }

        int width = (int)ReadBits(6);
        if (width == 0)
        {
            return default;
        }

        if (width <= 30)
        {
            return ReadPoint3DWithVariableBitNumber(width, tolerance);
        }

        double x = ReadDouble();
        double y = ReadDouble();
        double z = ReadDouble();
        return new PrcVector(x, y, z);
    }

    /// <summary>Five bits giving a width, then a number of that width.</summary>
    public uint ReadNumberOfBitsThenUnsignedInteger()
    {
        return ReadBits((int)ReadBits(5));
    }

    /// <summary>
    /// A string: a bit saying whether there is one, then its length in bytes and the bytes. UTF-8; a
    /// string that is not valid UTF-8 is read as Latin-1, as older writers produced it.
    /// </summary>
    public string ReadString()
    {
        if (!ReadBoolean())
        {
            return string.Empty;
        }

        int length = ReadLength(8);
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > MaxStringLength)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"A name in the PRC file is {length} bytes long; {MaxStringLength} are allowed.");
        }

        byte[]? rented = null;
        Span<byte> bytes = length <= 256 ? stackalloc byte[length] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            ReadBytes(bytes);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Reads past a string without building it.</summary>
    public void SkipString()
    {
        if (ReadBoolean())
        {
            Skip((long)ReadLength(8) * 8);
        }
    }

    /// <summary>User data: a bit count, then that many bits. Never interpreted.</summary>
    public void SkipUserData()
    {
        Skip(ReadUnsignedInteger());
    }

    /// <summary>Reads past a number of bits.</summary>
    public void Skip(long bitCount)
    {
        if (bitCount < 0 || BitsLeft < bitCount)
        {
            ThrowTruncated();
        }

        _position += bitCount;
    }

    /// <summary>Reads whole bytes that lie at any bit offset.</summary>
    public void ReadBytes(scoped Span<byte> destination)
    {
        if (BitsLeft < (long)destination.Length * 8)
        {
            ThrowTruncated();
        }

        if ((_position & 7) == 0)
        {
            _data.Slice((int)(_position >> 3), destination.Length).CopyTo(destination);
            _position += (long)destination.Length * 8;
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)ReadBits(8);
        }
    }

    /// <summary>
    /// Reads a count of things that each take at least <paramref name="minimumBitsEach"/> bits, and
    /// refuses a count the rest of the data could not hold: nothing is ever allocated on a count alone.
    /// </summary>
    public int ReadLength(int minimumBitsEach)
    {
        uint count = ReadUnsignedInteger();
        if (count > int.MaxValue || (long)count * minimumBitsEach > BitsLeft)
        {
            throw new Scene3DException(Scene3DError.Truncated, $"A PRC array of {count} entries does not fit in the {BitsLeft} bits left.");
        }

        return (int)count;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowTruncated()
    {
        throw new Scene3DException(Scene3DError.Truncated, "The PRC data ends inside a value.");
    }
}
