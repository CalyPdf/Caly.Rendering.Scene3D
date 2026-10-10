using System.Numerics;

namespace Caly.Rendering.Scene3D.U3D.Internal;

internal sealed partial class U3DDocument
{
    /// <summary>A lit texture shader as declared: it names its material and textures.</summary>
    private sealed class ShaderDeclaration
    {
        public required string Name { get; init; }

        public required string Material { get; init; }

        public required SceneMaterialFlags Flags { get; init; }

        public required float AlphaReference { get; init; }

        /// <summary>Set when the shader asks for an alpha test this reader does not carry.</summary>
        public required bool UnsupportedAlphaTest { get; init; }

        public required (string Texture, SceneTextureLayer Layer)[] Layers { get; init; }
    }

    /// <summary>
    /// The shader lists of a shading modifier, reduced to the first shader of each list. A null entry is a
    /// list without shaders.
    /// </summary>
    private sealed class ShadingDeclaration
    {
        public required string?[] FirstShaders { get; init; }

        /// <summary>Lists that name more than one shader (further render passes).</summary>
        public required int MultiPassLists { get; init; }

        // Where the lists were put in the scene's binding table, once a node has asked for them.
        public int FirstBinding { get; set; } = -1;
    }

    private const uint ShaderLightingEnabled = 0x1;
    private const uint ShaderAlphaTestEnabled = 0x2;
    private const uint ShaderUseVertexColor = 0x4;

    private const uint AlphaGreater = 0x612;
    private const uint AlphaGreaterOrEqual = 0x616;
    private const uint AlphaAlways = 0x617;

    // The kinds of geometry a shading modifier is for: meshes 1, lines 2, points 4 (and glyphs 8).
    private const int ShadingKinds = 3;

    private const int ChainTypeModelResource = 1;
    private const int ChainTypeTexture = 2;

    private readonly Dictionary<string, SceneMaterial> _materialResources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShaderDeclaration> _shaders = new(StringComparer.Ordinal);
    private readonly List<string> _shaderOrder = [];
    // By (name, kind of geometry): a chain may hold one modifier for its mesh and another for lines.
    private readonly Dictionary<(string Name, int Kind), ShadingDeclaration> _nodeShading = [];
    private readonly Dictionary<(string Name, int Kind), ShadingDeclaration> _resourceShading = [];

    // Scene material of each shader, filled by BuildMaterials.
    private readonly Dictionary<string, int> _materialIndices = new(StringComparer.Ordinal);

    /// <summary>The material resource nothing has to declare (ECMA-363 8.4.2).</summary>
    private static SceneMaterial DefaultMaterialResource => new() { Ambient = new Vector3(0.75f), Opacity = 1 };

    /// <summary>ECMA-363 9.8.4. A later resource of the same name replaces the earlier one.</summary>
    private void ReadMaterialResource(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);

        // The attribute flags say which of the values a renderer should use; the values are all present.
        reader.ReadU32();

        Span<float> v = stackalloc float[14];
        reader.ReadF32(v);
        _materialResources[name] = new SceneMaterial
        {
            Ambient = new Vector3(v[0], v[1], v[2]),
            Diffuse = new Vector3(v[3], v[4], v[5]),
            Specular = new Vector3(v[6], v[7], v[8]),
            Emissive = new Vector3(v[9], v[10], v[11]),
            Reflectivity = v[12],
            Opacity = v[13]
        };
    }

    /// <summary>
    /// ECMA-363 9.8.3. The field order is the reference implementation's: the shader channels and alpha
    /// texture channels precede the material name, and the texture intensity follows the texture name.
    /// </summary>
    private void ReadLitTextureShader(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        uint attributes = reader.ReadU32();
        float alphaReference = reader.ReadF32();
        uint alphaFunction = reader.ReadU32();
        reader.ReadU32(); // colour blend function
        reader.ReadU32(); // render pass flags
        uint channels = reader.ReadU32() & 0xFF;
        reader.ReadU32(); // alpha texture channels
        string material = ReadName(ref reader);

        var layers = new (string, SceneTextureLayer)[BitOperations.PopCount(channels)];
        Span<float> m = stackalloc float[16];
        for (int i = 0; i < layers.Length; i++)
        {
            string texture = ReadName(ref reader);
            var layer = new SceneTextureLayer
            {
                Texture = -1,
                Intensity = reader.ReadF32(),
                BlendFunction = BlendFunctionOf(reader.ReadU8()),
                BlendSource = reader.ReadU8(),
                BlendConstant = reader.ReadF32(),
                Mode = reader.ReadU8()
            };

            reader.ReadF32(m);
            layer.Transform = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            reader.ReadF32(m); // the wrap transform, for generated texture coordinates only
            layer.Repeat = reader.ReadU8();
            layers[i] = (texture, layer);
        }

        SceneMaterialFlags flags = SceneMaterialFlags.None;
        if ((attributes & ShaderLightingEnabled) != 0)
        {
            flags |= SceneMaterialFlags.Lit;
        }

        if ((attributes & ShaderUseVertexColor) != 0)
        {
            flags |= SceneMaterialFlags.UseVertexColor;
        }

        // A scene's alpha test keeps what is more opaque than the reference. A test that always passes is
        // no test; the other comparisons have no counterpart.
        bool unsupportedAlphaTest = false;
        if ((attributes & ShaderAlphaTestEnabled) != 0)
        {
            if (alphaFunction is AlphaGreater or AlphaGreaterOrEqual)
            {
                flags |= SceneMaterialFlags.AlphaTest;
            }
            else
            {
                unsupportedAlphaTest = alphaFunction != AlphaAlways;
            }
        }

        if (!_shaders.ContainsKey(name))
        {
            _shaderOrder.Add(name);
        }

        _shaders[name] = new ShaderDeclaration
        {
            Name = name,
            Material = material,
            Flags = flags,
            AlphaReference = alphaReference,
            UnsupportedAlphaTest = unsupportedAlphaTest,
            Layers = layers
        };
    }

    /// <summary>
    /// The blend function of a texture layer. ECMA-363 9.8.3.10.3 numbers them multiply 0, add 1,
    /// replace 2, blend 3, but the reference implementation — which wrote the sample files, and whose
    /// numbering files in circulation therefore follow — writes replace 0, add 1, multiply 2, blend 3.
    /// </summary>
    private static SceneBlendFunction BlendFunctionOf(byte code) => code switch
    {
        0 => SceneBlendFunction.Replace,
        1 => SceneBlendFunction.Add,
        3 => SceneBlendFunction.Blend,
        _ => SceneBlendFunction.Multiply
    };

    /// <summary>
    /// ECMA-363 9.7.5. <paramref name="chainType"/> is the type of the modifier chain the block sits in:
    /// the modifier's name is a node's or a model resource's accordingly.
    /// </summary>
    private void ReadShadingModifier(U3DBlock block, int chainType)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        reader.ReadU32(); // chain index
        uint attributes = reader.ReadU32();

        uint listCount = reader.ReadU32();
        if (listCount > (uint)(reader.RemainingBytes / 4))
        {
            throw Truncated($"U3D shading modifier '{name}' declares {listCount} shader lists, more than its block can hold.");
        }

        var firstShaders = new string?[listCount];
        int multiPass = 0;
        for (int i = 0; i < firstShaders.Length; i++)
        {
            uint shaderCount = reader.ReadU32();
            if (shaderCount > (uint)(reader.RemainingBytes / 2))
            {
                throw Truncated($"U3D shading modifier '{name}' declares {shaderCount} shaders in a list, more than its block can hold.");
            }

            if (shaderCount > 0)
            {
                firstShaders[i] = ReadName(ref reader);
                for (uint extra = 1; extra < shaderCount; extra++)
                {
                    reader.SkipString();
                }
            }

            if (shaderCount > 1)
            {
                multiPass++;
            }
        }

        // The modifier replaces the shading of the kinds of geometry its attributes name.
        if (chainType == ChainTypeTexture)
        {
            return;
        }

        var declaration = new ShadingDeclaration { FirstShaders = firstShaders, MultiPassLists = multiPass };
        for (int kind = 0; kind < ShadingKinds; kind++)
        {
            if ((attributes & (1u << kind)) != 0)
            {
                (chainType == ChainTypeModelResource ? _resourceShading : _nodeShading)[(name, kind)] = declaration;
            }
        }
    }

    /// <summary>
    /// Emits the scene's materials: the default shader first, so that index 0 is what anything unbound
    /// uses, then one material per declared shader.
    /// </summary>
    private void BuildMaterials(SceneBuilder builder)
    {
        SceneMaterial fallback = DefaultMaterialResource;
        fallback.Flags = SceneMaterialFlags.Lit;
        fallback.Name = builder.AddName("");
        builder.AddMaterial(fallback);

        foreach (string shaderName in _shaderOrder)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ShaderDeclaration shader = _shaders[shaderName];

            if (!_materialResources.TryGetValue(shader.Material, out SceneMaterial material))
            {
                material = DefaultMaterialResource;
                if (shader.Material.Length != 0)
                {
                    builder.Report(SceneDiagnosticSeverity.Warning, "material-missing", $"U3D shader '{shader.Name}' names a material '{shader.Material}' that does not exist; the default material is used.");
                }
            }

            if (shader.UnsupportedAlphaTest)
            {
                builder.Report(SceneDiagnosticSeverity.Info, "alpha-test-unsupported", $"U3D shader '{shader.Name}' uses an alpha test comparison that is not carried over.");
            }

            material.Flags = shader.Flags;
            material.AlphaReference = shader.AlphaReference;
            material.Name = builder.AddName(shader.Name);
            material.LayerCount = shader.Layers.Length;
            material.FirstLayer = 0;

            for (int i = 0; i < shader.Layers.Length; i++)
            {
                (string textureName, SceneTextureLayer layer) = shader.Layers[i];
                if (_textureIndices.TryGetValue(textureName, out int texture))
                {
                    layer.Texture = texture;
                }
                else if (textureName.Length != 0)
                {
                    builder.Report(SceneDiagnosticSeverity.Warning, "texture-missing", $"U3D shader '{shader.Name}' names a texture '{textureName}' that does not exist.");
                }

                int layerIndex = builder.AddTextureLayer(layer);
                if (i == 0)
                {
                    material.FirstLayer = layerIndex;
                }
            }

            _materialIndices[shader.Name] = builder.AddMaterial(material);
        }
    }

    /// <summary>
    /// The material bindings of a model node: its own shading modifier's, else those of the shading
    /// modifier on the model resource it uses, else none (every part then uses material 0).
    /// </summary>
    private (int FirstBinding, int Count) GetBindings(SceneBuilder builder, NodeDeclaration node)
    {
        // 0 a mesh, 1 a line set, 2 a point set.
        int kind = _sets.TryGetValue(node.Resource, out SetDeclaration? set) ? (set.Lines ? 1 : 2) : 0;
        if (!_nodeShading.TryGetValue((node.Name, kind), out ShadingDeclaration? shading) && !_resourceShading.TryGetValue((node.Resource, kind), out shading))
        {
            return (0, 0);
        }

        // Every instance of the node, and every node sharing a resource's shading, reads the same entries.
        if (shading.FirstBinding < 0)
        {
            shading.FirstBinding = 0;
            for (int i = 0; i < shading.FirstShaders.Length; i++)
            {
                // A list without shaders uses the default shader; so does the empty name, unless the file
                // declares a shader under it.
                int material = 0;
                string? shader = shading.FirstShaders[i];
                if (shader is not null && !_materialIndices.TryGetValue(shader, out material))
                {
                    material = 0;
                    if (shader.Length != 0)
                    {
                        builder.Report(SceneDiagnosticSeverity.Warning, "shader-missing", $"U3D node or resource '{node.Name}' names a shader '{shader}' that does not exist; the default shader is used.");
                    }
                }

                int position = builder.AddBinding(material);
                if (i == 0)
                {
                    shading.FirstBinding = position;
                }
            }

            for (int i = 0; i < shading.MultiPassLists; i++)
            {
                builder.Report(SceneDiagnosticSeverity.Info, "multi-pass-shading", $"A shader list of U3D node or resource '{node.Name}' names several shaders; only the first is used.");
            }
        }

        return (shading.FirstBinding, shading.FirstShaders.Length);
    }
}
