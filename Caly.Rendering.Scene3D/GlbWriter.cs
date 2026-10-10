using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Caly.Rendering.Scene3D;

/// <summary>
/// Writes a <see cref="Scene"/> as binary glTF 2.0 (a .glb file), for looking at a scene in any glTF
/// viewer and for checking it with glTF tools.
/// <para>
/// It is a faithful dump, not a lossless conversion. Geometry, the node tree, cameras and each material's
/// colours go across; of the texture layers only the first with a JPEG or PNG colour image does, as the
/// base colour texture. Lights, texture blending and transforms, and node visibility other than "not drawn"
/// have no place in core glTF and are left out. Two things are changed on the way, as glTF requires:
/// normals are made unit length, and texture coordinates are flipped vertically (glTF's origin is the top
/// left of the image).
/// </para>
/// </summary>
public static class GlbWriter
{
    private const uint Magic = 0x46546C67;      // "glTF"
    private const uint JsonChunk = 0x4E4F534A;  // "JSON"
    private const uint BinaryChunk = 0x004E4942; // "BIN\0"

    private const int Float = 5126, UnsignedInt = 5125, UnsignedByte = 5121;
    private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

    /// <summary>How far a matrix's fourth column may be from 0, 0, 0, 1 and still be meant as that.</summary>
    private const float AffineTolerance = 1e-6f;

    /// <summary>Floats converted per step when a stream has to be changed on its way out.</summary>
    private const int ChunkFloats = 6 * 1024;

    /// <summary>Writes <paramref name="scene"/> to <paramref name="output"/>, which need not be seekable.</summary>
    /// <exception cref="InvalidOperationException">
    /// The scene has released its geometry, or is larger than a .glb file can be (4 GiB).
    /// </exception>
    public static void Write(Scene scene, Stream output)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(output);
        if (!scene.HasGeometry)
        {
            throw new InvalidOperationException("The scene has released its geometry and cannot be written.");
        }

        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("glTF buffers are little-endian.");
        }

        var layout = new Layout(scene);
        var json = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(json))
        {
            layout.WriteJson(writer);
        }

        long jsonLength = Pad(json.WrittenCount);
        long binaryLength = Pad(layout.BinaryLength);
        long total = 12 + 8 + jsonLength + (binaryLength > 0 ? 8 + binaryLength : 0);
        if (total > uint.MaxValue)
        {
            throw new InvalidOperationException("The scene is larger than a .glb file can be.");
        }

        Span<byte> header = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], JsonChunk);
        output.Write(header);
        output.Write(json.WrittenSpan);
        WritePadding(output, json.WrittenCount, (byte)' ');

        if (binaryLength > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)binaryLength);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], BinaryChunk);
            output.Write(header[..8]);
            layout.WriteBinary(output);
        }
    }

    private static long Pad(long length) => (length + 3) & ~3L;

    private static void WritePadding(Stream output, long length, byte value)
    {
        Span<byte> padding = stackalloc byte[3];
        padding.Fill(value);
        output.Write(padding[..(int)(Pad(length) - length)]);
    }

    /// <summary>
    /// Decides once what goes where — which glTF objects exist and where each stream sits in the binary
    /// chunk — so that the JSON and the bytes that follow it agree.
    /// </summary>
    private sealed class Layout
    {
        private readonly Scene _scene;

        // The binary chunk: the five vertex and index streams, whole, then the embedded images.
        private readonly long _positionsOffset, _normalsOffset, _texCoordsOffset, _colorsOffset, _indicesOffset;
        private readonly int _positionsView = -1, _normalsView = -1, _texCoordsView = -1, _colorsView = -1, _indicesView = -1;
        private readonly int _streamViews;

        // Per scene mesh: its first accessor, or -1 when it has nothing to draw. Accessors follow in the
        // order position, normal, texture coordinate, colour, then the indices of each part.
        private readonly int[] _meshAccessor;
        private readonly int _accessorCount;

        // glTF meshes: glTF binds materials to mesh primitives, a scene to node instances, so each
        // distinct pairing of a scene mesh with a run of bindings becomes one glTF mesh.
        private readonly List<(int Mesh, int FirstBinding, int BindingCount)> _gltfMeshes = [];
        private readonly int[] _nodeMesh;

        // Per scene camera: its glTF camera, or -1 when glTF could not hold it.
        private readonly int[] _cameraIndex;
        private readonly List<int> _cameras = [];

        // Embedded images: (scene texture, image within it, offset in the binary chunk).
        private readonly List<(int Texture, int Image, long Offset)> _images = [];
        private readonly int[] _textureImage;
        private readonly bool _anyUnlit;

        public long BinaryLength { get; }

        public Layout(Scene scene)
        {
            _scene = scene;

            long offset = 0;
            int view = 0;
            Place(scene.Positions.Length, ref offset, ref view, out _positionsOffset, out _positionsView);
            Place(scene.Normals.Length, ref offset, ref view, out _normalsOffset, out _normalsView);
            Place(scene.TexCoords.Length, ref offset, ref view, out _texCoordsOffset, out _texCoordsView);
            Place(scene.Colors.Length, ref offset, ref view, out _colorsOffset, out _colorsView);
            Place(scene.Indices.Length, ref offset, ref view, out _indicesOffset, out _indicesView);
            _streamViews = view;

            ReadOnlySpan<SceneMesh> meshes = scene.Meshes;
            _meshAccessor = new int[meshes.Length];
            for (int m = 0; m < meshes.Length; m++)
            {
                SceneMesh mesh = meshes[m];
                if (mesh.VertexCount == 0 || mesh.PartCount == 0)
                {
                    _meshAccessor[m] = -1;
                    continue;
                }

                _meshAccessor[m] = _accessorCount;
                _accessorCount += 1 + (mesh.NormalStart >= 0 ? 1 : 0) + (mesh.TexCoordStart >= 0 ? 1 : 0) + (mesh.ColorStart >= 0 ? 1 : 0) + mesh.PartCount;
            }

            ReadOnlySpan<SceneNode> nodes = scene.Nodes;
            _nodeMesh = new int[nodes.Length];
            var known = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < nodes.Length; i++)
            {
                SceneNode node = nodes[i];
                _nodeMesh[i] = -1;
                if (node.Mesh < 0 || !node.Visible || _meshAccessor[node.Mesh] < 0)
                {
                    continue;
                }

                (int, int, int) key = (node.Mesh, node.BindingCount == 0 ? 0 : node.FirstBinding, node.BindingCount);
                if (!known.TryGetValue(key, out int gltfMesh))
                {
                    gltfMesh = _gltfMeshes.Count;
                    _gltfMeshes.Add(key);
                    known.Add(key, gltfMesh);
                }

                _nodeMesh[i] = gltfMesh;
            }

            ReadOnlySpan<SceneCamera> cameras = scene.Cameras;
            _cameraIndex = new int[cameras.Length];
            for (int c = 0; c < cameras.Length; c++)
            {
                _cameraIndex[c] = -1;
                if (IsExpressible(cameras[c]))
                {
                    _cameraIndex[c] = _cameras.Count;
                    _cameras.Add(c);
                }
            }

            // Images, for the textures a material shows as its base colour.
            _textureImage = new int[scene.Textures.Count];
            Array.Fill(_textureImage, -1);
            ReadOnlySpan<SceneMaterial> materials = scene.Materials;
            for (int i = 0; i < materials.Length; i++)
            {
                _anyUnlit |= (materials[i].Flags & SceneMaterialFlags.Lit) == 0;
                int texture = BaseColorTexture(materials[i], out int image);
                if (texture >= 0 && _textureImage[texture] < 0)
                {
                    _textureImage[texture] = _images.Count;
                    _images.Add((texture, image, offset));
                    offset = Pad(offset + scene.Textures[texture].Images[image].Data.Length);
                }
            }

            BinaryLength = offset;
        }

        private static void Place(int elements, ref long offset, ref int view, out long streamOffset, out int streamView)
        {
            streamOffset = offset;
            streamView = -1;
            if (elements > 0)
            {
                streamView = view++;
                offset += elements * 4L;
            }
        }

        /// <summary>
        /// The texture a material shows as its base colour: that of its first layer that has a JPEG or PNG
        /// image carrying colour, or -1.
        /// </summary>
        private int BaseColorTexture(in SceneMaterial material, out int image)
        {
            image = -1;
            ReadOnlySpan<SceneTextureLayer> layers = _scene.TextureLayers;
            const SceneTextureChannels color = SceneTextureChannels.Rgb | SceneTextureChannels.Luminance;

            for (int i = 0; i < material.LayerCount; i++)
            {
                int texture = layers[material.FirstLayer + i].Texture;
                if ((uint)texture >= (uint)_scene.Textures.Count)
                {
                    continue;
                }

                IReadOnlyList<SceneTextureImage> images = _scene.Textures[texture].Images;
                for (int k = 0; k < images.Count; k++)
                {
                    bool encodable = images[k].Encoding is SceneImageEncoding.Png or SceneImageEncoding.Jpeg24 or SceneImageEncoding.Jpeg8;
                    if (encodable && !images[k].Data.IsEmpty && (images[k].Channels & color) != 0)
                    {
                        image = k;
                        return texture;
                    }
                }
            }

            return -1;
        }

        /// <summary>glTF wants a field of view strictly between 0 and 180 degrees and ordered clip planes.</summary>
        private static bool IsExpressible(in SceneCamera camera)
        {
            bool far = !(camera.Far > 0) || !float.IsFinite(camera.Far) || camera.Far > camera.Near;
            if (camera.Projection == SceneProjection.Orthographic)
            {
                return float.IsFinite(camera.OrthographicHeight) && camera.OrthographicHeight > 0
                    && float.IsFinite(camera.Near) && camera.Near >= 0 && float.IsFinite(camera.Far) && camera.Far > camera.Near;
            }

            return camera.FieldOfView > 0 && camera.FieldOfView < 180 && float.IsFinite(camera.Near) && camera.Near > 0 && far;
        }

        // ---- JSON ----

        public void WriteJson(Utf8JsonWriter w)
        {
            Scene scene = _scene;
            w.WriteStartObject();

            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", "Caly.Rendering.Scene3D");
            w.WriteEndObject();

            if (_anyUnlit)
            {
                w.WriteStartArray("extensionsUsed");
                w.WriteStringValue("KHR_materials_unlit");
                w.WriteEndArray();
            }

            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes");
            w.WriteStartObject();
            WriteChildren(w, "nodes", parent: -1);
            w.WriteEndObject();
            w.WriteEndArray();

            WriteNodes(w);
            WriteMeshes(w);
            WriteMaterials(w);
            WriteTextures(w);
            WriteCameras(w);
            WriteAccessors(w);
            WriteBufferViews(w);

            if (BinaryLength > 0)
            {
                w.WriteStartArray("buffers");
                w.WriteStartObject();
                w.WriteNumber("byteLength", BinaryLength);
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        /// <summary>The indices of the nodes whose parent is <paramref name="parent"/>, if there are any.</summary>
        private void WriteChildren(Utf8JsonWriter w, string property, int parent)
        {
            ReadOnlySpan<SceneNode> nodes = _scene.Nodes;
            bool any = false;

            // Children follow their parent, so the scan can start right after it.
            for (int i = parent + 1; i < nodes.Length; i++)
            {
                if (nodes[i].Parent == parent)
                {
                    if (!any)
                    {
                        w.WriteStartArray(property);
                        any = true;
                    }

                    w.WriteNumberValue(i);
                }
            }

            if (any)
            {
                w.WriteEndArray();
            }
        }

        private void WriteNodes(Utf8JsonWriter w)
        {
            ReadOnlySpan<SceneNode> nodes = _scene.Nodes;
            if (nodes.IsEmpty)
            {
                return;
            }

            // One pass per node over its successors would be quadratic for a wide tree; index children first.
            int[] firstChild = ArrayPool<int>.Shared.Rent(nodes.Length + 1);
            int[] nextSibling = ArrayPool<int>.Shared.Rent(nodes.Length);
            try
            {
                firstChild.AsSpan(0, nodes.Length + 1).Fill(-1);
                for (int i = nodes.Length - 1; i >= 0; i--)
                {
                    int parent = nodes[i].Parent;
                    int slot = (uint)parent < (uint)nodes.Length ? parent : nodes.Length;
                    nextSibling[i] = firstChild[slot];
                    firstChild[slot] = i;
                }

                w.WriteStartArray("nodes");
                Span<float> m = stackalloc float[16];
                for (int i = 0; i < nodes.Length; i++)
                {
                    SceneNode node = nodes[i];
                    w.WriteStartObject();
                    w.WriteString("name", _scene.GetName(node.Name));

                    // glTF stores matrices column by column for column vectors: the same sixteen numbers,
                    // in the same order, as Matrix4x4's fields for row vectors.
                    MemoryMarshal.Write(MemoryMarshal.AsBytes(m), in node.Local);

                    // glTF requires the fourth column to be 0, 0, 0, 1 exactly; files carry rounding noise there.
                    if (MathF.Abs(m[3]) < AffineTolerance && MathF.Abs(m[7]) < AffineTolerance && MathF.Abs(m[11]) < AffineTolerance && MathF.Abs(m[15] - 1) < AffineTolerance)
                    {
                        m[3] = m[7] = m[11] = 0;
                        m[15] = 1;
                    }

                    if (!node.Local.IsIdentity && AllFinite(m))
                    {
                        w.WriteStartArray("matrix");
                        foreach (float value in m)
                        {
                            w.WriteNumberValue(value);
                        }

                        w.WriteEndArray();
                    }

                    if (_nodeMesh[i] >= 0)
                    {
                        w.WriteNumber("mesh", _nodeMesh[i]);
                    }

                    if ((uint)node.Camera < (uint)_cameraIndex.Length && _cameraIndex[node.Camera] >= 0)
                    {
                        w.WriteNumber("camera", _cameraIndex[node.Camera]);
                    }

                    if (firstChild[i] >= 0)
                    {
                        w.WriteStartArray("children");
                        for (int child = firstChild[i]; child >= 0; child = nextSibling[child])
                        {
                            w.WriteNumberValue(child);
                        }

                        w.WriteEndArray();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }
            finally
            {
                ArrayPool<int>.Shared.Return(firstChild);
                ArrayPool<int>.Shared.Return(nextSibling);
            }
        }

        private void WriteMeshes(Utf8JsonWriter w)
        {
            if (_gltfMeshes.Count == 0)
            {
                return;
            }

            Scene scene = _scene;
            w.WriteStartArray("meshes");
            foreach ((int meshIndex, int firstBinding, int bindingCount) in _gltfMeshes)
            {
                SceneMesh mesh = scene.Meshes[meshIndex];
                int accessor = _meshAccessor[meshIndex];
                int normal = mesh.NormalStart >= 0 ? accessor + 1 : -1;
                int texCoord = mesh.TexCoordStart >= 0 ? accessor + 1 + (normal >= 0 ? 1 : 0) : -1;
                int color = mesh.ColorStart >= 0 ? accessor + 1 + (normal >= 0 ? 1 : 0) + (texCoord >= 0 ? 1 : 0) : -1;
                int firstIndices = accessor + 1 + (normal >= 0 ? 1 : 0) + (texCoord >= 0 ? 1 : 0) + (color >= 0 ? 1 : 0);

                w.WriteStartObject();
                w.WriteString("name", scene.GetName(mesh.Name));
                w.WriteStartArray("primitives");
                for (int p = 0; p < mesh.PartCount; p++)
                {
                    SceneMeshPart part = scene.Parts[mesh.FirstPart + p];

                    // A part whose shading id the node does not bind uses material 0.
                    int material = (uint)part.ShadingId < (uint)bindingCount ? scene.Bindings[firstBinding + part.ShadingId] : 0;
                    bool hasMaterial = (uint)material < (uint)scene.Materials.Length;

                    w.WriteStartObject();
                    w.WriteStartObject("attributes");
                    w.WriteNumber("POSITION", accessor);
                    if (normal >= 0)
                    {
                        w.WriteNumber("NORMAL", normal);
                    }

                    if (texCoord >= 0)
                    {
                        w.WriteNumber("TEXCOORD_0", texCoord);
                    }

                    // glTF always applies vertex colours; a scene only where the material asks for them.
                    if (color >= 0 && hasMaterial && (scene.Materials[material].Flags & SceneMaterialFlags.UseVertexColor) != 0)
                    {
                        w.WriteNumber("COLOR_0", color);
                    }

                    w.WriteEndObject();
                    w.WriteNumber("indices", firstIndices + p);
                    if (hasMaterial)
                    {
                        w.WriteNumber("material", material);
                    }

                    // glTF's numbers for points, lines and triangles.
                    w.WriteNumber("mode", mesh.Topology switch { SceneTopology.Points => 0, SceneTopology.Lines => 1, _ => 4 });
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        private void WriteMaterials(Utf8JsonWriter w)
        {
            ReadOnlySpan<SceneMaterial> materials = _scene.Materials;
            if (materials.IsEmpty)
            {
                return;
            }

            w.WriteStartArray("materials");
            for (int i = 0; i < materials.Length; i++)
            {
                SceneMaterial material = materials[i];
                float opacity = Unit(material.Opacity, 1);

                // The diffuse colour is what light picks out. A material with none (the format default has
                // only an ambient colour) would be black in a glTF viewer; show its ambient colour instead.
                Vector3 baseColor = material.Diffuse == Vector3.Zero ? material.Ambient : material.Diffuse;

                w.WriteStartObject();
                w.WriteString("name", _scene.GetName(material.Name));

                w.WriteStartObject("pbrMetallicRoughness");
                w.WriteStartArray("baseColorFactor");
                w.WriteNumberValue(Unit(baseColor.X, 0));
                w.WriteNumberValue(Unit(baseColor.Y, 0));
                w.WriteNumberValue(Unit(baseColor.Z, 0));
                w.WriteNumberValue(opacity);
                w.WriteEndArray();

                int texture = BaseColorTexture(material, out _);
                if (texture >= 0)
                {
                    w.WriteStartObject("baseColorTexture");
                    w.WriteNumber("index", _textureImage[texture]);
                    w.WriteEndObject();
                }

                w.WriteNumber("metallicFactor", 0);
                w.WriteNumber("roughnessFactor", 1 - Unit(material.Reflectivity, 0));
                w.WriteEndObject();

                if (material.Emissive != Vector3.Zero)
                {
                    w.WriteStartArray("emissiveFactor");
                    w.WriteNumberValue(Unit(material.Emissive.X, 0));
                    w.WriteNumberValue(Unit(material.Emissive.Y, 0));
                    w.WriteNumberValue(Unit(material.Emissive.Z, 0));
                    w.WriteEndArray();
                }

                if ((material.Flags & SceneMaterialFlags.AlphaTest) != 0)
                {
                    w.WriteString("alphaMode", "MASK");
                    w.WriteNumber("alphaCutoff", Unit(material.AlphaReference, 0.5f));
                }
                else if (opacity < 1)
                {
                    w.WriteString("alphaMode", "BLEND");
                }

                // Faces of converted CAD data are often wound either way.
                w.WriteBoolean("doubleSided", true);

                if ((material.Flags & SceneMaterialFlags.Lit) == 0)
                {
                    w.WriteStartObject("extensions");
                    w.WriteStartObject("KHR_materials_unlit");
                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        private void WriteTextures(Utf8JsonWriter w)
        {
            if (_images.Count == 0)
            {
                return;
            }

            w.WriteStartArray("textures");
            for (int i = 0; i < _images.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("name", _scene.Textures[_images[i].Texture].Name);
                w.WriteNumber("source", i);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("images");
            for (int i = 0; i < _images.Count; i++)
            {
                (int texture, int image, _) = _images[i];
                w.WriteStartObject();
                w.WriteNumber("bufferView", _streamViews + i);
                w.WriteString("mimeType", _scene.Textures[texture].Images[image].Encoding == SceneImageEncoding.Png ? "image/png" : "image/jpeg");
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        private void WriteCameras(Utf8JsonWriter w)
        {
            if (_cameras.Count == 0)
            {
                return;
            }

            w.WriteStartArray("cameras");
            foreach (int index in _cameras)
            {
                SceneCamera camera = _scene.Cameras[index];
                w.WriteStartObject();
                w.WriteString("name", _scene.GetName(camera.Name));
                if (camera.Projection == SceneProjection.Orthographic)
                {
                    // The view's aspect ratio is not the scene's to know: a square volume.
                    w.WriteString("type", "orthographic");
                    w.WriteStartObject("orthographic");
                    w.WriteNumber("xmag", camera.OrthographicHeight / 2);
                    w.WriteNumber("ymag", camera.OrthographicHeight / 2);
                    w.WriteNumber("znear", camera.Near);
                    w.WriteNumber("zfar", camera.Far);
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteString("type", "perspective");
                    w.WriteStartObject("perspective");
                    w.WriteNumber("yfov", camera.FieldOfView * (MathF.PI / 180));
                    w.WriteNumber("znear", camera.Near);
                    if (camera.Far > camera.Near && float.IsFinite(camera.Far))
                    {
                        w.WriteNumber("zfar", camera.Far);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        private void WriteAccessors(Utf8JsonWriter w)
        {
            if (_accessorCount == 0)
            {
                return;
            }

            Scene scene = _scene;
            w.WriteStartArray("accessors");
            for (int m = 0; m < _meshAccessor.Length; m++)
            {
                if (_meshAccessor[m] < 0)
                {
                    continue;
                }

                SceneMesh mesh = scene.Meshes[m];
                StartAccessor(w, _positionsView, mesh.VertexStart * 12L, Float, mesh.VertexCount, "VEC3");
                (Vector3 min, Vector3 max) = FiniteBounds(scene.Positions.Slice(mesh.VertexStart * 3, mesh.VertexCount * 3));
                WriteVector(w, "min", min);
                WriteVector(w, "max", max);
                w.WriteEndObject();

                if (mesh.NormalStart >= 0)
                {
                    StartAccessor(w, _normalsView, mesh.NormalStart * 12L, Float, mesh.VertexCount, "VEC3");
                    w.WriteEndObject();
                }

                if (mesh.TexCoordStart >= 0)
                {
                    StartAccessor(w, _texCoordsView, mesh.TexCoordStart * 8L, Float, mesh.VertexCount, "VEC2");
                    w.WriteEndObject();
                }

                if (mesh.ColorStart >= 0)
                {
                    StartAccessor(w, _colorsView, mesh.ColorStart * 4L, UnsignedByte, mesh.VertexCount, "VEC4");
                    w.WriteBoolean("normalized", true);
                    w.WriteEndObject();
                }

                for (int p = 0; p < mesh.PartCount; p++)
                {
                    SceneMeshPart part = scene.Parts[mesh.FirstPart + p];
                    StartAccessor(w, _indicesView, part.IndexStart * 4L, UnsignedInt, part.IndexCount, "SCALAR");
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
        }

        private static void StartAccessor(Utf8JsonWriter w, int bufferView, long byteOffset, int componentType, int count, string type)
        {
            w.WriteStartObject();
            w.WriteNumber("bufferView", bufferView);
            w.WriteNumber("byteOffset", byteOffset);
            w.WriteNumber("componentType", componentType);
            w.WriteNumber("count", count);
            w.WriteString("type", type);
        }

        private void WriteBufferViews(Utf8JsonWriter w)
        {
            if (BinaryLength == 0)
            {
                return;
            }

            Scene scene = _scene;
            w.WriteStartArray("bufferViews");
            WriteStreamView(w, _positionsView, _positionsOffset, scene.Positions.Length, 12, ArrayBuffer);
            WriteStreamView(w, _normalsView, _normalsOffset, scene.Normals.Length, 12, ArrayBuffer);
            WriteStreamView(w, _texCoordsView, _texCoordsOffset, scene.TexCoords.Length, 8, ArrayBuffer);
            WriteStreamView(w, _colorsView, _colorsOffset, scene.Colors.Length, 4, ArrayBuffer);
            WriteStreamView(w, _indicesView, _indicesOffset, scene.Indices.Length, 0, ElementArrayBuffer);

            foreach ((int texture, int image, long offset) in _images)
            {
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", offset);
                w.WriteNumber("byteLength", scene.Textures[texture].Images[image].Data.Length);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        private static void WriteStreamView(Utf8JsonWriter w, int view, long offset, int elements, int stride, int target)
        {
            if (view < 0)
            {
                return;
            }

            w.WriteStartObject();
            w.WriteNumber("buffer", 0);
            w.WriteNumber("byteOffset", offset);
            w.WriteNumber("byteLength", elements * 4L);
            if (stride > 0)
            {
                w.WriteNumber("byteStride", stride);
            }

            w.WriteNumber("target", target);
            w.WriteEndObject();
        }

        private static void WriteVector(Utf8JsonWriter w, string property, Vector3 value)
        {
            w.WriteStartArray(property);
            w.WriteNumberValue(value.X);
            w.WriteNumberValue(value.Y);
            w.WriteNumberValue(value.Z);
            w.WriteEndArray();
        }

        /// <summary>
        /// The exact bounds of the positions, as glTF requires of an accessor's min and max. Values that are
        /// not finite cannot be written as JSON numbers and are passed over.
        /// </summary>
        private static (Vector3 Min, Vector3 Max) FiniteBounds(ReadOnlySpan<float> positions)
        {
            if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated && positions.Length >= 8)
            {
                ref float ptr = ref MemoryMarshal.GetReference(positions);
                var minVec = System.Runtime.Intrinsics.Vector128.Create(float.PositiveInfinity);
                var maxVec = System.Runtime.Intrinsics.Vector128.Create(float.NegativeInfinity);

                int i = 0;
                int limit = positions.Length - 4;
                for (; i <= limit; i += 3)
                {
                    var cur = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref ptr, (nuint)i);
                    // Check first 3 elements for finite
                    if (float.IsFinite(cur[0]) && float.IsFinite(cur[1]) && float.IsFinite(cur[2]))
                    {
                        var curPos = System.Runtime.Intrinsics.Vector128.Create(cur[0], cur[1], cur[2], 0f);
                        minVec = System.Runtime.Intrinsics.Vector128.Min(minVec, curPos);
                        maxVec = System.Runtime.Intrinsics.Vector128.Max(maxVec, curPos);
                    }
                }

                var min = new Vector3(minVec[0], minVec[1], minVec[2]);
                var max = new Vector3(maxVec[0], maxVec[1], maxVec[2]);

                for (; i + 2 < positions.Length; i += 3)
                {
                    var p = new Vector3(System.Runtime.CompilerServices.Unsafe.Add(ref ptr, i), System.Runtime.CompilerServices.Unsafe.Add(ref ptr, i + 1), System.Runtime.CompilerServices.Unsafe.Add(ref ptr, i + 2));
                    if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))
                    {
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                }

                return float.IsFinite(min.X) ? (min, max) : (Vector3.Zero, Vector3.Zero);
            }
            else
            {
                var min = new Vector3(float.PositiveInfinity);
                var max = new Vector3(float.NegativeInfinity);
                for (int i = 0; i + 2 < positions.Length; i += 3)
                {
                    var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
                    if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))
                    {
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                }

                return float.IsFinite(min.X) ? (min, max) : (Vector3.Zero, Vector3.Zero);
            }
        }

        private static bool AllFinite(ReadOnlySpan<float> values)
        {
            foreach (float value in values)
            {
                if (!float.IsFinite(value))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A value clamped to 0 – 1; <paramref name="fallback"/> when it is not a number.</summary>
        private static float Unit(float value, float fallback) => float.IsNaN(value) ? fallback : Math.Clamp(value, 0f, 1f);

        // ---- binary chunk ----

        public void WriteBinary(Stream output)
        {
            Scene scene = _scene;
            output.Write(MemoryMarshal.AsBytes(scene.Positions));
            WriteNormals(output, scene.Normals);
            WriteTexCoords(output, scene.TexCoords);
            output.Write(MemoryMarshal.AsBytes(scene.Colors));
            output.Write(MemoryMarshal.AsBytes(scene.Indices));

            foreach ((int texture, int image, _) in _images)
            {
                ReadOnlySpan<byte> data = scene.Textures[texture].Images[image].Data.Span;
                output.Write(data);
                WritePadding(output, data.Length, 0);
            }
        }

        /// <summary>Normals, each made unit length; one that has no direction points along +Z.</summary>
        private static void WriteNormals(Stream output, ReadOnlySpan<float> normals)
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(ChunkFloats);
            try
            {
                while (!normals.IsEmpty)
                {
                    Span<float> chunk = buffer.AsSpan(0, Math.Min(ChunkFloats, normals.Length));
                    for (int i = 0; i + 2 < chunk.Length; i += 3)
                    {
                        var n = new Vector3(normals[i], normals[i + 1], normals[i + 2]);
                        float length = n.Length();
                        n = length > 1e-20f && float.IsFinite(length) ? n / length : Vector3.UnitZ;
                        chunk[i] = n.X;
                        chunk[i + 1] = n.Y;
                        chunk[i + 2] = n.Z;
                    }

                    output.Write(MemoryMarshal.AsBytes(chunk));
                    normals = normals[chunk.Length..];
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }

        /// <summary>Texture coordinates with V measured from the top of the image, as glTF does.</summary>
        private static void WriteTexCoords(Stream output, ReadOnlySpan<float> texCoords)
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(ChunkFloats);
            try
            {
                while (!texCoords.IsEmpty)
                {
                    Span<float> chunk = buffer.AsSpan(0, Math.Min(ChunkFloats, texCoords.Length));
                    for (int i = 0; i + 1 < chunk.Length; i += 2)
                    {
                        float u = texCoords[i], v = texCoords[i + 1];
                        chunk[i] = float.IsFinite(u) ? u : 0;
                        chunk[i + 1] = float.IsFinite(v) ? 1 - v : 0;
                    }

                    output.Write(MemoryMarshal.AsBytes(chunk));
                    texCoords = texCoords[chunk.Length..];
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }
    }
}
