using System.Numerics;

namespace Caly.Rendering.Scene3D.Tool;

/// <summary>
/// A small software rasteriser that draws a scene from a fixed three-quarter view, lit from the viewer:
/// enough to see whether a decoded scene looks like the model it came from. A development aid, kept out of
/// the libraries; it draws material colours only (no textures, no transparency).
/// </summary>
internal static class PreviewRenderer
{
    private const int Supersampling = 2;

    /// <summary>Renders the scene into a square RGB image of <paramref name="size"/> pixels.</summary>
    /// <returns>Rows top to bottom, three bytes per pixel.</returns>
    public static byte[] Render(Scene scene, int size, out int trianglesDrawn)
    {
        int s = size * Supersampling;
        var color = new Vector3[s * s];
        var depth = new float[s * s];
        Array.Fill(depth, float.NegativeInfinity);
        for (int y = 0; y < s; y++)
        {
            // A light backdrop, slightly darker towards the bottom.
            Array.Fill(color, Vector3.Lerp(new Vector3(0.97f), new Vector3(0.82f, 0.84f, 0.88f), y / (float)s), y * s, s);
        }

        trianglesDrawn = 0;
        SceneBounds bounds = scene.ComputeWorldBounds();
        if (!bounds.IsEmpty && float.IsFinite((bounds.Max - bounds.Min).Length()))
        {
            // The format prefers Z up; look from the front right, a little from above.
            Vector3 toEye = Vector3.Normalize(new Vector3(1f, -1.25f, 0.8f));
            Vector3 right = Vector3.Normalize(Vector3.Cross(-toEye, Vector3.UnitZ));
            Vector3 up = Vector3.Cross(toEye, right);
            Vector3 centre = (bounds.Min + bounds.Max) / 2;

            // Orthographic: scale so that the bounding box's corners fit, with a margin.
            float extent = 1e-20f;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = new Vector3((corner & 1) == 0 ? bounds.Min.X : bounds.Max.X, (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z) - centre;
                extent = MathF.Max(extent, MathF.Max(MathF.Abs(Vector3.Dot(p, right)), MathF.Abs(Vector3.Dot(p, up))));
            }

            float scale = s * 0.46f / extent;
            var view = new View(right, up, toEye, centre, scale, s);

            ReadOnlySpan<SceneNode> nodes = scene.Nodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Mesh >= 0 && nodes[i].Visible)
                {
                    trianglesDrawn += DrawInstance(scene, i, view, color, depth);
                }
            }
        }

        // Average the samples down and encode with a gamma close to sRGB.
        var rgb = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector3 sum = Vector3.Zero;
                for (int dy = 0; dy < Supersampling; dy++)
                {
                    for (int dx = 0; dx < Supersampling; dx++)
                    {
                        sum += color[(y * Supersampling + dy) * s + x * Supersampling + dx];
                    }
                }

                sum /= Supersampling * Supersampling;
                int at = (y * size + x) * 3;
                rgb[at] = ToByte(sum.X);
                rgb[at + 1] = ToByte(sum.Y);
                rgb[at + 2] = ToByte(sum.Z);
            }
        }

        return rgb;
    }

    private static byte ToByte(float linear) => (byte)(MathF.Pow(Math.Clamp(linear, 0f, 1f), 1 / 2.2f) * 255f + 0.5f);

    private readonly record struct View(Vector3 Right, Vector3 Up, Vector3 ToEye, Vector3 Centre, float Scale, int Size)
    {
        /// <summary>Pixel x, pixel y (down), and depth (larger is nearer).</summary>
        public Vector3 Project(Vector3 world)
        {
            Vector3 p = world - Centre;
            return new Vector3(Size / 2f + Vector3.Dot(p, Right) * Scale, Size / 2f - Vector3.Dot(p, Up) * Scale, Vector3.Dot(p, ToEye));
        }
    }

    private static int DrawInstance(Scene scene, int nodeIndex, in View view, Vector3[] color, float[] depth)
    {
        SceneNode node = scene.Nodes[nodeIndex];
        SceneMesh mesh = scene.Meshes[node.Mesh];
        Matrix4x4 world = scene.GetWorldMatrix(nodeIndex);

        ReadOnlySpan<float> positions = scene.Positions.Slice(mesh.VertexStart * 3, mesh.VertexCount * 3);
        ReadOnlySpan<float> normals = mesh.NormalStart >= 0 ? scene.Normals.Slice(mesh.NormalStart * 3, mesh.VertexCount * 3) : default;
        ReadOnlySpan<uint> vertexColors = mesh.ColorStart >= 0 ? scene.Colors.Slice(mesh.ColorStart, mesh.VertexCount) : default;

        var worldPositions = new Vector3[mesh.VertexCount];
        var screen = new Vector3[mesh.VertexCount];
        var light = new float[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            worldPositions[v] = Vector3.Transform(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]), world);
            screen[v] = view.Project(worldPositions[v]);
            if (!normals.IsEmpty)
            {
                Vector3 n = Vector3.TransformNormal(new Vector3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]), world);
                light[v] = n.LengthSquared() > 0 ? Shade(Vector3.Normalize(n), view.ToEye) : 1;
            }
        }

        int drawn = 0;
        for (int p = 0; p < mesh.PartCount; p++)
        {
            SceneMeshPart part = scene.Parts[mesh.FirstPart + p];
            int materialIndex = part.ShadingId < node.BindingCount ? scene.Bindings[node.FirstBinding + part.ShadingId] : 0;
            SceneMaterial material = scene.Materials[materialIndex];
            Vector3 surface = material.Diffuse == Vector3.Zero ? material.Ambient : material.Diffuse;
            surface = Vector3.Clamp(surface + material.Emissive * 0.5f, Vector3.Zero, Vector3.One);
            bool useVertexColor = !vertexColors.IsEmpty && (material.Flags & SceneMaterialFlags.UseVertexColor) != 0;
            bool lit = (material.Flags & SceneMaterialFlags.Lit) != 0;

            ReadOnlySpan<uint> indices = scene.Indices.Slice(part.IndexStart, part.IndexCount);
            if (mesh.Topology != SceneTopology.Triangles)
            {
                // Line segments and points, unlit: a thin quad along each segment, a small square at each point.
                int step = mesh.Topology == SceneTopology.Lines ? 2 : 1;
                for (int i = 0; i + step - 1 < indices.Length; i += step)
                {
                    int a = (int)indices[i], b = (int)indices[i + step - 1];
                    Vector3 ca = useVertexColor ? surface * Unpack(vertexColors[a]) : surface, cb = useVertexColor ? surface * Unpack(vertexColors[b]) : surface;
                    Vector3 from = screen[a], to = screen[b];
                    Vector3 along = step == 2 ? new Vector3(to.X - from.X, to.Y - from.Y, 0) : new Vector3(1.6f, 0, 0);
                    float length = along.Length();
                    Vector3 across = length > 0 ? new Vector3(-along.Y, along.X, 0) / length * (step == 2 ? 0.7f : 1.6f) : new Vector3(0, 0.7f, 0);
                    if (step == 1)
                    {
                        from -= along;
                        to += along;
                    }

                    bool first = Fill(from - across, from + across, to + across, ca, ca, cb, view.Size, color, depth);
                    bool second = Fill(from - across, to + across, to - across, ca, cb, cb, view.Size, color, depth);
                    drawn += first || second ? 1 : 0;
                }

                continue;
            }

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
                Vector3 la;
                if (!lit)
                {
                    la = Vector3.One;
                }
                else if (normals.IsEmpty)
                {
                    // No normals: the reader invents none, a consumer shades flat.
                    Vector3 face = Vector3.Cross(worldPositions[b] - worldPositions[a], worldPositions[c] - worldPositions[a]);
                    float flat = face.LengthSquared() > 0 ? Shade(Vector3.Normalize(face), view.ToEye) : 1;
                    la = new Vector3(flat);
                }
                else
                {
                    la = new Vector3(light[a], light[b], light[c]);
                }

                Vector3 ca = surface, cb = surface, cc = surface;
                if (useVertexColor)
                {
                    ca *= Unpack(vertexColors[a]);
                    cb *= Unpack(vertexColors[b]);
                    cc *= Unpack(vertexColors[c]);
                }

                if (Fill(screen[a], screen[b], screen[c], ca * la.X, cb * la.Y, cc * la.Z, view.Size, color, depth))
                {
                    drawn++;
                }
            }
        }

        return drawn;
    }

    /// <summary>Two-sided diffuse light from the viewer, over an ambient floor.</summary>
    private static float Shade(Vector3 normal, Vector3 toEye)
    {
        Vector3 toLight = Vector3.Normalize(toEye + new Vector3(-0.35f, 0.1f, 0.45f));
        return 0.28f + 0.72f * MathF.Abs(Vector3.Dot(normal, toLight));
    }

    private static Vector3 Unpack(uint rgba) => new Vector3(rgba & 0xFF, (rgba >> 8) & 0xFF, (rgba >> 16) & 0xFF) / 255f;

    private static bool Fill(Vector3 a, Vector3 b, Vector3 c, Vector3 ca, Vector3 cb, Vector3 cc, int size, Vector3[] color, float[] depth)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (!(MathF.Abs(area) > 1e-12f) || !float.IsFinite(area))
        {
            return false;
        }

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        float inverseArea = 1 / area;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;

                // Barycentric weights; the division by the signed area makes them positive inside the
                // triangle whichever way it is wound.
                float wa = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * inverseArea;
                float wb = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * inverseArea;
                float wc = 1 - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0)
                {
                    continue;
                }

                float z = wa * a.Z + wb * b.Z + wc * c.Z;
                int at = y * size + x;
                if (z > depth[at])
                {
                    depth[at] = z;
                    color[at] = wa * ca + wb * cb + wc * cc;
                }
            }
        }

        return true;
    }
}
