using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Caly.Rendering.Scene3D.Internal;

namespace Caly.Rendering.Scene3D;

/// <summary>
/// Builds a <see cref="Scene"/> by appending. Buffers are pooled and grow geometrically; every append is
/// checked against the <see cref="SceneLimits"/>. Call <see cref="Build"/> once to hand everything to the
/// scene, or <see cref="Dispose"/> to give the buffers back. Not thread-safe.
/// </summary>
public sealed class SceneBuilder : IDisposable
{
    private PooledBuffer<SceneNode> _nodes;
    private PooledBuffer<SceneMesh> _meshes;
    private PooledBuffer<SceneMeshPart> _parts;
    private PooledBuffer<SceneMaterial> _materials;
    private PooledBuffer<SceneTextureLayer> _textureLayers;
    private PooledBuffer<int> _bindings;
    private PooledBuffer<SceneCamera> _cameras;
    private PooledBuffer<SceneLight> _lights;
    private PooledBuffer<float> _positions;
    private PooledBuffer<float> _normals;
    private PooledBuffer<float> _texCoords;
    private PooledBuffer<uint> _colors;
    private PooledBuffer<uint> _indices;

    private readonly List<string> _names = [];
    private readonly Dictionary<string, int> _nameLookup = new(StringComparer.Ordinal);
    private readonly List<SceneTexture> _textures = [];
    private readonly List<SceneDiagnostic> _diagnostics = [];
    private readonly List<SceneAnimation> _animations = [];
    private readonly List<SceneSkeleton> _skeletons = [];
    private readonly List<SceneSkin> _skins = [];

    private long _totalBytes;
    private long _textureBytes;
    private bool _disposed;

    private bool _meshOpen;
    private bool _meshVerticesAllocated;
    private SceneMesh _mesh;
    private int _meshIndexStart;
    private int _meshPartCursor;

    /// <summary>Creates a builder enforcing <paramref name="limits"/>, or the defaults.</summary>
    public SceneBuilder(SceneLimits? limits = null)
    {
        Limits = limits ?? SceneLimits.Default;
    }

    /// <summary>The limits this builder enforces.</summary>
    public SceneLimits Limits { get; }

    /// <summary>Scale that converts the scene's length unit to metres, when the source declares one; else 1.</summary>
    public double UnitScale { get; set; } = 1;

    /// <summary>Whether the source asks for its animations to start playing when the scene is shown.</summary>
    public bool AutoPlayAnimations { get; set; }

    /// <summary>Adds an animation of the node named by <see cref="SceneAnimation.NodeName"/>.</summary>
    public void AddAnimation(SceneAnimation animation)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(animation);
        CheckCount(_animations.Count, Limits.MaxNodes, "animations");
        Charge(animation.Keys.Length * Unsafe.SizeOf<SceneKeyFrame>());
        _animations.Add(animation);
    }

    /// <summary>Adds a skeleton and returns its index.</summary>
    public int AddSkeleton(SceneSkeleton skeleton)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(skeleton);
        CheckCount(_skeletons.Count, Limits.MaxNodes, "skeletons");
        Charge(skeleton.Bones.Length * Unsafe.SizeOf<SceneBone>() * 2L);
        _skeletons.Add(skeleton);
        return _skeletons.Count - 1;
    }

    /// <summary>Adds the skin of a mesh and returns its index; the mesh takes it with <see cref="SetMeshSkin"/>.</summary>
    public int AddSkin(SceneSkin skin)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(skin);
        if ((uint)skin.Skeleton >= (uint)_skeletons.Count)
        {
            throw new ArgumentException("The skin names a skeleton that was not added.", nameof(skin));
        }

        CheckCount(_skins.Count, Limits.MaxMeshes, "skins");
        Charge(skin.VertexCount * 8L);
        _skins.Add(skin);
        return _skins.Count - 1;
    }

    /// <summary>Makes the mesh being built follow a skin. Its vertex count must be the skin's.</summary>
    public void SetMeshSkin(int skin)
    {
        ThrowIfDisposed();
        RequireMesh();
        if ((uint)skin >= (uint)_skins.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(skin));
        }

        _mesh.Skin = skin;
    }

    /// <summary>Number of nodes added so far.</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>Number of meshes completed so far.</summary>
    public int MeshCount => _meshes.Count;

    /// <summary>Number of materials added so far.</summary>
    public int MaterialCount => _materials.Count;

    /// <summary>Returns the index of <paramref name="name"/>, adding it if it is new.</summary>
    public int AddName(string name)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);

        ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(_nameLookup, name, out bool exists);
        if (!exists)
        {
            Charge(name.Length * 2L + 32);
            index = _names.Count;
            _names.Add(name);
        }

        return index;
    }

    /// <summary>Adds a node and returns its index. Parents must be added before their children.</summary>
    public int AddNode(in SceneNode node)
    {
        ThrowIfDisposed();
        CheckCount(_nodes.Count, Limits.MaxNodes, "nodes");
        Charge(Unsafe.SizeOf<SceneNode>());
        return _nodes.Add(node);
    }

    /// <summary>Adds a material and returns its index.</summary>
    public int AddMaterial(in SceneMaterial material)
    {
        ThrowIfDisposed();
        CheckCount(_materials.Count, Limits.MaxMaterials, "materials");
        Charge(Unsafe.SizeOf<SceneMaterial>());
        return _materials.Add(material);
    }

    /// <summary>Adds a texture layer and returns its index. A material's layers must be contiguous.</summary>
    public int AddTextureLayer(in SceneTextureLayer layer)
    {
        ThrowIfDisposed();
        CheckCount(_textureLayers.Count, Limits.MaxMaterials * 8L, "texture layers");
        Charge(Unsafe.SizeOf<SceneTextureLayer>());
        return _textureLayers.Add(layer);
    }

    /// <summary>Adds a texture and returns its index.</summary>
    public int AddTexture(SceneTexture texture)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);
        CheckCount(_textures.Count, Limits.MaxTextures, "textures");

        long bytes = 0;
        for (int i = 0; i < texture.Images.Count; i++)
        {
            bytes += texture.Images[i].Data.Length;
        }

        _textureBytes += bytes;
        if (_textureBytes > Limits.MaxTextureBytes)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene's texture images exceed {Limits.MaxTextureBytes} bytes.");
        }

        _textures.Add(texture);
        return _textures.Count - 1;
    }

    /// <summary>Adds a camera and returns its index.</summary>
    public int AddCamera(in SceneCamera camera)
    {
        ThrowIfDisposed();
        CheckCount(_cameras.Count, Limits.MaxNodes, "cameras");
        Charge(Unsafe.SizeOf<SceneCamera>());
        return _cameras.Add(camera);
    }

    /// <summary>Adds a light and returns its index.</summary>
    public int AddLight(in SceneLight light)
    {
        ThrowIfDisposed();
        CheckCount(_lights.Count, Limits.MaxNodes, "lights");
        Charge(Unsafe.SizeOf<SceneLight>());
        return _lights.Add(light);
    }

    /// <summary>Appends a material index to the binding table and returns its position.</summary>
    public int AddBinding(int material)
    {
        ThrowIfDisposed();
        CheckCount(_bindings.Count, int.MaxValue - 1L, "material bindings");
        Charge(sizeof(int));
        return _bindings.Add(material);
    }

    /// <summary>Starts a mesh. Follow with <see cref="AllocateVertices"/>, <see cref="AllocateIndices"/>,
    /// <see cref="AddPart"/> and <see cref="EndMesh"/>.</summary>
    public void BeginMesh(int name, SceneMeshFlags flags, SceneTopology topology = SceneTopology.Triangles)
    {
        ThrowIfDisposed();
        if (_meshOpen)
        {
            throw new InvalidOperationException("A mesh is already being built.");
        }

        CheckCount(_meshes.Count, Limits.MaxMeshes, "meshes");

        _meshOpen = true;
        _meshVerticesAllocated = false;
        _meshIndexStart = _indices.Count;
        _meshPartCursor = _indices.Count;
        _mesh = new SceneMesh
        {
            FirstPart = _parts.Count,
            VertexStart = _positions.Count / 3,
            NormalStart = -1,
            TexCoordStart = -1,
            ColorStart = -1,
            Bounds = SceneBounds.Empty,
            Skin = -1,
            Flags = flags,
            Topology = topology,
            Name = name
        };
    }

    /// <summary>
    /// Reserves <paramref name="count"/> vertices in every stream the mesh has and returns them for
    /// writing. Their content is undefined until written. Once per mesh; use <see cref="TrimVertices"/> if
    /// fewer turn out to be needed.
    /// </summary>
    public SceneVertexSpans AllocateVertices(int count)
    {
        ThrowIfDisposed();
        RequireMesh();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_meshVerticesAllocated)
        {
            throw new InvalidOperationException("The mesh's vertices were already allocated.");
        }

        long totalVertices = _positions.Count / 3L + count;
        if (totalVertices > Limits.MaxVertices)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {Limits.MaxVertices} vertices.");
        }

        bool normals = (_mesh.Flags & SceneMeshFlags.HasNormals) != 0;
        bool texCoords = (_mesh.Flags & SceneMeshFlags.HasTexCoords) != 0;
        bool colors = (_mesh.Flags & SceneMeshFlags.HasColors) != 0;
        Charge(count * (12L + (normals ? 12 : 0) + (texCoords ? 8 : 0) + (colors ? 4 : 0)));

        _meshVerticesAllocated = true;
        _mesh.VertexCount = count;
        if (normals)
        {
            _mesh.NormalStart = _normals.Count / 3;
        }

        if (texCoords)
        {
            _mesh.TexCoordStart = _texCoords.Count / 2;
        }

        if (colors)
        {
            _mesh.ColorStart = _colors.Count;
        }

        return new SceneVertexSpans(
            _positions.AppendUninitialized(checked(count * 3)),
            normals ? _normals.AppendUninitialized(checked(count * 3)) : default,
            texCoords ? _texCoords.AppendUninitialized(checked(count * 2)) : default,
            colors ? _colors.AppendUninitialized(count) : default);
    }

    /// <summary>Keeps only the first <paramref name="count"/> vertices of the mesh being built.</summary>
    public void TrimVertices(int count)
    {
        ThrowIfDisposed();
        RequireMesh();
        if (!_meshVerticesAllocated || (uint)count > (uint)_mesh.VertexCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _mesh.VertexCount = count;
        _positions.Truncate((_mesh.VertexStart + count) * 3);
        if (_mesh.NormalStart >= 0)
        {
            _normals.Truncate((_mesh.NormalStart + count) * 3);
        }

        if (_mesh.TexCoordStart >= 0)
        {
            _texCoords.Truncate((_mesh.TexCoordStart + count) * 2);
        }

        if (_mesh.ColorStart >= 0)
        {
            _colors.Truncate(_mesh.ColorStart + count);
        }
    }

    /// <summary>
    /// Reserves <paramref name="count"/> indices for the mesh being built and returns them for writing.
    /// Index values are relative to the mesh's first vertex. May be called several times.
    /// </summary>
    public Span<uint> AllocateIndices(int count)
    {
        ThrowIfDisposed();
        RequireMesh();
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (_indices.Count + (long)count > Limits.MaxIndices)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {Limits.MaxIndices} indices.");
        }

        Charge(count * 4L);
        return _indices.AppendUninitialized(count);
    }

    /// <summary>
    /// Declares that the next <paramref name="indexCount"/> indices of the mesh, in allocation order, form
    /// a part using material slot <paramref name="shadingId"/>.
    /// </summary>
    public void AddPart(int indexCount, int shadingId)
    {
        ThrowIfDisposed();
        RequireMesh();
        if (indexCount < 0 || _meshPartCursor + (long)indexCount > _indices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(indexCount), "The part extends past the indices allocated for the mesh.");
        }

        Charge(Unsafe.SizeOf<SceneMeshPart>());
        _parts.Add(new SceneMeshPart { IndexStart = _meshPartCursor, IndexCount = indexCount, ShadingId = shadingId });
        _meshPartCursor += indexCount;
    }

    /// <summary>Completes the mesh, computes its bounds and returns its index.</summary>
    public int EndMesh()
    {
        ThrowIfDisposed();
        RequireMesh();

        _mesh.PartCount = _parts.Count - _mesh.FirstPart;
        _mesh.Bounds = ComputeBounds(_positions.Span.Slice(_mesh.VertexStart * 3, _mesh.VertexCount * 3));
        _meshOpen = false;
        Charge(Unsafe.SizeOf<SceneMesh>());
        return _meshes.Add(_mesh);
    }

    /// <summary>Records something that was skipped or approximated. Repeats of a code are counted.</summary>
    public void Report(SceneDiagnosticSeverity severity, string code, string message)
    {
        ThrowIfDisposed();
        for (int i = 0; i < _diagnostics.Count; i++)
        {
            if (_diagnostics[i].Code == code && _diagnostics[i].Severity == severity)
            {
                _diagnostics[i] = _diagnostics[i] with { Count = _diagnostics[i].Count + 1 };
                return;
            }
        }

        _diagnostics.Add(new SceneDiagnostic(severity, code, message, 1));
    }

    /// <summary>Hands everything to a new <see cref="Scene"/>. The builder cannot be used afterwards.</summary>
    public Scene Build()
    {
        ThrowIfDisposed();
        if (_meshOpen)
        {
            throw new InvalidOperationException("A mesh is still being built.");
        }

        _disposed = true;
        return new Scene(
            ref _nodes, ref _meshes, ref _parts, ref _materials, ref _textureLayers, ref _bindings, ref _cameras, ref _lights,
            ref _positions, ref _normals, ref _texCoords, ref _colors, ref _indices,
            _names.ToArray(), _textures.ToArray(), _diagnostics.ToArray(), _animations.ToArray(), _skeletons.ToArray(), _skins.ToArray(), AutoPlayAnimations, UnitScale);
    }

    /// <summary>Returns the buffers to the pool, unless <see cref="Build"/> already handed them over.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nodes.Return();
        _meshes.Return();
        _parts.Return();
        _materials.Return();
        _textureLayers.Return();
        _bindings.Return();
        _cameras.Return();
        _lights.Return();
        _positions.Return();
        _normals.Return();
        _texCoords.Return();
        _colors.Return();
        _indices.Return();
    }

    internal static SceneBounds ComputeBounds(ReadOnlySpan<float> positions)
    {
        if (positions.Length < 3)
        {
            return SceneBounds.Empty;
        }

        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated && positions.Length >= 8)
        {
            ref float ptr = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(positions);
            var minVec = System.Runtime.Intrinsics.Vector128.Create(ptr, System.Runtime.CompilerServices.Unsafe.Add(ref ptr, 1), System.Runtime.CompilerServices.Unsafe.Add(ref ptr, 2), 0f);
            var maxVec = minVec;

            int i = 3;
            int limit = positions.Length - 4; // safe for 4-float vector load
            for (; i <= limit; i += 3)
            {
                var cur = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref ptr, (nuint)i);
                minVec = System.Runtime.Intrinsics.Vector128.Min(minVec, cur);
                maxVec = System.Runtime.Intrinsics.Vector128.Max(maxVec, cur);
            }

            var min = new Vector3(minVec[0], minVec[1], minVec[2]);
            var max = new Vector3(maxVec[0], maxVec[1], maxVec[2]);

            for (; i + 2 < positions.Length; i += 3)
            {
                var p = new Vector3(System.Runtime.CompilerServices.Unsafe.Add(ref ptr, i), System.Runtime.CompilerServices.Unsafe.Add(ref ptr, i + 1), System.Runtime.CompilerServices.Unsafe.Add(ref ptr, i + 2));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            return new SceneBounds { Min = min, Max = max, IsEmpty = false };
        }
        else
        {
            var min = new Vector3(positions[0], positions[1], positions[2]);
            Vector3 max = min;
            for (int i = 3; i + 2 < positions.Length; i += 3)
            {
                var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            return new SceneBounds { Min = min, Max = max, IsEmpty = false };
        }
    }

    private void RequireMesh()
    {
        if (!_meshOpen)
        {
            throw new InvalidOperationException("No mesh is being built; call BeginMesh first.");
        }
    }

    private void CheckCount(long current, long max, string what)
    {
        if (current >= max)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {max} {what}.");
        }
    }

    private void Charge(long bytes)
    {
        _totalBytes += bytes;
        if (_totalBytes > Limits.MaxTotalBytes)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene needs more than {Limits.MaxTotalBytes} bytes.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
