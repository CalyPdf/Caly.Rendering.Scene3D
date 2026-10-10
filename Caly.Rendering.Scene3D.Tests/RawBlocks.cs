using System.Buffers.Binary;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// A deliberately naive, independent walk over the blocks of a U3D file, used by tests to locate blocks and
/// to cross-check the product's block walker. Reads headers as plain little-endian bytes.
/// </summary>
internal static class RawBlocks
{
    public readonly record struct Block(uint Type, int DataStart, int DataSize, bool Nested);

    public const uint FileHeader = 0x00443355, ModifierChain = 0xFFFFFF14, MeshDeclaration = 0xFFFFFF31, BaseMesh = 0xFFFFFF3B;

    public static List<Block> Enumerate(byte[] file)
    {
        var blocks = new List<Block>();
        Walk(file, 0, file.Length, nested: false, blocks);
        return blocks;
    }

    public static ReadOnlySpan<byte> Data(byte[] file, Block block) => file.AsSpan(block.DataStart, block.DataSize);

    private static void Walk(byte[] d, int start, int end, bool nested, List<Block> blocks)
    {
        int pos = start;
        while (pos + 12 <= end)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
            int dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 4));
            int metaSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 8));
            int dataStart = pos + 12;
            blocks.Add(new Block(type, dataStart, dataSize, nested));

            if (type == ModifierChain)
            {
                ReadOnlySpan<byte> data = d.AsSpan(dataStart, dataSize);
                int p = 2 + BinaryPrimitives.ReadUInt16LittleEndian(data);
                uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]);
                p += 8;
                if ((attributes & 1) != 0) p += 16;
                if ((attributes & 2) != 0) p += 24;
                p = ((p + 3) & ~3) + 4;
                Walk(d, dataStart + p, dataStart + dataSize, nested: true, blocks);
            }

            pos = dataStart + ((dataSize + 3) & ~3) + ((metaSize + 3) & ~3);
        }
    }
}
