using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

public class U3DRHAdobeTests
{
    private static byte[] StringBytes(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        byte[] result = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)bytes.Length);
        bytes.CopyTo(result, 2);
        return result;
    }

    [Fact]
    public void CubeFromSpecificationDecodesPositionsCorrectly()
    {
        // 1x1x1 cube from Adobe "U3D Supported Elements" page 38
        byte[] cubePayload = Convert.FromHexString(
            "02000000bf0000003f84eb0f000a07004cb8fe5d5d5d000000bf0000003f84eb0f000a070d4cb8fe0e4000000000bf0000003f84eb0f000a064cb8fe0e400e"
        );

        byte[] faceIndices =
        [
            0, 2, 3, 0, 3, 1, // Front
            4, 5, 7, 4, 7, 6, // Back
            0, 1, 5, 0, 5, 4, // Bottom
            2, 6, 7, 2, 7, 3, // Top
            0, 4, 6, 0, 6, 2, // Left
            1, 3, 7, 1, 7, 5  // Right
        ];

        var rhData = new List<byte>();
        // Name string: "Cube"
        rhData.AddRange(StringBytes("Cube"));
        // ChunkFlags: version 0, 1 material
        rhData.Add(0x00);
        // Material 0: mf = 0, layers = 0, originalShadingId = 0
        rhData.Add(0x00);
        rhData.Add(0x00);
        rhData.Add(0x00);
        // ValuesFlags: faces (bit 0) and positions (bit 1), width 1 byte (bits 6,7 = 0)
        rhData.Add(0x03);
        rhData.Add(12); // faces
        rhData.Add(8);  // positions

        // Float positions:
        rhData.AddRange(cubePayload);

        // Position indices: Type 3 (U8 raw), 1 byte size (sb=1)
        rhData.Add(0x03);
        rhData.Add((byte)faceIndices.Length);
        rhData.AddRange(faceIndices);

        byte[] u3d = new TestU3D()
            .ModifierChain("Cube", TestU3D.ModelResourceChain, chain =>
            {
                chain.Block(0x00000100, rhData.ToArray());
                chain.ShadingModifier("Cube", 1, [["Material_0"]]);
            })
            .ModelNode("Node_Cube", "Cube", ("", Matrix4x4.Identity))
            .ToArray();

        using Scene scene = U3DReader.Read(u3d, cancellationToken: TestContext.Current.CancellationToken);
        SceneInvariants.Check(scene);

        Assert.Equal(1, scene.Meshes.Length);
        SceneMesh mesh = scene.Meshes[0];
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(36, scene.Indices.Length);

        // Verify world bounds frame the -0.5 to 0.5 cube
        SceneBounds bounds = scene.ComputeWorldBounds();
        Assert.InRange(bounds.Min.X, -0.51f, -0.49f);
        Assert.InRange(bounds.Max.X, 0.49f, 0.51f);
        Assert.InRange(bounds.Min.Y, -0.51f, -0.49f);
        Assert.InRange(bounds.Max.Y, 0.49f, 0.51f);
        Assert.InRange(bounds.Min.Z, -0.51f, -0.49f);
        Assert.InRange(bounds.Max.Z, 0.49f, 0.51f);
    }

    [Fact]
    public void IntegerEncodingTypesDecodeCorrectly()
    {
        const int faceCount = 4;
        const int posCount = 6;
        const int normCount = 4;
        const int diffCount = 3;
        const int texCount = 4;

        var rhData = new List<byte>();
        rhData.AddRange(StringBytes("Mesh1"));
        rhData.Add(0x00); // chunkFlags: mt=0 (1 material)
        // Material: has diffuse (bit 6), 1 texture layer (layers=1)
        // mf = 64 (bit 6: diffuse)
        rhData.Add(0x40);
        rhData.Add(0x01); // layers = 1
        rhData.Add(0x00); // originalShadingId = 0

        // ValuesFlags: faces(1), positions(2), normals(4), diffuse(8), tex(32) -> 1 | 2 | 4 | 8 | 32 = 47 (0x2F)
        rhData.Add(0x2F);
        rhData.Add((byte)faceCount);
        rhData.Add((byte)posCount);
        rhData.Add((byte)normCount);
        rhData.Add((byte)diffCount);
        rhData.Add((byte)texCount);

        // Positions: Type 0 uncompressed
        rhData.Add(0x00);
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < posCount; i++)
            {
                byte[] fBytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(fBytes, i * 1.5f + c);
                rhData.AddRange(fBytes);
            }
        }

        // Normals: Type 0 uncompressed
        rhData.Add(0x00);
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < normCount; i++)
            {
                byte[] fBytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(fBytes, c == 1 ? 1.0f : 0.0f);
                rhData.AddRange(fBytes);
            }
        }

        // Diffuse: Type 0 uncompressed (RGBA)
        rhData.Add(0x00);
        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i < diffCount; i++)
            {
                byte[] fBytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(fBytes, 1.0f);
                rhData.AddRange(fBytes);
            }
        }

        // TexCoords: Type 0 uncompressed (UVST)
        rhData.Add(0x00);
        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i < texCount; i++)
            {
                byte[] fBytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(fBytes, (float)i / texCount);
                rhData.AddRange(fBytes);
            }
        }

        // Face Material IDs: omitted since materialsCount == 1

        // Position Indices (12 ints): Type 10 (UIC1)
        var uic1Bytes = new List<byte>();
        for (int i = 0; i < faceCount * 3; i++)
        {
            int pIdx = i % posCount;
            if (pIdx <= 4)
            {
                uic1Bytes.Add((byte)(0x0F | (pIdx << 4)));
            }
            else
            {
                uic1Bytes.Add((byte)(0x0B | ((pIdx & 0xF) << 4)));
                uic1Bytes.Add((byte)(pIdx >> 4));
            }
        }
        rhData.Add(0x0A); // Type 10
        rhData.Add((byte)uic1Bytes.Count);
        rhData.AddRange(uic1Bytes);

        // Normal Indices (12 ints): Type 7 (RH39)
        var rh39Bytes = new List<byte> { 0x6B };
        for (int i = 0; i < faceCount * 3; i++)
        {
            rh39Bytes.Add((byte)(i % normCount));
        }
        rhData.Add(0x07); // Type 7
        rhData.Add((byte)rh39Bytes.Count);
        rhData.AddRange(rh39Bytes);

        // Diffuse Indices (12 ints): Type 3 (Raw U8)
        rhData.Add(0x03);
        rhData.Add((byte)(faceCount * 3));
        for (int i = 0; i < faceCount * 3; i++)
        {
            rhData.Add((byte)(i % diffCount));
        }

        // TexCoord Indices (12 ints): Type 4 (Raw U16)
        rhData.Add(0x04);
        rhData.Add((byte)(faceCount * 3 * 2));
        for (int i = 0; i < faceCount * 3; i++)
        {
            byte[] u16 = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)(i % texCount));
            rhData.AddRange(u16);
        }

        byte[] u3d = new TestU3D()
            .ModifierChain("Mesh1", TestU3D.ModelResourceChain, chain =>
            {
                chain.Block(0x00000100, rhData.ToArray());
                chain.ShadingModifier("Mesh1", 1, [["Material_0"]]);
            })
            .ModelNode("Node_1", "Mesh1", ("", Matrix4x4.Identity))
            .ToArray();

        using Scene scene = U3DReader.Read(u3d, cancellationToken: TestContext.Current.CancellationToken);
        SceneInvariants.Check(scene);

        Assert.Equal(1, scene.Meshes.Length);
        SceneMesh mesh = scene.Meshes[0];
        Assert.Equal(12, scene.Indices.Length);
        Assert.True(mesh.Flags.HasFlag(SceneMeshFlags.HasNormals));
        Assert.True(mesh.Flags.HasFlag(SceneMeshFlags.HasColors));
        Assert.True(mesh.Flags.HasFlag(SceneMeshFlags.HasTexCoords));
    }

    [Fact]
    public void NewObjectTypeDeclarationRegistersCustomBlockType()
    {
        var newObjTypeData = new List<byte>();
        newObjTypeData.AddRange(StringBytes("RHAdobeMeshResource"));
        newObjTypeData.AddRange(BitConverter.GetBytes(1u)); // modifier type
        newObjTypeData.AddRange(new byte[16]); // guid
        newObjTypeData.AddRange(BitConverter.GetBytes(0x00000200u)); // new decl block type
        newObjTypeData.AddRange(BitConverter.GetBytes(0u)); // continuation block count
        newObjTypeData.AddRange(StringBytes("Right Hemisphere Adobe Systems"));
        newObjTypeData.AddRange(BitConverter.GetBytes(0u)); // url count
        newObjTypeData.AddRange(StringBytes("version 1.0"));

        var rhData = new List<byte>();
        rhData.AddRange(StringBytes("CustomRHMesh"));
        rhData.Add(0x00); // chunkFlags: mt=0
        rhData.Add(0x00); // mf = 0
        rhData.Add(0x00); // layers = 0
        rhData.Add(0x00); // originalShadingId = 0
        rhData.Add(0x03); // valuesFlags: faces, positions
        rhData.Add(1); // 1 face
        rhData.Add(3); // 3 positions

        // Float positions (Type 0):
        rhData.Add(0x00);
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 3; i++)
            {
                byte[] fBytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(fBytes, (float)i);
                rhData.AddRange(fBytes);
            }
        }

        // Indices (Type 0 all zero):
        rhData.Add(0x00); // Type 0 (All zero)
        rhData.Add(0x00); // size 0

        byte[] u3d = new TestU3D()
            .Block(0xFFFFFF16, newObjTypeData.ToArray())
            .ModifierChain("CustomRHMesh", TestU3D.ModelResourceChain, chain =>
            {
                chain.Block(0x00000200, rhData.ToArray());
                chain.ShadingModifier("CustomRHMesh", 1, [["Material_0"]]);
            })
            .ModelNode("Node_Custom", "CustomRHMesh", ("", Matrix4x4.Identity))
            .ToArray();

        using Scene scene = U3DReader.Read(u3d, cancellationToken: TestContext.Current.CancellationToken);
        SceneInvariants.Check(scene);
        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal("CustomRHMesh", scene.GetName(scene.Meshes[0].Name));
    }

    [Fact]
    public void ShortIntegerDataIsADeliberateErrorNotARawException()
    {
        var rhData = new List<byte>();
        rhData.AddRange(StringBytes("Short"));
        rhData.Add(0x00);
        rhData.AddRange(new byte[] { 0x00, 0x00, 0x00 });
        rhData.Add(0x03); // faces, positions
        rhData.Add(1);
        rhData.Add(3);
        rhData.Add(0x00);
        for (int i = 0; i < 9; i++)
        {
            rhData.AddRange(BitConverter.GetBytes((float)i));
        }

        // Three U16 indices announced, two bytes given.
        rhData.Add(0x04);
        rhData.Add(2);
        rhData.AddRange(new byte[] { 0, 0 });

        byte[] u3d = new TestU3D()
            .ModifierChain("Short", TestU3D.ModelResourceChain, chain =>
            {
                chain.Block(0x00000100, rhData.ToArray());
                chain.ShadingModifier("Short", 1, [["Material_0"]]);
            })
            .ModelNode("Node_Short", "Short", ("", Matrix4x4.Identity))
            .ToArray();

        Assert.IsType<Scene3DException>(Record.Exception(() => U3DReader.Read(u3d, cancellationToken: TestContext.Current.CancellationToken).Dispose()));
    }

    [Fact]
    public void HugeDeclaredCountsAreRefusedBeforeAnythingIsAllocated()
    {
        var rhData = new List<byte>();
        rhData.AddRange(StringBytes("Huge"));
        rhData.Add(0x00);
        rhData.AddRange(new byte[] { 0x00, 0x00, 0x00 });
        rhData.Add(0xC7); // faces, positions, normals; 4-byte counts
        rhData.AddRange(BitConverter.GetBytes(1u));
        rhData.AddRange(BitConverter.GetBytes(3u));
        rhData.AddRange(BitConverter.GetBytes(0xFFFFFFF0u));

        byte[] u3d = new TestU3D()
            .ModifierChain("Huge", TestU3D.ModelResourceChain, chain =>
            {
                chain.Block(0x00000100, rhData.ToArray());
                chain.ShadingModifier("Huge", 1, [["Material_0"]]);
            })
            .ModelNode("Node_Huge", "Huge", ("", Matrix4x4.Identity))
            .ToArray();

        Assert.IsType<Scene3DException>(Record.Exception(() => U3DReader.Read(u3d, cancellationToken: TestContext.Current.CancellationToken).Dispose()));
    }
}
