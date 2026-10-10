using System.Buffers;
using System.Numerics;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>What a compressed tessellation decodes to: points, triangles and, where stored, normals.</summary>
internal sealed class PrcCompressedGeometry : IDisposable
{
    /// <summary>Three coordinates per point, in the order the points were made (pooled: longer than used).</summary>
    public double[] Points = [];

    public int PointCount;

    /// <summary>
    /// Three points per triangle, in the order the triangles were treated, each wound so that its front
    /// is the side of its normal (pooled).
    /// </summary>
    public int[] Corners = [];

    public int TriangleCount;

    /// <summary>Three coordinates per stored normal (pooled); empty when normals are to be calculated.</summary>
    public double[] Normals = [];

    public int NormalCount;

    /// <summary>Per corner, the index of its normal, or -1 for one to calculate (pooled).</summary>
    public int[] CornerNormals = [];

    /// <summary>Two texture coordinates per pair the file stores (pooled); empty when there are none.</summary>
    public double[] TextureCoordinates = [];

    public int TextureCount;

    /// <summary>Per corner, the index of its pair of texture coordinates, or -1 (pooled); empty when there are none.</summary>
    public int[] CornerTextures = [];

    /// <summary>Whether the file has texture coordinates that could not be told apart corner by corner, and were left out.</summary>
    public bool TextureDropped;

    /// <summary>
    /// Per corner, its colour as the scene packs one (red lowest, alpha highest), or -1 where the face
    /// has none (pooled); empty when the tessellation has no colours on points.
    /// </summary>
    public int[] CornerColors = [];

    /// <summary>Whether the file has colours on points that do not come out even, five bytes a corner, and were left out.</summary>
    public bool ColorsDropped;

    /// <summary>How many points were placed with made axes, after a flat triangle.</summary>
    public int MadeFrames;

    /// <summary>
    /// Whether the tessellation gives an origin and the decoded points are not around it: neither the
    /// middle of their box nor their mean is there. Writers that follow the standard put the origin
    /// at the middle of the box; some do not, so this is a doubt and not an error.
    /// </summary>
    public bool OffOrigin;

    public void Dispose()
    {
        Return(ref Points);
        Return(ref Corners);
        Return(ref Normals);
        Return(ref CornerNormals);
        Return(ref TextureCoordinates);
        Return(ref CornerTextures);
        Return(ref CornerColors);
    }

    private static void Return<T>(ref T[] array)
    {
        if (array.Length > 0)
        {
            ArrayPool<T>.Shared.Return(array);
            array = [];
        }
    }
}

/// <summary>
/// Decodes a compressed tessellation (<c>PRC_TYPE_TESS_3D_Compressed</c>, ISO 14739-1 7.8.9) in one
/// pass: the triangles in the order the writer walked them, each point where the writer rebuilt it,
/// and each triangle's side, which says on which of its edges its neighbours hang.
/// <para>
/// The writer stored every new point in a frame built from points it had already approximated, and
/// decided sides from them; a reader has to arrive at the very same numbers. So the arithmetic here
/// follows the standard's own (<see cref="PrcBasis"/>) operation for operation: doubles, lengths by
/// the annex's routine, sums in the order written. After a triangle whose points are in line the
/// result depends on the last bit of earlier results. Do not rearrange it.
/// </para>
/// <para>
/// Sources: the 2009 draft's 7.8.9 and annex, PDF Association issues #727 (its decoding outline),
/// #773, #540, #436, and the sample files; see the workspace's <c>decoding-rules.md</c>.
/// </para>
/// </summary>
internal static class PrcCompressedDecoder
{
    private const byte Unseen = 0, OneNormal = 1, SeveralNormals = 2;


    /// <summary>
    /// The number of bits the standard uses to store an unsigned integer
    /// (<c>GetNumberOfBitsUsedToStoreUnsignedInteger</c>, 11.1): 1, 1, 2, 3, 3, 4, 4, 4, 4, 5...
    /// </summary>
    public static int BitsToStore(int value)
    {
        int bits = 1;
        long reach = 1;
        while (value > reach)
        {
            reach *= 2;
            bits++;
        }

        return bits;
    }

    /// <summary>Decodes the tessellation.</summary>
    /// <exception cref="Scene3DException">Its arrays contradict each other, or are not used up exactly.</exception>
    public static PrcCompressedGeometry Decode(PrcTess3DCompressed tess, CancellationToken cancellation = default)
    {
        int triangleCount = tess.TriangleCount;
        if (tess.Points.Length % 3 != 0)
        {
            throw Invalid("its point array is not made of triples");
        }

        int pointCount = tess.Points.Length / 3;
        bool stored = !tess.MustRecalculateNormals;
        if (tess.EdgeStatus.Length < triangleCount)
        {
            throw Invalid("it has fewer neighbour flags than triangles");
        }

        if (!stored && tess.NormalIsReversed.Count < triangleCount)
        {
            throw Invalid("it has fewer orientation bits than triangles");
        }

        if (stored && (tess.NormalAngleBits is < 1 or > 16 || (tess.NormalAngles.Length & 1) != 0))
        {
            throw Invalid("its normal angles are not pairs of 1 to 16 bits");
        }

        if (!(tess.Tolerance > 0) || !double.IsFinite(tess.Tolerance))
        {
            throw Invalid("its tolerance is not a length");
        }

        var geometry = new PrcCompressedGeometry();
        var state = new Decoder(tess, geometry, pointCount, stored);
        try
        {
            state.Run(cancellation);
            return geometry;
        }
        catch
        {
            geometry.Dispose();
            throw;
        }
        finally
        {
            state.Dispose();
        }
    }

    private static Scene3DException Invalid(string what) =>
        new(Scene3DError.InvalidValue, $"A compressed tessellation of the PRC model cannot be decoded: {what}.");

    /// <summary>A gate: an edge with a triangle on one side, waiting for the one on the other.</summary>
    private readonly record struct Gate(int Lower, int Higher, int Far);

    private ref struct Decoder
    {
        private readonly PrcTess3DCompressed _tess;
        private readonly PrcCompressedGeometry _geometry;
        private readonly int _pointCount;
        private readonly int _triangleCount;
        private readonly bool _stored;
        private readonly double _tolerance;
        private readonly double[] _xyz;
        private readonly int[] _corners;

        // Edges, by their two points: how many triangles each carries. Open addressing; key 0 is empty.
        private readonly ulong[] _edgeKeys;
        private readonly byte[] _edgeCounts;
        private readonly int _edgeShift;

        private Gate[] _stack;
        private int _stackCount;

        // Cursors into the tessellation's arrays.
        private int _flag, _reference, _points, _storedPoint, _bit, _angle;

        // Stored normals: per point its state, how many normals are stored for it and the last of them;
        // per normal the one stored before it for the same point.
        private readonly byte[] _pointState;
        private readonly int[] _lastNormal;
        private readonly int[] _normalsOfPoint;
        private readonly int[] _previousNormal;
        private readonly int[] _faceNormal;
        private readonly double[] _normals;
        private readonly int[] _cornerNormals;
        private int _normalCount;
        private readonly double _angleStep;

        // Texture coordinates: per point how many pairs are stored for it and the last of them; per pair
        // the one stored before it for the same point. Cursors into the bits, the references, the pairs.
        private readonly PrcCompressedTexture? _texture;
        private readonly int[] _lastPair;
        private readonly int[] _pairsOfPoint;
        private readonly int[] _previousPair;
        private readonly int[] _cornerTextures;

        // Colours on points: five bytes a corner of every triangle of a face that has them.
        private readonly int[] _cornerColors;
        private int _colorAt;
        private bool _colorBroken;
        private int _textureBit, _textureReference, _texturePair;
        private bool _textureBroken;

        public Decoder(PrcTess3DCompressed tess, PrcCompressedGeometry geometry, int pointCount, bool stored)
        {
            _tess = tess;
            _geometry = geometry;
            _pointCount = pointCount;
            _triangleCount = tess.TriangleCount;
            _stored = stored;
            _tolerance = tess.Tolerance;
            _xyz = ArrayPool<double>.Shared.Rent(Math.Max(pointCount * 3, 3));
            _corners = ArrayPool<int>.Shared.Rent(Math.Max(_triangleCount * 3, 3));
            geometry.Points = _xyz;
            geometry.Corners = _corners;

            // At most three edges a triangle; the table stays under three quarters full.
            int bits = Math.Max(4, BitOperations.Log2((uint)Math.Max(_triangleCount, 1) * 4) + 1);
            _edgeShift = 64 - bits;
            _edgeKeys = ArrayPool<ulong>.Shared.Rent(1 << bits);
            _edgeCounts = ArrayPool<byte>.Shared.Rent(1 << bits);
            _edgeKeys.AsSpan(0, 1 << bits).Clear();
            _stack = ArrayPool<Gate>.Shared.Rent(64);
            _stackCount = 0;
            _flag = _reference = _points = _storedPoint = _bit = _angle = 0;
            _normalCount = 0;

            if (stored)
            {
                int normalCapacity = tess.NormalAngles.Length / 2;
                _pointState = ArrayPool<byte>.Shared.Rent(Math.Max(pointCount, 1));
                _lastNormal = ArrayPool<int>.Shared.Rent(Math.Max(pointCount, 1));
                _normalsOfPoint = ArrayPool<int>.Shared.Rent(Math.Max(pointCount, 1));
                _previousNormal = ArrayPool<int>.Shared.Rent(Math.Max(normalCapacity, 1));
                _faceNormal = ArrayPool<int>.Shared.Rent(Math.Max(tess.FaceCount, 1));
                _normals = ArrayPool<double>.Shared.Rent(Math.Max(normalCapacity * 3, 3));
                _cornerNormals = ArrayPool<int>.Shared.Rent(Math.Max(_triangleCount * 3, 3));
                _pointState.AsSpan(0, Math.Max(pointCount, 1)).Clear();
                _normalsOfPoint.AsSpan(0, Math.Max(pointCount, 1)).Clear();
                _faceNormal.AsSpan(0, Math.Max(tess.FaceCount, 1)).Fill(-1);
                _angleStep = (Math.PI / 2) / ((1 << tess.NormalAngleBits) - 1);
                geometry.Normals = _normals;
            }
            else
            {
                _pointState = [];
                _lastNormal = [];
                _normalsOfPoint = [];
                _previousNormal = [];
                _faceNormal = [];
                _normals = [];
                _cornerNormals = ArrayPool<int>.Shared.Rent(Math.Max(_triangleCount * 3, 3));
                _angleStep = 0;
            }

            geometry.CornerNormals = _cornerNormals;

            _texture = tess.Texture is { Bits.Count: > 0 } texture && (texture.Parameters.Length & 1) == 0 ? texture : null;
            _textureBit = _textureReference = _texturePair = 0;
            _textureBroken = false;
            if (_texture is not null)
            {
                int pairs = _texture.Parameters.Length / 2;
                _lastPair = ArrayPool<int>.Shared.Rent(Math.Max(pointCount, 1));
                _pairsOfPoint = ArrayPool<int>.Shared.Rent(Math.Max(pointCount, 1));
                _previousPair = ArrayPool<int>.Shared.Rent(Math.Max(pairs, 1));
                _cornerTextures = ArrayPool<int>.Shared.Rent(Math.Max(_triangleCount * 3, 3));
                _pairsOfPoint.AsSpan(0, Math.Max(pointCount, 1)).Clear();
                geometry.CornerTextures = _cornerTextures;
            }
            else
            {
                _lastPair = [];
                _pairsOfPoint = [];
                _previousPair = [];
                _cornerTextures = [];
                geometry.TextureDropped = tess.Texture is { Bits.Count: > 0 };
            }

            _colorAt = 0;
            _colorBroken = false;
            _cornerColors = tess.PointColors.Length > 0 ? ArrayPool<int>.Shared.Rent(Math.Max(_triangleCount * 3, 3)) : [];
            geometry.CornerColors = _cornerColors;
        }

        public void Dispose()
        {
            ArrayPool<ulong>.Shared.Return(_edgeKeys);
            ArrayPool<byte>.Shared.Return(_edgeCounts);
            ArrayPool<Gate>.Shared.Return(_stack);
            if (_texture is not null)
            {
                ArrayPool<int>.Shared.Return(_lastPair);
                ArrayPool<int>.Shared.Return(_pairsOfPoint);
                ArrayPool<int>.Shared.Return(_previousPair);
            }

            if (_stored)
            {
                ArrayPool<byte>.Shared.Return(_pointState);
                ArrayPool<int>.Shared.Return(_lastNormal);
                ArrayPool<int>.Shared.Return(_normalsOfPoint);
                ArrayPool<int>.Shared.Return(_previousNormal);
                ArrayPool<int>.Shared.Return(_faceNormal);
            }
        }

        public void Run(CancellationToken cancellation)
        {
            PrcTess3DCompressed tess = _tess;
            var origin = tess.HasOrigin ? new PrcVector(tess.OriginX, tess.OriginY, tess.OriginZ) : default;
            for (int t = 0; t < _triangleCount; t++)
            {
                if ((t & 0xFFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                // The triangle's points in listing order: the entering edge by increasing point, then the apex.
                int first, second, third;
                if (!TryPopGate(out Gate gate))
                {
                    // A seed: three points, the first from the origin, the second from the first, the
                    // third from the middle of both.
                    int a = NextPoint(out bool newA);
                    if (newA)
                    {
                        Set(a, origin + TakeOffsets());
                    }

                    int b = NextPoint(out bool newB);
                    if (newB)
                    {
                        Set(b, At(a) + TakeOffsets());
                    }

                    third = NextPoint(out bool newThird);
                    if (newThird)
                    {
                        Set(third, ((At(a) + At(b)) * 0.5) + TakeOffsets());
                    }

                    first = Math.Min(a, b);
                    second = Math.Max(a, b);
                }
                else
                {
                    first = gate.Lower;
                    second = gate.Higher;
                    third = NextPoint(out bool isNew);
                    if (isNew)
                    {
                        Set(third, Apex(At(first), At(second), At(gate.Far)));
                    }
                }

                if (first == second || third == first || third == second)
                {
                    throw Invalid("a triangle names a point twice");
                }

                AddEdge(first, second);
                AddEdge(first, third);
                AddEdge(second, third);

                // Which side its front is on: said outright when normals are to be calculated, else
                // found from the first corner's normal.
                bool reversed = _stored ? ReadNormals(t, first, second, third) : tess.NormalIsReversed[t];

                if (_texture is not null && !_textureBroken)
                {
                    ReadTextures(t, first, second, third);
                }

                if (_cornerColors.Length > 0 && !_colorBroken)
                {
                    ReadColors(t);
                }

                // Left hangs on the first point, right on the second; the other way round when
                // reversed. Right is pushed first, so that left is treated next.
                int status = tess.EdgeStatus[t] & 3;
                int leftCorner = reversed ? second : first, rightCorner = reversed ? first : second;
                if ((status & 1) != 0 && EdgeCount(rightCorner, third) < 2)
                {
                    Push(rightCorner, third, leftCorner);
                }

                if ((status & 2) != 0 && EdgeCount(leftCorner, third) < 2)
                {
                    Push(leftCorner, third, rightCorner);
                }

                // The triangle, wound so that its front is the side of its normal.
                int c = t * 3;
                _corners[c] = first;
                if (reversed)
                {
                    _corners[c + 1] = second;
                    _corners[c + 2] = third;
                }
                else
                {
                    _corners[c + 1] = third;
                    _corners[c + 2] = second;
                    if (_stored)
                    {
                        (_cornerNormals[c + 1], _cornerNormals[c + 2]) = (_cornerNormals[c + 2], _cornerNormals[c + 1]);
                    }

                    if (_texture is not null)
                    {
                        (_cornerTextures[c + 1], _cornerTextures[c + 2]) = (_cornerTextures[c + 2], _cornerTextures[c + 1]);
                    }

                    if (_cornerColors.Length > 0)
                    {
                        (_cornerColors[c + 1], _cornerColors[c + 2]) = (_cornerColors[c + 2], _cornerColors[c + 1]);
                    }
                }
            }

            // Everything is used up, and nothing is still waited for.
            while (_stackCount > 0)
            {
                Gate gate = _stack[--_stackCount];
                if (EdgeCount(gate.Lower, gate.Higher) < 2)
                {
                    throw Invalid("a neighbour is announced and never given");
                }
            }

            if (_flag != tess.PointIsReference.Count || _reference != tess.PointReferences.Length || _points != _pointCount)
            {
                throw Invalid("its points are not used up by its triangles");
            }

            if (_stored && (_bit != tess.NormalBits.Count || _angle != tess.NormalAngles.Length))
            {
                throw Invalid("its normals are not used up by its triangles");
            }

            if (!_stored)
            {
                _cornerNormals.AsSpan(0, _triangleCount * 3).Fill(-1);
            }

            if (_cornerColors.Length > 0 && (_colorBroken || _colorAt != tess.PointColors.Length))
            {
                // Like texture coordinates: colours that do not come out even are left out, the mesh is kept.
                _geometry.ColorsDropped = true;
                ArrayPool<int>.Shared.Return(_cornerColors);
                _geometry.CornerColors = [];
            }

            if (_texture is { } texture)
            {
                // Texture coordinates that do not come out even are left out; the mesh is no worse for it.
                if (_textureBroken || _textureBit != texture.Bits.Count || _textureReference != texture.References.Length || _texturePair * 2 != texture.Parameters.Length)
                {
                    _geometry.TextureDropped = true;
                    ArrayPool<int>.Shared.Return(_cornerTextures);
                    _geometry.CornerTextures = [];
                }
                else
                {
                    double[] coordinates = ArrayPool<double>.Shared.Rent(Math.Max(texture.Parameters.Length, 2));
                    for (int i = 0; i < texture.Parameters.Length; i++)
                    {
                        coordinates[i] = texture.Parameters[i];
                    }

                    _geometry.TextureCoordinates = coordinates;
                    _geometry.TextureCount = _texturePair;
                }
            }

            _geometry.PointCount = _pointCount;
            _geometry.TriangleCount = _triangleCount;
            _geometry.NormalCount = _normalCount;
            _geometry.OffOrigin = tess.HasOrigin && IsOffOrigin(origin);
        }

        private readonly PrcVector At(int point) => new(_xyz[point * 3], _xyz[point * 3 + 1], _xyz[point * 3 + 2]);

        private readonly void Set(int point, PrcVector value)
        {
            _xyz[point * 3] = value.X;
            _xyz[point * 3 + 1] = value.Y;
            _xyz[point * 3 + 2] = value.Z;
        }

        /// <summary>The next point of the walk: a new one, or one already made.</summary>
        private int NextPoint(out bool isNew)
        {
            PrcTess3DCompressed tess = _tess;
            if (_flag >= tess.PointIsReference.Count)
            {
                throw Invalid("its triangles need more points than it has");
            }

            if (!tess.PointIsReference[_flag++])
            {
                if (_points >= _pointCount)
                {
                    throw Invalid("its triangles need more points than it has");
                }

                isNew = true;
                return _points++;
            }

            isNew = false;
            if (_reference >= tess.PointReferences.Length)
            {
                throw Invalid("its triangles need more points than it has");
            }

            int value = tess.PointReferences[_reference++];
            if ((uint)value >= (uint)_points)
            {
                throw Invalid("a triangle refers to a point that is not made yet");
            }

            return value;
        }

        /// <summary>The stored triple of the next new point, as lengths.</summary>
        private PrcVector TakeOffsets()
        {
            int[] stored = _tess.Points;
            int at = _storedPoint;
            _storedPoint += 3;
            return new PrcVector(stored[at], stored[at + 1], stored[at + 2]) * _tolerance;
        }

        /// <summary>
        /// A new point across a gate edge (7.8.9.2, with the frame as files have it: PDF Association
        /// issue #773). Origin at the middle of the edge; X from its later point to its earlier; Z
        /// across the plane of the triangle before; Y towards that triangle's third point. When that
        /// triangle is flat the axes are made from X alone, and kept on the third point's side.
        /// </summary>
        private PrcVector Apex(PrcVector lower, PrcVector higher, PrcVector far)
        {
            PrcVector o = (lower + higher) * 0.5;
            PrcVector x = lower - higher;
            if (!PrcBasis.TryUnitize(ref x))
            {
                throw Invalid("a point is placed from an edge without length");
            }

            PrcVector toFar = far - o;
            PrcVector z = x.Cross(toFar);
            bool fine = PrcBasis.TryUnitize(ref z);
            PrcVector y = z.Cross(x);
            fine &= PrcBasis.TryUnitize(ref y);
            if (!fine)
            {
                _geometry.MadeFrames++;
                if (!PrcBasis.TryMakeOrthoRep(x, out _, out z))
                {
                    throw Invalid("a point is placed from an edge without length");
                }

                // Only Z is kept of the made axes: Y is Z ^ X with the edge's own X, as for any other
                // edge, and it is this Y that says on which side the far point is. MakeOrthoRep's own Y
                // comes from its copy of X, made a unit vector once more, and differs in the last bits
                // whenever the squares of X do not add up to one in single precision; on a tube of long
                // thin triangles those bits grow into whole tolerances within a few hundred points.
                y = z.Cross(x);
                if (!PrcBasis.TryUnitize(ref y))
                {
                    throw Invalid("a point is placed from an edge without length");
                }

                if (toFar.Dot(y) < 0)
                {
                    y = -y;
                    z = -z;
                }
            }

            PrcVector offsets = TakeOffsets();
            return o + (x * offsets.X) + (y * offsets.Y) + (z * offsets.Z);
        }

        /// <summary>
        /// Reads the normals of a triangle's corners (7.8.9.3 and 7.8.9.4) and says whether the triangle
        /// is reversed: whether the normal of its first corner is against the cross product of the
        /// triangle taken from the middle of its first edge (PDF Association issues #727 and #540).
        /// </summary>
        private bool ReadNormals(int t, int first, int second, int third)
        {
            PrcTess3DCompressed tess = _tess;
            PrcVector pf = At(first), ps = At(second), pt = At(third);
            int face = tess.TriangleFaces[t];
            int c = t * 3;
            int firstNormal;
            if ((uint)face < (uint)tess.IsFacePlanar.Count && tess.IsFacePlanar[face])
            {
                // One normal for the whole face, stored with the first corner of its first triangle.
                if ((uint)face >= (uint)tess.FaceCount)
                {
                    throw Invalid("a triangle is of a face it does not have");
                }

                if (_faceNormal[face] < 0)
                {
                    _faceNormal[face] = Corner(first, pf, ps, pt);
                }

                firstNormal = _faceNormal[face];
                _cornerNormals[c] = _cornerNormals[c + 1] = _cornerNormals[c + 2] = firstNormal;
            }
            else
            {
                firstNormal = _cornerNormals[c] = Corner(first, pf, ps, pt);
                _cornerNormals[c + 1] = Corner(second, pf, ps, pt);
                _cornerNormals[c + 2] = Corner(third, pf, ps, pt);
            }

            PrcVector derived = (pt - ((pf + ps) * 0.5)).Cross(ps - pf);
            var normal = new PrcVector(_normals[firstNormal * 3], _normals[firstNormal * 3 + 1], _normals[firstNormal * 3 + 2]);
            return derived.Dot(normal) < 0;
        }

        /// <summary>The normal of one corner: read, referred to, or the point's only one.</summary>
        private int Corner(int point, PrcVector pf, PrcVector ps, PrcVector pt)
        {
            switch (_pointState[point])
            {
                case Unseen:
                {
                    bool several = ReadBit();
                    int normal = ReadNormal(pf, ps, pt);
                    _pointState[point] = several ? SeveralNormals : OneNormal;
                    _previousNormal[normal] = -1;
                    _lastNormal[point] = normal;
                    _normalsOfPoint[point] = 1;
                    return normal;
                }

                case OneNormal:
                    return _lastNormal[point];

                default:
                {
                    if (!ReadBit())
                    {
                        int normal = ReadNormal(pf, ps, pt);
                        _previousNormal[normal] = _lastNormal[point];
                        _lastNormal[point] = normal;
                        _normalsOfPoint[point]++;
                        return normal;
                    }

                    // A reference counts back from the normal stored last for the point.
                    int count = _normalsOfPoint[point];
                    int width = BitsToStore(count - 1);
                    int back = 0;
                    for (int i = 0; i < width; i++)
                    {
                        if (ReadBit())
                        {
                            back |= 1 << i;
                        }
                    }

                    if (back >= count)
                    {
                        throw Invalid("a corner refers to a normal its point does not have");
                    }

                    int found = _lastNormal[point];
                    while (back-- > 0)
                    {
                        found = _previousNormal[found];
                    }

                    return found;
                }
            }
        }

        /// <summary>
        /// The texture coordinates of a triangle's corners (7.8.9.8; PDF Association issue #829): one
        /// bit a corner, in listing order, for the triangles of faces that have a texture. Clear: the
        /// next pair the file stores, which becomes one of the point's. Set: a reference to a pair
        /// already stored for the point, by its place among them.
        /// </summary>
        private void ReadTextures(int t, int first, int second, int third)
        {
            PrcTess3DCompressed tess = _tess;
            int c = t * 3;
            int face = tess.TriangleFaces[t];
            if (tess.FaceHasTexture.Count > 0 && !((uint)face < (uint)tess.FaceHasTexture.Count && tess.FaceHasTexture[face]))
            {
                _cornerTextures[c] = _cornerTextures[c + 1] = _cornerTextures[c + 2] = -1;
                return;
            }

            _cornerTextures[c] = TextureCorner(first);
            _cornerTextures[c + 1] = TextureCorner(second);
            _cornerTextures[c + 2] = TextureCorner(third);
        }

        private int TextureCorner(int point)
        {
            PrcCompressedTexture texture = _texture!;
            if (_textureBroken || _textureBit >= texture.Bits.Count)
            {
                _textureBroken = true;
                return -1;
            }

            if (!texture.Bits[_textureBit++])
            {
                if ((_texturePair + 1) * 2 > texture.Parameters.Length)
                {
                    _textureBroken = true;
                    return -1;
                }

                int pair = _texturePair++;
                _previousPair[pair] = _pairsOfPoint[point] == 0 ? -1 : _lastPair[point];
                _lastPair[point] = pair;
                _pairsOfPoint[point]++;
                return pair;
            }

            if (_textureReference >= texture.References.Length)
            {
                _textureBroken = true;
                return -1;
            }

            // Unlike a reference to a normal, this one counts from the first pair stored for the point
            // (seen in files: read the other way, triangles sprawl over the texture).
            uint index = texture.References[_textureReference++];
            int count = _pairsOfPoint[point];
            if (index >= (uint)count)
            {
                _textureBroken = true;
                return -1;
            }

            int found = _lastPair[point];
            uint back = (uint)(count - 1) - index;
            while (back-- > 0)
            {
                found = _previousPair[found];
            }

            return found;
        }

        /// <summary>
        /// The colours of a triangle's corners (7.8.9.6): for every corner of every triangle of a face
        /// that has colours on its points, in listing order, five bytes: whether the colour has an
        /// alpha, then red, green, blue and alpha (the last one there either way).
        /// </summary>
        private void ReadColors(int t)
        {
            PrcTess3DCompressed tess = _tess;
            int c = t * 3;
            int face = tess.TriangleFaces[t];
            // A tessellation that says it has no faces stores one flag, for all its triangles.
            PrcBits flags = tess.IsPointColorOnFace;
            if (!(tess.HasFaces ? (uint)face < (uint)flags.Count && flags[face] : flags.Count > 0 && flags[0]))
            {
                _cornerColors[c] = _cornerColors[c + 1] = _cornerColors[c + 2] = -1;
                return;
            }

            byte[] colors = tess.PointColors;
            if (_colorAt + 15 > colors.Length)
            {
                _colorBroken = true;
                return;
            }

            for (int corner = 0; corner < 3; corner++, _colorAt += 5)
            {
                uint alpha = colors[_colorAt] != 0 ? colors[_colorAt + 4] : 255u;
                _cornerColors[c + corner] = (int)(colors[_colorAt + 1] | ((uint)colors[_colorAt + 2] << 8) | ((uint)colors[_colorAt + 3] << 16) | (alpha << 24));
            }
        }

        private bool ReadBit()
        {
            if (_bit >= _tess.NormalBits.Count)
            {
                throw Invalid("its normals end before its triangles");
            }

            return _tess.NormalBits[_bit++];
        }

        /// <summary>
        /// Reads a stored normal in the frame of the triangle (7.8.9.4). X is taken from the corner
        /// nearest a right angle; Z is the triangle's normal, made from X when the triangle is flat,
        /// then reversed when the bit says so; Y completes them.
        /// </summary>
        private int ReadNormal(PrcVector pf, PrcVector ps, PrcVector pt)
        {
            bool reversed = ReadBit(), xReversed = ReadBit(), yReversed = ReadBit();
            ushort[] angles = _tess.NormalAngles;
            if (_angle + 2 > angles.Length)
            {
                throw Invalid("its normals end before its triangles");
            }

            double theta = angles[_angle++] * _angleStep, phi = angles[_angle++] * _angleStep;
            PrcVector v1 = ps - pf, v2 = pt - pf, v3 = pt - ps;
            bool fine = PrcBasis.TryUnitize(ref v1) & PrcBasis.TryUnitize(ref v2) & PrcBasis.TryUnitize(ref v3);
            PrcVector x = v3, z = v1.Cross(v2);
            if (fine)
            {
                PrcBasis.TryAngleBetween(v1, v2, out double a1);
                PrcBasis.TryAngleBetween(v3, -v1, out double a2);
                PrcBasis.TryAngleBetween(-v2, -v3, out double a3);
                double t1 = Math.Abs(a1 - (Math.PI / 2)), t2 = Math.Abs(a2 - (Math.PI / 2)), t3 = Math.Abs(a3 - (Math.PI / 2));
                if (t1 < t2 && t1 < t3)
                {
                    // The draft prints V3 here, as for the next case: a misprint.
                    x = v1;
                    z = v1.Cross(v2);
                }
                else if (t2 < t3)
                {
                    x = v3;
                    z = -v3.Cross(v1);
                }
                else
                {
                    x = -v2;
                    z = v2.Cross(v3);
                }
            }

            if (!PrcBasis.TryUnitize(ref z))
            {
                PrcBasis.TryMakeOrthoRep(x, out _, out z);
            }

            if (reversed)
            {
                z = -z;
            }

            PrcVector y = z.Cross(x);
            if (!PrcBasis.TryUnitize(ref y))
            {
                PrcBasis.TryMakeOrthoRep(x, out y, out z);
            }

            double cx = PrcBasis.Cos(theta) * PrcBasis.Cos(phi), cy = PrcBasis.Sin(theta) * PrcBasis.Cos(phi), cz = PrcBasis.Sin(phi);
            PrcVector n = (x * (xReversed ? -cx : cx)) + (y * (yReversed ? -cy : cy)) + (z * cz);
            int index = _normalCount++;
            _normals[index * 3] = n.X;
            _normals[index * 3 + 1] = n.Y;
            _normals[index * 3 + 2] = n.Z;
            return index;
        }

        private readonly bool IsOffOrigin(PrcVector origin)
        {
            if (_pointCount == 0)
            {
                return false;
            }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            double sumX = 0, sumY = 0, sumZ = 0;
            for (int i = 0; i < _pointCount * 3; i += 3)
            {
                double x = _xyz[i], y = _xyz[i + 1], z = _xyz[i + 2];
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
                sumX += x;
                sumY += y;
                sumZ += z;
            }

            double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)) + ((maxZ - minZ) * (maxZ - minZ)));
            double limit = Math.Max(8 * _tolerance, 1e-3 * diagonal);
            return Distance(origin, (minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2) > limit
                && Distance(origin, sumX / _pointCount, sumY / _pointCount, sumZ / _pointCount) > limit;
        }

        private static double Distance(PrcVector a, double x, double y, double z) =>
            Math.Sqrt(((a.X - x) * (a.X - x)) + ((a.Y - y) * (a.Y - y)) + ((a.Z - z) * (a.Z - z)));

        private bool TryPopGate(out Gate gate)
        {
            while (_stackCount > 0)
            {
                gate = _stack[--_stackCount];
                if (EdgeCount(gate.Lower, gate.Higher) < 2)
                {
                    return true;
                }
            }

            gate = default;
            return false;
        }

        private void Push(int a, int b, int far)
        {
            if (_stackCount == _stack.Length)
            {
                Gate[] larger = ArrayPool<Gate>.Shared.Rent(_stack.Length * 2);
                _stack.AsSpan(0, _stackCount).CopyTo(larger);
                ArrayPool<Gate>.Shared.Return(_stack);
                _stack = larger;
            }

            _stack[_stackCount++] = a < b ? new Gate(a, b, far) : new Gate(b, a, far);
        }

        private readonly int Slot(int a, int b, out ulong key)
        {
            (int lower, int higher) = a < b ? (a, b) : (b, a);
            key = (((ulong)(uint)lower << 32) | (uint)higher) + 1;
            int mask = (1 << (64 - _edgeShift)) - 1;
            int slot = (int)((key * 0x9E3779B97F4A7C15UL) >> _edgeShift);
            while (_edgeKeys[slot] != 0 && _edgeKeys[slot] != key)
            {
                slot = (slot + 1) & mask;
            }

            return slot;
        }

        private readonly int EdgeCount(int a, int b)
        {
            int slot = Slot(a, b, out ulong key);
            return _edgeKeys[slot] == key ? _edgeCounts[slot] : 0;
        }

        private readonly void AddEdge(int a, int b)
        {
            int slot = Slot(a, b, out ulong key);
            if (_edgeKeys[slot] != key)
            {
                _edgeKeys[slot] = key;
                _edgeCounts[slot] = 1;
            }
            else if (_edgeCounts[slot] < byte.MaxValue)
            {
                _edgeCounts[slot]++;
            }
        }
    }
}
