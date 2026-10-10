namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>The shape of a primitive of a tessellated face.</summary>
internal enum PrcPrimitiveShape : byte
{
    /// <summary>Three points.</summary>
    Triangle,

    /// <summary>A first point and a run of others: every two neighbours of the run make a triangle with the first.</summary>
    Fan,

    /// <summary>A run of points: every three neighbours make a triangle.</summary>
    Strip,
}

/// <summary>
/// One primitive of a face and where its indices are in the tessellation's <c>triangulated_index_array</c>.
/// Per point the array holds, in this order: a normal index (unless the primitive has one normal for
/// all its points, or the tessellation stores no normals at all), the texture coordinate indices of a
/// textured primitive, and the point index.
/// </summary>
internal readonly struct PrcPrimitive
{
    public required PrcPrimitiveShape Shape { get; init; }

    /// <summary>Number of points: 3 for a triangle.</summary>
    public required int PointCount { get; init; }

    /// <summary>Where the primitive starts in the index array.</summary>
    public required long Start { get; init; }

    /// <summary>Where the index of the one normal is, or -1 when normals are per point or not stored.</summary>
    public required long SharedNormal { get; init; }

    /// <summary>Where the first point's group of indices starts.</summary>
    public required long FirstPoint { get; init; }

    /// <summary>Indices per point: normal (0 or 1), texture coordinates, point.</summary>
    public required int Stride { get; init; }

    /// <summary>Whether each point's group starts with a normal index.</summary>
    public required bool NormalPerPoint { get; init; }

    /// <summary>
    /// Whether the normal of a point's group is that of a whole triangle (a fan or strip "with one normal"
    /// that nevertheless stores one per point: ISO 14739-1, PRC tessellation types).
    /// </summary>
    public required bool NormalPerTriangle { get; init; }

    /// <summary>Texture coordinate indices per point; 0 for a primitive without texture.</summary>
    public required int TextureIndices { get; init; }

    /// <summary>Number of indices the primitive takes.</summary>
    public long IndexCount => FirstPoint - Start + (long)PointCount * Stride;

    /// <summary>Number of triangles the primitive makes.</summary>
    public int TriangleCount => Shape == PrcPrimitiveShape.Triangle ? 1 : Math.Max(0, PointCount - 2);
}

/// <summary>
/// How the primitives of a tessellated face are laid out. A face says which kinds of primitives it has by
/// the bits of <c>used_entities_flag</c>, and how many of each in <c>TriangulatedData</c>, kind after
/// kind from the lowest bit: for triangles one number, how many; for fans and strips the number of them,
/// then the number of points of each (ISO 14739-1, PRC_TYPE_TESS_Face and PRC Tessellation Types).
/// </summary>
internal static class PrcFaceLayout
{
    /// <summary>Bits of <c>used_entities_flag</c> whose primitives are triangles, each of three points.</summary>
    public const uint TriangleKinds = 0x0002 | 0x0020 | 0x0200 | 0x2000;

    /// <summary>Bits of <c>used_entities_flag</c> whose primitives are fans or strips.</summary>
    public const uint FanAndStripKinds = 0x000C | 0x00C0 | 0x0C00 | 0xC000;

    /// <summary>Bits whose primitives are fans.</summary>
    public const uint FanKinds = 0x0004 | 0x0040 | 0x0400 | 0x4000;

    /// <summary>Bits whose primitives have one normal for all their points.</summary>
    public const uint OneNormalKinds = 0x00F0 | 0xF000;

    /// <summary>Bits whose primitives have texture coordinate indices.</summary>
    public const uint TexturedKinds = 0xFF00;

    /// <summary>Bits of <c>used_entities_flag</c> the format reserves ("polyface, not used").</summary>
    public const uint UnusedKinds = 0x1111;

    /// <summary>On the point count of a fan or strip with one normal: the primitive is planar and has a single normal.</summary>
    public const uint SingleNormal = 0x40000000;

    /// <summary>What is left of a point count of a fan or strip without its flag.</summary>
    public const uint PointCountMask = 0x3FFFFFFF;

    /// <summary>The number of point indices of all the primitives of a face.</summary>
    public static long PointCount(uint usedEntitiesFlag, ReadOnlySpan<uint> triangulatedData)
    {
        long points = 0;
        int at = 0;
        for (int bit = 0; bit < 16; bit++)
        {
            uint kind = 1u << bit;
            if ((usedEntitiesFlag & kind) == 0)
            {
                continue;
            }

            if ((kind & UnusedKinds) != 0)
            {
                throw new Scene3DException(Scene3DError.Unsupported, "A face of a PRC tessellation uses polyfaces, which the format reserves.");
            }

            uint count = Next(triangulatedData, ref at);
            if ((kind & TriangleKinds) != 0)
            {
                points += 3L * count;
                continue;
            }

            for (uint i = 0; i < count; i++)
            {
                points += Next(triangulatedData, ref at) & PointCountMask;
            }
        }

        return points;
    }

    private static uint Next(ReadOnlySpan<uint> triangulatedData, ref int at)
    {
        if (at >= triangulatedData.Length)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, "A face of a PRC tessellation names more primitives than it gives sizes for.");
        }

        return triangulatedData[at++];
    }

    /// <summary>Goes through the primitives of a face in the order of their indices.</summary>
    /// <param name="usedEntitiesFlag">The face's kinds of primitives.</param>
    /// <param name="triangulatedData">The face's numbers of primitives and of points.</param>
    /// <param name="start">Where the face's indices start in the tessellation's index array.</param>
    /// <param name="normalsStored">Whether the tessellation stores normal indices (it does not when normals are to be calculated).</param>
    /// <param name="textureIndices">The face's number of texture coordinate indices per point of a textured primitive.</param>
    public static PrimitiveEnumerator Primitives(uint usedEntitiesFlag, ReadOnlySpan<uint> triangulatedData, long start, bool normalsStored, int textureIndices) =>
        new(usedEntitiesFlag, triangulatedData, start, normalsStored, textureIndices);

    internal ref struct PrimitiveEnumerator
    {
        private readonly uint _flags;
        private readonly ReadOnlySpan<uint> _sizes;
        private readonly bool _normalsStored;
        private readonly int _textureIndices;
        private int _bit;
        private int _at;
        private uint _left;
        private long _cursor;

        internal PrimitiveEnumerator(uint flags, ReadOnlySpan<uint> sizes, long start, bool normalsStored, int textureIndices)
        {
            _flags = flags;
            _sizes = sizes;
            _normalsStored = normalsStored;
            _textureIndices = textureIndices;
            _bit = -1;
            _cursor = start;
        }

        public PrcPrimitive Current { get; private set; }

        public readonly PrimitiveEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_left == 0)
            {
                // The next kind the face has.
                do
                {
                    if (++_bit >= 16)
                    {
                        return false;
                    }
                }
                while ((_flags & (1u << _bit)) == 0);

                if (((1u << _bit) & UnusedKinds) != 0)
                {
                    throw new Scene3DException(Scene3DError.Unsupported, "A face of a PRC tessellation uses polyfaces, which the format reserves.");
                }

                _left = Next(_sizes, ref _at);
            }

            _left--;
            uint kind = 1u << _bit;
            bool triangles = (kind & TriangleKinds) != 0;
            bool oneNormal = (kind & OneNormalKinds) != 0;
            int texture = (kind & TexturedKinds) != 0 ? _textureIndices : 0;

            int points = 3;
            bool single = oneNormal;
            if (!triangles)
            {
                // A fan or strip "with one normal" has a single one only when its size is flagged so.
                uint size = Next(_sizes, ref _at);
                single = oneNormal && (size & SingleNormal) != 0;
                points = (int)(size & PointCountMask);
            }

            bool shared = single && _normalsStored;
            bool perPoint = !single && _normalsStored;
            Current = new PrcPrimitive
            {
                Shape = triangles ? PrcPrimitiveShape.Triangle : (kind & FanKinds) != 0 ? PrcPrimitiveShape.Fan : PrcPrimitiveShape.Strip,
                PointCount = points,
                Start = _cursor,
                SharedNormal = shared ? _cursor : -1,
                FirstPoint = _cursor + (shared ? 1 : 0),
                Stride = (perPoint ? 1 : 0) + texture + 1,
                NormalPerPoint = perPoint,
                NormalPerTriangle = perPoint && oneNormal,
                TextureIndices = texture,
            };
            _cursor += Current.IndexCount;
            return true;
        }
    }
}
