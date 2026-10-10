using System.Numerics;
using Caly.Rendering.Scene3D.Tests.Idtf;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

public class U3DAnimationTests
{
    /// <summary>
    /// What the IDTF source of a scene says its animations are: every animation modifier whose motion has
    /// a single track (a motion of several tracks moves the bones of a skeleton, not a node).
    /// </summary>
    private static List<(string Node, IdtfElement Track, IdtfElement Info, IdtfElement Modifier)> ExpectedAnimations(IdtfDocument idtf)
    {
        var expected = new List<(string, IdtfElement, IdtfElement, IdtfElement)>();
        foreach (IdtfElement modifier in idtf.Modifiers("ANIMATION"))
        {
            IdtfElement parameters = modifier.Get("PARAMETERS");
            IdtfElement info = parameters.Get("MOTION_INFO_LIST").All("MOTION_INFO").First();
            IdtfElement motion = idtf.Resources("MOTION").First(r => r.Text("RESOURCE_NAME") == info.Text("MOTION_NAME"));
            IdtfElement[] tracks = motion.Get("MOTION_TRACK_LIST").All("MOTION_TRACK").ToArray();
            if (tracks.Length == 1)
            {
                expected.Add((modifier.Text("MODIFIER_NAME"), tracks[0], info, parameters));
            }
        }

        return expected;
    }

    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void AnimationsMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        var expected = ExpectedAnimations(idtf);

        using Scene scene = U3DReader.Read(TestFiles.Scene(name + ".u3d"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected.Count, scene.Animations.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            (string node, IdtfElement track, IdtfElement info, IdtfElement modifier) = expected[i];
            SceneAnimation animation = scene.Animations.Single(a => scene.GetName(a.NodeName) == node);

            Assert.Equal(info.Text("ATTRIBUTE_LOOP") == "TRUE", animation.Loop);
            // The converter that made these files writes the motion's own time scale, but not the modifier's
            // (a TIME_SCALE of 0.01 in the source is 1 in the file) and not the motion's time offset (1.0 in
            // mp_plane64_anim.idtf, 0 in the file): the file decides, so those two are tested on files built here.
            Assert.Equal((float)info.Number("TIME_SCALE"), animation.TimeScale, 5);
            Assert.Equal(modifier.Text("ATTRIBUTE_ANIMATION_PLAYING") == "TRUE", scene.AutoPlayAnimations);

            IdtfElement[] frames = track.Get("KEY_FRAME_LIST").All("KEY_FRAME").ToArray();
            Assert.Equal(frames.Length, animation.Keys.Length);
            for (int k = 0; k < frames.Length; k++)
            {
                SceneKeyFrame key = animation.Keys[k];
                float[] location = frames[k].Floats("KEY_FRAME_DISPLACEMENT"), rotation = frames[k].Floats("KEY_FRAME_ROTATION"), scale = frames[k].Floats("KEY_FRAME_SCALE");

                // The sources are written with one decimal in places, and quantised in the file.
                Assert.Equal(frames[k].Number("KEY_FRAME_TIME"), key.Time, 0.06);
                Assert.Equal(location[0], key.Location.X, 0.06);
                Assert.Equal(location[1], key.Location.Y, 0.06);
                Assert.Equal(location[2], key.Location.Z, 0.06);
                Assert.Equal(scale[0], key.Scale.X, 0.06);
                Assert.Equal(scale[1], key.Scale.Y, 0.06);
                Assert.Equal(scale[2], key.Scale.Z, 0.06);

                // A rotation is only given as a unit quaternion by the sources that mean one.
                var source = new Quaternion(rotation[1], rotation[2], rotation[3], rotation[0]);
                if (MathF.Abs(source.Length() - 1) < 0.02f)
                {
                    Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(source), Quaternion.Normalize(key.Rotation))) > 0.99f, $"key {k} of {node}: {source} against {key.Rotation}");
                }
            }
        }
    }

    [Fact]
    public void AMotionOfSeveralTracksForANodeWithoutASkeletonIsNotPlayed()
    {
        SceneKeyFrame[] keys = [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One), new(1, Vector3.UnitX, Quaternion.Identity, Vector3.One)];
        byte[] file = new TestU3D(TestU3D.NoCompression)
            .GroupNode("Box", (string.Empty, Matrix4x4.Identity))
            .MotionResource("Wave", 0.01f, 0.001f, ("One", 0.01f, 0.01f, keys), ("Two", 0.01f, 0.01f, keys))
            .AnimationModifier("Box", 0x1, 1, 0, ("Wave", 0x1, 0, 1))
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(scene.Animations);
        Assert.Empty(scene.Skins);
        Assert.Contains(scene.Diagnostics, d => d.Code == "animation-skipped");
    }

    [Fact]
    public void AnimationsOfASceneThatAreReadAreNotReportedAsSkipped()
    {
        using Scene scene = U3DReader.Read(TestFiles.Scene("SimpleShapesAnimation.u3d"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, scene.Animations.Count);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "animation-skipped");
    }

    [Fact]
    public void AMotionMovesEveryNodeOfThatName()
    {
        // multiparent_anim hangs Box01 under three parents: three nodes of one name, one animation.
        using Scene scene = U3DReader.Read(TestFiles.Scene("multiparent_anim.u3d"), cancellationToken: TestContext.Current.CancellationToken);

        SceneAnimation animation = scene.Animations.Single(a => scene.GetName(a.NodeName) == "Box01");
        int nodes = scene.Nodes.ToArray().Count(n => n.Name == animation.NodeName);
        Assert.True(nodes >= 3, $"{nodes} nodes");
    }

    private static byte[] AnimatedBox(Action<TestU3D> modifiers, params SceneKeyFrame[] keys)
    {
        var file = new TestU3D(TestU3D.NoCompression);
        file.GroupNode("Box", (string.Empty, Matrix4x4.CreateTranslation(1, 2, 3)));
        file.MotionResource("Slide", timeQuant: 0.01f, rotationQuant: 0.001f, ("Track", 0.01f, 0.01f, keys));
        modifiers(file);
        return file.ToArray();
    }

    private static SceneKeyFrame[] ThreeKeys() =>
    [
        new(0, Vector3.Zero, Quaternion.Identity, Vector3.One),
        new(0.5f, new Vector3(2, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(1, 2, 1)),
        new(1, new Vector3(0, 4, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f), Vector3.One)
    ];

    [Fact]
    public void KeysAreRebuiltFromTheirQuantisedDifferences()
    {
        byte[] file = AnimatedBox(m => m.AnimationModifier("Box", attributes: 0x1, timeScale: 1, blendTime: 0, ("Slide", 0x1, 0, 1)), ThreeKeys());

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneAnimation animation = Assert.Single(scene.Animations);
        Assert.Equal("Slide", animation.Name);
        Assert.Equal(3, animation.Keys.Length);
        Assert.Equal(0.5f, animation.Keys[1].Time, 0.011);
        Assert.Equal(2f, animation.Keys[1].Location.X, 0.011);
        Assert.Equal(2f, animation.Keys[1].Scale.Y, 0.011);
        Assert.Equal(4f, animation.Keys[2].Location.Y, 0.011);
        Assert.True(Quaternion.Dot(animation.Keys[1].Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)) > 0.9999f);
        Assert.True(Quaternion.Dot(animation.Keys[2].Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f)) > 0.9999f);
    }

    [Theory]
    [InlineData(0x0u, false)]
    [InlineData(0x1u, true)]
    public void StartingIsTheModifiersFirstAttribute(uint attributes, bool autoPlay)
    {
        byte[] file = AnimatedBox(m => m.AnimationModifier("Box", attributes, 1, 0, ("Slide", 0x1, 0, 1)), ThreeKeys());

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(autoPlay, scene.AutoPlayAnimations);
        Assert.Single(scene.Animations);
    }

    [Theory]
    [InlineData(0x0u, false)]
    [InlineData(0x1u, true)]
    [InlineData(0x2u, false)]
    [InlineData(0x3u, true)]
    public void LoopIsTheMotionsFirstAttribute(uint motionAttributes, bool loop)
    {
        byte[] file = AnimatedBox(m => m.AnimationModifier("Box", 0x1, 1, 0, ("Slide", motionAttributes, 0, 1)), ThreeKeys());

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(loop, Assert.Single(scene.Animations).Loop);
    }

    [Fact]
    public void ThePlaybackRateIsTheProductOfTheModifiersAndTheMotions()
    {
        byte[] file = AnimatedBox(m => m.AnimationModifier("Box", 0x1, timeScale: 2, 0, ("Slide", 0x1, 0.25f, 3)), ThreeKeys());

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneAnimation animation = Assert.Single(scene.Animations);
        Assert.Equal(6f, animation.TimeScale, 5);
        Assert.Equal(0.25f, animation.TimeOffset, 5);
    }

    [Fact]
    public void AMotionThatDoesNotExistIsReportedAndSkipped()
    {
        byte[] file = AnimatedBox(m => m.AnimationModifier("Box", 0x1, 1, 0, ("Nowhere", 0x1, 0, 1)), ThreeKeys());

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(scene.Animations);
        Assert.Contains(scene.Diagnostics, d => d.Code == "motion-missing");
    }

    [Fact]
    public void AnimatingANodeThatDoesNotExistIsReportedAndSkipped()
    {
        byte[] file = AnimatedBox(m => m.AnimationModifier("Ghost", 0x1, 1, 0, ("Slide", 0x1, 0, 1)), ThreeKeys());

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(scene.Animations);
        Assert.Contains(scene.Diagnostics, d => d.Code == "animated-node-missing");
    }

    [Fact]
    public void ADamagedMotionDoesNotSpoilTheScene()
    {
        // A motion that claims 5000 keys and has none.
        var broken = new TestU3D(TestU3D.NoCompression);
        broken.GroupNode("Box", (string.Empty, Matrix4x4.Identity));
        broken.Block(0xFFFFFF56, new TestU3D.Data().String("Slide").U32(1).F32(1).F32(1).String("T").U32(5000));
        broken.AnimationModifier("Box", 0x1, 1, 0, ("Slide", 0x1, 0, 1));

        using Scene scene = U3DReader.Read(broken.ToArray(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(scene.Animations);
        Assert.Contains(scene.Diagnostics, d => d.Code == "motion-unreadable");
    }

    [Fact]
    public void KeysAreSortedByTimeWhateverTheFileSays()
    {
        SceneKeyFrame[] keys = [new(1, Vector3.Zero, Quaternion.Identity, Vector3.One), new(0.5f, Vector3.Zero, Quaternion.Identity, Vector3.One), new(2, Vector3.Zero, Quaternion.Identity, Vector3.One)];
        byte[] file = AnimatedBox(m => m.AnimationModifier("Box", 0x1, 1, 0, ("Slide", 0x1, 0, 1)), keys);

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneAnimation animation = Assert.Single(scene.Animations);
        for (int i = 1; i < animation.Keys.Length; i++)
        {
            Assert.True(animation.Keys[i].Time >= animation.Keys[i - 1].Time);
        }
    }
}
