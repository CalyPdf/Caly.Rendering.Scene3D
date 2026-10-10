using System.Numerics;

namespace Caly.Rendering.Scene3D;

/// <summary>The lights a view asks for: the scene's own, or one of the fixed rigs 3D viewers offer.</summary>
public enum SceneLighting : byte
{
    /// <summary>The lights authored in the scene.</summary>
    Artwork = 0,

    /// <summary>No lights; the scene's own are ignored.</summary>
    None,

    /// <summary>Three blue-grey lights.</summary>
    White,

    /// <summary>Three light-grey lights.</summary>
    Day,

    /// <summary>One yellow, one aqua and one blue light.</summary>
    Night,

    /// <summary>Three grey lights.</summary>
    Hard,

    /// <summary>One red, one green and one blue light.</summary>
    Primary,

    /// <summary>Three blue lights.</summary>
    Blue,

    /// <summary>Three red lights.</summary>
    Red,

    /// <summary>Six grey lights along the axes.</summary>
    Cube,

    /// <summary>Three grey lights and one attached to the camera.</summary>
    Cad,

    /// <summary>One light attached to the camera.</summary>
    Headlamp
}

/// <summary>Which extent of the view a field of view or an orthographic scale is measured against.</summary>
public enum SceneViewBinding : byte
{
    /// <summary>The view's width.</summary>
    Width = 0,

    /// <summary>The view's height.</summary>
    Height,

    /// <summary>The smaller of width and height.</summary>
    Min,

    /// <summary>The larger of width and height.</summary>
    Max
}

/// <summary>How a view projects the scene.</summary>
/// <param name="Kind">Perspective or orthographic.</param>
/// <param name="FieldOfView">Perspective: the angle, in degrees, that <paramref name="Binding"/> spans.</param>
/// <param name="Binding">The extent of the view the field of view or the scale refers to.</param>
/// <param name="OrthographicScale">
/// Orthographic: the factor from scene units to the extent named by <paramref name="Binding"/> taken as 1;
/// that extent shows <c>1 / OrthographicScale</c> scene units.
/// </param>
/// <param name="Near">Near clipping distance, or null to fit it to the scene.</param>
/// <param name="Far">Far clipping distance, or null to fit it to the scene.</param>
public readonly record struct SceneViewProjection(
    SceneProjection Kind,
    float FieldOfView,
    SceneViewBinding Binding,
    float OrthographicScale,
    float? Near,
    float? Far);

/// <summary>What a view changes about the nodes of one name. Null leaves that aspect as the scene has it.</summary>
/// <param name="Name">The node's name; every node of that name is affected.</param>
/// <param name="Visible">Whether the node is drawn: this node, not those under it.</param>
/// <param name="Opacity">Opacity, 0 to 1, multiplied into the node's materials.</param>
/// <param name="Transform">Replaces the node's transform relative to its parent.</param>
public readonly record struct SceneNodeOverride(string Name, bool? Visible, float? Opacity, Matrix4x4? Transform);

/// <summary>
/// A way of looking at a scene: where the camera is, how it projects, what is behind the scene, how it is
/// lit and what is shown. Plain data; anything left unset is taken from the scene.
/// </summary>
public sealed class SceneView
{
    /// <summary>The view's display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The camera's pose. Camera space is the one of <see cref="SceneCamera"/>: +X right, +Y up, looking
    /// along −Z. Ignored when <see cref="CameraNodePath"/> finds a camera node.
    /// </summary>
    public Matrix4x4? CameraToWorld { get; init; }

    /// <summary>
    /// Names a camera node of the scene to look through, for <see cref="Scene.FindNode"/>. Empty for none.
    /// </summary>
    public IReadOnlyList<string> CameraNodePath { get; init; } = [];

    /// <summary>Distance from the camera, along its view direction, to the point orbiting turns about.</summary>
    public float? OrbitDistance { get; init; }

    /// <summary>The projection, or null to use the camera node's.</summary>
    public SceneViewProjection? Projection { get; init; }

    /// <summary>Background colour, or null for the viewer's default.</summary>
    public Vector3? Background { get; init; }

    /// <summary>The lights to use.</summary>
    public SceneLighting Lighting { get; init; } = SceneLighting.Artwork;

    /// <summary>Per-node changes this view makes.</summary>
    public IReadOnlyList<SceneNodeOverride> NodeOverrides { get; init; } = [];
}
