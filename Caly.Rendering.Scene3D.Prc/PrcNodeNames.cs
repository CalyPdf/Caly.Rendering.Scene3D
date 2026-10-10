using System.Text;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc;

/// <summary>
/// The names of the nodes of a PRC scene, which are the names PDF viewers give them: a 3D view of a PDF
/// shows, hides and moves parts of a PRC model by names of the form <c>name.identifier</c>.
/// <para>
/// The identifier says which entity of the file the node is. It is a string of hexadecimal digits: a
/// reference to the entity itself and, for a representation item, one to the product occurrence it is
/// shown under. A reference is the file structure's four-number identifier, the entity's type and the
/// entity's own identifier within its file structure, written as the format writes unsigned integers
/// and padded with zero bits to a whole byte. A product occurrence without a name takes the name of
/// its prototype; one whose prototypes have none either is called <c>node</c> and has its reference
/// twice.
/// </para>
/// <para>
/// This form is not in ISO 14739-1 or ISO 32000. It was read off the files: every one of the 3,868
/// distinct node names in the views of the sample documents decodes this way, and every product
/// occurrence and surface item among them is found in the scene of its model by its identifier.
/// The text before the identifier agrees for most; where a viewer made a name up, it may not, which
/// is what <see cref="Resolve"/> is for.
/// </para>
/// </summary>
public static class PrcNodeNames
{
    /// <summary>
    /// The identifier of a node name: what follows its last dot, when that is hexadecimal digits of the
    /// length of at least one reference; empty otherwise.
    /// </summary>
    public static ReadOnlySpan<char> IdentifierOf(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return default;
        }

        // The shortest reference: four identifier numbers, a type and an identifier of one bit each.
        ReadOnlySpan<char> identifier = name[(dot + 1)..];
        if (identifier.Length < 2 || (identifier.Length & 1) != 0)
        {
            return default;
        }

        foreach (char c in identifier)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return default;
            }
        }

        return identifier;
    }

    /// <summary>
    /// The view with the nodes it names called as <paramref name="scene"/> calls them. A view and a
    /// scene agree on a node's identifier; the text before it is the node's display name, which a
    /// viewer that wrote the view may have made up differently (for a node the file leaves unnamed).
    /// Names without an identifier, identifiers the scene does not have, and the folders a viewer hangs
    /// under an occurrence (named after it, with its identifier) are left as they are.
    /// </summary>
    public static SceneView Resolve(Scene scene, SceneView view)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(view);
        if (view.NodeOverrides.Count == 0)
        {
            return view;
        }

        Dictionary<string, string>? byIdentifier = null;
        SceneNodeOverride[]? resolved = null;
        for (int i = 0; i < view.NodeOverrides.Count; i++)
        {
            SceneNodeOverride node = view.NodeOverrides[i];
            ReadOnlySpan<char> identifier = IdentifierOf(node.Name);
            if (identifier.IsEmpty)
            {
                continue;
            }

            byIdentifier ??= IndexByIdentifier(scene);
            if (byIdentifier.TryGetValue(identifier.ToString(), out string? name) && !string.Equals(name, node.Name, StringComparison.Ordinal))
            {
                // A viewer's own folder under an occurrence ("Name.PRODUCT VIEWS", "Name.3D PMI") has the
                // occurrence's name before its own and the occurrence's identifier: it is not the occurrence.
                ReadOnlySpan<char> text = node.Name.AsSpan(0, node.Name.Length - identifier.Length - 1);
                ReadOnlySpan<char> ofScene = name.AsSpan(0, name.Length - identifier.Length - 1);
                if (text.Length > ofScene.Length && text[ofScene.Length] == '.' && text.StartsWith(ofScene, StringComparison.Ordinal))
                {
                    continue;
                }

                resolved ??= [.. view.NodeOverrides];
                resolved[i] = node with { Name = name };
            }
        }

        if (resolved is null)
        {
            return view;
        }

        return new SceneView
        {
            Name = view.Name,
            CameraToWorld = view.CameraToWorld,
            CameraNodePath = view.CameraNodePath,
            OrbitDistance = view.OrbitDistance,
            Projection = view.Projection,
            Background = view.Background,
            Lighting = view.Lighting,
            NodeOverrides = resolved,
        };
    }

    private static Dictionary<string, string> IndexByIdentifier(Scene scene)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SceneNode node in scene.Nodes)
        {
            string name = scene.GetName(node.Name);
            ReadOnlySpan<char> identifier = IdentifierOf(name);
            if (!identifier.IsEmpty)
            {
                index.TryAdd(identifier.ToString(), name);
            }
        }

        return index;
    }

    /// <summary>What a product occurrence without a name is called.</summary>
    internal const string UnnamedOccurrence = "node";

    /// <summary>A name and an identifier as one node name.</summary>
    internal static string Join(string name, string identifier) => identifier.Length == 0 ? name : name + "." + identifier;

    /// <summary>One reference: a file structure, the type of an entity and the entity's identifier in that file structure.</summary>
    internal static string Reference(PrcUniqueId structure, uint type, uint identifier)
    {
        // Six unsigned integers of at most 37 bits each.
        Span<byte> bytes = stackalloc byte[32];
        bytes.Clear();
        int bit = 0;
        Write(bytes, ref bit, structure.UniqueId0);
        Write(bytes, ref bit, structure.UniqueId1);
        Write(bytes, ref bit, structure.UniqueId2);
        Write(bytes, ref bit, structure.UniqueId3);
        Write(bytes, ref bit, type);
        Write(bytes, ref bit, identifier);
        return Convert.ToHexString(bytes[..((bit + 7) / 8)]).ToLowerInvariant();
    }

    /// <summary>The format's unsigned integer: per byte, lowest first, a set bit and the byte; then a clear bit.</summary>
    private static void Write(Span<byte> bytes, ref int bit, uint value)
    {
        for (; value != 0; value >>= 8)
        {
            Put(bytes, ref bit, 1, 1);
            Put(bytes, ref bit, value & 0xFF, 8);
        }

        bit++;
    }

    private static void Put(Span<byte> bytes, ref int bit, uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--, bit++)
        {
            if (((value >> i) & 1) != 0)
            {
                bytes[bit >> 3] |= (byte)(0x80 >> (bit & 7));
            }
        }
    }
}
