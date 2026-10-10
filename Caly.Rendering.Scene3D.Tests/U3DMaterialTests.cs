using System.Numerics;
using Caly.Rendering.Scene3D.Tests.Idtf;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

public class U3DMaterialTests
{
    private static readonly Vector3 Grey = new(0.75f);

    private static void AssertClose(Vector3 expected, Vector3 actual, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-6f, $"{what}: expected {expected}, got {actual}");
    }

    private static int MaterialNamed(Scene scene, string name)
    {
        int found = -1;
        for (int i = 1; i < scene.Materials.Length; i++)
        {
            if (scene.GetName(scene.Materials[i].Name) == name)
            {
                Assert.True(found < 0, $"material '{name}' appears twice");
                found = i;
            }
        }

        return found;
    }

    private static void AssertIsDefaultMaterial(SceneMaterial material)
    {
        Assert.Equal(Grey, material.Ambient);
        Assert.Equal(Vector3.Zero, material.Diffuse);
        Assert.Equal(Vector3.Zero, material.Specular);
        Assert.Equal(Vector3.Zero, material.Emissive);
        Assert.Equal(1f, material.Opacity);
        Assert.Equal(0f, material.Reflectivity);
        Assert.Equal(SceneMaterialFlags.Lit, material.Flags);
        Assert.Equal(0, material.LayerCount);
    }

    /// <summary>Material 0 is always the format's default shader (ECMA-363 8.4.2, 8.4.3).</summary>
    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void MaterialZeroIsTheDefault(string name)
    {
        using Scene scene = U3DSceneGraphTests.Read(name);
        Assert.True(scene.Materials.Length >= 1);
        AssertIsDefaultMaterial(scene.Materials[0]);
        Assert.Equal("", scene.GetName(scene.Materials[0].Name));
    }

    /// <summary>Every IDTF shader is a scene material carrying the colours of the material resource it names.</summary>
    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void ShadersMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = U3DSceneGraphTests.Read(name);
        Dictionary<string, IdtfElement> materials = idtf.Resources("MATERIAL").ToDictionary(r => r.Text("RESOURCE_NAME"));
        HashSet<string> textures = idtf.Resources("TEXTURE").Select(r => r.Text("RESOURCE_NAME")).ToHashSet();

        foreach (IdtfElement shader in idtf.Resources("SHADER"))
        {
            string shaderName = shader.Text("RESOURCE_NAME");
            int index = MaterialNamed(scene, shaderName);
            Assert.True(index > 0, $"{name}: shader '{shaderName}' is not a scene material");
            SceneMaterial material = scene.Materials[index];

            Assert.True((material.Flags & SceneMaterialFlags.Lit) != 0);
            Assert.Equal(shader.TextOrNull("ATTRIBUTE_USE_VERTEX_COLOR") == "TRUE", (material.Flags & SceneMaterialFlags.UseVertexColor) != 0);

            if (materials.TryGetValue(shader.Text("SHADER_MATERIAL_NAME"), out IdtfElement? resource))
            {
                AssertClose(resource.Vector("MATERIAL_AMBIENT"), material.Ambient, $"{name}/{shaderName} ambient");
                AssertClose(resource.Vector("MATERIAL_DIFFUSE"), material.Diffuse, $"{name}/{shaderName} diffuse");
                AssertClose(resource.Vector("MATERIAL_SPECULAR"), material.Specular, $"{name}/{shaderName} specular");
                AssertClose(resource.Vector("MATERIAL_EMISSIVE"), material.Emissive, $"{name}/{shaderName} emissive");
                Assert.Equal((float)resource.Number("MATERIAL_REFLECTIVITY"), material.Reflectivity, 1e-6f);
                Assert.Equal((float)resource.Number("MATERIAL_OPACITY"), material.Opacity, 1e-6f);
            }
            else
            {
                AssertClose(Grey, material.Ambient, $"{name}/{shaderName} default ambient");
            }

            IdtfElement[] layers = shader.Find("SHADER_TEXTURE_LAYER_LIST")?.All("TEXTURE_LAYER").OrderBy(l => int.Parse(l.Argument)).ToArray() ?? [];
            Assert.Equal((int)shader.Number("SHADER_ACTIVE_TEXTURE_COUNT"), layers.Length);
            Assert.Equal(layers.Length, material.LayerCount);

            for (int i = 0; i < layers.Length; i++)
            {
                SceneTextureLayer layer = scene.TextureLayers[material.FirstLayer + i];
                string textureName = layers[i].Text("TEXTURE_NAME");
                if (textures.Contains(textureName))
                {
                    Assert.True(layer.Texture >= 0, $"{name}/{shaderName}: layer {i} lost its texture '{textureName}'");
                    Assert.Equal(textureName, scene.Textures[layer.Texture].Name);
                }
                else
                {
                    Assert.Equal(-1, layer.Texture);
                }

                if (layers[i].NumberOrNull("TEXTURE_LAYER_INTENSITY") is double intensity)
                {
                    Assert.Equal((float)intensity, layer.Intensity);
                }

                if (layers[i].TextOrNull("TEXTURE_LAYER_BLEND_FUNCTION") is string function)
                {
                    Assert.Equal((SceneBlendFunction)Array.IndexOf(["MULTIPLY", "ADD", "REPLACE", "BLEND"], function), layer.BlendFunction);
                }

                if (layers[i].TextOrNull("TEXTURE_LAYER_BLEND_SOURCE") is string source)
                {
                    Assert.Equal(Array.IndexOf(["ALPHA", "CONSTANT"], source), layer.BlendSource);
                }

                if (layers[i].NumberOrNull("TEXTURE_LAYER_BLEND_CONSTANT") is double constant)
                {
                    Assert.Equal((float)constant, layer.BlendConstant);
                }

                if (layers[i].TextOrNull("TEXTURE_LAYER_MODE") is string mode)
                {
                    Assert.Equal(Array.IndexOf(["TM_NONE", "TM_PLANAR", "TM_CYLINDRICAL", "TM_SPHERICAL", "TM_REFLECTION"], mode), layer.Mode);
                }
            }
        }
    }

    /// <summary>
    /// A shading modifier gives the node it is attached to one material per shader list; a modifier on a
    /// model resource serves the nodes that use the resource and have none of their own.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void ShadingModifiersMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = U3DSceneGraphTests.Read(name);
        HashSet<string> shaders = idtf.Resources("SHADER").Select(r => r.Text("RESOURCE_NAME")).ToHashSet();

        // The samples spell the list of shader lists two ways.
        static IdtfElement ShaderLists(IdtfElement modifier) => modifier.Get("PARAMETERS").Find("SHADING_GROUP") ?? modifier.Get("PARAMETERS").Get("SHADER_LIST_LIST");

        static bool IsResourceChain(IdtfElement modifier) => modifier.TextOrNull("MODIFIER_CHAIN_TYPE") == "MODEL";

        // The last shading modifier of a chain wins.
        var nodeModifiers = new Dictionary<string, IdtfElement>();
        var resourceModifiers = new Dictionary<string, IdtfElement>();
        foreach (IdtfElement modifier in idtf.Modifiers("SHADING"))
        {
            (IsResourceChain(modifier) ? resourceModifiers : nodeModifiers)[modifier.Text("MODIFIER_NAME")] = modifier;
        }

        int checkedNodes = 0;
        foreach (IdtfElement idtfNode in idtf.Nodes.Where(n => n.Argument == "MODEL"))
        {
            string nodeName = idtfNode.Text("NODE_NAME");
            if (!nodeModifiers.TryGetValue(nodeName, out IdtfElement? modifier))
            {
                resourceModifiers.TryGetValue(idtfNode.Text("RESOURCE_NAME"), out modifier);
            }

            foreach (SceneNode node in scene.Nodes.ToArray().Where(n => scene.GetName(n.Name) == nodeName))
            {
                Assert.Equal(SceneNodeKind.Model, node.Kind);
                if (modifier is null)
                {
                    Assert.Equal(0, node.BindingCount);
                    continue;
                }

                IdtfElement[] lists = ShaderLists(modifier).All("SHADER_LIST").OrderBy(l => int.Parse(l.Argument)).ToArray();
                Assert.Equal((int)modifier.Get("PARAMETERS").Number("SHADER_LIST_COUNT"), lists.Length);
                Assert.Equal(lists.Length, node.BindingCount);

                for (int i = 0; i < lists.Length; i++)
                {
                    string? first = lists[i].Get("SHADER_NAME_LIST").All("NAME:").Select(s => s.Argument).FirstOrDefault();
                    int material = scene.Bindings[node.FirstBinding + i];
                    if (first is not null && shaders.Contains(first))
                    {
                        Assert.True(material > 0, $"{name}: node '{nodeName}' list {i} should use shader '{first}'");
                        Assert.Equal(first, scene.GetName(scene.Materials[material].Name));
                    }
                    else
                    {
                        Assert.Equal(0, material);
                    }
                }

                checkedNodes++;
            }
        }

        Assert.Equal(nodeModifiers.Keys.Concat(resourceModifiers.Keys).Any(), checkedNodes > 0 || !idtf.Nodes.Any(n => n.Argument == "MODEL"));
    }

    private static TestU3D TwoShaders()
    {
        return new TestU3D()
            .Material("Red", new Vector3(0.1f), new Vector3(1, 0, 0), new Vector3(0.2f), new Vector3(0.3f), 0.4f, 0.5f)
            .Shader("RedShader", "Red")
            .Shader("Orphan", "NoSuchMaterial");
    }

    [Fact]
    public void AShaderCarriesItsMaterialAndFlags()
    {
        byte[] file = TwoShaders()
            .Shader("Cutout", "Red", TestU3D.Lit | TestU3D.AlphaTest | TestU3D.UseVertexColor, alphaReference: 0.25f, alphaFunction: 0x612)
            .Shader("Unlit", "Red", attributes: 0)
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneMaterial red = scene.Materials[MaterialNamed(scene, "RedShader")];
        Assert.Equal(new Vector3(0.1f), red.Ambient);
        Assert.Equal(new Vector3(1, 0, 0), red.Diffuse);
        Assert.Equal(new Vector3(0.2f), red.Specular);
        Assert.Equal(new Vector3(0.3f), red.Emissive);
        Assert.Equal(0.4f, red.Reflectivity);
        Assert.Equal(0.5f, red.Opacity);
        Assert.Equal(SceneMaterialFlags.Lit, red.Flags);

        SceneMaterial cutout = scene.Materials[MaterialNamed(scene, "Cutout")];
        Assert.Equal(SceneMaterialFlags.Lit | SceneMaterialFlags.AlphaTest | SceneMaterialFlags.UseVertexColor, cutout.Flags);
        Assert.Equal(0.25f, cutout.AlphaReference);

        Assert.Equal(SceneMaterialFlags.None, scene.Materials[MaterialNamed(scene, "Unlit")].Flags);
    }

    /// <summary>An alpha test that always passes is no alpha test.</summary>
    [Fact]
    public void AnAlwaysPassingAlphaTestIsNotAnAlphaTest()
    {
        byte[] file = TwoShaders().Shader("Always", "Red", TestU3D.Lit | TestU3D.AlphaTest, alphaReference: 0.25f, alphaFunction: 0x617).ToArray();
        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SceneMaterialFlags.Lit, scene.Materials[MaterialNamed(scene, "Always")].Flags);
    }

    [Fact]
    public void AShaderNamingAMissingMaterialUsesTheDefaultMaterial()
    {
        using Scene scene = U3DReader.Read(TwoShaders().ToArray(), cancellationToken: TestContext.Current.CancellationToken);

        SceneMaterial orphan = scene.Materials[MaterialNamed(scene, "Orphan")];
        Assert.Equal(Grey, orphan.Ambient);
        Assert.Equal(Vector3.Zero, orphan.Diffuse);
        Assert.Equal(1f, orphan.Opacity);
        Assert.Contains(scene.Diagnostics, d => d.Code == "material-missing" && d.Severity == SceneDiagnosticSeverity.Warning);
    }

    [Fact]
    public void TextureLayersFollowTheShaderChannels()
    {
        Matrix4x4 transform = Matrix4x4.CreateScale(2, 3, 1) * Matrix4x4.CreateTranslation(0.5f, 0.25f, 0);
        byte[] file = TwoShaders()
            .TextureDeclaration("Wood", 4, 2, 0x0E, (2, 0x0E, 3, null))
            .TextureContinuation("Wood", 0, [1, 2, 3])
            .Shader("Textured", "Red", layers:
            [
                new TestU3D.Layer(5, "Missing") { Intensity = 0.5f },
                new TestU3D.Layer(1, "Wood") { BlendFunction = 0, BlendSource = 0, BlendConstant = 0.75f, Mode = 3, Repeat = 1, Transform = transform }
            ])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneMaterial material = scene.Materials[MaterialNamed(scene, "Textured")];
        Assert.Equal(2, material.LayerCount);

        SceneTextureLayer wood = scene.TextureLayers[material.FirstLayer];
        Assert.Equal("Wood", scene.Textures[wood.Texture].Name);
        Assert.Equal(1f, wood.Intensity);
        Assert.Equal(SceneBlendFunction.Replace, wood.BlendFunction);
        Assert.Equal(0, wood.BlendSource);
        Assert.Equal(0.75f, wood.BlendConstant);
        Assert.Equal(3, wood.Mode);
        Assert.Equal(1, wood.Repeat);
        Assert.Equal(transform, wood.Transform);

        SceneTextureLayer missing = scene.TextureLayers[material.FirstLayer + 1];
        Assert.Equal(-1, missing.Texture);
        Assert.Equal(0.5f, missing.Intensity);
        Assert.Contains(scene.Diagnostics, d => d.Code == "texture-missing");
    }

    /// <summary>
    /// The layer blend function is numbered as the reference implementation writes it (replace, add,
    /// multiply, blend), not as ECMA-363 9.8.3.10.3 lists it.
    /// </summary>
    [Theory]
    [InlineData(0, SceneBlendFunction.Replace)]
    [InlineData(1, SceneBlendFunction.Add)]
    [InlineData(2, SceneBlendFunction.Multiply)]
    [InlineData(3, SceneBlendFunction.Blend)]
    [InlineData(200, SceneBlendFunction.Multiply)]
    public void BlendFunctionsFollowTheReferenceNumbering(byte code, SceneBlendFunction expected)
    {
        byte[] file = TwoShaders().Shader("Textured", "Red", layers: [new TestU3D.Layer(0, "T") { BlendFunction = code }]).ToArray();
        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, scene.TextureLayers[scene.Materials[MaterialNamed(scene, "Textured")].FirstLayer].BlendFunction);
    }

    private static int[] Bindings(Scene scene, string nodeName)
    {
        SceneNode node = scene.Nodes.ToArray().Single(n => scene.GetName(n.Name) == nodeName);
        return scene.Bindings.Slice(node.FirstBinding, node.BindingCount).ToArray();
    }

    /// <summary>Review focus: short lists, empty lists and unknown shader names fall back to the default.</summary>
    [Fact]
    public void ShaderListsFallBackToTheDefaultMaterial()
    {
        byte[] file = TwoShaders()
            .ModifierChain("Box", TestU3D.NodeChain, chain => chain
                .ModelNode("Box", "BoxMesh", ("", Matrix4x4.Identity))
                .ShadingModifier("Box", 1, ["RedShader"], [], ["NoSuchShader"], ["Orphan", "RedShader"]))
            .ModelNode("Bare", "BoxMesh", ("", Matrix4x4.Identity))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        int red = MaterialNamed(scene, "RedShader"), orphan = MaterialNamed(scene, "Orphan");
        Assert.Equal([red, 0, 0, orphan], Bindings(scene, "Box"));
        Assert.Empty(Bindings(scene, "Bare"));

        Assert.Contains(scene.Diagnostics, d => d.Code == "shader-missing" && d.Severity == SceneDiagnosticSeverity.Warning);
        Assert.Contains(scene.Diagnostics, d => d.Code == "multi-pass-shading" && d.Severity == SceneDiagnosticSeverity.Info);
    }

    [Fact]
    public void AResourceShadingModifierServesNodesWithoutTheirOwn()
    {
        byte[] file = TwoShaders()
            .ModifierChain("BoxMesh", TestU3D.ModelResourceChain, chain => chain.ShadingModifier("BoxMesh", 1, ["Orphan"]))
            .ModifierChain("Own", TestU3D.NodeChain, chain => chain
                .ModelNode("Own", "BoxMesh", ("", Matrix4x4.Identity))
                .ShadingModifier("Own", 1, ["RedShader"], ["RedShader"]))
            .ModelNode("Inherits", "BoxMesh", ("", Matrix4x4.Identity))
            .ModelNode("Other", "OtherMesh", ("", Matrix4x4.Identity))
            // A node chain that happens to share the resource's name must not be taken for the resource's.
            .ModifierChain("OtherMesh", TestU3D.NodeChain, chain => chain
                .GroupNode("OtherMesh", ("", Matrix4x4.Identity))
                .ShadingModifier("OtherMesh", 1, ["RedShader"]))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        int red = MaterialNamed(scene, "RedShader"), orphan = MaterialNamed(scene, "Orphan");
        Assert.Equal([red, red], Bindings(scene, "Own"));
        Assert.Equal([orphan], Bindings(scene, "Inherits"));
        Assert.Empty(Bindings(scene, "Other"));
    }

    [Fact]
    public void AShadingModifierNotForMeshesIsIgnored()
    {
        byte[] file = TwoShaders()
            .ModifierChain("Box", TestU3D.NodeChain, chain => chain
                .ModelNode("Box", "BoxMesh", ("", Matrix4x4.Identity))
                .ShadingModifier("Box", 1, ["RedShader"])
                .ShadingModifier("Box", 2 | 4, ["Orphan"], ["Orphan"]))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([MaterialNamed(scene, "RedShader")], Bindings(scene, "Box"));
    }

    [Fact]
    public void InstancesOfANodeShareTheirBindings()
    {
        byte[] file = TwoShaders()
            .GroupNode("A", ("", Matrix4x4.Identity))
            .GroupNode("B", ("", Matrix4x4.Identity))
            .ModifierChain("Box", TestU3D.NodeChain, chain => chain
                .ModelNode("Box", "BoxMesh", ("A", Matrix4x4.Identity), ("B", Matrix4x4.Identity))
                .ShadingModifier("Box", 1, ["RedShader"], ["Orphan"]))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneNode[] boxes = scene.Nodes.ToArray().Where(n => scene.GetName(n.Name) == "Box").ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.Equal(boxes[0].FirstBinding, boxes[1].FirstBinding);
        Assert.Equal(2, boxes[1].BindingCount);
        Assert.Equal(2, scene.Bindings.Length);
    }

    [Fact]
    public void AbsurdShaderCountsAreRejectedBeforeAllocating()
    {
        byte[] lists = new TestU3D()
            .Block(0xFFFFFF45, new TestU3D.Data().String("Box").U32(1).U32(1).U32(0xFFFFFFF0))
            .ToArray();
        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => U3DReader.Read(lists, cancellationToken: TestContext.Current.CancellationToken)).Error);

        byte[] shaders = new TestU3D()
            .Block(0xFFFFFF45, new TestU3D.Data().String("Box").U32(1).U32(1).U32(1).U32(0xFFFFFFF0))
            .ToArray();
        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => U3DReader.Read(shaders, cancellationToken: TestContext.Current.CancellationToken)).Error);
    }

    [Fact]
    public void ALaterResourceOfTheSameNameReplacesTheEarlierOne()
    {
        byte[] file = new TestU3D()
            .Material("M", Vector3.One, Vector3.One, Vector3.One, Vector3.One, 1, 1)
            .Material("M", Vector3.Zero, new Vector3(0, 1, 0), Vector3.Zero, Vector3.Zero, 0, 1)
            .Shader("S", "NoSuchMaterial")
            .Shader("S", "M")
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, scene.Materials.Length);
        Assert.Equal(new Vector3(0, 1, 0), scene.Materials[MaterialNamed(scene, "S")].Diffuse);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "material-missing");
    }
}
