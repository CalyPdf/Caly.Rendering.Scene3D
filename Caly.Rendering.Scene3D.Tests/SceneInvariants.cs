namespace Caly.Rendering.Scene3D.Tests;

/// <summary>What must hold for any scene a reader returns, whatever the input was.</summary>
internal static class SceneInvariants
{
    private static bool IsFinite(System.Numerics.Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public static void Check(Scene scene)
    {
        int nameCount = scene.NameCount;

        ReadOnlySpan<SceneNode> nodes = scene.Nodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            SceneNode node = nodes[i];
            Assert.InRange(node.Parent, -1, i - 1);
            Assert.InRange(node.Name, 0, nameCount - 1);
            Assert.InRange(node.Mesh, -1, scene.Meshes.Length - 1);
            Assert.InRange(node.Light, -1, scene.Lights.Length - 1);
            Assert.InRange(node.Camera, -1, scene.Cameras.Length - 1);
            Assert.True(node.BindingCount >= 0 && node.FirstBinding >= 0 && (long)node.FirstBinding + node.BindingCount <= scene.Bindings.Length, "node bindings out of range");
        }

        foreach (int material in scene.Bindings)
        {
            Assert.InRange(material, 0, scene.Materials.Length - 1);
        }

        ReadOnlySpan<SceneMaterial> materials = scene.Materials;
        for (int i = 0; i < materials.Length; i++)
        {
            SceneMaterial material = materials[i];
            Assert.InRange(material.Name, 0, nameCount - 1);
            Assert.True(material.LayerCount >= 0 && material.FirstLayer >= 0 && (long)material.FirstLayer + material.LayerCount <= scene.TextureLayers.Length, "material layers out of range");
        }

        foreach (SceneTextureLayer layer in scene.TextureLayers)
        {
            Assert.InRange(layer.Texture, -1, scene.Textures.Count - 1);
        }

        // Parts tile the index buffer mesh after mesh; vertices tile every stream a mesh has.
        ReadOnlySpan<SceneMesh> meshes = scene.Meshes;
        ReadOnlySpan<SceneMeshPart> parts = scene.Parts;
        ReadOnlySpan<uint> indices = scene.Indices;
        int nextIndex = 0, nextPart = 0, nextVertex = 0;
        for (int m = 0; m < meshes.Length; m++)
        {
            SceneMesh mesh = meshes[m];
            Assert.InRange(mesh.Name, 0, nameCount - 1);
            Assert.Equal(nextPart, mesh.FirstPart);
            Assert.Equal(nextVertex, mesh.VertexStart);
            Assert.True(mesh.VertexCount >= 0 && ((long)mesh.VertexStart + mesh.VertexCount) * 3 <= scene.Positions.Length, "mesh positions out of range");

            Assert.Equal((mesh.Flags & SceneMeshFlags.HasNormals) != 0, mesh.NormalStart >= 0);
            Assert.Equal((mesh.Flags & SceneMeshFlags.HasTexCoords) != 0, mesh.TexCoordStart >= 0);
            Assert.Equal((mesh.Flags & SceneMeshFlags.HasColors) != 0, mesh.ColorStart >= 0);
            Assert.True(mesh.NormalStart < 0 || ((long)mesh.NormalStart + mesh.VertexCount) * 3 <= scene.Normals.Length, "mesh normals out of range");
            Assert.True(mesh.TexCoordStart < 0 || ((long)mesh.TexCoordStart + mesh.VertexCount) * 2 <= scene.TexCoords.Length, "mesh texture coordinates out of range");
            Assert.True(mesh.ColorStart < 0 || (long)mesh.ColorStart + mesh.VertexCount <= scene.Colors.Length, "mesh colours out of range");

            for (int p = 0; p < mesh.PartCount; p++)
            {
                SceneMeshPart part = parts[mesh.FirstPart + p];
                Assert.Equal(nextIndex, part.IndexStart);
                int perPrimitive = mesh.Topology switch { SceneTopology.Lines => 2, SceneTopology.Points => 1, _ => 3 };
                Assert.True(part.IndexCount > 0 && part.IndexCount % perPrimitive == 0, "a part is a positive number of triangles, line segments or points");
                Assert.True(part.ShadingId >= 0);
                foreach (uint index in indices.Slice(part.IndexStart, part.IndexCount))
                {
                    if (index >= (uint)mesh.VertexCount)
                    {
                        Assert.Fail($"mesh {m}: index {index} of {mesh.VertexCount} vertices");
                    }
                }

                nextIndex += part.IndexCount;
            }

            nextPart += mesh.PartCount;
            nextVertex += mesh.VertexCount;

            if (mesh.VertexCount > 0)
            {
                Assert.False(mesh.Bounds.IsEmpty);
                // Component by component: a length would overflow for coordinates that are huge yet finite.
                Assert.True(IsFinite(mesh.Bounds.Min) && IsFinite(mesh.Bounds.Max), "mesh bounds are finite");
                Assert.True(mesh.Bounds.Min.X <= mesh.Bounds.Max.X && mesh.Bounds.Min.Y <= mesh.Bounds.Max.Y && mesh.Bounds.Min.Z <= mesh.Bounds.Max.Z, "mesh bounds are ordered");
            }
        }

        Assert.Equal(indices.Length, nextIndex);
        Assert.Equal(parts.Length, nextPart);
        Assert.Equal(scene.Positions.Length, nextVertex * 3);

        foreach (float value in scene.Positions)
        {
            if (!float.IsFinite(value))
            {
                Assert.Fail("a position is not finite");
            }
        }

        foreach (float value in scene.Normals)
        {
            if (!float.IsFinite(value))
            {
                Assert.Fail("a normal is not finite");
            }
        }

        foreach (SceneTexture texture in scene.Textures)
        {
            Assert.NotNull(texture.Name);
            Assert.True(texture.Width >= 0 && texture.Height >= 0);
        }

        // Whatever the file held, what a camera is fitted to is nothing or a finite box.
        SceneBounds world = scene.ComputeWorldBounds();
        Assert.True(world.IsEmpty || (IsFinite(world.Min) && IsFinite(world.Max)), "world bounds are finite");

        foreach (SceneNode node in nodes)
        {
            System.Numerics.Matrix4x4 local = node.Local;
            foreach (float value in System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref local.M11, 16))
            {
                Assert.True(float.IsFinite(value), "node transforms are finite");
            }
        }
    }
}
