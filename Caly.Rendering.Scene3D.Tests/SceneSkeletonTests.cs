using System.Numerics;

namespace Caly.Rendering.Scene3D.Tests;

public class SceneSkeletonTests
{
    private static readonly Quaternion Turn90Z = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

    private static SceneBone Bone(int parent, float length, Vector3? displacement = null, Quaternion? rotation = null)
    {
        return new SceneBone(Name: 0, parent, length, displacement ?? Vector3.Zero, rotation ?? Quaternion.Identity);
    }

    /// <summary>Two bones end to end along X: the root is 5 long, the second starts where it ends.</summary>
    private static SceneSkeleton Arm() => new([Bone(-1, 5), Bone(0, 5)]);

    private static SceneKeyFrame Key(float time, Vector3? location = null, Quaternion? rotation = null, Vector3? scale = null)
    {
        return new SceneKeyFrame(time, location ?? Vector3.Zero, rotation ?? Quaternion.Identity, scale ?? Vector3.One);
    }

    private static SceneBoneMotion Motion(SceneKeyFrame[]?[] tracks, bool loop = false, float timeScale = 1, float timeOffset = 0, bool rootLocked = false)
    {
        return new SceneBoneMotion("move", tracks, loop, timeScale, timeOffset, rootLocked);
    }

    private static Matrix4x4[] Pose(SceneSkeleton skeleton, SceneBoneMotion? motion, double time)
    {
        var skin = new Matrix4x4[skeleton.Bones.Length];
        skeleton.ComputeSkinMatrices(motion, time, skin);
        return skin;
    }

    private static void AssertPoint(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void TheReferencePoseDoesNotMoveAnything()
    {
        Matrix4x4[] skin = Pose(Arm(), null, 3);

        foreach (Matrix4x4 m in skin)
        {
            AssertPoint(new Vector3(7, 2, -1), Vector3.Transform(new Vector3(7, 2, -1), m));
        }
    }

    [Fact]
    public void ABoneWithoutATrackStaysInTheReferencePose()
    {
        SceneBoneMotion motion = Motion([null, null]);

        Matrix4x4[] skin = Pose(Arm(), motion, 1);

        AssertPoint(new Vector3(8, 1, 1), Vector3.Transform(new Vector3(8, 1, 1), skin[1]));
    }

    [Fact]
    public void TurningTheRootCarriesItsChild()
    {
        // The root turns a quarter about Z: the second bone, which starts at x = 5, now starts at y = 5.
        SceneBoneMotion motion = Motion([[Key(0), Key(1, rotation: Turn90Z)], null]);

        Matrix4x4[] skin = Pose(Arm(), motion, 1);

        AssertPoint(new Vector3(0, 5, 0), Vector3.Transform(new Vector3(5, 0, 0), skin[1]));
        AssertPoint(new Vector3(0, 6, 0), Vector3.Transform(new Vector3(6, 0, 0), skin[1]));
        AssertPoint(new Vector3(0, 2, 0), Vector3.Transform(new Vector3(2, 0, 0), skin[0]));
    }

    [Fact]
    public void ATracksRotationIsAppliedBeforeTheBonesOwnOrientation()
    {
        // The bone's own orientation is a quarter about Z; a track of the identity leaves it there.
        var skeleton = new SceneSkeleton([Bone(-1, 5, rotation: Turn90Z)]);
        SceneBoneMotion motion = Motion([[Key(0), Key(1)]]);

        Matrix4x4[] skin = Pose(skeleton, motion, 0.5);

        AssertPoint(new Vector3(3, 4, 0), Vector3.Transform(new Vector3(3, 4, 0), skin[0]));
    }

    [Fact]
    public void ATracksLocationIsAddedToTheBonesReferenceDisplacement()
    {
        var skeleton = new SceneSkeleton([Bone(-1, 5, displacement: new Vector3(0, 2, 0))]);
        SceneBoneMotion motion = Motion([[Key(0), Key(1, location: new Vector3(4, 0, 0))]]);

        Matrix4x4[] skin = Pose(skeleton, motion, 1);

        AssertPoint(new Vector3(4, 0, 0), Vector3.Transform(Vector3.Zero, skin[0]));
        AssertPoint(new Vector3(2, 0, 0), Vector3.Transform(Vector3.Zero, Pose(skeleton, motion, 0.5)[0]));
    }

    [Fact]
    public void AReferenceDisplacementPlacesTheBoneInTheModel()
    {
        // The second bone starts 3 up from the end of the first (x = 5, y = 3); sliding it by 2 along X moves
        // a vertex at its start by 2.
        var skeleton = new SceneSkeleton([Bone(-1, 5), Bone(0, 4, displacement: new Vector3(0, 3, 0))]);
        SceneBoneMotion motion = Motion([null, [Key(0), Key(1, location: new Vector3(2, 0, 0))]]);

        Matrix4x4[] skin = Pose(skeleton, motion, 1);

        AssertPoint(new Vector3(7, 3, 0), Vector3.Transform(new Vector3(5, 3, 0), skin[1]));
    }

    [Fact]
    public void ScaleStretchesTheBonesGeometryAboutItsStart()
    {
        var skeleton = new SceneSkeleton([Bone(-1, 5)]);
        SceneBoneMotion motion = Motion([[Key(0), Key(1, scale: new Vector3(2, 1, 1))]]);

        Matrix4x4[] skin = Pose(skeleton, motion, 1);

        AssertPoint(new Vector3(6, 1, 0), Vector3.Transform(new Vector3(3, 1, 0), skin[0]));
    }

    [Fact]
    public void ALockedRootKeepsItsReferenceDisplacement()
    {
        var skeleton = new SceneSkeleton([Bone(-1, 5), Bone(0, 5)]);
        SceneKeyFrame[] slide = [Key(0), Key(1, location: new Vector3(4, 0, 0))];

        Matrix4x4[] free = Pose(skeleton, Motion([slide, slide]), 1);
        Matrix4x4[] locked = Pose(skeleton, Motion([slide, slide], rootLocked: true), 1);

        AssertPoint(new Vector3(4, 0, 0), Vector3.Transform(Vector3.Zero, free[0]));
        AssertPoint(Vector3.Zero, Vector3.Transform(Vector3.Zero, locked[0]));

        // The second bone still moves by its own track, from where the locked root left it.
        AssertPoint(new Vector3(4, 0, 0), Vector3.Transform(new Vector3(5, 0, 0), locked[1]) - new Vector3(5, 0, 0));
    }

    [Fact]
    public void TimeWrapsOverTheWholeMotionWhenItLoops()
    {
        SceneBoneMotion motion = Motion([[Key(0), Key(2, location: new Vector3(8, 0, 0))]], loop: true);
        var skeleton = new SceneSkeleton([Bone(-1, 5)]);

        AssertPoint(new Vector3(2, 0, 0), Vector3.Transform(Vector3.Zero, Pose(skeleton, motion, 2.5)[0]));
        Assert.Equal(2, motion.Duration, 5);
    }

    [Fact]
    public void WithoutLoopTheLastKeyHolds()
    {
        SceneBoneMotion motion = Motion([[Key(0), Key(2, location: new Vector3(8, 0, 0))]]);
        var skeleton = new SceneSkeleton([Bone(-1, 5)]);

        AssertPoint(new Vector3(8, 0, 0), Vector3.Transform(Vector3.Zero, Pose(skeleton, motion, 50)[0]));
    }

    [Fact]
    public void ATimeScaleAndOffsetMapTheScenesClockToTheMotions()
    {
        SceneBoneMotion motion = Motion([[Key(0), Key(2, location: new Vector3(8, 0, 0))]], timeScale: 2, timeOffset: 1);
        var skeleton = new SceneSkeleton([Bone(-1, 5)]);

        // Clock 1.5: half a second after the start, at twice the rate: motion time 1.
        AssertPoint(new Vector3(4, 0, 0), Vector3.Transform(Vector3.Zero, Pose(skeleton, motion, 1.5)[0]));
    }

    [Fact]
    public void ABoneWhoseParentComesLaterIsARoot()
    {
        var skeleton = new SceneSkeleton([Bone(1, 5), Bone(-1, 5)]);

        Assert.Equal(-1, skeleton.Bones[0].Parent);
    }

    [Fact]
    public void ASkeletonOfNoBonesIsFine()
    {
        var skeleton = new SceneSkeleton([]);

        skeleton.ComputeSkinMatrices(null, 0, []);
        Assert.Empty(skeleton.Bones.ToArray());
    }

    [Fact]
    public void NonFiniteKeysLeaveTheBoneInItsReferencePose()
    {
        SceneBoneMotion motion = Motion([[Key(0), Key(1, location: new Vector3(float.NaN, 0, 0))]]);
        var skeleton = new SceneSkeleton([Bone(-1, 5)]);

        Matrix4x4 m = Pose(skeleton, motion, 1)[0];

        Assert.True(float.IsFinite(m.M41) && float.IsFinite(m.M11));
    }

    [Fact]
    public void ASkinMixesTheMatricesOfItsBonesByWeight()
    {
        Matrix4x4[] matrices = [Matrix4x4.CreateTranslation(10, 0, 0), Matrix4x4.CreateTranslation(0, 20, 0)];
        uint indices = SceneSkin.PackIndices(0, 1, 0, 0), weights = SceneSkin.PackWeights(0.75f, 0.25f, 0, 0);

        Vector3 moved = SceneSkin.TransformPosition(Vector3.Zero, indices, weights, matrices);

        // Weights are kept in a byte each: 0.75 is 191/255.
        Assert.True(Vector3.Distance(new Vector3(7.5f, 5, 0), moved) < 0.05f, moved.ToString());
    }

    [Fact]
    public void AVertexWithNoWeightsStaysWhereItIs()
    {
        Vector3 moved = SceneSkin.TransformPosition(new Vector3(1, 2, 3), 0, 0, [Matrix4x4.CreateTranslation(10, 0, 0)]);

        AssertPoint(new Vector3(1, 2, 3), moved);
    }

    [Fact]
    public void WeightsAreKeptToFourBonesAndShareOneWhole()
    {
        // Five influences: the weakest goes, the rest are scaled to sum to one.
        (uint indices, uint weights) = SceneSkin.Pack([(0, 0.4f), (1, 0.3f), (2, 0.15f), (3, 0.1f), (4, 0.05f)]);

        float total = 0;
        for (int i = 0; i < 4; i++)
        {
            total += ((weights >> (8 * i)) & 0xFF) / 255f;
        }

        Assert.Equal(1f, total, 2);
        Assert.Equal(0x03020100u, indices);
    }

    [Fact]
    public void ASceneCarriesItsSkeletonsAndSkins()
    {
        using var builder = new SceneBuilder();
        int skeleton = builder.AddSkeleton(Arm());
        builder.BeginMesh(builder.AddName("tri"), SceneMeshFlags.None);
        new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }.CopyTo(builder.AllocateVertices(3).Positions);
        new uint[] { 0, 1, 2 }.CopyTo(builder.AllocateIndices(3));
        builder.AddPart(3, 0);
        int skin = builder.AddSkin(new SceneSkin(skeleton, null, [SceneSkin.PackIndices(0, 0, 0, 0), SceneSkin.PackIndices(1, 0, 0, 0), SceneSkin.PackIndices(1, 0, 0, 0)], [SceneSkin.PackWeights(1, 0, 0, 0), SceneSkin.PackWeights(1, 0, 0, 0), SceneSkin.PackWeights(1, 0, 0, 0)]));
        builder.SetMeshSkin(skin);
        builder.EndMesh();
        using Scene scene = builder.Build();

        Assert.Single(scene.Skeletons);
        Assert.Single(scene.Skins);
        Assert.Equal(0, scene.Meshes[0].Skin);
        Assert.Equal(2, scene.Skeletons[0].Bones.Length);
        Assert.Equal(3, scene.Skins[0].VertexCount);
    }

    [Fact]
    public void ASceneIsAnimatedWhenItsSkinHasAMotion()
    {
        using Scene plain = BuildTriangle();
        Assert.False(plain.IsAnimated);

        using var builder = new SceneBuilder();
        int skeleton = builder.AddSkeleton(Arm());
        builder.BeginMesh(builder.AddName("tri"), SceneMeshFlags.None);
        new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }.CopyTo(builder.AllocateVertices(3).Positions);
        new uint[] { 0, 1, 2 }.CopyTo(builder.AllocateIndices(3));
        builder.AddPart(3, 0);
        SceneBoneMotion motion = Motion([[Key(0), Key(2, location: new Vector3(1, 0, 0))], null], timeScale: 0.5f, timeOffset: 1);
        builder.SetMeshSkin(builder.AddSkin(new SceneSkin(skeleton, motion, new uint[3], new uint[3])));
        builder.EndMesh();
        using Scene animated = builder.Build();

        Assert.True(animated.IsAnimated);

        // One start delay of 1 s, then 2 motion seconds at half rate.
        Assert.Equal(5, animated.AnimationDuration, 5);
    }

    [Fact]
    public void AMeshWithoutASkinSaysSo()
    {
        using Scene scene = BuildTriangle();

        Assert.Equal(-1, scene.Meshes[0].Skin);
        Assert.Empty(scene.Skins);
    }

    private static Scene BuildTriangle()
    {
        using var builder = new SceneBuilder();
        builder.BeginMesh(builder.AddName("tri"), SceneMeshFlags.None);
        new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }.CopyTo(builder.AllocateVertices(3).Positions);
        new uint[] { 0, 1, 2 }.CopyTo(builder.AllocateIndices(3));
        builder.AddPart(3, 0);
        builder.EndMesh();
        return builder.Build();
    }
}
