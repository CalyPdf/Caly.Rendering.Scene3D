using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

public class GlbWriterTests
{
    /// <summary>A GLB file taken apart: its JSON and its binary chunk.</summary>
    private sealed class Glb : IDisposable
    {
        public required JsonDocument Document { get; init; }

        public required byte[] Binary { get; init; }

        public JsonElement Root => Document.RootElement;

        public JsonElement Get(string array, int index) => Root.GetProperty(array)[index];

        public int Count(string array) => Root.TryGetProperty(array, out JsonElement value) ? value.GetArrayLength() : 0;

        /// <summary>The bytes an accessor covers.</summary>
        public ReadOnlySpan<byte> AccessorBytes(int accessor, int elementSize)
        {
            JsonElement a = Get("accessors", accessor);
            JsonElement view = Get("bufferViews", a.GetProperty("bufferView").GetInt32());
            int offset = view.GetProperty("byteOffset").GetInt32() + (a.TryGetProperty("byteOffset", out JsonElement o) ? o.GetInt32() : 0);
            return Binary.AsSpan(offset, a.GetProperty("count").GetInt32() * elementSize);
        }

        public ReadOnlySpan<byte> ViewBytes(int bufferView)
        {
            JsonElement view = Get("bufferViews", bufferView);
            return Binary.AsSpan(view.GetProperty("byteOffset").GetInt32(), view.GetProperty("byteLength").GetInt32());
        }

        public void Dispose() => Document.Dispose();
    }

    private static Glb Write(Scene scene)
    {
        using var stream = new MemoryStream();
        GlbWriter.Write(scene, stream);
        byte[] file = stream.ToArray();

        // Header: magic, version, total length.
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(file));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        Assert.Equal((uint)file.Length, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal(0, file.Length % 4);

        // The JSON chunk, then at most one binary chunk; each a multiple of four bytes long.
        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)));
        Assert.Equal(0, jsonLength % 4);
        JsonDocument document = JsonDocument.Parse(file.AsMemory(20, jsonLength));

        byte[] binary = [];
        int position = 20 + jsonLength;
        if (position < file.Length)
        {
            int binaryLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position));
            Assert.Equal(0x004E4942u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4)));
            Assert.Equal(0, binaryLength % 4);
            binary = file.AsSpan(position + 8, binaryLength).ToArray();
            position += 8 + binaryLength;

            int declared = document.RootElement.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32();
            Assert.InRange(binaryLength - declared, 0, 3);
        }
        else
        {
            Assert.False(document.RootElement.TryGetProperty("buffers", out _));
        }

        Assert.Equal(file.Length, position);
        Assert.Equal("2.0", document.RootElement.GetProperty("asset").GetProperty("version").GetString());
        return new Glb { Document = document, Binary = binary };
    }

    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(e => e.GetSingle()).ToArray();

    [Fact]
    public void ARealSceneRoundTripsItsStructure()
    {
        using Scene scene = U3DSceneGraphTests.Read("box_base");
        using Glb glb = Write(scene);

        // Nodes, in scene order, with their matrices in glTF's column-major order — which is the order
        // of Matrix4x4's fields.
        Assert.Equal(scene.Nodes.Length, glb.Count("nodes"));
        var roots = new List<int>();
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            SceneNode node = scene.Nodes[i];
            JsonElement json = glb.Get("nodes", i);
            Assert.Equal(scene.GetName(node.Name), json.GetProperty("name").GetString());

            Matrix4x4 m = node.Local;
            float[] expected = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
            if (m.IsIdentity)
            {
                Assert.False(json.TryGetProperty("matrix", out _));
            }
            else
            {
                Assert.Equal(expected, Floats(json.GetProperty("matrix")));
            }

            if (node.Parent < 0)
            {
                roots.Add(i);
            }
            else
            {
                Assert.Contains(i, glb.Get("nodes", node.Parent).GetProperty("children").EnumerateArray().Select(c => c.GetInt32()));
            }

            Assert.Equal(node.Mesh >= 0, json.TryGetProperty("mesh", out _));
        }

        Assert.Equal(roots, glb.Get("scenes", 0).GetProperty("nodes").EnumerateArray().Select(n => n.GetInt32()).ToList());
        Assert.Equal(0, glb.Root.GetProperty("scene").GetInt32());

        // The one mesh: its accessors hold the scene's streams as they are.
        SceneMesh mesh = Assert.Single(scene.Meshes.ToArray());
        JsonElement primitive = Assert.Single(glb.Get("meshes", 0).GetProperty("primitives").EnumerateArray().ToArray());
        Assert.Equal(4, primitive.GetProperty("mode").GetInt32());

        int positionAccessor = primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
        JsonElement position = glb.Get("accessors", positionAccessor);
        Assert.Equal(mesh.VertexCount, position.GetProperty("count").GetInt32());
        Assert.Equal("VEC3", position.GetProperty("type").GetString());
        Assert.Equal(5126, position.GetProperty("componentType").GetInt32());
        Assert.Equal<float>([mesh.Bounds.Min.X, mesh.Bounds.Min.Y, mesh.Bounds.Min.Z], Floats(position.GetProperty("min")));
        Assert.Equal<float>([mesh.Bounds.Max.X, mesh.Bounds.Max.Y, mesh.Bounds.Max.Z], Floats(position.GetProperty("max")));
        Assert.True(glb.AccessorBytes(positionAccessor, 12).SequenceEqual(MemoryMarshal.AsBytes(scene.Positions.Slice(mesh.VertexStart * 3, mesh.VertexCount * 3))));

        int normalAccessor = primitive.GetProperty("attributes").GetProperty("NORMAL").GetInt32();
        Assert.Equal(mesh.VertexCount, glb.Get("accessors", normalAccessor).GetProperty("count").GetInt32());
        foreach (Vector3 normal in MemoryMarshal.Cast<byte, Vector3>(glb.AccessorBytes(normalAccessor, 12)))
        {
            Assert.Equal(1f, normal.Length(), 1e-5f);
        }

        Assert.False(primitive.GetProperty("attributes").TryGetProperty("TEXCOORD_0", out _));
        Assert.False(primitive.GetProperty("attributes").TryGetProperty("COLOR_0", out _));

        int indexAccessor = primitive.GetProperty("indices").GetInt32();
        Assert.Equal(5125, glb.Get("accessors", indexAccessor).GetProperty("componentType").GetInt32());
        Assert.Equal("SCALAR", glb.Get("accessors", indexAccessor).GetProperty("type").GetString());
        Assert.True(glb.AccessorBytes(indexAccessor, 4).SequenceEqual(MemoryMarshal.AsBytes(scene.Indices)));

        // Materials, index for index; the primitive uses the one the node binds to shading 0.
        Assert.Equal(scene.Materials.Length, glb.Count("materials"));
        SceneNode model = scene.Nodes.ToArray().Single(n => n.Mesh >= 0);
        int bound = scene.Bindings[model.FirstBinding];
        Assert.Equal(bound, primitive.GetProperty("material").GetInt32());

        SceneMaterial material = scene.Materials[bound];
        JsonElement pbr = glb.Get("materials", bound).GetProperty("pbrMetallicRoughness");
        Assert.Equal<float>([material.Diffuse.X, material.Diffuse.Y, material.Diffuse.Z, material.Opacity], Floats(pbr.GetProperty("baseColorFactor")));
        Assert.Equal(0f, pbr.GetProperty("metallicFactor").GetSingle());
        Assert.Equal(scene.GetName(material.Name), glb.Get("materials", bound).GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void EverySampleSceneWrites(string name)
    {
        using Scene scene = U3DSceneGraphTests.Read(name);
        using Glb glb = Write(scene);

        Assert.Equal(scene.Nodes.Length, glb.Count("nodes"));
        Assert.Equal(scene.Materials.Length, glb.Count("materials"));
        Assert.Equal(scene.Meshes.Length > 0, glb.Count("meshes") > 0);

        // Every accessor lies inside its buffer view, and every view inside the buffer.
        for (int i = 0; i < glb.Count("bufferViews"); i++)
        {
            JsonElement view = glb.Get("bufferViews", i);
            Assert.True(view.GetProperty("byteOffset").GetInt64() + view.GetProperty("byteLength").GetInt64() <= glb.Binary.Length);
            Assert.Equal(0, view.GetProperty("byteOffset").GetInt64() % 4);
        }
    }

    /// <summary>A scene made by hand, to reach what the samples do not.</summary>
    private static Scene HandMadeScene(byte[] png, byte[] alphaPng)
    {
        using var builder = new SceneBuilder();

        int texture = builder.AddTexture(new SceneTexture("Wood", 2, 2, SceneTextureChannels.Rgba,
        [
            new SceneTextureImage(SceneImageEncoding.Png, SceneTextureChannels.Alpha, alphaPng),
            new SceneTextureImage(SceneImageEncoding.Png, SceneTextureChannels.Rgb, png)
        ]));
        int tiff = builder.AddTexture(new SceneTexture("Tiff", 2, 2, SceneTextureChannels.Rgb, [new SceneTextureImage(SceneImageEncoding.Tiff, SceneTextureChannels.Rgb, new byte[] { 1, 2, 3 })]));

        builder.AddMaterial(new SceneMaterial { Ambient = new Vector3(0.75f), Opacity = 1, Flags = SceneMaterialFlags.Lit, Name = builder.AddName("") });
        int layer = builder.AddTextureLayer(new SceneTextureLayer { Texture = texture, Intensity = 1, Transform = Matrix4x4.Identity });
        int textured = builder.AddMaterial(new SceneMaterial
        {
            Diffuse = new Vector3(0.2f, 0.4f, 0.6f), Emissive = new Vector3(0.1f, 2f, float.NaN), Opacity = 0.5f, Reflectivity = 0.25f,
            Flags = SceneMaterialFlags.Lit | SceneMaterialFlags.UseVertexColor, FirstLayer = layer, LayerCount = 1, Name = builder.AddName("Textured")
        });
        int tiffLayer = builder.AddTextureLayer(new SceneTextureLayer { Texture = tiff, Intensity = 1, Transform = Matrix4x4.Identity });
        int cutout = builder.AddMaterial(new SceneMaterial
        {
            Diffuse = Vector3.One, Opacity = 1, AlphaReference = 0.3f, Flags = SceneMaterialFlags.AlphaTest,
            FirstLayer = tiffLayer, LayerCount = 1, Name = builder.AddName("Cutout")
        });

        // A quad in two parts, with every stream.
        builder.BeginMesh(builder.AddName("Quad"), SceneMeshFlags.HasNormals | SceneMeshFlags.HasTexCoords | SceneMeshFlags.HasColors);
        SceneVertexSpans v = builder.AllocateVertices(4);
        new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }.CopyTo(v.Positions);
        new float[] { 0, 0, 2, 0, 0, 0, 0, 3, 0, 0, 0, 1 }.CopyTo(v.Normals);
        new float[] { 0, 0, 1, 0, 1, 1, 0, 0.25f }.CopyTo(v.TexCoords);
        new uint[] { 0xFF0000FF, 0xFF00FF00, 0xFFFF0000, 0x80FFFFFF }.CopyTo(v.Colors);
        new uint[] { 0, 1, 2, 0, 2, 3 }.CopyTo(builder.AllocateIndices(6));
        builder.AddPart(3, 0);
        builder.AddPart(3, 1);
        int mesh = builder.EndMesh();

        int a = builder.AddBinding(textured);
        builder.AddBinding(cutout);
        int b = builder.AddBinding(cutout);

        int name = builder.AddName("Node");
        int root = builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.CreateTranslation(1, 2, 3), Kind = SceneNodeKind.Group, Mesh = -1, Light = -1, Camera = -1, Name = name, Visible = true });
        builder.AddNode(new SceneNode { Parent = root, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, FirstBinding = a, BindingCount = 2, Name = name, Visible = true });
        builder.AddNode(new SceneNode { Parent = root, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, FirstBinding = b, BindingCount = 1, Name = name, Visible = true });
        builder.AddNode(new SceneNode { Parent = root, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, FirstBinding = a, BindingCount = 2, Name = name, Visible = true });
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, Name = name, Visible = false });

        var broken = Matrix4x4.Identity;
        broken.M42 = float.NaN;
        int perspective = builder.AddCamera(new SceneCamera { Projection = SceneProjection.Perspective, FieldOfView = 90, Near = 0.5f, Far = 100, Name = name });
        int orthographic = builder.AddCamera(new SceneCamera { Projection = SceneProjection.Orthographic, OrthographicHeight = 8, Near = 0, Far = 50, Name = name });
        int unusable = builder.AddCamera(new SceneCamera { Projection = SceneProjection.Perspective, FieldOfView = 0, Near = 1, Far = 10, Name = name });
        builder.AddNode(new SceneNode { Parent = -1, Local = broken, Kind = SceneNodeKind.Camera, Mesh = -1, Light = -1, Camera = perspective, Name = name, Visible = true });
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Camera, Mesh = -1, Light = -1, Camera = orthographic, Name = name, Visible = true });
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Camera, Mesh = -1, Light = -1, Camera = unusable, Name = name, Visible = true });

        return builder.Build();
    }

    [Fact]
    public void AHandMadeSceneWritesEveryFeature()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5];
        using Scene scene = HandMadeScene(png, [0x89, 0x50, 0x4E, 0x47, 9]);
        using Glb glb = Write(scene);

        // Nodes 1 and 3 bind the same materials and share a glTF mesh; node 2 binds others and gets its own,
        // over the same accessors. The invisible node 4 keeps its place but shows nothing.
        Assert.Equal(2, glb.Count("meshes"));
        int first = glb.Get("nodes", 1).GetProperty("mesh").GetInt32();
        int second = glb.Get("nodes", 2).GetProperty("mesh").GetInt32();
        Assert.NotEqual(first, second);
        Assert.Equal(first, glb.Get("nodes", 3).GetProperty("mesh").GetInt32());
        Assert.False(glb.Get("nodes", 4).TryGetProperty("mesh", out _));
        Assert.Equal<int>([1, 2, 3], glb.Get("nodes", 0).GetProperty("children").EnumerateArray().Select(c => c.GetInt32()).ToArray());

        JsonElement[] primitives = glb.Get("meshes", first).GetProperty("primitives").EnumerateArray().ToArray();
        JsonElement[] others = glb.Get("meshes", second).GetProperty("primitives").EnumerateArray().ToArray();
        Assert.Equal(2, primitives.Length);
        Assert.Equal(1, primitives[0].GetProperty("material").GetInt32());
        Assert.Equal(2, primitives[1].GetProperty("material").GetInt32());

        // Node 2 has one binding for two parts: the second part falls back to material 0.
        Assert.Equal(2, others[0].GetProperty("material").GetInt32());
        Assert.Equal(0, others[1].GetProperty("material").GetInt32());
        Assert.Equal(primitives[0].GetProperty("indices").GetInt32(), others[0].GetProperty("indices").GetInt32());
        Assert.Equal(primitives[0].GetProperty("attributes").GetProperty("POSITION").GetInt32(), others[0].GetProperty("attributes").GetProperty("POSITION").GetInt32());

        // Vertex colours go only to primitives whose material uses them.
        JsonElement attributes = primitives[0].GetProperty("attributes");
        Assert.True(attributes.TryGetProperty("COLOR_0", out JsonElement colorAccessor));
        Assert.False(primitives[1].GetProperty("attributes").TryGetProperty("COLOR_0", out _));
        JsonElement color = glb.Get("accessors", colorAccessor.GetInt32());
        Assert.Equal((5121, true, "VEC4"), (color.GetProperty("componentType").GetInt32(), color.GetProperty("normalized").GetBoolean(), color.GetProperty("type").GetString()));
        Assert.Equal<byte>([0xFF, 0, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x80], glb.AccessorBytes(colorAccessor.GetInt32(), 4).ToArray());

        // Normals are made unit length (a zero one points up the Z axis); V is flipped to glTF's top-left origin.
        float[] normals = MemoryMarshal.Cast<byte, float>(glb.AccessorBytes(attributes.GetProperty("NORMAL").GetInt32(), 12)).ToArray();
        Assert.Equal<float>([0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 0, 1], normals);
        float[] uvs = MemoryMarshal.Cast<byte, float>(glb.AccessorBytes(attributes.GetProperty("TEXCOORD_0").GetInt32(), 8)).ToArray();
        Assert.Equal<float>([0, 1, 1, 1, 1, 0, 0, 0.75f], uvs);

        Assert.Equal<uint>([0, 1, 2], MemoryMarshal.Cast<byte, uint>(glb.AccessorBytes(primitives[0].GetProperty("indices").GetInt32(), 4)).ToArray());
        Assert.Equal<uint>([0, 2, 3], MemoryMarshal.Cast<byte, uint>(glb.AccessorBytes(primitives[1].GetProperty("indices").GetInt32(), 4)).ToArray());

        // Materials.
        JsonElement defaultMaterial = glb.Get("materials", 0);
        Assert.Equal<float>([0.75f, 0.75f, 0.75f, 1], Floats(defaultMaterial.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")));
        Assert.True(defaultMaterial.GetProperty("doubleSided").GetBoolean());
        Assert.False(defaultMaterial.TryGetProperty("alphaMode", out _));

        JsonElement textured = glb.Get("materials", 1);
        Assert.Equal<float>([0.2f, 0.4f, 0.6f, 0.5f], Floats(textured.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")));
        Assert.Equal(0.75f, textured.GetProperty("pbrMetallicRoughness").GetProperty("roughnessFactor").GetSingle());
        Assert.Equal("BLEND", textured.GetProperty("alphaMode").GetString());
        Assert.Equal<float>([0.1f, 1, 0], Floats(textured.GetProperty("emissiveFactor")));

        // The texture's colour image is embedded byte for byte; its alpha-only image is not used.
        int textureIndex = textured.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture").GetProperty("index").GetInt32();
        JsonElement image = glb.Get("images", glb.Get("textures", textureIndex).GetProperty("source").GetInt32());
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        Assert.Equal(png, glb.ViewBytes(image.GetProperty("bufferView").GetInt32()).ToArray());
        Assert.Equal(1, glb.Count("images"));

        // A TIFF cannot be a glTF image; an unlit material with an alpha test.
        JsonElement cutout = glb.Get("materials", 2);
        Assert.False(cutout.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
        Assert.Equal("MASK", cutout.GetProperty("alphaMode").GetString());
        Assert.Equal(0.3f, cutout.GetProperty("alphaCutoff").GetSingle());
        Assert.True(cutout.GetProperty("extensions").TryGetProperty("KHR_materials_unlit", out _));
        Assert.Contains("KHR_materials_unlit", glb.Root.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()));

        // Cameras: the unusable one (no field of view) is left out; a matrix that is not finite is too.
        Assert.Equal(2, glb.Count("cameras"));
        JsonElement perspectiveNode = glb.Get("nodes", 5);
        Assert.False(perspectiveNode.TryGetProperty("matrix", out _));
        JsonElement perspective = glb.Get("cameras", perspectiveNode.GetProperty("camera").GetInt32());
        Assert.Equal("perspective", perspective.GetProperty("type").GetString());
        Assert.Equal(MathF.PI / 2, perspective.GetProperty("perspective").GetProperty("yfov").GetSingle(), 1e-6f);
        Assert.Equal(0.5f, perspective.GetProperty("perspective").GetProperty("znear").GetSingle());
        Assert.Equal(100f, perspective.GetProperty("perspective").GetProperty("zfar").GetSingle());

        JsonElement orthographic = glb.Get("cameras", glb.Get("nodes", 6).GetProperty("camera").GetInt32());
        Assert.Equal("orthographic", orthographic.GetProperty("type").GetString());
        Assert.Equal(4f, orthographic.GetProperty("orthographic").GetProperty("ymag").GetSingle());
        Assert.Equal(4f, orthographic.GetProperty("orthographic").GetProperty("xmag").GetSingle());
        Assert.False(glb.Get("nodes", 7).TryGetProperty("camera", out _));
    }

    [Theory]
    [InlineData(SceneTopology.Triangles, 4)]
    [InlineData(SceneTopology.Lines, 1)]
    [InlineData(SceneTopology.Points, 0)]
    public void APrimitiveSaysWhatItsIndicesJoin(SceneTopology topology, int mode)
    {
        using var builder = new SceneBuilder();
        builder.BeginMesh(builder.AddName("m"), SceneMeshFlags.None, topology);
        new float[] { 0, 0, 0, 1, 0, 0, 1, 2, 0, 0, 2, 0, 0, 0, 1, 1, 1, 1 }.CopyTo(builder.AllocateVertices(6).Positions);
        new uint[] { 0, 1, 2, 3, 4, 5 }.CopyTo(builder.AllocateIndices(6));
        builder.AddPart(6, shadingId: 0);
        int mesh = builder.EndMesh();
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, Name = builder.AddName("n"), Visible = true });
        using Scene scene = builder.Build();

        using Glb glb = Write(scene);

        Assert.Equal(mode, glb.Document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("mode").GetInt32());
    }

    [Fact]
    public void AnEmptySceneIsAValidFile()
    {
        using var builder = new SceneBuilder();
        using Scene scene = builder.Build();
        using Glb glb = Write(scene);

        Assert.Empty(glb.Binary);
        foreach (string array in new[] { "nodes", "meshes", "materials", "accessors", "bufferViews", "buffers", "images", "textures", "cameras" })
        {
            Assert.False(glb.Root.TryGetProperty(array, out _), array);
        }

        Assert.False(glb.Get("scenes", 0).TryGetProperty("nodes", out _));
    }

    [Fact]
    public void WritingNeedsTheGeometry()
    {
        using Scene scene = U3DSceneGraphTests.Read("box_base");
        scene.ReleaseGeometry();
        Assert.Throws<InvalidOperationException>(() => GlbWriter.Write(scene, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => GlbWriter.Write(null!, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => GlbWriter.Write(scene, null!));
    }

    [Fact]
    public void TheJsonIsPaddedWithSpacesAndNamesAreEscaped()
    {
        using var builder = new SceneBuilder();
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.Identity, Mesh = -1, Light = -1, Camera = -1, Name = builder.AddName("a \"quoted\" näme\n"), Visible = true });
        using Scene scene = builder.Build();

        using var stream = new MemoryStream();
        GlbWriter.Write(scene, stream);
        byte[] file = stream.ToArray();

        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        string json = Encoding.UTF8.GetString(file, 20, jsonLength);
        Assert.Equal(json.TrimEnd(' ').Length, json.TrimEnd().Length);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("a \"quoted\" näme\n", document.RootElement.GetProperty("nodes")[0].GetProperty("name").GetString());
    }

    /// <summary>
    /// glTF wants a node matrix to be affine exactly. Real files carry rounding noise in the fourth column
    /// (2.8e-17 where 0 is meant), which the Khronos validator rejects; the writer rounds it away.
    /// </summary>
    [Fact]
    public void RoundingNoiseInAMatrixIsNotWritten()
    {
        Matrix4x4 noisy = Matrix4x4.CreateRotationZ(0.5f) * Matrix4x4.CreateTranslation(1, 2, 3);
        noisy.M14 = 2.8e-17f;
        noisy.M24 = -8.7e-19f;
        noisy.M34 = 4.7e-33f;
        noisy.M44 = 1.0000001f;

        using var builder = new SceneBuilder();
        builder.AddNode(new SceneNode { Parent = -1, Local = noisy, Mesh = -1, Light = -1, Camera = -1, Name = builder.AddName("n"), Visible = true });
        using Scene scene = builder.Build();
        using Glb glb = Write(scene);

        float[] m = Floats(glb.Get("nodes", 0).GetProperty("matrix"));
        Assert.Equal<float>([0, 0, 0, 1], [m[3], m[7], m[11], m[15]]);
        Assert.Equal<float>([noisy.M11, noisy.M12, noisy.M13, noisy.M41, noisy.M42, noisy.M43], [m[0], m[1], m[2], m[12], m[13], m[14]]);
    }
}
