using System.Buffers;
using System.Numerics;
using Caly.Rendering.Scene3D.Internal;

namespace Caly.Rendering.Scene3D;

/// <summary>
/// A decoded 3D scene: a tree of nodes instancing meshes, with materials, textures, cameras and lights.
/// <para>
/// Everything is held in flat arrays linked by index. Vertex streams are unified (one entry per vertex in
/// each stream a mesh has), so they can be uploaded to a GPU as they are; after uploading, call
/// <see cref="ReleaseGeometry"/> to give the vertex and index memory back while keeping the rest.
/// </para>
/// <para>
/// Buffers are pooled: dispose the scene when done, and do not use spans obtained from it afterwards.
/// Reading is thread-safe; <see cref="ReleaseGeometry"/> and <see cref="Dispose"/> are not.
/// </para>
/// </summary>
public sealed class Scene : IDisposable
{
    private SceneNode[]? _nodes;
    private SceneMesh[]? _meshes;
    private SceneMeshPart[]? _parts;
    private SceneMaterial[]? _materials;
    private SceneTextureLayer[]? _textureLayers;
    private int[]? _bindings;
    private SceneCamera[]? _cameras;
    private SceneLight[]? _lights;
    private float[]? _positions;
    private float[]? _normals;
    private float[]? _texCoords;
    private uint[]? _colors;
    private uint[]? _indices;

    private readonly int _nodeCount, _meshCount, _partCount, _materialCount, _textureLayerCount, _bindingCount, _cameraCount, _lightCount;
    private int _positionCount, _normalCount, _texCoordCount, _colorCount, _indexCount;

    private readonly string[] _names;
    private readonly SceneTexture[] _textures;
    private readonly SceneDiagnostic[] _diagnostics;
    private readonly SceneAnimation[] _animations;
    private readonly SceneSkeleton[] _skeletons;
    private readonly SceneSkin[] _skins;
    private bool _disposed;

    internal Scene(
        ref PooledBuffer<SceneNode> nodes,
        ref PooledBuffer<SceneMesh> meshes,
        ref PooledBuffer<SceneMeshPart> parts,
        ref PooledBuffer<SceneMaterial> materials,
        ref PooledBuffer<SceneTextureLayer> textureLayers,
        ref PooledBuffer<int> bindings,
        ref PooledBuffer<SceneCamera> cameras,
        ref PooledBuffer<SceneLight> lights,
        ref PooledBuffer<float> positions,
        ref PooledBuffer<float> normals,
        ref PooledBuffer<float> texCoords,
        ref PooledBuffer<uint> colors,
        ref PooledBuffer<uint> indices,
        string[] names,
        SceneTexture[] textures,
        SceneDiagnostic[] diagnostics,
        SceneAnimation[] animations,
        SceneSkeleton[] skeletons,
        SceneSkin[] skins,
        bool autoPlayAnimations,
        double unitScale)
    {
        _nodes = nodes.Detach(out _nodeCount);
        _meshes = meshes.Detach(out _meshCount);
        _parts = parts.Detach(out _partCount);
        _materials = materials.Detach(out _materialCount);
        _textureLayers = textureLayers.Detach(out _textureLayerCount);
        _bindings = bindings.Detach(out _bindingCount);
        _cameras = cameras.Detach(out _cameraCount);
        _lights = lights.Detach(out _lightCount);
        _positions = positions.Detach(out _positionCount);
        _normals = normals.Detach(out _normalCount);
        _texCoords = texCoords.Detach(out _texCoordCount);
        _colors = colors.Detach(out _colorCount);
        _indices = indices.Detach(out _indexCount);
        _names = names;
        _textures = textures;
        _diagnostics = diagnostics;
        _animations = animations;
        _skeletons = skeletons;
        _skins = skins;
        AutoPlayAnimations = autoPlayAnimations;
        UnitScale = unitScale;
        HasGeometry = true;
    }

    /// <summary>The nodes, parents before children.</summary>
    public ReadOnlySpan<SceneNode> Nodes => Read(_nodes, _nodeCount);

    /// <summary>The meshes.</summary>
    public ReadOnlySpan<SceneMesh> Meshes => Read(_meshes, _meshCount);

    /// <summary>The parts of all meshes; see <see cref="SceneMesh.FirstPart"/>.</summary>
    public ReadOnlySpan<SceneMeshPart> Parts => Read(_parts, _partCount);

    /// <summary>The materials.</summary>
    public ReadOnlySpan<SceneMaterial> Materials => Read(_materials, _materialCount);

    /// <summary>The texture layers of all materials; see <see cref="SceneMaterial.FirstLayer"/>.</summary>
    public ReadOnlySpan<SceneTextureLayer> TextureLayers => Read(_textureLayers, _textureLayerCount);

    /// <summary>Material indices per node instance and mesh part; see <see cref="SceneNode.FirstBinding"/>.</summary>
    public ReadOnlySpan<int> Bindings => Read(_bindings, _bindingCount);

    /// <summary>The cameras.</summary>
    public ReadOnlySpan<SceneCamera> Cameras => Read(_cameras, _cameraCount);

    /// <summary>The lights.</summary>
    public ReadOnlySpan<SceneLight> Lights => Read(_lights, _lightCount);

    /// <summary>Vertex positions, three floats each. Empty after <see cref="ReleaseGeometry"/>.</summary>
    public ReadOnlySpan<float> Positions => Read(_positions, _positionCount);

    /// <summary>Vertex normals, three floats each, for meshes that have them.</summary>
    public ReadOnlySpan<float> Normals => Read(_normals, _normalCount);

    /// <summary>Texture coordinates, two floats each, for meshes that have them.</summary>
    public ReadOnlySpan<float> TexCoords => Read(_texCoords, _texCoordCount);

    /// <summary>Vertex colours, RGBA with red in the low byte, for meshes that have them.</summary>
    public ReadOnlySpan<uint> Colors => Read(_colors, _colorCount);

    /// <summary>Triangle indices, relative to each mesh's first vertex.</summary>
    public ReadOnlySpan<uint> Indices => Read(_indices, _indexCount);

    /// <summary>The textures. Their images are encoded files, not pixels.</summary>
    public IReadOnlyList<SceneTexture> Textures
    {
        get
        {
            ThrowIfDisposed();
            return _textures;
        }
    }

    /// <summary>What the reader skipped or approximated.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics
    {
        get
        {
            ThrowIfDisposed();
            return _diagnostics;
        }
    }

    /// <summary>Scale that converts the scene's length unit to metres, when the source declares one; else 1.</summary>
    public double UnitScale { get; }

    /// <summary>
    /// Motions that move nodes over time; empty for a still scene. They are small and stay after
    /// <see cref="ReleaseGeometry"/>.
    /// </summary>
    public IReadOnlyList<SceneAnimation> Animations => _animations;

    /// <summary>Skeletons of skinned meshes.</summary>
    public IReadOnlyList<SceneSkeleton> Skeletons => _skeletons;

    /// <summary>How meshes follow skeletons; a mesh names its skin in <see cref="SceneMesh.Skin"/>.</summary>
    public IReadOnlyList<SceneSkin> Skins => _skins;

    /// <summary>Whether anything in the scene moves: a node with an animation, or a skinned mesh whose skeleton has a motion.</summary>
    public bool IsAnimated
    {
        get
        {
            if (_animations.Length > 0)
            {
                return true;
            }

            foreach (SceneSkin skin in _skins)
            {
                if (skin.Motion is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether the source asks for the animations to play as soon as the scene is shown.</summary>
    public bool AutoPlayAnimations { get; }

    /// <summary>
    /// Scene seconds until the longest animation has run once (its start delay plus its keys' span on the
    /// scene's clock); 0 without animations.
    /// </summary>
    public double AnimationDuration
    {
        get
        {
            double longest = 0;
            foreach (SceneAnimation animation in _animations)
            {
                double scale = animation.TimeScale > 0 ? animation.TimeScale : 1;
                longest = Math.Max(longest, animation.TimeOffset + animation.Duration / scale);
            }

            foreach (SceneSkin skin in _skins)
            {
                if (skin.Motion is { } motion)
                {
                    double scale = motion.TimeScale > 0 ? motion.TimeScale : 1;
                    longest = Math.Max(longest, motion.TimeOffset + motion.Duration / scale);
                }
            }

            return longest;
        }
    }

    /// <summary>Whether vertex and index data are still held.</summary>
    public bool HasGeometry { get; private set; }

    /// <summary>Bytes of vertex and index data currently held.</summary>
    public long GeometryBytes
    {
        get
        {
            ThrowIfDisposed();
            return ((long)_positionCount + _normalCount + _texCoordCount + _colorCount + _indexCount) * 4;
        }
    }

    /// <summary>Number of names.</summary>
    public int NameCount
    {
        get
        {
            ThrowIfDisposed();
            return _names.Length;
        }
    }

    /// <summary>The name at <paramref name="index"/>.</summary>
    public string GetName(int index)
    {
        ThrowIfDisposed();
        return _names[index];
    }

    /// <summary>
    /// Finds a node by name. A single name matches the first node of that name anywhere in the tree; a
    /// longer path is followed from a root, one name per level.
    /// </summary>
    /// <param name="path">The name, or the names from a root down to the node.</param>
    /// <param name="kind">When given, only nodes of this kind match.</param>
    /// <returns>The node's index, or -1.</returns>
    public int FindNode(ReadOnlySpan<string> path, SceneNodeKind? kind = null)
    {
        ReadOnlySpan<SceneNode> nodes = Nodes;
        if (path.IsEmpty)
        {
            return -1;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            if ((kind is null || nodes[i].Kind == kind) && MatchesPath(nodes, i, path))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether the node and its ancestors carry the names of <paramref name="path"/>, last name first.</summary>
    private bool MatchesPath(ReadOnlySpan<SceneNode> nodes, int node, ReadOnlySpan<string> path)
    {
        for (int level = path.Length - 1; level >= 0; level--)
        {
            if ((uint)node >= (uint)nodes.Length || !string.Equals(_names[nodes[node].Name], path[level], StringComparison.Ordinal))
            {
                return false;
            }

            node = nodes[node].Parent;
        }

        // A single name may sit anywhere; a path starts at a root.
        return path.Length == 1 || node < 0;
    }

    /// <summary>The transform from the node's space to scene space.</summary>
    public Matrix4x4 GetWorldMatrix(int node)
    {
        ReadOnlySpan<SceneNode> nodes = Nodes;
        Matrix4x4 world = nodes[node].Local;
        int parent = nodes[node].Parent;

        // Parents precede children, so the walk terminates; the counter guards against a corrupt tree.
        for (int guard = 0; parent >= 0 && guard < nodes.Length; guard++)
        {
            world *= nodes[parent].Local;
            parent = nodes[parent].Parent;
        }

        return world;
    }

    /// <summary>
    /// The box around every visible mesh instance, in scene space: what a camera should frame. Hidden
    /// nodes are left out, and so is an instance whose box is not finite once transformed; the result is
    /// empty or finite. Works after <see cref="ReleaseGeometry"/>.
    /// </summary>
    public SceneBounds ComputeWorldBounds()
    {
        ReadOnlySpan<SceneNode> nodes = Nodes;
        ReadOnlySpan<SceneMesh> meshes = Meshes;
        SceneBounds bounds = SceneBounds.Empty;

        for (int i = 0; i < nodes.Length; i++)
        {
            int mesh = nodes[i].Mesh;
            if (mesh < 0 || !nodes[i].Visible || meshes[mesh].Bounds.IsEmpty)
            {
                continue;
            }

            Matrix4x4 world = GetWorldMatrix(i);
            Vector3 min = meshes[mesh].Bounds.Min, max = meshes[mesh].Bounds.Max;
            SceneBounds box = SceneBounds.Empty;

            if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
            {
                var rowX = System.Runtime.Intrinsics.Vector128.Create(world.M11, world.M12, world.M13, 0f);
                var rowY = System.Runtime.Intrinsics.Vector128.Create(world.M21, world.M22, world.M23, 0f);
                var rowZ = System.Runtime.Intrinsics.Vector128.Create(world.M31, world.M32, world.M33, 0f);
                var rowT = System.Runtime.Intrinsics.Vector128.Create(world.M41, world.M42, world.M43, 0f);

                Vector3 c = (min + max) * 0.5f;
                Vector3 e = (max - min) * 0.5f;

                var newCenter = rowT + rowX * c.X + rowY * c.Y + rowZ * c.Z;
                var newExtent = System.Runtime.Intrinsics.Vector128.Abs(rowX) * e.X + System.Runtime.Intrinsics.Vector128.Abs(rowY) * e.Y + System.Runtime.Intrinsics.Vector128.Abs(rowZ) * e.Z;

                var minVec = newCenter - newExtent;
                var maxVec = newCenter + newExtent;

                box.Min = new Vector3(minVec[0], minVec[1], minVec[2]);
                box.Max = new Vector3(maxVec[0], maxVec[1], maxVec[2]);
                box.IsEmpty = false;
            }
            else
            {
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z);
                    box.Include(Vector3.Transform(point, world));
                }
            }

            // An instance that cannot be placed (a NaN in its transforms, or coordinates so large that
            // transforming them overflows) is left out rather than making the whole box meaningless.
            if (IsFinite(box.Min) && IsFinite(box.Max))
            {
                bounds.Include(box.Min);
                bounds.Include(box.Max);
            }
        }

        return bounds;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>
    /// Gives the vertex and index buffers back, typically once they have been uploaded to a GPU. Nodes,
    /// meshes (with their counts and bounds), materials, textures, cameras and lights stay.
    /// </summary>
    public void ReleaseGeometry()
    {
        ThrowIfDisposed();
        Return(ref _positions, ref _positionCount);
        Return(ref _normals, ref _normalCount);
        Return(ref _texCoords, ref _texCoordCount);
        Return(ref _colors, ref _colorCount);
        Return(ref _indices, ref _indexCount);
        foreach (SceneSkin skin in _skins)
        {
            skin.Release();
        }

        HasGeometry = false;
    }

    /// <summary>Returns every buffer to the pool.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseGeometry();
        _disposed = true;

        int ignored = 0;
        Return(ref _nodes, ref ignored);
        Return(ref _meshes, ref ignored);
        Return(ref _parts, ref ignored);
        Return(ref _materials, ref ignored);
        Return(ref _textureLayers, ref ignored);
        Return(ref _bindings, ref ignored);
        Return(ref _cameras, ref ignored);
        Return(ref _lights, ref ignored);
    }

    private ReadOnlySpan<T> Read<T>(T[]? array, int count)
    {
        ThrowIfDisposed();
        return array is null ? default : new ReadOnlySpan<T>(array, 0, count);
    }

    private static void Return<T>(ref T[]? array, ref int count)
    {
        if (array is not null)
        {
            ArrayPool<T>.Shared.Return(array);
            array = null;
        }

        count = 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
