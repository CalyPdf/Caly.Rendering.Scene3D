using System.Numerics;

namespace Caly.Rendering.Scene3D.U3D.Internal;

internal sealed partial class U3DDocument
{
    /// <summary>A node as declared: it may hang under several parents, each with its own transform.</summary>
    private sealed class NodeDeclaration
    {
        public required string Name { get; init; }

        public required SceneNodeKind Kind { get; init; }

        public required (string Name, Matrix4x4 Matrix)[] Parents { get; init; }

        /// <summary>The model resource, light resource or view resource the node uses.</summary>
        public string Resource { get; init; } = "";

        /// <summary>Model visibility: 0 none, 1 front, 2 back, 3 both.</summary>
        public uint Visibility { get; init; } = 3;

        public SceneCamera Camera { get; init; }
    }

    // A parent costs at least its name's length field and sixteen floats.
    private const int MinimumParentBytes = 2 + 64;

    private const uint ViewProjectionMask = 0x6;
    private const uint ViewOrthographic = 0x2;

    private readonly Dictionary<string, NodeDeclaration> _nodes = new(StringComparer.Ordinal);
    private readonly List<string> _nodeOrder = [];
    private readonly Dictionary<string, SceneLight> _lightResources = new(StringComparer.Ordinal);
    private int _duplicateNodes;
    private int _nonFiniteTransforms;

    /// <summary>Group, model, light and view nodes share their start (ECMA-363 9.5.1 – 9.5.4).</summary>
    private void ReadNode(U3DBlock block, SceneNodeKind kind)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);

        uint parentCount = reader.ReadU32();
        if (parentCount > (uint)(reader.RemainingBytes / MinimumParentBytes))
        {
            throw Truncated($"U3D node '{name}' declares {parentCount} parents, more than its block can hold.");
        }

        var parents = new (string, Matrix4x4)[parentCount];
        Span<float> m = stackalloc float[16];
        for (int i = 0; i < parents.Length; i++)
        {
            string parentName = ReadName(ref reader);
            reader.ReadF32(m);

            // Sixteen floats, column by column; in the row-vector convention of Matrix4x4 the same order
            // fills the rows, with the translation in the last one.
            var matrix = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

            // A NaN or infinity here would spread to everything computed from the node tree, the bounds
            // of the whole scene first.
            if (!AllFinite(m))
            {
                matrix = Matrix4x4.Identity;
                _nonFiniteTransforms++;
            }

            parents[i] = (parentName, matrix);
        }

        string resource = "";
        uint visibility = 3;
        SceneCamera camera = default;

        switch (kind)
        {
            case SceneNodeKind.Model:
                resource = ReadName(ref reader);
                visibility = reader.ReadU32();
                break;

            case SceneNodeKind.Light:
                resource = ReadName(ref reader);
                break;

            case SceneNodeKind.Camera:
                resource = ReadName(ref reader);
                uint attributes = reader.ReadU32();
                camera.Near = reader.ReadF32();
                camera.Far = reader.ReadF32();
                if ((attributes & ViewProjectionMask) == ViewOrthographic)
                {
                    camera.Projection = SceneProjection.Orthographic;
                    camera.OrthographicHeight = reader.ReadF32();
                }
                else if ((attributes & ViewProjectionMask) == 0)
                {
                    camera.Projection = SceneProjection.Perspective;
                    camera.FieldOfView = reader.ReadF32();
                }
                else
                {
                    // One- and two-point perspective carry a projection vector instead of a field of view.
                    camera.Projection = SceneProjection.Perspective;
                }

                // The view port, backdrops and overlays that follow place the view on a screen; not scene data.
                break;
        }

        var declaration = new NodeDeclaration { Name = name, Kind = kind, Parents = parents, Resource = resource, Visibility = visibility, Camera = camera };
        // A later declaration of the same name replaces the earlier one, as it does for every resource.
        if (_nodes.ContainsKey(name))
        {
            _duplicateNodes++;
        }
        else
        {
            _nodeOrder.Add(name);
        }

        _nodes[name] = declaration;
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

    /// <summary>ECMA-363 9.8.1.</summary>
    private void ReadLightResource(U3DBlock block)
    {
        const uint enabled = 0x1, specular = 0x2;

        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        uint attributes = reader.ReadU32();
        byte type = reader.ReadU8();

        var light = new SceneLight
        {
            Enabled = (attributes & enabled) != 0,
            Specular = (attributes & specular) != 0,
            Kind = type switch
            {
                0 => SceneLightKind.Ambient,
                1 => SceneLightKind.Directional,
                2 => SceneLightKind.Point,
                3 => SceneLightKind.Spot,
                _ => throw Invalid($"U3D light '{name}' has unknown type {type}.")
            }
        };

        light.Color = new Vector3(reader.ReadF32(), reader.ReadF32(), reader.ReadF32());
        reader.ReadF32(); // reserved
        light.AttenuationConstant = reader.ReadF32();
        light.AttenuationLinear = reader.ReadF32();
        light.AttenuationQuadratic = reader.ReadF32();
        light.SpotAngle = reader.ReadF32();
        light.Intensity = reader.ReadF32();

        _lightResources[name] = light;
    }
}
