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

/// <summary>
/// How a view draws the scene: its surfaces, edges or points (the render modes of ISO 32000-2, Table 319).
/// </summary>
public enum SceneRenderMode
{
    /// <summary>Lit and textured surfaces.</summary>
    Solid,

    /// <summary>Lit and textured surfaces with every edge on top, in <see cref="SceneView.EdgeColor"/>.</summary>
    SolidWireframe,

    /// <summary>Lit and textured surfaces, made more transparent.</summary>
    Transparent,

    /// <summary>Transparent surfaces with every edge on top, opaque, in <see cref="SceneView.EdgeColor"/>.</summary>
    TransparentWireframe,

    /// <summary>The edges of each node's bounding box.</summary>
    BoundingBox,

    /// <summary>The faces of each node's bounding box, transparent.</summary>
    TransparentBoundingBox,

    /// <summary>The edges and transparent faces of each node's bounding box.</summary>
    TransparentBoundingBoxOutline,

    /// <summary>Edges only, in <see cref="SceneView.EdgeColor"/>.</summary>
    Wireframe,

    /// <summary>Edges only, coloured and lit like the surfaces they bound.</summary>
    ShadedWireframe,

    /// <summary>Edges only, in <see cref="SceneView.EdgeColor"/>, without those hidden by surfaces.</summary>
    HiddenWireframe,

    /// <summary>Vertices only, in <see cref="SceneView.EdgeColor"/>.</summary>
    Vertices,

    /// <summary>Vertices only, coloured and lit.</summary>
    ShadedVertices,

    /// <summary>Silhouette edges over surfaces of one face colour, hidden lines removed.</summary>
    Illustration,

    /// <summary>
    /// Silhouette edges, in <see cref="SceneView.EdgeColor"/>, over lit and textured surfaces, hidden lines
    /// removed. An edge is a silhouette edge where one of its faces turns towards the viewer and the other
    /// away, or where its faces meet at <see cref="SceneView.CreaseAngle"/> or more.
    /// </summary>
    SolidOutline,

    /// <summary>Silhouette edges over lit and textured surfaces with an added emissive term.</summary>
    ShadedIllustration
}

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

    /// <summary>How the scene is drawn: surfaces, edges, points.</summary>
    public SceneRenderMode RenderMode { get; init; } = SceneRenderMode.Solid;

    /// <summary>The colour of the edges and points <see cref="RenderMode"/> draws; black unless the view says otherwise.</summary>
    public Vector3 EdgeColor { get; init; }

    /// <summary>
    /// In degrees: two faces that meet at this angle or more (between their normals) have a silhouette edge
    /// between them, in the render modes that draw silhouettes. 45 unless the view says otherwise.
    /// </summary>
    public float CreaseAngle { get; init; } = 45;

    /// <summary>
    /// The colour of surfaces in the render modes that paint them in one colour (Illustration, the faces of
    /// bounding boxes); null for the background colour.
    /// </summary>
    public Vector3? FaceColor { get; init; }

    /// <summary>The opacity, 0 to 1, of the transparency the transparent render modes add. 0.5 unless the view says otherwise.</summary>
    public float Opacity { get; init; } = 0.5f;

    /// <summary>Per-node changes this view makes.</summary>
    public IReadOnlyList<SceneNodeOverride> NodeOverrides { get; init; } = [];
}
