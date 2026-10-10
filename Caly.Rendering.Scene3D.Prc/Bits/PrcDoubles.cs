namespace Caly.Rendering.Scene3D.Prc.Bits;

/// <summary>
/// The PRC encoding of double-precision numbers. A prefix code of up to 22 bits names either a whole
/// frequent value or an exponent; after an exponent come the sign, and optionally the mantissa, byte by
/// byte from the most significant, each byte either given or copied from one before it.
/// <para>
/// Ported from prc-rs (src/double.rs, MIT; see THIRD-PARTY-NOTICES.md), which also supplies the table.
/// </para>
/// </summary>
internal static partial class PrcDoubles
{
    private const int MaxCodeBits = 22;

    // The prefix code as a binary tree in an array: for node n, Tree[2n] and Tree[2n + 1] are what a 0
    // and a 1 lead to. A value >= 0 is the next node; a value < 0 is ~entry.
    private static readonly int[] Tree = BuildTree();

    private static int[] BuildTree()
    {
        ReadOnlySpan<uint> codes = TableCodes;
        var tree = new List<int> { 0, 0 };
        for (int entry = 0; entry < codes.Length; entry++)
        {
            int length = (int)((codes[entry] >> 24) & 0x1F);
            uint code = codes[entry] & 0x3FFFFF;
            int node = 0;
            for (int bit = length - 1; bit >= 0; bit--)
            {
                int slot = node * 2 + (int)((code >> bit) & 1);
                if (tree[slot] < 0)
                {
                    // A shorter code already ends here; it is found first, so this one never is.
                    break;
                }

                if (bit == 0)
                {
                    tree[slot] = ~entry;
                }
                else
                {
                    if (tree[slot] == 0)
                    {
                        tree[slot] = tree.Count / 2;
                        tree.Add(0);
                        tree.Add(0);
                    }

                    node = tree[slot];
                }
            }
        }

        return tree.ToArray();
    }

    /// <summary>
    /// The code that stands for an exponent (the eleven exponent bits of a double, in place, the rest
    /// zero), for writers of the encoding: the tests have one.
    /// </summary>
    internal static bool TryFindExponent(ulong exponentBits, out uint code, out int length)
    {
        ReadOnlySpan<uint> codes = TableCodes;
        ReadOnlySpan<ulong> values = TableValues;
        for (int entry = 0; entry < codes.Length; entry++)
        {
            if ((codes[entry] & 0x80000000) != 0 && (values[entry] & 0x7FFFFFFFFFFFFFFF) == exponentBits)
            {
                code = codes[entry] & 0x3FFFFF;
                length = (int)((codes[entry] >> 24) & 0x1F);
                return true;
            }
        }

        code = 0;
        length = 0;
        return false;
    }

    public static double Read(ref PrcBitReader reader)
    {
        int[] tree = Tree;
        int node = 0;
        int entry = -1;
        for (int i = 0; i < MaxCodeBits; i++)
        {
            int next = tree[node * 2 + (reader.ReadBoolean() ? 1 : 0)];
            if (next < 0)
            {
                entry = ~next;
                break;
            }

            if (next == 0)
            {
                break;
            }

            node = next;
        }

        if (entry < 0)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, "A PRC double starts with no known code.");
        }

        uint code = TableCodes[entry];
        ulong bits = TableValues[entry];
        bool isExponent = (code & 0x80000000) != 0;

        // The two-bit code 01 is zero, complete as it is.
        if (!isExponent && (code & 0x1FFFFFFF) == ((2u << 24) | 1u))
        {
            return BitConverter.UInt64BitsToDouble(bits);
        }

        if (reader.ReadBoolean())
        {
            bits |= 0x8000000000000000;
        }
        else
        {
            bits &= 0x7FFFFFFFFFFFFFFF;
        }

        if (!isExponent || !reader.ReadBoolean())
        {
            return BitConverter.UInt64BitsToDouble(bits);
        }

        // The mantissa: its top four bits, then bytes 5 down to 0 of the number.
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, bits);
        bytes[6] |= (byte)reader.ReadBits(4);

        int last = 0;
        int current = 5;
        while (current >= last)
        {
            if (reader.ReadBoolean())
            {
                bytes[current] = (byte)reader.ReadBits(8);
            }
            else
            {
                int offset = (int)reader.ReadBits(3);
                if (offset == 0)
                {
                    // Every remaining byte repeats the one before.
                    byte repeated = bytes[current + 1];
                    while (current >= last)
                    {
                        bytes[current--] = repeated;
                    }

                    break;
                }

                if (offset == 6)
                {
                    // As above, but for the last byte, which is given.
                    byte repeated = bytes[current + 1];
                    last++;
                    while (current >= last)
                    {
                        bytes[current--] = repeated;
                    }

                    bytes[current] = (byte)reader.ReadBits(8);
                    break;
                }

                if (current + offset >= 8)
                {
                    throw new Scene3DException(Scene3DError.InvalidValue, "A PRC double copies a byte from outside itself.");
                }

                bytes[current] = bytes[current + offset];
            }

            current--;
        }

        return BitConverter.Int64BitsToDouble(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes));
    }
}
