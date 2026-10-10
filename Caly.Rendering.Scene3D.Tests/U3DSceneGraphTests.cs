using System.Globalization;
using System.Numerics;
using Caly.Rendering.Scene3D.Tests.Idtf;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

public class U3DSceneGraphTests
{
    internal static Scene Read(string name) => U3DReader.Read(TestFiles.Scene(name + ".u3d"));

    internal static Matrix4x4 Matrix(IdtfElement parent)
    {
        List<double> n = parent.Get("PARENT_TM").Numbers;
        Assert.Equal(16, n.Count);
        return new Matrix4x4(
            (float)n[0], (float)n[1], (float)n[2], (float)n[3], (float)n[4], (float)n[5], (float)n[6], (float)n[7],
            (float)n[8], (float)n[9], (float)n[10], (float)n[11], (float)n[12], (float)n[13], (float)n[14], (float)n[15]);
    }

    private static string ParentName(IdtfElement parent)
    {
        string name = parent.Text("PARENT_NAME");
        return name == "<NULL>" ? "" : name;
    }

    private static SceneNodeKind Kind(string idtfType) => idtfType switch
    {
        "GROUP" => SceneNodeKind.Group,
        "MODEL" => SceneNodeKind.Model,
        "LIGHT" => SceneNodeKind.Light,
        "VIEW" => SceneNodeKind.Camera,
        _ => throw new ArgumentException(idtfType)
    };

    /// <summary>
    /// Every IDTF node appears in the scene once per path from the world, with the kind, parent and matrix
    /// the IDTF source gives it.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void NodesMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = Read(name);

        Dictionary<string, IdtfElement> idtfNodes = idtf.Nodes.ToDictionary(n => n.Text("NODE_NAME"));

        // How many times each node is instanced: once per parent instance; the world counts once.
        var instances = new Dictionary<string, int> { [""] = 1 };
        int Instances(string node)
        {
            if (instances.TryGetValue(node, out int known))
            {
                return known;
            }

            int count = idtfNodes.TryGetValue(node, out IdtfElement? element)
                ? element.Get("PARENT_LIST").All("PARENT").Sum(p => Instances(ParentName(p)))
                : 0;
            instances[node] = count;
            return count;
        }

        Dictionary<string, int> actualCounts = [];
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            SceneNode node = scene.Nodes[i];
            string nodeName = scene.GetName(node.Name);
            actualCounts[nodeName] = actualCounts.GetValueOrDefault(nodeName) + 1;

            Assert.True(idtfNodes.TryGetValue(nodeName, out IdtfElement? element), $"{name}: scene node '{nodeName}' is not in the IDTF source");
            Assert.Equal(Kind(element.Argument), node.Kind);
            Assert.True(node.Parent < i, "parents come before children");

            string parentName = node.Parent < 0 ? "" : scene.GetName(scene.Nodes[node.Parent].Name);
            bool matched = element.Get("PARENT_LIST").All("PARENT").Any(p => ParentName(p) == parentName && Matrix(p) == node.Local);
            Assert.True(matched, $"{name}: node '{nodeName}' under '{parentName}' has a transform the IDTF source does not list");
        }

        foreach (string nodeName in idtfNodes.Keys)
        {
            Assert.Equal(Instances(nodeName), actualCounts.GetValueOrDefault(nodeName));
        }
    }

    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void LightsMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = Read(name);
        Dictionary<string, IdtfElement> resources = idtf.Resources("LIGHT").ToDictionary(r => r.Text("RESOURCE_NAME"));

        int checkedLights = 0;
        foreach (IdtfElement lightNode in idtf.Nodes.Where(n => n.Argument == "LIGHT"))
        {
            IdtfElement resource = resources[lightNode.Text("RESOURCE_NAME")];
            foreach (SceneNode node in scene.Nodes.ToArray().Where(n => scene.GetName(n.Name) == lightNode.Text("NODE_NAME")))
            {
                Assert.True(node.Light >= 0);
                SceneLight light = scene.Lights[node.Light];
                SceneLightKind expectedKind = resource.Text("LIGHT_TYPE") switch
                {
                    "AMBIENT" => SceneLightKind.Ambient,
                    "DIRECTIONAL" => SceneLightKind.Directional,
                    "POINT" => SceneLightKind.Point,
                    "SPOT" => SceneLightKind.Spot,
                    string other => throw new ArgumentException(other)
                };

                Assert.Equal(expectedKind, light.Kind);
                Assert.Equal(resource.Vector("LIGHT_COLOR"), light.Color);
                Assert.Equal(resource.Vector("LIGHT_ATTENUATION"), new Vector3(light.AttenuationConstant, light.AttenuationLinear, light.AttenuationQuadratic));

                // The sample converter did not write LIGHT_INTENSITY: sphere_shading.idtf asks for 2.0 and the
                // light resource block of sphere_shading.u3d holds 1.0 (checked in the raw bytes). Every sample
                // file therefore has intensity 1.
                Assert.Equal(1f, light.Intensity);
                Assert.True(light.Enabled);
                checkedLights++;
            }
        }

        Assert.Equal(idtf.Nodes.Count(n => n.Argument == "LIGHT") > 0, checkedLights > 0);
    }

    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void CamerasMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = Read(name);

        foreach (IdtfElement viewNode in idtf.Nodes.Where(n => n.Argument == "VIEW"))
        {
            IdtfElement data = viewNode.Get("VIEW_DATA");
            SceneNode[] nodes = scene.Nodes.ToArray().Where(n => scene.GetName(n.Name) == viewNode.Text("NODE_NAME")).ToArray();
            Assert.NotEmpty(nodes);

            foreach (SceneNode node in nodes)
            {
                Assert.True(node.Camera >= 0);
                SceneCamera camera = scene.Cameras[node.Camera];
                float projection = (float)data.Number("VIEW_PROJECTION");
                if (data.Text("VIEW_TYPE") == "ORTHO")
                {
                    Assert.Equal(SceneProjection.Orthographic, camera.Projection);
                    Assert.Equal(projection, camera.OrthographicHeight);
                }
                else
                {
                    Assert.Equal(SceneProjection.Perspective, camera.Projection);
                    Assert.Equal(projection, camera.FieldOfView);
                }

                if (data.NumberOrNull("VIEW_NEAR_CLIP") is double near)
                {
                    Assert.Equal((float)near, camera.Near);
                    Assert.Equal((float)data.Number("VIEW_FAR_CLIP"), camera.Far);
                }
            }
        }
    }

    [Fact]
    public void ANodeWithSeveralParentsIsInstancedOncePerPath()
    {
        Matrix4x4 left = Matrix4x4.CreateTranslation(-5, 0, 0), right = Matrix4x4.CreateTranslation(5, 0, 0);
        byte[] file = new TestU3D()
            .GroupNode("A", ("", Matrix4x4.Identity))
            .GroupNode("B", ("", Matrix4x4.CreateTranslation(0, 10, 0)))
            .GroupNode("Shared", ("A", left), ("B", right))
            .GroupNode("Leaf", ("Shared", Matrix4x4.CreateScale(2)))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        string[] names = scene.Nodes.ToArray().Select(n => scene.GetName(n.Name)).ToArray();
        Assert.Equal(2, names.Count(n => n == "Shared"));
        Assert.Equal(2, names.Count(n => n == "Leaf"));
        Assert.Equal(6, names.Length);

        var worlds = new List<Matrix4x4>();
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            if (names[i] == "Leaf")
            {
                worlds.Add(scene.GetWorldMatrix(i));
            }
        }

        Assert.Contains(Matrix4x4.CreateScale(2) * left, worlds);
        Assert.Contains(Matrix4x4.CreateScale(2) * right * Matrix4x4.CreateTranslation(0, 10, 0), worlds);
    }

    [Fact]
    public void ALoopInTheNodeGraphIsRejected()
    {
        byte[] file = new TestU3D()
            .GroupNode("Root", ("", Matrix4x4.Identity))
            .GroupNode("A", ("B", Matrix4x4.Identity))
            .GroupNode("B", ("A", Matrix4x4.Identity))
            .ToArray();

        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
        Assert.Null(e.InnerException);

        byte[] selfLoop = new TestU3D().GroupNode("A", ("A", Matrix4x4.Identity)).ToArray();
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => U3DReader.Read(selfLoop, cancellationToken: TestContext.Current.CancellationToken)).Error);
    }

    /// <summary>
    /// A writer of circuit board exports gives none of its nodes a parent (9 files of the second sample
    /// set): read to the letter there is no scene at all. When nothing hangs on the world, the nodes
    /// without a parent are its children.
    /// </summary>
    [Fact]
    public void WhenNothingHangsOnTheWorldNodesWithoutAParentAreItsChildren()
    {
        byte[] file = new TestU3D()
            .GroupNode("Board")
            .GroupNode("Part", ("Board", Matrix4x4.CreateTranslation(1, 2, 3)))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Board", "Part"], scene.Nodes.ToArray().Select(n => scene.GetName(n.Name)).ToArray());
        Assert.Equal((-1, Matrix4x4.Identity), (scene.Nodes[0].Parent, scene.Nodes[0].Local));
        Assert.Equal(0, scene.Nodes[1].Parent);
        Assert.Contains(scene.Diagnostics, d => d.Code == "node-roots-assumed");
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "node-not-attached");
    }

    /// <summary>
    /// Another writer hangs its one root on a node it never declares ("PDF3D Scene" under
    /// "PDF3D Scene_sceneName": 30 files of the second sample set). When nothing hangs on the world, a node
    /// whose parent does not exist is a child of the world too, placed as it says.
    /// </summary>
    [Fact]
    public void WhenNothingHangsOnTheWorldANodeUnderAMissingParentIsItsChild()
    {
        byte[] file = new TestU3D()
            .GroupNode("Scene", ("Scene_sceneName", Matrix4x4.CreateTranslation(4, 5, 6)))
            .GroupNode("Part", ("Scene", Matrix4x4.Identity))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Scene", "Part"], scene.Nodes.ToArray().Select(n => scene.GetName(n.Name)).ToArray());
        Assert.Equal((-1, Matrix4x4.CreateTranslation(4, 5, 6)), (scene.Nodes[0].Parent, scene.Nodes[0].Local));
        Assert.Contains(scene.Diagnostics, d => d.Code == "node-roots-assumed");
    }

    [Fact]
    public void UnattachedAndDanglingNodes()
    {
        byte[] file = new TestU3D()
            .GroupNode("Attached", ("", Matrix4x4.Identity))
            .GroupNode("NoParents")
            .GroupNode("Orphan", ("DoesNotExist", Matrix4x4.Identity))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        string[] names = scene.Nodes.ToArray().Select(n => scene.GetName(n.Name)).ToArray();
        Assert.Contains("Attached", names);
        Assert.DoesNotContain("NoParents", names);
        Assert.DoesNotContain("Orphan", names);
        Assert.Contains(scene.Diagnostics, d => d.Code == "node-not-attached");
        Assert.Contains(scene.Diagnostics, d => d.Code == "node-parent-missing");
    }

    [Fact]
    public void ALaterNodeOfTheSameNameReplacesTheEarlierOne()
    {
        byte[] file = new TestU3D()
            .GroupNode("A", ("", Matrix4x4.CreateTranslation(1, 0, 0)))
            .GroupNode("B", ("", Matrix4x4.Identity))
            .GroupNode("A", ("B", Matrix4x4.CreateTranslation(2, 0, 0)))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, scene.Nodes.Length);
        SceneNode a = scene.Nodes.ToArray().Single(n => scene.GetName(n.Name) == "A");
        Assert.Equal("B", scene.GetName(scene.Nodes[a.Parent].Name));
        Assert.Equal(Matrix4x4.CreateTranslation(2, 0, 0), a.Local);
        Assert.Contains(scene.Diagnostics, d => d.Code == "duplicate-node");
    }

    [Fact]
    public void ADeepNodeChainIsBoundedByTheNestingLimit()
    {
        var file = new TestU3D();
        string parent = "";
        for (int i = 0; i < 40; i++)
        {
            string node = "n" + i.ToString(CultureInfo.InvariantCulture);
            file.GroupNode(node, (parent, Matrix4x4.Identity));
            parent = node;
        }

        byte[] bytes = file.ToArray();

        using (Scene scene = U3DReader.Read(bytes, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(40, scene.Nodes.Length);
        }

        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(bytes, SceneLimits.Default with { MaxNestingDepth = 10 }, TestContext.Current.CancellationToken));
        Assert.Equal(Scene3DError.LimitExceeded, e.Error);
    }

    [Fact]
    public void TooManyNodeInstancesHitTheNodeLimit()
    {
        // Each level doubles the instances of the next: 2^12 leaves from 24 declared nodes.
        var file = new TestU3D();
        file.GroupNode("L0a", ("", Matrix4x4.Identity)).GroupNode("L0b", ("", Matrix4x4.Identity));
        for (int level = 1; level < 12; level++)
        {
            foreach (string suffix in new[] { "a", "b" })
            {
                file.GroupNode($"L{level}{suffix}", ($"L{level - 1}a", Matrix4x4.Identity), ($"L{level - 1}b", Matrix4x4.Identity));
            }
        }

        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(file.ToArray(), SceneLimits.Default with { MaxNodes = 1000 }, TestContext.Current.CancellationToken));
        Assert.Equal(Scene3DError.LimitExceeded, e.Error);
    }

    /// <summary>
    /// One NaN in one node matrix would turn the bounds of the whole scene, and so the camera fitted to
    /// them, into NaN. Such a transform is replaced by the identity and reported.
    /// </summary>
    [Fact]
    public void ANonFiniteNodeTransformIsReplacedByTheIdentity()
    {
        Matrix4x4 broken = Matrix4x4.CreateTranslation(1, 2, 3);
        broken.M22 = float.NaN;
        Matrix4x4 infinite = Matrix4x4.Identity;
        infinite.M41 = float.PositiveInfinity;

        byte[] file = new TestU3D()
            .GroupNode("Fine", ("", Matrix4x4.CreateTranslation(4, 5, 6)))
            .GroupNode("Broken", ("", broken), ("Fine", infinite))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, scene.Nodes.Length);
        Assert.Equal(Matrix4x4.CreateTranslation(4, 5, 6), scene.Nodes[0].Local);
        Assert.Equal(2, scene.Nodes.ToArray().Count(n => scene.GetName(n.Name) == "Broken" && n.Local.IsIdentity));
        SceneDiagnostic diagnostic = Assert.Single(scene.Diagnostics, d => d.Code == "non-finite-transform");
        Assert.Equal(SceneDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(2, diagnostic.Count);
    }
}
