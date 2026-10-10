using System.Numerics;

namespace Caly.Rendering.Scene3D.Tests;

public class SceneViewTests
{
    /// <summary>
    /// World
    ///   A (group)
    ///     Cam (camera)
    ///     B (group)
    ///       Cam (camera)
    ///   Cam (group, not a camera)
    /// </summary>
    private static Scene Tree()
    {
        using var builder = new SceneBuilder();
        int Add(string name, int parent, SceneNodeKind kind)
        {
            int camera = kind == SceneNodeKind.Camera ? builder.AddCamera(new SceneCamera { FieldOfView = 30, Near = 1, Far = 10 }) : -1;
            return builder.AddNode(new SceneNode { Parent = parent, Local = Matrix4x4.Identity, Kind = kind, Mesh = -1, Light = -1, Camera = camera, Name = builder.AddName(name), Visible = true });
        }

        int a = Add("A", -1, SceneNodeKind.Group);
        Add("Cam", a, SceneNodeKind.Camera);
        int b = Add("B", a, SceneNodeKind.Group);
        Add("Cam", b, SceneNodeKind.Camera);
        Add("Cam", -1, SceneNodeKind.Group);
        return builder.Build();
    }

    [Fact]
    public void ASingleNameFindsTheFirstNodeOfThatNameAnywhere()
    {
        using Scene scene = Tree();
        Assert.Equal(1, scene.FindNode(["Cam"]));
        Assert.Equal(2, scene.FindNode(["B"]));
        Assert.Equal(0, scene.FindNode(["A"]));
    }

    [Fact]
    public void AKindNarrowsTheSearch()
    {
        using Scene scene = Tree();
        Assert.Equal(1, scene.FindNode(["Cam"], SceneNodeKind.Camera));
        Assert.Equal(4, scene.FindNode(["Cam"], SceneNodeKind.Group));
        Assert.Equal(-1, scene.FindNode(["Cam"], SceneNodeKind.Light));
    }

    [Fact]
    public void APathIsFollowedFromTheRoot()
    {
        using Scene scene = Tree();
        Assert.Equal(3, scene.FindNode(["A", "B", "Cam"]));
        Assert.Equal(1, scene.FindNode(["A", "Cam"]));
        Assert.Equal(4, scene.FindNode(["Cam"], SceneNodeKind.Group));

        // A path names every step: "B" is not a root, and "A" has no child "X".
        Assert.Equal(-1, scene.FindNode(["B", "Cam"]));
        Assert.Equal(-1, scene.FindNode(["A", "X"]));
        Assert.Equal(-1, scene.FindNode(["A", "B", "Cam"], SceneNodeKind.Group));
    }

    [Fact]
    public void NothingIsFoundForAMissingNameOrAnEmptyPath()
    {
        using Scene scene = Tree();
        Assert.Equal(-1, scene.FindNode(["Nope"]));
        Assert.Equal(-1, scene.FindNode([]));

        using var builder = new SceneBuilder();
        using Scene empty = builder.Build();
        Assert.Equal(-1, empty.FindNode(["A"]));
    }

    [Fact]
    public void AViewDefaultsToTheScenesOwnSettings()
    {
        var view = new SceneView();

        Assert.Equal("", view.Name);
        Assert.Null(view.CameraToWorld);
        Assert.Empty(view.CameraNodePath);
        Assert.Null(view.OrbitDistance);
        Assert.Null(view.Projection);
        Assert.Null(view.Background);
        Assert.Equal(SceneLighting.Artwork, view.Lighting);
        Assert.Empty(view.NodeOverrides);
    }

    [Fact]
    public void AViewCarriesWhatItIsGiven()
    {
        var view = new SceneView
        {
            Name = "Front",
            CameraToWorld = Matrix4x4.CreateTranslation(1, 2, 3),
            CameraNodePath = ["A", "Cam"],
            OrbitDistance = 4,
            Projection = new SceneViewProjection(SceneProjection.Orthographic, 0, SceneViewBinding.Height, 2.5f, 0.1f, null),
            Background = new Vector3(1, 0.5f, 0),
            Lighting = SceneLighting.Cad,
            NodeOverrides = [new SceneNodeOverride("Part", false, 0.5f, null)]
        };

        Assert.Equal("Front", view.Name);
        Assert.Equal(new Vector3(1, 2, 3), view.CameraToWorld!.Value.Translation);
        Assert.Equal(["A", "Cam"], view.CameraNodePath);
        Assert.Equal(SceneViewBinding.Height, view.Projection!.Value.Binding);
        Assert.Equal(2.5f, view.Projection.Value.OrthographicScale);
        Assert.Null(view.Projection.Value.Far);
        Assert.Equal("Part", view.NodeOverrides[0].Name);
        Assert.False(view.NodeOverrides[0].Visible);
    }
}
