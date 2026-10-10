using System.Buffers;
using System.Buffers.Binary;

namespace Caly.Rendering.Scene3D.Prc.Bits;

/// <summary>
/// The compressed arrays of PRC: arrays of small integers that are either stored plainly or
/// Huffman-coded, and the arrays of integers and of indices built on them.
/// <para>
/// A Huffman-coded array is a block of 32-bit words read as a bit stream of its own, least significant
/// bit first: the code table (symbol, code length, code), the element count, then the codes. Ported from
/// prc-rs (src/huffman.rs and src/builtin.rs, MIT; see THIRD-PARTY-NOTICES.md).
/// </para>
/// </summary>
internal static class PrcHuffman
{
    /// <summary>
    /// Most elements of an array whose table has a single code of no length: such elements take no bits,
    /// so nothing else bounds their number.
    /// </summary>
    public const int MaxFreeElements = 1 << 24;

    /// <summary>
    /// Reads an array of values of <paramref name="bitsPerElement"/> bits each (1 to 16) into a pooled
    /// buffer. The caller returns <paramref name="rented"/> to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <param name="reader">The section's bit stream.</param>
    /// <param name="hasCompressedBit">Whether a bit in the stream says if the array is Huffman-coded.</param>
    /// <param name="bitsPerElement">Width of a symbol in the Huffman table.</param>
    /// <param name="compressedByDefault">What holds when there is no such bit.</param>
    /// <param name="signExtend">Whether symbols are signed numbers of that width.</param>
    /// <param name="wide">Whether a plainly stored element takes two bytes instead of one.</param>
    /// <param name="rented">The elements, in a rented array that may be longer than their number.</param>
    /// <returns>The number of elements.</returns>
    public static int ReadElements(ref PrcBitReader reader, bool hasCompressedBit, int bitsPerElement, bool compressedByDefault,
        bool signExtend, bool wide, out short[] rented)
    {
        bool compressed = hasCompressedBit ? reader.ReadBoolean() : compressedByDefault;
        if (!compressed)
        {
            // Plain: a count, then one byte (or two) per element.
            int count = reader.ReadLength(wide ? 16 : 8);
            rented = ArrayPool<short>.Shared.Rent(Math.Max(count, 1));
            for (int i = 0; i < count; i++)
            {
                rented[i] = wide ? (short)reader.ReadUnsignedShort() : (short)(sbyte)reader.ReadBits(8);
            }

            return count;
        }

        int wordCount = reader.ReadLength(32);
        if (wordCount == 0)
        {
            rented = ArrayPool<short>.Shared.Rent(1);
            return 0;
        }

        byte[] block = ArrayPool<byte>.Shared.Rent(wordCount * 4);
        try
        {
            Span<byte> bytes = block.AsSpan(0, wordCount * 4);
            reader.ReadBytes(bytes);
            uint bitsInLastWord = reader.ReadUnsignedInteger();
            if (bitsInLastWord > 32)
            {
                throw Invalid("A PRC Huffman block uses more than 32 bits of its last word.");
            }

            long totalBits = (long)bytes.Length * 8 - 32 + bitsInLastWord;
            return Decode(bytes, totalBits, bitsPerElement, signExtend, out rented);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    /// <summary>Reads past such an array.</summary>
    public static void SkipElements(ref PrcBitReader reader, bool hasCompressedBit, bool compressedByDefault, bool wide)
    {
        bool compressed = hasCompressedBit ? reader.ReadBoolean() : compressedByDefault;
        if (!compressed)
        {
            int count = reader.ReadLength(wide ? 16 : 8);
            reader.Skip((long)count * (wide ? 16 : 8));
            return;
        }

        int wordCount = reader.ReadLength(32);
        if (wordCount == 0)
        {
            return;
        }

        reader.Skip((long)wordCount * 32);
        if (reader.ReadUnsignedInteger() > 32)
        {
            throw Invalid("A PRC Huffman block uses more than 32 bits of its last word.");
        }
    }

    /// <summary>
    /// A compressed array of integers: the width of each as a Huffman-coded array of 6-bit values, then
    /// each integer as a sign bit and its magnitude.
    /// </summary>
    public static int ReadIntegerArray(ref PrcBitReader reader, out int[] rented)
    {
        int count = ReadElements(ref reader, true, 6, true, true, false, out short[] widths);
        try
        {
            rented = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
            for (int i = 0; i < count; i++)
            {
                rented[i] = reader.ReadIntegerWithVariableBitNumber(Width(widths[i]));
            }

            return count;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(widths);
        }
    }

    /// <summary>
    /// A compressed array of indices: the widths are stored as differences from one to the next, and so
    /// are the values.
    /// </summary>
    public static int ReadIndiceArray(ref PrcBitReader reader, bool hasCompressedBit, bool compressedByDefault, out int[] rented)
    {
        int count = ReadElements(ref reader, hasCompressedBit, 6, compressedByDefault, true, false, out short[] widthSteps);
        try
        {
            rented = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
            int width = 0;
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                width += widthSteps[i];
                int value = reader.ReadIntegerWithVariableBitNumber(Width(width)) + previous;
                if (value < 0)
                {
                    throw Invalid("A PRC index array holds a negative index.");
                }

                rented[i] = value;
                previous = value;
            }

            return count;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(widthSteps);
        }
    }

    private static int Width(int width)
    {
        if ((uint)width > 32)
        {
            throw Invalid($"A PRC compressed array gives a value {width} bits.");
        }

        return width;
    }

    /// <summary>Decodes a Huffman block.</summary>
    internal static int Decode(ReadOnlySpan<byte> block, long totalBits, int bitsPerElement, bool signExtend, out short[] rented)
    {
        if (bitsPerElement is < 1 or > 16)
        {
            throw Invalid($"A PRC Huffman array has elements of {bitsPerElement} bits.");
        }

        var bits = new LsbReader(block);
        int leafCount = (int)bits.Read(bitsPerElement + 1);
        if (leafCount == 0)
        {
            rented = ArrayPool<short>.Shared.Rent(1);
            return 0;
        }

        int maxCodeLength = (int)bits.Read(8);
        if (maxCodeLength is < 1 or > 32)
        {
            throw Invalid($"A PRC Huffman table has codes of up to {maxCodeLength} bits.");
        }

        // The code tree in an array: for node n, [2n] and [2n + 1] are where a 0 and a 1 lead; a value
        // > 0 is a node, a value < 0 is ~symbol-slot, 0 is nothing. Codes are walked from their most
        // significant bit.
        int[] tree = ArrayPool<int>.Shared.Rent(Math.Max(4, leafCount * 2 * 2 + 2));
        short[] symbols = ArrayPool<short>.Shared.Rent(leafCount);
        try
        {
            tree.AsSpan(0, 2).Clear();
            int nodeCount = 1;
            int soleLeaf = -1;
            for (int leaf = 0; leaf < leafCount; leaf++)
            {
                uint raw = bits.Read(bitsPerElement);
                symbols[leaf] = signExtend
                    ? (short)((int)(raw << (32 - bitsPerElement)) >> (32 - bitsPerElement))
                    : (short)raw;
                int codeLength = (int)bits.Read(maxCodeLength);
                if (codeLength > 32)
                {
                    throw Invalid("A PRC Huffman code is longer than 32 bits.");
                }

                if (codeLength == 0)
                {
                    // A table of one symbol: its code is empty and every element is that symbol. (prc-rs
                    // decodes such elements as zero; the symbol is what the table says.)
                    if (leafCount != 1)
                    {
                        throw Invalid("A PRC Huffman table has an empty code beside others.");
                    }

                    soleLeaf = leaf;
                    continue;
                }

                uint code = bits.Read(codeLength);
                int node = 0;
                for (int bit = codeLength - 1; bit >= 0; bit--)
                {
                    int slot = node * 2 + (int)((code >> bit) & 1);
                    if (bit == 0)
                    {
                        if (tree[slot] != 0)
                        {
                            throw Invalid("A PRC Huffman table gives two symbols one code.");
                        }

                        tree[slot] = ~leaf;
                    }
                    else
                    {
                        if (tree[slot] < 0)
                        {
                            throw Invalid("A PRC Huffman code continues past another code.");
                        }

                        if (tree[slot] == 0)
                        {
                            if ((nodeCount + 1) * 2 > tree.Length)
                            {
                                int[] larger = ArrayPool<int>.Shared.Rent(tree.Length * 2);
                                tree.AsSpan(0, nodeCount * 2).CopyTo(larger);
                                ArrayPool<int>.Shared.Return(tree);
                                tree = larger;
                            }

                            tree[slot] = nodeCount;
                            tree.AsSpan(nodeCount * 2, 2).Clear();
                            nodeCount++;
                        }

                        node = tree[slot];
                    }
                }
            }

            uint elementCount = bits.Read(32);
            if (elementCount > (soleLeaf >= 0 ? (ulong)MaxFreeElements : (ulong)Math.Max(0, totalBits)))
            {
                // Every element takes at least one bit of the block, unless the table has the single
                // empty code.
                throw Invalid($"A PRC Huffman array claims {elementCount} elements in a block of {totalBits} bits.");
            }

            rented = ArrayPool<short>.Shared.Rent(Math.Max((int)elementCount, 1));
            if (soleLeaf >= 0)
            {
                rented.AsSpan(0, (int)elementCount).Fill(symbols[soleLeaf]);
                return (int)elementCount;
            }

            for (int i = 0; i < (int)elementCount; i++)
            {
                int node = 0;
                while (true)
                {
                    int next = tree[node * 2 + (int)bits.Read(1)];
                    if (next < 0)
                    {
                        rented[i] = symbols[~next];
                        break;
                    }

                    if (next == 0)
                    {
                        ArrayPool<short>.Shared.Return(rented);
                        throw Invalid("A PRC Huffman array holds a code that is in no table.");
                    }

                    node = next;
                }
            }

            return (int)elementCount;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(tree);
            ArrayPool<short>.Shared.Return(symbols);
        }
    }

    private static Scene3DException Invalid(string message) => new(Scene3DError.InvalidValue, message);

    /// <summary>Reads a Huffman block: bits from the least significant end of each byte.</summary>
    private ref struct LsbReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _position;

        public LsbReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        /// <summary>Reads up to 32 bits; the first bit read is the least significant of the result.</summary>
        public uint Read(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            long position = _position;
            if ((long)_data.Length * 8 - position < count)
            {
                throw new Scene3DException(Scene3DError.Truncated, "A PRC Huffman block ends inside a value.");
            }

            int index = (int)(position >> 3);
            int offset = (int)(position & 7);
            ulong window;
            if (index + 8 <= _data.Length)
            {
                window = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(index));
            }
            else
            {
                window = 0;
                for (int i = 0; index + i < _data.Length; i++)
                {
                    window |= (ulong)_data[index + i] << (8 * i);
                }
            }

            _position = position + count;
            ulong value = window >> offset;
            return count == 32 ? (uint)value : (uint)(value & ((1UL << count) - 1));
        }
    }
}
