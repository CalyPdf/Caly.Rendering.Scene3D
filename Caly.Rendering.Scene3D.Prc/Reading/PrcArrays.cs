using Caly.Rendering.Scene3D.Prc.Bits;

namespace Caly.Rendering.Scene3D.Prc;

/// <summary>
/// Arrays of the format's simple values, kept or read past. A count comes from the file: it is checked
/// against the data left (each value takes a known least number of bits) and against the limit on what
/// is kept before anything is allocated.
/// </summary>
internal static class PrcArrays
{
    public static double[] ReadDoubles(ref PrcBitReader r, PrcContext ctx, long count)
    {
        var values = new double[Allocate(ref r, ctx, count, 2, sizeof(double))];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = r.ReadDouble();
        }

        return values;
    }

    public static void SkipDoubles(ref PrcBitReader r, long count)
    {
        for (int i = 0, n = PrcContext.CheckCount(ref r, count, 2); i < n; i++)
        {
            r.ReadDouble();
        }
    }

    public static uint[] ReadUnsignedIntegers(ref PrcBitReader r, PrcContext ctx, long count)
    {
        var values = new uint[Allocate(ref r, ctx, count, 1, sizeof(uint))];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = r.ReadUnsignedInteger();
        }

        return values;
    }

    public static void SkipUnsignedIntegers(ref PrcBitReader r, long count)
    {
        for (int i = 0, n = PrcContext.CheckCount(ref r, count, 1); i < n; i++)
        {
            r.ReadUnsignedInteger();
        }
    }

    public static int[] ReadIntegers(ref PrcBitReader r, PrcContext ctx, long count)
    {
        var values = new int[Allocate(ref r, ctx, count, 1, sizeof(int))];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = r.ReadInteger();
        }

        return values;
    }

    public static void SkipIntegers(ref PrcBitReader r, long count)
    {
        for (int i = 0, n = PrcContext.CheckCount(ref r, count, 1); i < n; i++)
        {
            r.ReadInteger();
        }
    }

    public static uint[] ReadBitCountedUnsignedIntegers(ref PrcBitReader r, PrcContext ctx, long count)
    {
        var values = new uint[Allocate(ref r, ctx, count, 5, sizeof(uint))];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = r.ReadNumberOfBitsThenUnsignedInteger();
        }

        return values;
    }

    public static void SkipBitCountedUnsignedIntegers(ref PrcBitReader r, long count)
    {
        for (int i = 0, n = PrcContext.CheckCount(ref r, count, 5); i < n; i++)
        {
            r.ReadNumberOfBitsThenUnsignedInteger();
        }
    }

    public static string[] ReadStrings(ref PrcBitReader r, PrcContext ctx, long count)
    {
        var values = new string[Allocate(ref r, ctx, count, 1, 32)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = r.ReadString();
        }

        return values;
    }

    public static void SkipStrings(ref PrcBitReader r, long count)
    {
        for (int i = 0, n = PrcContext.CheckCount(ref r, count, 1); i < n; i++)
        {
            r.SkipString();
        }
    }

    public static float[] ReadFloats(ref PrcBitReader r, PrcContext ctx, long count)
    {
        var values = new float[Allocate(ref r, ctx, count, 32, sizeof(float))];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = r.ReadFloatAsBytes();
        }

        return values;
    }

    public static void SkipFloats(ref PrcBitReader r, long count) => r.Skip(32L * PrcContext.CheckCount(ref r, count, 32));

    public static sbyte[] ReadCharacters(ref PrcBitReader r, PrcContext ctx, long count)
    {
        var values = new sbyte[Allocate(ref r, ctx, count, 8, 1)];
        r.ReadBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    public static void SkipCharacters(ref PrcBitReader r, long count) => r.Skip(8L * PrcContext.CheckCount(ref r, count, 8));

    public static byte[] ReadUnsignedCharacters(ref PrcBitReader r, PrcContext ctx, long count)
    {
        var values = new byte[Allocate(ref r, ctx, count, 8, 1)];
        r.ReadBytes(values);
        return values;
    }

    public static void SkipUnsignedCharacters(ref PrcBitReader r, long count) => r.Skip(8L * PrcContext.CheckCount(ref r, count, 8));

    private static int Allocate(ref PrcBitReader r, PrcContext ctx, long count, int minimumBitsEach, int bytesEach)
    {
        int n = ctx.Count(ref r, count, minimumBitsEach);
        ctx.Charge((long)n * bytesEach);
        return n;
    }
}
