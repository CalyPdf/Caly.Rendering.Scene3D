using System.Numerics;

namespace Caly.Rendering.Scene3D.Tests;

public class SceneAnimationTests
{
    private static SceneKeyFrame Key(float time, Vector3 location, Quaternion? rotation = null, Vector3? scale = null)
    {
        return new SceneKeyFrame(time, location, rotation ?? Quaternion.Identity, scale ?? Vector3.One);
    }

    private static SceneAnimation Slide(bool loop = false, float timeScale = 1, float timeOffset = 0)
    {
        // 0 -> 10 along X in one second, then back to 0 in the next.
        return new SceneAnimation("slide", nodeName: 3, [Key(0, Vector3.Zero), Key(1, new Vector3(10, 0, 0)), Key(2, Vector3.Zero)], loop, timeScale, timeOffset);
    }

    private static void AssertTranslation(float x, Matrix4x4 m)
    {
        Assert.Equal(x, m.M41, 3);
        Assert.Equal(0, m.M42, 3);
        Assert.Equal(0, m.M43, 3);
    }

    [Fact]
    public void LocationsAreInterpolatedLinearlyBetweenKeys()
    {
        SceneAnimation animation = Slide();

        AssertTranslation(0, animation.Sample(0));
        AssertTranslation(2.5f, animation.Sample(0.25));
        AssertTranslation(10, animation.Sample(1));
        AssertTranslation(5, animation.Sample(1.5));
    }

    [Fact]
    public void ADurationIsTheSpanOfTheKeys()
    {
        Assert.Equal(2, Slide().Duration, 5);
        Assert.Equal(0, new SceneAnimation("one", 0, [Key(4, Vector3.Zero)], false, 1, 0).Duration);
    }

    [Fact]
    public void WithoutLoopTheLastKeyHolds()
    {
        SceneAnimation animation = Slide(loop: false);

        AssertTranslation(0, animation.Sample(2));
        AssertTranslation(0, animation.Sample(40));
        AssertTranslation(0, animation.Sample(-5));
    }

    [Fact]
    public void WithLoopTimeWrapsAroundTheKeys()
    {
        SceneAnimation animation = Slide(loop: true);

        AssertTranslation(5, animation.Sample(2.5));
        AssertTranslation(10, animation.Sample(5));
        AssertTranslation(2.5f, animation.Sample(-0.25));
    }

    [Fact]
    public void TimeScaleSpeedsTheMotionUp()
    {
        AssertTranslation(10, Slide(timeScale: 2).Sample(0.5));
    }

    [Fact]
    public void ATimeOffsetDelaysTheStart()
    {
        SceneAnimation animation = Slide(timeOffset: 1);

        AssertTranslation(0, animation.Sample(0.5));
        AssertTranslation(0, animation.Sample(1));
        AssertTranslation(5, animation.Sample(1.5));
    }

    [Fact]
    public void RotationsTurnTheShortWayAndComeOutUnitLength()
    {
        Quaternion quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        // Not unit length, as a lossy file can give it.
        var animation = new SceneAnimation("turn", 0, [Key(0, Vector3.Zero), Key(1, Vector3.Zero, new Quaternion(quarter.X * 1.2f, quarter.Y * 1.2f, quarter.Z * 1.2f, quarter.W * 1.2f))], false, 1, 0);

        Matrix4x4 half = animation.Sample(0.5);
        Vector3 turned = Vector3.TransformNormal(Vector3.UnitX, half);

        // Half of a quarter turn about Z: 45 degrees, and no scaling from the length of the key.
        Assert.Equal(MathF.Cos(MathF.PI / 4), turned.X, 3);
        Assert.Equal(MathF.Sin(MathF.PI / 4), turned.Y, 3);
        Assert.Equal(1, turned.Length(), 3);
    }

    [Fact]
    public void ScaleThenRotateThenTranslateInTheRowVectorConvention()
    {
        Quaternion quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var animation = new SceneAnimation("trs", 0, [Key(0, new Vector3(5, 0, 0), quarter, new Vector3(2, 1, 1))], false, 1, 0);

        Vector3 point = Vector3.Transform(Vector3.UnitX, animation.Sample(0));

        // (1,0,0) scaled to (2,0,0), turned to (0,2,0), moved to (5,2,0).
        Assert.Equal(5, point.X, 3);
        Assert.Equal(2, point.Y, 3);
        Assert.Equal(0, point.Z, 3);
    }

    [Fact]
    public void AnAnimationWithoutKeysDoesNothing()
    {
        Assert.Equal(Matrix4x4.Identity, new SceneAnimation("empty", 0, [], true, 1, 0).Sample(3));
    }

    [Fact]
    public void ANonFiniteTimeIsTheStart()
    {
        AssertTranslation(0, Slide(loop: true).Sample(double.NaN));
        AssertTranslation(0, Slide(loop: true).Sample(double.PositiveInfinity));
    }

    [Fact]
    public void ASceneCarriesItsAnimations()
    {
        using var builder = new SceneBuilder();
        builder.AutoPlayAnimations = true;
        builder.AddAnimation(Slide());
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.Identity, Kind = SceneNodeKind.Group, Mesh = -1, Light = -1, Camera = -1, Name = builder.AddName("root"), Visible = true });
        using Scene scene = builder.Build();

        Assert.Single(scene.Animations);
        Assert.Equal("slide", scene.Animations[0].Name);
        Assert.True(scene.AutoPlayAnimations);
        Assert.Equal(2, scene.AnimationDuration, 5);
    }

    [Fact]
    public void ASceneWithoutAnimationsHasNone()
    {
        using var builder = new SceneBuilder();
        using Scene scene = builder.Build();

        Assert.Empty(scene.Animations);
        Assert.False(scene.AutoPlayAnimations);
        Assert.Equal(0, scene.AnimationDuration);
    }
}
