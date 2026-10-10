using System.Buffers.Binary;

namespace Caly.Rendering.Scene3D.U3D.Internal;

/// <summary>One block of a U3D file: where its data section is, and how deeply it is nested in modifier chains.</summary>
internal readonly record struct U3DBlock(uint Type, int DataOffset, int DataLength, int Depth);

/// <summary>The fields of the file header block (ECMA-363 9.4.1).</summary>
internal readonly record struct U3DHeader(short MajorVersion, short MinorVersion, uint Profile, uint DeclarationSize, ulong FileSize, uint CharacterEncoding, double UnitsScale)
{
    private const uint ProfileNoCompression = 0x4;
    private const uint ProfileDefinedUnits = 0x8;

    /// <summary>The whole file is written without arithmetic coding.</summary>
    public bool NoCompression => (Profile & ProfileNoCompression) != 0;

    public static U3DHeader Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 + 24)
        {
            throw new Scene3DException(Scene3DError.Truncated, "The data is too short to be a U3D file.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(file) != U3DConstants.BlockFileHeader)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, "The data does not start with a U3D file header.");
        }

        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        if (dataSize < 24 || dataSize > (uint)(file.Length - 12))
        {
            throw new Scene3DException(Scene3DError.InvalidValue, "The U3D file header has an invalid size.");
        }

        ReadOnlySpan<byte> data = file.Slice(12, (int)dataSize);
        uint profile = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        double units = 1;
        if ((profile & ProfileDefinedUnits) != 0 && data.Length >= 32)
        {
            units = BinaryPrimitives.ReadDoubleLittleEndian(data[24..]);
            if (!double.IsFinite(units) || units <= 0)
            {
                units = 1;
            }
        }

        return new U3DHeader(
            BinaryPrimitives.ReadInt16LittleEndian(data),
            BinaryPrimitives.ReadInt16LittleEndian(data[2..]),
            profile,
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            units);
    }
}

/// <summary>
/// Enumerates the blocks of a U3D file in file order, descending into modifier chains (ECMA-363 9.2, 9.4.3).
/// Block headers and the modifier chain preamble are never arithmetic-coded, so they are read as bytes.
/// </summary>
internal static class U3DBlockWalker
{
    private const int HeaderSize = 12;

    public static List<U3DBlock> Walk(ReadOnlySpan<byte> file, SceneLimits limits, CancellationToken cancellationToken)
    {
        var blocks = new List<U3DBlock>(256);
        Walk(file, 0, file.Length, depth: 0, limits, blocks, cancellationToken);
        return blocks;
    }

    private static void Walk(ReadOnlySpan<byte> file, int start, int end, int depth, SceneLimits limits, List<U3DBlock> blocks, CancellationToken cancellationToken)
    {
        if (depth > limits.MaxNestingDepth)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"Modifier chains are nested deeper than {limits.MaxNestingDepth}.");
        }

        long position = start;
        while (position + HeaderSize <= end)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> header = file.Slice((int)position, HeaderSize);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            uint metaSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            long dataStart = position + HeaderSize;

            if (dataStart + dataSize > end)
            {
                throw new Scene3DException(Scene3DError.Truncated, $"A U3D block of type 0x{type:X8} at offset {position} extends past the end of its container.");
            }

            blocks.Add(new U3DBlock(type, (int)dataStart, (int)dataSize, depth));

            if (type == U3DConstants.BlockModifierChain)
            {
                int nestedStart = ModifierChainContentOffset(file.Slice((int)dataStart, (int)dataSize));
                Walk(file, (int)dataStart + nestedStart, (int)(dataStart + dataSize), depth + 1, limits, blocks, cancellationToken);
            }

            // Data and metadata are each padded to a 32-bit boundary; the last block may omit trailing padding.
            position = dataStart + Pad(dataSize) + Pad(metaSize);
        }
    }

    /// <summary>
    /// Offset, within a modifier chain's data, of its first contained declaration block: after the name,
    /// type, attributes, optional bounds, alignment padding and the modifier count.
    /// </summary>
    private static int ModifierChainContentOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw Truncated();
        }

        long offset = 2 + BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (offset + 8 > data.Length)
        {
            throw Truncated();
        }

        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[((int)offset + 4)..]);
        offset += 8;
        if ((attributes & 1) != 0)
        {
            offset += 16; // bounding sphere
        }

        if ((attributes & 2) != 0)
        {
            offset += 24; // axis-aligned bounding box
        }

        offset = Pad(offset) + 4; // alignment, then the modifier count
        if (offset > data.Length)
        {
            throw Truncated();
        }

        return (int)offset;

        static Scene3DException Truncated() => new(Scene3DError.Truncated, "A U3D modifier chain is shorter than its own preamble.");
    }

    private static long Pad(long size) => (size + 3) & ~3L;
}
