using Caly.Rendering.Scene3D.Prc.Bits;

namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>Bits as the file has them, in the order they were written.</summary>
internal readonly struct PrcBits
{
    private readonly uint[]? _words;

    /// <param name="words">The bits, the first in the highest bit of the first word.</param>
    /// <param name="count">How many of them count.</param>
    public PrcBits(uint[] words, int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, words.Length * 32L);
        _words = words;
        Count = count;
    }

    public int Count { get; }

    public bool this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return (_words![index >> 5] & (0x80000000u >> (index & 31))) != 0;
        }
    }

    public int CountSet()
    {
        int set = 0;
        if (_words is not null)
        {
            int whole = Count >> 5;
            for (int i = 0; i < whole; i++)
            {
                set += System.Numerics.BitOperations.PopCount(_words[i]);
            }

            int rest = Count & 31;
            if (rest != 0)
            {
                set += System.Numerics.BitOperations.PopCount(_words[whole] >> (32 - rest));
            }
        }

        return set;
    }

    /// <summary>Reads <paramref name="count"/> bits; they count against the limit of the read.</summary>
    /// <exception cref="Scene3DException">The bits are not there, or keeping them exceeds the limit.</exception>
    public static PrcBits Read(ref PrcBitReader r, PrcContext ctx, long count)
    {
        if (count < 0 || count > r.BitsLeft || count > int.MaxValue)
        {
            throw new Scene3DException(Scene3DError.Truncated, $"A PRC array of {count} bits does not fit in the {r.BitsLeft} bits left.");
        }

        int n = (int)count;
        int wordCount = (n + 31) >> 5;
        ctx.Charge(wordCount * 4L);
        var words = new uint[wordCount];
        int whole = n >> 5;
        for (int i = 0; i < whole; i++)
        {
            words[i] = r.ReadBits(32);
        }

        int rest = n & 31;
        if (rest != 0)
        {
            words[whole] = r.ReadBits(rest) << (32 - rest);
        }

        return new PrcBits(words, n);
    }
}

/// <summary>The texture coordinates of a compressed tessellation, as the file has them (CompressedTextureParameter).</summary>
internal sealed class PrcCompressedTexture
{
    /// <summary>Per textured corner the traversal meets: whether its coordinates refer to earlier ones of its point.</summary>
    public required PrcBits Bits;

    public required uint[] References;

    /// <summary>The coordinates that are not references.</summary>
    public required float[] Parameters;
}
