using System.Buffers.Binary;
using Caly.Rendering.Scene3D.Tests.Idtf;
using Caly.Rendering.Scene3D.U3D;
using Caly.Rendering.Scene3D.U3D.Internal;

namespace Caly.Rendering.Scene3D.Tests;

public class U3DBlockWalkerTests
{
    [Fact]
    public void IdtfParserReadsBoxBase()
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath("box_base.idtf"));

        IdtfElement model = idtf.Nodes.Single(n => n.Argument == "MODEL");
        Assert.Equal("LightBoxModel", model.Text("RESOURCE_NAME"));
        IdtfElement parent = model.Get("PARENT_LIST").Get("PARENT");
        Assert.Equal(16, parent.Get("PARENT_TM").Numbers.Count);

        IdtfElement mesh = idtf.Resources("MODEL").Single().Get("MESH");
        Assert.Equal(12, mesh.Number("FACE_COUNT"));
        Assert.Equal(8 * 3, mesh.Get("MODEL_POSITION_LIST").Numbers.Count);
        Assert.Equal(-20, mesh.Get("MODEL_POSITION_LIST").Numbers[0]);
        Assert.Equal(12 * 3, mesh.Get("MESH_FACE_POSITION_LIST").Numbers.Count);
    }

    [Fact]
    public void IdtfParserReadsEverySample()
    {
        foreach (string name in TestFiles.SceneNames)
        {
            IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
            Assert.True(idtf.Nodes.Any(), name);
        }
    }

    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void WalkerFindsTheSameBlocksAsTheNaiveWalk(string name)
    {
        byte[] file = TestFiles.Scene(name + ".u3d");
        List<RawBlocks.Block> expected = RawBlocks.Enumerate(file);

        List<U3DBlock> actual = U3DBlockWalker.Walk(file, SceneLimits.Default, CancellationToken.None);

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Type, actual[i].Type);
            Assert.Equal(expected[i].DataStart, actual[i].DataOffset);
            Assert.Equal(expected[i].DataSize, actual[i].DataLength);
            Assert.Equal(expected[i].Nested, actual[i].Depth > 0);
        }
    }

    [Fact]
    public void BlockTotalsMatchTheSurvey()
    {
        var totals = new Dictionary<uint, int>();
        foreach (string name in TestFiles.SceneNames)
        {
            foreach (U3DBlock block in U3DBlockWalker.Walk(TestFiles.Scene(name + ".u3d"), SceneLimits.Default, CancellationToken.None))
            {
                totals[block.Type] = totals.GetValueOrDefault(block.Type) + 1;
            }
        }

        // survey-u3d/u3d-blocks.md, 2026-10-10
        Assert.Equal(54, totals[U3DConstants.BlockFileHeader]);
        Assert.Equal(298, totals[U3DConstants.BlockModifierChain]);
        Assert.Equal(72, totals[U3DConstants.BlockModelNode]);
        Assert.Equal(63, totals[U3DConstants.BlockViewNode]);
        Assert.Equal(62, totals[U3DConstants.BlockLightNode]);
        Assert.Equal(11, totals[U3DConstants.BlockGroupNode]);
        Assert.Equal(59, totals[U3DConstants.BlockMeshDeclaration]);
        Assert.Equal(6, totals[U3DConstants.BlockBaseMeshContinuation]);
        Assert.Equal(60, totals[U3DConstants.BlockProgressiveMeshContinuation]);
        Assert.Equal(72, totals[U3DConstants.BlockShadingModifier]);
        Assert.Equal(73, totals[U3DConstants.BlockLitTextureShader]);
        Assert.Equal(73, totals[U3DConstants.BlockMaterialResource]);
        Assert.Equal(26, totals[U3DConstants.BlockTextureDeclaration]);
        Assert.Equal(29, totals[U3DConstants.BlockTextureContinuation]);
    }

    [Fact]
    public void HeaderOfBoxBase()
    {
        byte[] file = TestFiles.Scene("box_base.u3d");

        U3DHeader header = U3DHeader.Read(file);

        Assert.Equal(0, header.MajorVersion);
        Assert.Equal(0u, header.Profile);
        Assert.False(header.NoCompression);
        Assert.Equal((ulong)file.Length, header.FileSize);
        Assert.Equal(106u, header.CharacterEncoding);
        Assert.Equal(1.0, header.UnitsScale);
    }

    [Fact]
    public void IsU3DChecksTheSignature()
    {
        Assert.True(U3DReader.IsU3D(TestFiles.Scene("box_base.u3d")));
        Assert.False(U3DReader.IsU3D("PRC\0"u8));
        Assert.False(U3DReader.IsU3D("U3"u8));
        Assert.False(U3DReader.IsU3D(default));
    }

    [Fact]
    public void ABlockLongerThanTheFileIsTruncation()
    {
        byte[] file = TestFiles.Scene("box_base.u3d");
        byte[] cut = file.AsSpan(0, file.Length - 40).ToArray();

        var e = Assert.Throws<Scene3DException>(() => U3DBlockWalker.Walk(cut, SceneLimits.Default, CancellationToken.None));

        Assert.Equal(Scene3DError.Truncated, e.Error);
    }

    [Fact]
    public void AFileThatDoesNotStartWithAHeaderIsRejected()
    {
        byte[] file = TestFiles.Scene("box_base.u3d");
        file[0] = (byte)'X';

        var e = Assert.Throws<Scene3DException>(() => U3DHeader.Read(file));
        Assert.Equal(Scene3DError.InvalidValue, e.Error);

        var tooShort = Assert.Throws<Scene3DException>(() => U3DHeader.Read(new byte[10]));
        Assert.Equal(Scene3DError.Truncated, tooShort.Error);
    }

    [Fact]
    public void ModifierChainsNestedTooDeeplyAreRejected()
    {
        // A chain whose only content is itself, repeated: build depth 5 and allow depth 3.
        byte[] inner = [];
        for (int depth = 0; depth < 5; depth++)
        {
            inner = Chain(inner);
        }

        byte[] header = TestFiles.Scene("box_base.u3d").AsSpan(0, 12 + 24).ToArray();
        byte[] file = [.. header, .. inner];

        var e = Assert.Throws<Scene3DException>(() => U3DBlockWalker.Walk(file, SceneLimits.Default with { MaxNestingDepth = 3 }, CancellationToken.None));
        Assert.Equal(Scene3DError.LimitExceeded, e.Error);

        Assert.Equal(6, U3DBlockWalker.Walk(file, SceneLimits.Default, CancellationToken.None).Count);

        static byte[] Chain(byte[] content)
        {
            // name "" (2 bytes), type, attributes, padding to 4, modifier count, content
            byte[] data = new byte[2 + 4 + 4 + 2 + 4 + content.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
            content.CopyTo(data, 16);
            byte[] block = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(block, 0xFFFFFF14);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)data.Length);
            data.CopyTo(block, 12);
            return block;
        }
    }
}
