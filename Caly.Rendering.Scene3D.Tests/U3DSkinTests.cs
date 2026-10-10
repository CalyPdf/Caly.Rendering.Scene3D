using System.Numerics;
using Caly.Rendering.Scene3D.Tests.Idtf;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>Skeletons, bone weights and the motions of bones (ECMA-363 9.6.1.1.6, 9.7.3, 9.7.4, 9.8.6).</summary>
public class U3DSkinTests
{
    private static IdtfElement? Descend(IdtfElement element, string keyword)
    {
        if (element.Find(keyword) is { } found)
        {
            return found;
        }

        foreach (IdtfElement child in element.Children)
        {
            if (Descend(child, keyword) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    // ---- the sample scene with a skeleton, against its IDTF source ----

    [Fact]
    public void TheSkeletonOfTheSampleMatchesItsIdtfSource()
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath("box_ba.idtf"));
        using Scene scene = U3DReader.Read(TestFiles.Scene("box_ba.u3d"), cancellationToken: TestContext.Current.CancellationToken);

        IdtfElement idtfSkeleton = Descend(idtf.Root, "MODEL_SKELETON")!;
        IdtfElement[] idtfBones = idtfSkeleton.All("BONE").ToArray();

        SceneSkeleton skeleton = Assert.Single(scene.Skeletons);
        Assert.Equal(idtfBones.Length, skeleton.Bones.Length);
        for (int i = 0; i < idtfBones.Length; i++)
        {
            SceneBone bone = skeleton.Bones[i];
            Assert.Equal(idtfBones[i].Text("BONE_NAME"), scene.GetName(bone.Name));
            Assert.Equal((float)idtfBones[i].Number("BONE_LENGTH"), bone.Length, 4);
            Assert.True(Vector3.Distance(idtfBones[i].Vector("BONE_DISPLACEMENT"), bone.Displacement) < 1e-4f);

            float[] q = idtfBones[i].Floats("BONE_ORIENTATION");
            Assert.True(MathF.Abs(Quaternion.Dot(new Quaternion(q[1], q[2], q[3], q[0]), bone.Rotation)) > 0.9999f);

            string parent = idtfBones[i].Text("PARENT_BONE_NAME");
            Assert.Equal(i == 0 ? -1 : Array.FindIndex(idtfBones, b => b.Text("BONE_NAME") == parent), bone.Parent);
        }
    }

    [Fact]
    public void EveryVertexOfTheSampleFollowsTheBoneOfItsPosition()
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath("box_ba.idtf"));
        using Scene scene = U3DReader.Read(TestFiles.Scene("box_ba.u3d"), cancellationToken: TestContext.Current.CancellationToken);

        IdtfElement resource = idtf.Resources("MODEL").First(r => r.Text("MODEL_TYPE") == "MESH");
        List<double> n = resource.Get("MESH").Get("MODEL_POSITION_LIST").Numbers;
        var idtfPositions = new Vector3[n.Count / 3];
        for (int i = 0; i < idtfPositions.Length; i++)
        {
            idtfPositions[i] = new Vector3((float)n[i * 3], (float)n[i * 3 + 1], (float)n[i * 3 + 2]);
        }

        IdtfElement[] weightLists = idtf.Modifiers("BONE_WEIGHT").Single().Get("PARAMETERS").Get("POSITION_BONE_WEIGHT_LIST").All("BONE_WEIGHT_LIST").ToArray();
        Assert.Equal(idtfPositions.Length, weightLists.Length);

        SceneMesh mesh = scene.Meshes[0];
        Assert.True(mesh.Skin >= 0);
        SceneSkin skin = scene.Skins[mesh.Skin];
        Assert.Equal(mesh.VertexCount, skin.VertexCount);

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var position = new Vector3(scene.Positions[(mesh.VertexStart + v) * 3], scene.Positions[(mesh.VertexStart + v) * 3 + 1], scene.Positions[(mesh.VertexStart + v) * 3 + 2]);
            int source = Array.FindIndex(idtfPositions, p => Vector3.Distance(p, position) < 1e-4f);
            Assert.True(source >= 0, $"vertex {v} at {position} is at none of the positions");

            IdtfElement list = weightLists[source];
            int expectedBone = (int)list.Get("BONE_INDEX_LIST").Numbers[0];
            Assert.Equal(1, (int)list.Number("BONE_WEIGHT_COUNT"));
            Assert.Equal((uint)expectedBone, skin.Indices[v] & 0xFF);
            Assert.Equal(255u, skin.Weights[v] & 0xFF);
        }
    }

    [Fact]
    public void ThePlayingOfTheSampleMovesTheVerticesOfEachBoneByTheKeysOfItsTrack()
    {
        using Scene scene = U3DReader.Read(TestFiles.Scene("box_ba.u3d"), cancellationToken: TestContext.Current.CancellationToken);
        SceneSkin skin = scene.Skins[0];
        SceneSkeleton skeleton = scene.Skeletons[skin.Skeleton];
        Assert.NotNull(skin.Motion);

        var atStart = new Matrix4x4[4];
        var atOne = new Matrix4x4[4];
        skeleton.ComputeSkinMatrices(skin.Motion, 0, atStart);
        skeleton.ComputeSkinMatrices(skin.Motion, 1, atOne);

        // The sample's keys are slides: at 0 nothing has moved, at 1 the Humerus is 10 along -X (a bone
        // with no rotation, scale or moving parent leaves a vertex a pure translation of its track).
        foreach (Matrix4x4 m in atStart)
        {
            Assert.True(Vector3.Distance(Vector3.Zero, Vector3.Transform(Vector3.Zero, m)) < 1e-3f);
        }

        // (The key sits at 0.9991 in the file: times are quantised.)
        Assert.True(Vector3.Distance(new Vector3(-10, 0, 0), Vector3.Transform(Vector3.Zero, atOne[1])) < 0.05f);

        // A child bone goes with its parent and adds its own: the Ulna slides 10 along Z as well.
        Assert.True(Vector3.Distance(new Vector3(-10, 0, 10), Vector3.Transform(Vector3.Zero, atOne[2])) < 0.05f);
        Assert.True(Vector3.Distance(Vector3.Zero, Vector3.Transform(Vector3.Zero, atOne[0])) < 1e-3f);
    }

    [Fact]
    public void TheMotionOfTheSampleBelongsToItsSkinAndIsNotANodeAnimation()
    {
        using Scene scene = U3DReader.Read(TestFiles.Scene("box_ba.u3d"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(scene.Animations);
        SceneBoneMotion motion = scene.Skins[0].Motion!;
        Assert.Equal("Box01-Key", motion.Name);
        Assert.True(motion.Loop);
        Assert.True(scene.AutoPlayAnimations);
        Assert.False(motion.RootLocked);
        Assert.Equal(2f, motion.Duration, 4);
        for (int bone = 0; bone < 4; bone++)
        {
            Assert.Equal(3, motion.Track(bone).Length);
        }

        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "animation-skipped");
    }

    [Fact]
    public void ASkinnedMeshIsOneMeshOfTheScene()
    {
        using Scene scene = U3DReader.Read(TestFiles.Scene("box_ba.u3d"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(scene.Meshes.ToArray());
        Assert.Equal(0, scene.Meshes[0].Skin);
    }

    // ---- hand-built files ----

    private static readonly Vector3[] Quad = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];

    private static readonly TestU3D.Bone[] TwoBones =
    [
        new("Upper", "", 2, Vector3.Zero, Quaternion.Identity),
        new("Lower", "Upper", 2, new Vector3(0, 1, 0), Quaternion.Identity)
    ];

    private static TestU3D SkinnedQuad(Action<TestU3D> modifiers, TestU3D.Bone[]? bones = null)
    {
        var file = new TestU3D(TestU3D.NoCompression)
            .MeshDeclaration("Quad", 0, 2, 4, 1, 0, 0, 0, [(0, 0)], 4, bones ?? TwoBones)
            .ModelNode("Node", "Quad", ("", Matrix4x4.Identity))
            .BaseMesh("Quad", Quad, [Vector3.UnitZ], [], [], [], [0, 0, 0, 1, 0, 2, 0], [0, 0, 0, 2, 0, 3, 0]);
        modifiers(file);
        return file;
    }

    private static SceneKeyFrame Key(float time, Vector3? location = null) => new(time, location ?? Vector3.Zero, Quaternion.Identity, Vector3.One);

    [Fact]
    public void WeightsAreReadPerPositionAndTheLastOneIsWhatIsLeft()
    {
        // Positions 0 and 1 follow bone 0; 2 and 3 are shared: 0.3 of bone 0, the rest bone 1.
        byte[] file = SkinnedQuad(m => m.BoneWeights("Node", 0.1f, [(0, 1f)], [(0, 1f)], [(0, 0.3f), (1, 0.7f)], [(1, 0.5f), (0, 0.5f)])).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneSkin skin = scene.Skins[scene.Meshes[0].Skin];
        Assert.Equal(4, skin.VertexCount);

        // Vertex 2 is position 2 (the quad's third corner): bones 0 and 1, 0.3 and 0.7, strongest first.
        Assert.Equal(1u, skin.Indices[2] & 0xFF);
        Assert.Equal(0u, (skin.Indices[2] >> 8) & 0xFF);
        Assert.InRange(((skin.Weights[2] & 0xFF) / 255f), 0.69f, 0.71f);
        Assert.InRange((((skin.Weights[2] >> 8) & 0xFF) / 255f), 0.29f, 0.31f);

        // Vertex 3: 0.5 and 0.5, in the order the file gives them (an equal pair keeps its order).
        Assert.Equal(1u, skin.Indices[3] & 0xFF);
        Assert.Equal(0u, (skin.Indices[3] >> 8) & 0xFF);
    }

    [Fact]
    public void ASkinTakesTheMotionOfTheNodesAnimationModifierBoneByBoneName()
    {
        SceneKeyFrame[] upper = [Key(0), Key(1, new Vector3(0, 3, 0))];
        byte[] file = SkinnedQuad(m => m
            .MotionResource("Wave", 0.01f, 0.001f, ("Lower", 0.01f, 0.01f, upper), ("Upper", 0.01f, 0.01f, upper))
            .BoneWeights("Node", 0.1f, [(0, 1f)], [(0, 1f)], [(1, 1f)], [(1, 1f)])
            .AnimationModifier("Node", 0x3, 1, 0, ("Wave", 0x1, 0, 1))).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneSkin skin = scene.Skins[scene.Meshes[0].Skin];
        SceneBoneMotion motion = Assert.IsType<SceneBoneMotion>(skin.Motion);

        // The motion lists "Lower" first, but bone 0 is "Upper": tracks follow the names.
        Assert.Equal(2, motion.Track(0).Length);
        Assert.Equal(2, motion.Track(1).Length);
        Assert.True(motion.Loop);
        Assert.True(motion.RootLocked);          // attribute 0x2
        Assert.True(scene.AutoPlayAnimations);   // attribute 0x1
        Assert.Empty(scene.Animations);
    }

    [Fact]
    public void ATrackOfABoneThatDoesNotExistIsReportedAndLeftOut()
    {
        SceneKeyFrame[] keys = [Key(0), Key(1, new Vector3(0, 3, 0))];
        byte[] file = SkinnedQuad(m => m
            .MotionResource("Wave", 0.01f, 0.001f, ("Upper", 0.01f, 0.01f, keys), ("Tail", 0.01f, 0.01f, keys))
            .BoneWeights("Node", 0.1f, [(0, 1f)], [(0, 1f)], [(1, 1f)], [(1, 1f)])
            .AnimationModifier("Node", 0x1, 1, 0, ("Wave", 0x1, 0, 1))).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneBoneMotion motion = scene.Skins[scene.Meshes[0].Skin].Motion!;
        Assert.Equal(2, motion.Track(0).Length);
        Assert.Empty(motion.Track(1).ToArray());
        Assert.Contains(scene.Diagnostics, d => d.Code == "bone-track-unknown");
    }

    [Fact]
    public void ASkeletonWithWeightsButNoAnimationStaysInItsReferencePose()
    {
        byte[] file = SkinnedQuad(m => m.BoneWeights("Node", 0.1f, [(0, 1f)], [(0, 1f)], [(1, 1f)], [(1, 1f)])).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneSkin skin = scene.Skins[scene.Meshes[0].Skin];
        Assert.Null(skin.Motion);
        Assert.False(scene.AutoPlayAnimations);
    }

    [Fact]
    public void AMeshWithBonesButWithoutWeightsIsAnOrdinaryMesh()
    {
        byte[] file = SkinnedQuad(_ => { }).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(-1, scene.Meshes[0].Skin);
        Assert.Empty(scene.Skins);
    }

    [Fact]
    public void WeightsForAMeshWithoutBonesAreReportedAndIgnored()
    {
        byte[] file = SkinnedQuad(m => m.BoneWeights("Node", 0.1f, [(0, 1f)], [(0, 1f)], [(0, 1f)], [(0, 1f)]), bones: []).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(-1, scene.Meshes[0].Skin);
        Assert.Contains(scene.Diagnostics, d => d.Code == "bone-weights-ignored");
    }

    [Fact]
    public void WeightsForFewerPositionsThanTheMeshHasLeaveTheRestInPlace()
    {
        byte[] file = SkinnedQuad(m => m.BoneWeights("Node", 0.1f, [(1, 1f)])).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneSkin skin = scene.Skins[scene.Meshes[0].Skin];
        Assert.Equal(255u, skin.Weights[0] & 0xFF);
        Assert.Equal(0u, skin.Weights[1]);
        Assert.Equal(0u, skin.Weights[3]);
    }

    [Fact]
    public void ABoneIndexBeyondTheSkeletonIsLeftOut()
    {
        byte[] file = SkinnedQuad(m => m.BoneWeights("Node", 0.1f, [(7, 0.5f), (0, 0.5f)], [(0, 1f)], [(0, 1f)], [(0, 1f)])).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneSkin skin = scene.Skins[scene.Meshes[0].Skin];
        Assert.Equal(0u, skin.Indices[0] & 0xFF);
        Assert.Equal(255u, skin.Weights[0] & 0xFF);
    }

    [Fact]
    public void ABoneWhoseParentIsNotDeclaredIsARoot()
    {
        TestU3D.Bone[] bones = [new("A", "", 1, Vector3.Zero, Quaternion.Identity), new("B", "Nobody", 1, Vector3.Zero, Quaternion.Identity)];
        byte[] file = SkinnedQuad(m => m.BoneWeights("Node", 0.1f, [(0, 1f)], [(1, 1f)], [(1, 1f)], [(1, 1f)]), bones).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(-1, scene.Skeletons[0].Bones[1].Parent);
    }

    [Fact]
    public void ADamagedSetOfWeightsDoesNotSpoilTheMesh()
    {
        var broken = new TestU3D.Data().String("Node").U32(0).U32(1).F32(0.1f).U32(4000000);
        byte[] file = SkinnedQuad(m => m.Block(0xFFFFFF44, broken)).ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(-1, scene.Meshes[0].Skin);
        Assert.Contains(scene.Diagnostics, d => d.Code == "bone-weights-unreadable");
    }

    [Fact]
    public void ASkeletonThatDeclaresMoreBonesThanItsBlockHoldsIsRejected()
    {
        var data = new TestU3D.Data().String("Quad").U32(0).U32(0).U32(2).U32(4).U32(1).U32(0).U32(0).U32(0).U32(1).U32(0).U32(0).U32(0);
        data.U32(4).U32(4);
        data.U32(1000).U32(1000).U32(1000).F32(1).F32(1).F32(1).F32(1).F32(1).F32(0.9f).F32(0.9f).F32(0.985f).U32(0xFFFFFF);
        byte[] file = new TestU3D(TestU3D.NoCompression).Block(0xFFFFFF31, data).ToArray();

        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken)).Error);
    }

    [Fact]
    public void TwoNodesOfOneMeshWithTheirOwnWeightsEachFollowTheirOwn()
    {
        byte[] file = new TestU3D(TestU3D.NoCompression)
            .MeshDeclaration("Quad", 0, 2, 4, 1, 0, 0, 0, [(0, 0)], 4, TwoBones)
            .ModelNode("Left", "Quad", ("", Matrix4x4.Identity))
            .ModelNode("Right", "Quad", ("", Matrix4x4.CreateTranslation(5, 0, 0)))
            .BaseMesh("Quad", Quad, [Vector3.UnitZ], [], [], [], [0, 0, 0, 1, 0, 2, 0], [0, 0, 0, 2, 0, 3, 0])
            .BoneWeights("Left", 0.1f, [(0, 1f)], [(0, 1f)], [(0, 1f)], [(0, 1f)])
            .BoneWeights("Right", 0.1f, [(1, 1f)], [(1, 1f)], [(1, 1f)], [(1, 1f)])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        int left = scene.Nodes.ToArray().First(n => scene.GetName(n.Name) == "Left").Mesh;
        int right = scene.Nodes.ToArray().First(n => scene.GetName(n.Name) == "Right").Mesh;
        Assert.NotEqual(left, right);
        Assert.Equal(0u, scene.Skins[scene.Meshes[left].Skin].Indices[0] & 0xFF);
        Assert.Equal(1u, scene.Skins[scene.Meshes[right].Skin].Indices[0] & 0xFF);
    }
}
