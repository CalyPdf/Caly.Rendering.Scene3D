using System.Buffers;
using System.Numerics;

namespace Caly.Rendering.Scene3D.U3D.Internal;

/// <summary>Constants of the ECMA-363 bit encoding (clause 10, Annex A.3.5) and of the block structure.</summary>
internal static class U3DConstants
{
    /// <summary>The context of uncompressed bytes.</summary>
    public const uint Context8 = 0;

    /// <summary>Contexts above this value are static: <c>context - StaticFull</c> equally likely symbols.</summary>
    public const uint StaticFull = 0x400;

    /// <summary>Contexts at or above this value are not compressed at all.</summary>
    public const uint MaxRange = StaticFull + 0x3FFF;

    /// <summary>Dynamic context of the shading id of a base-mesh face (reference decoder).</summary>
    public const uint ContextBaseShadingId = 1;

    // The contexts of motion resources (IFXACContext.h): signs and quantised differences of time,
    // displacement, rotation and scale.
    public const uint ContextMotionSignTime = 1;
    public const uint ContextMotionDiffTime = 2;
    public const uint ContextMotionSignDisplacement = 3;
    public const uint ContextMotionDiffDisplacement = 4;
    public const uint ContextMotionSignRotation = 5;
    public const uint ContextMotionDiffRotation = 6;
    public const uint ContextMotionSignScale = 7;
    public const uint ContextMotionDiffScale = 8;

    // The contexts of bone weights (IFXACContext.h).
    public const uint ContextBoneWeightCount = 66;
    public const uint ContextBoneWeightBoneId = 67;
    public const uint ContextBoneWeightWeight = 68;

    // The contexts of progressive detail, line sets and point sets, numbered as the Universal 3D Sample
    // Software numbers them (IFXACContext.h): contexts that share a number share their statistics.
    public const uint ContextNumNewFaces = 1;
    public const uint ContextOrientationType = 2;
    public const uint ContextThirdIndexType = 3;
    public const uint ContextLocal3rdPosition = 4;

    /// <summary>Five contexts from here: one for each guess of whether a face stays or moves.</summary>
    public const uint ContextStayMove = 15;
    public const uint ContextTexCoordSplitType = 29;
    public const uint ContextVertColorSplitType = 55;
    public const uint ContextShadingId = 65;
    public const uint ContextNewDiffuseColorsCount = 99;
    public const uint ContextDiffuseColorSign = 100;
    public const uint ContextNewSpecularColorsCount = 101;
    public const uint ContextSpecularColorSign = 102;
    public const uint ContextTexCoordSign = 103;
    public const uint ContextDiffuseKeepChange = 104;
    public const uint ContextDiffuseChangeType = 105;
    public const uint ContextDiffuseChangeIndexNew = 106;
    public const uint ContextDiffuseChangeIndexLocal = 107;
    public const uint ContextDiffuseChangeIndexGlobal = 108;
    public const uint ContextSpecularKeepChange = 109;
    public const uint ContextSpecularChangeType = 110;
    public const uint ContextSpecularChangeIndexNew = 111;
    public const uint ContextSpecularChangeIndexLocal = 112;
    public const uint ContextSpecularChangeIndexGlobal = 113;
    public const uint ContextTexCoordKeepChange = 114;
    public const uint ContextTexCoordChangeType = 115;
    public const uint ContextTexCoordChangeIndexNew = 116;
    public const uint ContextTexCoordChangeIndexLocal = 117;
    public const uint ContextTexCoordChangeIndexGlobal = 118;
    public const uint ContextVertColorSplitIndexLocal = 119;
    public const uint ContextVertColorSplitIndexGlobal = 120;
    public const uint ContextTexCoordSplitIndexLocal = 121;
    public const uint ContextTexCoordSplitIndexGlobal = 122;
    public const uint ContextNewTexCoordsCount = 123;
    public const uint ContextSetShadingId = 1;
    public const uint ContextPositionDiffSigns = 20;
    public const uint ContextPositionDiffMagX = 21;
    public const uint ContextPositionDiffMagY = 22;
    public const uint ContextPositionDiffMagZ = 23;
    public const uint ContextTexCoordDiffSigns = 32;
    public const uint ContextTexCoordDiffMagU = 33;
    public const uint ContextTexCoordDiffMagV = 34;
    public const uint ContextTexCoordDiffMagS = 35;
    public const uint ContextTexCoordDiffMagT = 36;
    public const uint ContextTexCoordDupType = 39;
    public const uint ContextNumLocalNormals = 40;
    public const uint ContextNormalDiffSigns = 41;
    public const uint ContextNormalDiffMagX = 42;
    public const uint ContextNormalDiffMagY = 43;
    public const uint ContextNormalDiffMagZ = 44;
    public const uint ContextNormalLocalIndex = 45;
    public const uint ContextVertColorDupType = 56;
    public const uint ContextVertexColorDiffMagR = 60;
    public const uint ContextVertexColorDiffMagG = 61;
    public const uint ContextVertexColorDiffMagB = 62;
    public const uint ContextVertexColorDiffMagA = 63;

    public const uint BlockFileHeader = 0x00443355;
    public const uint BlockFileReference = 0xFFFFFF12;
    public const uint BlockModifierChain = 0xFFFFFF14;
    public const uint BlockPriorityUpdate = 0xFFFFFF15;
    public const uint BlockNewObjectType = 0xFFFFFF16;
    public const uint BlockGroupNode = 0xFFFFFF21;
    public const uint BlockModelNode = 0xFFFFFF22;
    public const uint BlockLightNode = 0xFFFFFF23;
    public const uint BlockViewNode = 0xFFFFFF24;
    public const uint BlockMeshDeclaration = 0xFFFFFF31;
    public const uint BlockPointSetDeclaration = 0xFFFFFF36;
    public const uint BlockLineSetDeclaration = 0xFFFFFF37;
    public const uint BlockBaseMeshContinuation = 0xFFFFFF3B;
    public const uint BlockProgressiveMeshContinuation = 0xFFFFFF3C;
    public const uint BlockPointSetContinuation = 0xFFFFFF3E;
    public const uint BlockLineSetContinuation = 0xFFFFFF3F;
    public const uint BlockGlyphModifier = 0xFFFFFF41;
    public const uint BlockSubdivisionModifier = 0xFFFFFF42;
    public const uint BlockAnimationModifier = 0xFFFFFF43;
    public const uint BlockBoneWeightModifier = 0xFFFFFF44;
    public const uint BlockShadingModifier = 0xFFFFFF45;
    public const uint BlockClodModifier = 0xFFFFFF46;
    public const uint BlockLightResource = 0xFFFFFF51;
    public const uint BlockViewResource = 0xFFFFFF52;
    public const uint BlockLitTextureShader = 0xFFFFFF53;
    public const uint BlockMaterialResource = 0xFFFFFF54;
    public const uint BlockTextureDeclaration = 0xFFFFFF55;
    public const uint BlockMotionResource = 0xFFFFFF56;
    public const uint BlockTextureContinuation = 0xFFFFFF5C;
    public const uint BlockRHAdobeMeshResource = 0x00000100;
}

/// <summary>
/// The adaptive histograms of the dynamic compression contexts (ECMA-363 Annex A.3.3). One instance serves
/// one block at a time: call <see cref="Reset"/> between blocks.
/// <para>
/// Following the reference decoder, context <see cref="U3DConstants.StaticFull"/> itself (a static range of
/// zero symbols) is dynamic; the standard's listing would divide by zero there.
/// </para>
/// </summary>
internal sealed class U3DContexts : IDisposable
{
    private const uint Elephant = 0x1FFF;
    private const uint MaximumSymbolInHistogram = 0xFFFF;
    private const int MinimumLength = 32;

    /// <summary>
    /// How often each symbol of a context has occurred, and a Fenwick tree over those counts: progressive
    /// detail puts symbols in the thousands into a context for every position it adds, and a table of
    /// running totals would be rewritten from end to end each time.
    /// </summary>
    private sealed class Histogram
    {
        public ushort[] Count = [];

        // Tree[i], i from 1: the sum of the (i & -i) counts that end with the i-th.
        public int[] Tree = [];
        public int Length;
        public uint Total;
    }

    private Histogram?[] _histograms = ArrayPool<Histogram?>.Shared.Rent((int)U3DConstants.StaticFull + 1);
    private readonly List<int> _used = [];
    private readonly Stack<Histogram> _spare = new();

    public static bool IsDynamic(uint context) => context <= U3DConstants.StaticFull && context != U3DConstants.Context8;

    /// <summary>Forgets every histogram, keeping nothing from the previous block.</summary>
    public void Reset()
    {
        foreach (int context in _used)
        {
            Histogram histogram = _histograms[context]!;
            ArrayPool<ushort>.Shared.Return(histogram.Count);
            ArrayPool<int>.Shared.Return(histogram.Tree);
            histogram.Count = [];
            histogram.Tree = [];
            histogram.Length = 0;
            histogram.Total = 0;
            _spare.Push(histogram);
            _histograms[context] = null;
        }

        _used.Clear();
    }

    public void Dispose()
    {
        Reset();
        if (_histograms is not null)
        {
            ArrayPool<Histogram?>.Shared.Return(_histograms, clearArray: true);
            _histograms = [];
        }
    }

    public void AddSymbol(uint context, uint symbol)
    {
        if (!IsDynamic(context) || symbol > MaximumSymbolInHistogram)
        {
            return;
        }

        Histogram? histogram = _histograms[context];
        if (histogram is null)
        {
            // A new context holds only the escape symbol.
            _histograms[context] = histogram = _spare.Count > 0 ? _spare.Pop() : new Histogram();
            _used.Add((int)context);
            Grow(histogram, (int)symbol + 1);
            histogram.Count[0] = 1;
            histogram.Total = 1;
            Rebuild(histogram);
        }
        else if (symbol >= (uint)histogram.Length)
        {
            Grow(histogram, (int)symbol + 1);
            Rebuild(histogram);
        }

        if (histogram.Total >= Elephant)
        {
            // Halve everything so the totals stay within 16 bits and the model keeps adapting.
            uint total = 0;
            Span<ushort> counts = histogram.Count.AsSpan(0, histogram.Length);
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] >>= 1;
                total += counts[i];
            }

            counts[0]++;
            histogram.Total = total + 1;
            Rebuild(histogram);
        }

        histogram.Count[symbol]++;
        histogram.Total++;
        int[] tree = histogram.Tree;
        for (int i = (int)symbol + 1; i <= histogram.Length; i += i & -i)
        {
            tree[i]++;
        }
    }

    public uint GetTotalSymbolFrequency(uint context)
    {
        return _histograms[context]?.Total ?? 1u;
    }

    public uint GetSymbolFrequency(uint context, uint symbol)
    {
        Histogram? histogram = _histograms[context];
        if (histogram is not null && symbol < (uint)histogram.Length)
        {
            return histogram.Count[symbol];
        }

        return symbol == 0 && histogram is null ? 1u : 0u;
    }

    /// <summary>How often the symbols below <paramref name="symbol"/> have occurred.</summary>
    public uint GetCumulativeSymbolFrequency(uint context, uint symbol)
    {
        Histogram? histogram = _histograms[context];
        if (histogram is null)
        {
            return 0;
        }

        if (symbol >= (uint)histogram.Length)
        {
            return histogram.Total;
        }

        int[] tree = histogram.Tree;
        uint below = 0;
        for (int i = (int)symbol; i > 0; i -= i & -i)
        {
            below += (uint)tree[i];
        }

        return below;
    }

    /// <summary>The last symbol below which no more than <paramref name="symbolFrequency"/> occurrences lie.</summary>
    public uint GetSymbolFromFrequency(uint context, uint symbolFrequency)
    {
        Histogram? histogram = _histograms[context];
        if (histogram is null || symbolFrequency == 0 || histogram.Total < symbolFrequency)
        {
            return 0;
        }

        // Down the tree: the longest run of counts from the first whose sum does not exceed the frequency.
        int[] tree = histogram.Tree;
        int length = histogram.Length, position = 0;
        uint remaining = symbolFrequency;
        for (int step = (int)BitOperations.RoundUpToPowerOf2((uint)length + 1) >> 1; step > 0; step >>= 1)
        {
            int next = position + step;
            if (next <= length && (uint)tree[next] <= remaining)
            {
                position = next;
                remaining -= (uint)tree[next];
            }
        }

        return (uint)Math.Min(position, length - 1);
    }

    /// <summary>Room for at least <paramref name="needed"/> symbols; what is new counts nothing.</summary>
    private static void Grow(Histogram histogram, int needed)
    {
        int length = Math.Max(Math.Max(needed, MinimumLength), histogram.Length * 2);
        ushort[] count = ArrayPool<ushort>.Shared.Rent(length);
        int[] tree = ArrayPool<int>.Shared.Rent(length + 1);
        histogram.Count.AsSpan(0, histogram.Length).CopyTo(count);
        count.AsSpan(histogram.Length, length - histogram.Length).Clear();
        if (histogram.Count.Length > 0)
        {
            ArrayPool<ushort>.Shared.Return(histogram.Count);
            ArrayPool<int>.Shared.Return(histogram.Tree);
        }

        histogram.Count = count;
        histogram.Tree = tree;
        histogram.Length = length;
    }

    private static void Rebuild(Histogram histogram)
    {
        int length = histogram.Length;
        int[] tree = histogram.Tree;
        ushort[] count = histogram.Count;
        tree[0] = 0;
        for (int i = 1; i <= length; i++)
        {
            tree[i] = count[i - 1];
        }

        for (int i = 1; i <= length; i++)
        {
            int parent = i + (i & -i);
            if (parent <= length)
            {
                tree[parent] += tree[i];
            }
        }
    }
}
