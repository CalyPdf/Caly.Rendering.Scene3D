using System.Numerics;

namespace Caly.Rendering.Scene3D;

/// <summary>What a <see cref="SceneNode"/> places in the scene.</summary>
public enum SceneNodeKind : byte
{
    /// <summary>A transform only; groups its children.</summary>
    Group = 0,

    /// <summary>An instance of a mesh.</summary>
    Model = 1,

    /// <summary>A light.</summary>
    Light = 2,

    /// <summary>A camera.</summary>
    Camera = 3
}

/// <summary>
/// One node of the scene tree. A node has exactly one parent (or none): source formats that let a node
/// hang under several parents are expanded into one node per path.
/// </summary>
public struct SceneNode
{
    /// <summary>Index of the parent node, or -1 for a root.</summary>
    public int Parent;

    /// <summary>
    /// Transform relative to the parent, in the row-vector convention of <see cref="Matrix4x4"/>:
    /// <c>world = Local × parentWorld</c>, translation in <c>M41..M43</c>.
    /// </summary>
    public Matrix4x4 Local;

    /// <summary>What the node places in the scene.</summary>
    public SceneNodeKind Kind;

    /// <summary>Index into <see cref="Scene.Meshes"/> for a model node that has geometry, else -1.</summary>
    public int Mesh;

    /// <summary>Index into <see cref="Scene.Lights"/> for a light node, else -1.</summary>
    public int Light;

    /// <summary>Index into <see cref="Scene.Cameras"/> for a camera node, else -1.</summary>
    public int Camera;

    /// <summary>
    /// First entry in <see cref="Scene.Bindings"/> for this instance. Entry <c>FirstBinding + shadingId</c>
    /// is the material index of the mesh part with that shading id.
    /// </summary>
    public int FirstBinding;

    /// <summary>Number of binding entries. A part whose shading id is not covered uses material 0.</summary>
    public int BindingCount;

    /// <summary>Index of the node's name, for <see cref="Scene.GetName"/>.</summary>
    public int Name;

    /// <summary>Whether the node is drawn. Children are not affected.</summary>
    public bool Visible;
}

/// <summary>What the indices of a mesh join: how many of them make one primitive.</summary>
public enum SceneTopology : byte
{
    /// <summary>Triangles: three indices each.</summary>
    Triangles = 0,

    /// <summary>Line segments: two indices each.</summary>
    Lines = 1,

    /// <summary>Points: one index each.</summary>
    Points = 2
}

/// <summary>Which vertex streams a mesh has, and how complete it is.</summary>
[Flags]
public enum SceneMeshFlags
{
    /// <summary>Positions only.</summary>
    None = 0,

    /// <summary>The mesh has a normal per vertex.</summary>
    HasNormals = 1,

    /// <summary>The mesh has a texture coordinate pair per vertex.</summary>
    HasTexCoords = 2,

    /// <summary>The mesh has an RGBA colour per vertex.</summary>
    HasColors = 4,

    /// <summary>
    /// The source stores more detail than was decoded (for example a progressive mesh of which only the
    /// base level was read).
    /// </summary>
    PartialDetail = 8
}

/// <summary>An axis-aligned box.</summary>
public struct SceneBounds
{
    /// <summary>Smallest corner. Meaningless when <see cref="IsEmpty"/>.</summary>
    public Vector3 Min;

    /// <summary>Largest corner. Meaningless when <see cref="IsEmpty"/>.</summary>
    public Vector3 Max;

    /// <summary>Whether the box contains nothing.</summary>
    public bool IsEmpty;

    /// <summary>A box containing nothing.</summary>
    public static SceneBounds Empty => new() { IsEmpty = true };

    /// <summary>Grows the box to contain <paramref name="point"/>.</summary>
    public void Include(Vector3 point)
    {
        if (IsEmpty)
        {
            Min = point;
            Max = point;
            IsEmpty = false;
        }
        else
        {
            Min = Vector3.Min(Min, point);
            Max = Vector3.Max(Max, point);
        }
    }
}

/// <summary>
/// Geometry shared by any number of model nodes. Its vertices occupy the same range in every vertex stream
/// it has; indices are relative to the mesh's first vertex.
/// </summary>
public struct SceneMesh
{
    /// <summary>First entry in <see cref="Scene.Parts"/>.</summary>
    public int FirstPart;

    /// <summary>Number of parts.</summary>
    public int PartCount;

    /// <summary>Number of vertices.</summary>
    public int VertexCount;

    /// <summary>First vertex in <see cref="Scene.Positions"/> (three floats per vertex).</summary>
    public int VertexStart;

    /// <summary>First vertex in <see cref="Scene.Normals"/> (three floats per vertex), or -1 without normals.</summary>
    public int NormalStart;

    /// <summary>First vertex in <see cref="Scene.TexCoords"/> (two floats per vertex), or -1 without them.</summary>
    public int TexCoordStart;

    /// <summary>First vertex in <see cref="Scene.Colors"/> (one RGBA value per vertex), or -1 without colours.</summary>
    public int ColorStart;

    /// <summary>Box around the mesh's vertices, in mesh space.</summary>
    public SceneBounds Bounds;

    /// <summary>Streams present and completeness.</summary>
    public SceneMeshFlags Flags;

    /// <summary>Whether the indices of the mesh's parts join triangles, line segments or single points.</summary>
    public SceneTopology Topology;

    /// <summary>Index of the mesh's name.</summary>
    public int Name;

    /// <summary>Index into <see cref="Scene.Skins"/> when the mesh follows a skeleton, else -1.</summary>
    public int Skin;
}

/// <summary>A run of triangles (or of line segments, or points: the mesh's <see cref="SceneMesh.Topology"/>) that share a material slot.</summary>
public struct SceneMeshPart
{
    /// <summary>First index in <see cref="Scene.Indices"/>.</summary>
    public int IndexStart;

    /// <summary>Number of indices: three per triangle, two per line segment, one per point.</summary>
    public int IndexCount;

    /// <summary>The part's material slot: an offset into the bindings of each node that instances the mesh.</summary>
    public int ShadingId;
}

/// <summary>Switches of a <see cref="SceneMaterial"/>.</summary>
[Flags]
public enum SceneMaterialFlags
{
    /// <summary>No switch set.</summary>
    None = 0,

    /// <summary>The surface reacts to lights.</summary>
    Lit = 1,

    /// <summary>Fragments failing the alpha test against <see cref="SceneMaterial.AlphaReference"/> are discarded.</summary>
    AlphaTest = 2,

    /// <summary>Per-vertex colours modulate the material.</summary>
    UseVertexColor = 4
}

/// <summary>Surface appearance, in the classic ambient/diffuse/specular model the source formats use.</summary>
public struct SceneMaterial
{
    /// <summary>Ambient colour.</summary>
    public Vector3 Ambient;

    /// <summary>Diffuse colour.</summary>
    public Vector3 Diffuse;

    /// <summary>Specular colour.</summary>
    public Vector3 Specular;

    /// <summary>Emissive colour.</summary>
    public Vector3 Emissive;

    /// <summary>Opacity, 0 transparent to 1 opaque.</summary>
    public float Opacity;

    /// <summary>Reflectivity (shininess) as the source gives it.</summary>
    public float Reflectivity;

    /// <summary>Reference value for <see cref="SceneMaterialFlags.AlphaTest"/>.</summary>
    public float AlphaReference;

    /// <summary>Switches.</summary>
    public SceneMaterialFlags Flags;

    /// <summary>First entry in <see cref="Scene.TextureLayers"/>.</summary>
    public int FirstLayer;

    /// <summary>Number of texture layers.</summary>
    public int LayerCount;

    /// <summary>Index of the material's name.</summary>
    public int Name;
}

/// <summary>How a texture layer is combined with the result of the layers before it.</summary>
public enum SceneBlendFunction : byte
{
    /// <summary>layer × previous.</summary>
    Multiply = 0,

    /// <summary>layer + previous.</summary>
    Add = 1,

    /// <summary>The layer alone.</summary>
    Replace = 2,

    /// <summary>layer × factor + previous × (1 − factor), the factor chosen by <see cref="SceneTextureLayer.BlendSource"/>.</summary>
    Blend = 3
}

/// <summary>One texture applied by a material.</summary>
public struct SceneTextureLayer
{
    /// <summary>Index into <see cref="Scene.Textures"/>, or -1 when the texture is missing.</summary>
    public int Texture;

    /// <summary>Intensity of the texture.</summary>
    public float Intensity;

    /// <summary>Constant used when <see cref="BlendSource"/> is 1.</summary>
    public float BlendConstant;

    /// <summary>How the layer is combined with the result so far.</summary>
    public SceneBlendFunction BlendFunction;

    /// <summary>Where the blend factor comes from: 0 the alpha of each pixel, 1 <see cref="BlendConstant"/>.</summary>
    public byte BlendSource;

    /// <summary>
    /// Where texture coordinates come from: 0 the mesh's own; 1 planar, 2 cylindrical, 3 spherical and
    /// 4 reflection mapping generated from the geometry.
    /// </summary>
    public byte Mode;

    /// <summary>Repeat flags: bit 0 along U, bit 1 along V.</summary>
    public byte Repeat;

    /// <summary>Transform applied to texture coordinates.</summary>
    public Matrix4x4 Transform;
}

/// <summary>Kind of projection of a <see cref="SceneCamera"/>.</summary>
public enum SceneProjection : byte
{
    /// <summary>Perspective.</summary>
    Perspective = 0,

    /// <summary>Orthographic.</summary>
    Orthographic = 1
}

/// <summary>
/// A camera authored inside the 3D data. It sits at its node's origin and looks along the node's negative
/// Z axis with positive Y up.
/// </summary>
public struct SceneCamera
{
    /// <summary>The camera's projection.</summary>
    public SceneProjection Projection;

    /// <summary>Field of view in degrees, for a perspective camera.</summary>
    public float FieldOfView;

    /// <summary>Height of the view volume, for an orthographic camera.</summary>
    public float OrthographicHeight;

    /// <summary>Near clipping distance.</summary>
    public float Near;

    /// <summary>Far clipping distance.</summary>
    public float Far;

    /// <summary>Index of the camera's name (the source's view node or resource name).</summary>
    public int Name;
}

/// <summary>Kind of a <see cref="SceneLight"/>.</summary>
public enum SceneLightKind : byte
{
    /// <summary>Uniform light from everywhere.</summary>
    Ambient = 0,

    /// <summary>Parallel light along the node's direction.</summary>
    Directional = 1,

    /// <summary>Light radiating from the node's position.</summary>
    Point = 2,

    /// <summary>A cone of light from the node's position.</summary>
    Spot = 3
}

/// <summary>A light authored inside the 3D data.</summary>
public struct SceneLight
{
    /// <summary>The kind of light.</summary>
    public SceneLightKind Kind;

    /// <summary>Whether the light is switched on.</summary>
    public bool Enabled;

    /// <summary>Whether the light contributes specular highlights.</summary>
    public bool Specular;

    /// <summary>Colour.</summary>
    public Vector3 Color;

    /// <summary>Intensity multiplier.</summary>
    public float Intensity;

    /// <summary>Constant attenuation factor.</summary>
    public float AttenuationConstant;

    /// <summary>Linear attenuation factor.</summary>
    public float AttenuationLinear;

    /// <summary>Quadratic attenuation factor.</summary>
    public float AttenuationQuadratic;

    /// <summary>Cone angle in degrees, for a spot light.</summary>
    public float SpotAngle;

    /// <summary>Index of the light's name.</summary>
    public int Name;
}

/// <summary>Spans over the vertex streams of the mesh being built. Streams the mesh lacks are empty.</summary>
public readonly ref struct SceneVertexSpans
{
    /// <summary>Three floats per vertex.</summary>
    public Span<float> Positions { get; }

    /// <summary>Three floats per vertex, or empty.</summary>
    public Span<float> Normals { get; }

    /// <summary>Two floats per vertex, or empty.</summary>
    public Span<float> TexCoords { get; }

    /// <summary>One RGBA value per vertex (red in the low byte), or empty.</summary>
    public Span<uint> Colors { get; }

    internal SceneVertexSpans(Span<float> positions, Span<float> normals, Span<float> texCoords, Span<uint> colors)
    {
        Positions = positions;
        Normals = normals;
        TexCoords = texCoords;
        Colors = colors;
    }
}
