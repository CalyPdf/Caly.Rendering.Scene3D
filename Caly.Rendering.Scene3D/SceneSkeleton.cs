using System.Buffers;
using System.Numerics;

namespace Caly.Rendering.Scene3D;

/// <summary>
/// One bone of a skeleton. Bones form a tree; a bone's frame starts where its parent ends: at the
/// parent's length along the parent's X axis, plus <see cref="Displacement"/>.
/// </summary>
/// <param name="Name">Index of the bone's name, for <see cref="Scene.GetName"/>.</param>
/// <param name="Parent">Index of the parent bone, which comes before this one; -1 for a root.</param>
/// <param name="Length">How far the bone reaches along its own X axis.</param>
/// <param name="Displacement">Where the bone starts, relative to the end of its parent, in the parent's frame.</param>
/// <param name="Rotation">The bone's orientation relative to its parent.</param>
public readonly record struct SceneBone(int Name, int Parent, float Length, Vector3 Displacement, Quaternion Rotation);

/// <summary>
/// A tree of bones in the pose the model was made in (the reference pose), and what is needed to turn any
/// other pose into the matrices that move a skinned mesh from the reference pose to it.
/// </summary>
public sealed class SceneSkeleton
{
    private readonly SceneBone[] _bones;
    private readonly Matrix4x4[] _referenceInverse;

    /// <param name="bones">The bones, each after its parent; a bone whose parent is not before it is made a root.</param>
    public SceneSkeleton(ReadOnlySpan<SceneBone> bones)
    {
        _bones = bones.ToArray();
        for (int i = 0; i < _bones.Length; i++)
        {
            if ((uint)_bones[i].Parent >= (uint)i)
            {
                _bones[i] = _bones[i] with { Parent = -1 };
            }
        }

        // The reference pose is the skeleton as described; its inverse takes a vertex into each bone's frame.
        _referenceInverse = new Matrix4x4[_bones.Length];
        var world = new Matrix4x4[_bones.Length];
        for (int i = 0; i < _bones.Length; i++)
        {
            SceneBone bone = _bones[i];
            world[i] = BoneWorld(bone.Displacement, bone.Rotation, Vector3.One, bone.Parent < 0 ? null : (_bones[bone.Parent].Length, world[bone.Parent]));
            _referenceInverse[i] = Matrix4x4.Invert(world[i], out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
        }
    }

    /// <summary>The bones, parents first.</summary>
    public ReadOnlySpan<SceneBone> Bones => _bones;

    /// <summary>
    /// Writes, for each bone, the matrix that takes a point of the mesh in the reference pose to where
    /// the pose of <paramref name="motion"/> at <paramref name="seconds"/> of the scene clock puts it
    /// (row-vector convention: <c>moved = point × skin[bone]</c>). A bone the motion has no track for, or
    /// no motion at all, stays in the reference pose.
    /// </summary>
    /// <param name="motion">What moves the bones, or null for the reference pose.</param>
    /// <param name="seconds">Seconds of the scene clock.</param>
    /// <param name="skin">At least as long as <see cref="Bones"/>.</param>
    public void ComputeSkinMatrices(SceneBoneMotion? motion, double seconds, Span<Matrix4x4> skin)
    {
        int count = _bones.Length;
        if (count == 0)
        {
            return;
        }

        if (skin.Length < count)
        {
            throw new ArgumentException("The span is shorter than the skeleton.", nameof(skin));
        }

        Matrix4x4[] world = ArrayPool<Matrix4x4>.Shared.Rent(count);
        try
        {
            double time = motion?.MotionTime(seconds) ?? 0;
            for (int i = 0; i < count; i++)
            {
                SceneBone bone = _bones[i];
                Vector3 location = bone.Displacement, scale = Vector3.One;
                Quaternion rotation = bone.Rotation;

                ReadOnlySpan<SceneKeyFrame> keys = motion is null ? default : motion.Track(i);
                if (!keys.IsEmpty)
                {
                    SceneKeys.Interpolate(keys, time, out Vector3 trackLocation, out Quaternion trackRotation, out Vector3 trackScale);

                    // The track is a change to the reference pose; a scale is the whole scale.
                    Vector3 posed = bone.Displacement + trackLocation;
                    Quaternion turned = Quaternion.Multiply(trackRotation, SceneKeys.SafeNormalize(bone.Rotation));
                    if (IsFinite(posed) && IsFinite(trackScale) && float.IsFinite(turned.X + turned.Y + turned.Z + turned.W))
                    {
                        (location, rotation, scale) = (posed, turned, trackScale);
                    }
                }

                if (bone.Parent < 0 && motion is { RootLocked: true })
                {
                    location = bone.Displacement;
                }

                world[i] = BoneWorld(location, rotation, scale, bone.Parent < 0 ? null : (_bones[bone.Parent].Length, world[bone.Parent]));
                skin[i] = _referenceInverse[i] * world[i];
            }
        }
        finally
        {
            ArrayPool<Matrix4x4>.Shared.Return(world);
        }
    }

    private static Matrix4x4 BoneWorld(Vector3 location, Quaternion rotation, Vector3 scale, (float Length, Matrix4x4 World)? parent)
    {
        // Turned, then scaled, then moved to where it starts: all in the frame at the end of the parent.
        Matrix4x4 local = Matrix4x4.CreateFromQuaternion(SceneKeys.SafeNormalize(rotation)) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(location);
        return parent is { } p ? local * Matrix4x4.CreateTranslation(p.Length, 0, 0) * p.World : local;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X + v.Y + v.Z);
}

/// <summary>
/// The motion of a skeleton: for each bone that has one, a track of keys that change the bone's pose
/// from the reference pose, and how the scene clock maps to theirs.
/// </summary>
public sealed class SceneBoneMotion
{
    private readonly SceneKeyFrame[]?[] _tracks;
    private readonly float _first, _last;

    /// <param name="name">The motion's name.</param>
    /// <param name="tracks">By bone index: the bone's keys in ascending time, or null for a bone the motion leaves alone.</param>
    /// <param name="loop">Whether the motion repeats; otherwise each track holds its last key.</param>
    /// <param name="timeScale">Motion seconds per scene second.</param>
    /// <param name="timeOffset">Scene seconds before the motion starts.</param>
    /// <param name="rootLocked">Whether the root bone stays where the reference pose puts it, however the motion moves it.</param>
    public SceneBoneMotion(string name, SceneKeyFrame[]?[] tracks, bool loop, float timeScale, float timeOffset, bool rootLocked)
    {
        Name = name;
        _tracks = new SceneKeyFrame[]?[tracks.Length];
        _first = float.MaxValue;
        _last = float.MinValue;
        for (int i = 0; i < tracks.Length; i++)
        {
            if (tracks[i] is { Length: > 0 } keys)
            {
                _tracks[i] = (SceneKeyFrame[])keys.Clone();
                _first = Math.Min(_first, keys[0].Time);
                _last = Math.Max(_last, keys[^1].Time);
            }
        }

        if (_first > _last)
        {
            _first = _last = 0;
        }

        Loop = loop;
        TimeScale = float.IsFinite(timeScale) ? timeScale : 1;
        TimeOffset = float.IsFinite(timeOffset) ? timeOffset : 0;
        RootLocked = rootLocked;
    }

    /// <summary>The motion's name.</summary>
    public string Name { get; }

    /// <summary>Whether the motion repeats.</summary>
    public bool Loop { get; }

    /// <summary>Motion seconds per scene second.</summary>
    public float TimeScale { get; }

    /// <summary>Scene seconds before the motion starts.</summary>
    public float TimeOffset { get; }

    /// <summary>Whether the root bone stays in its reference position.</summary>
    public bool RootLocked { get; }

    /// <summary>Seconds from the first key of any track to the last of any, on the motion's clock.</summary>
    public float Duration => _last - _first;

    /// <summary>The keys of a bone, or empty for a bone without a track.</summary>
    public ReadOnlySpan<SceneKeyFrame> Track(int bone) => (uint)bone < (uint)_tracks.Length && _tracks[bone] is { } keys ? keys : default;

    internal double MotionTime(double seconds) => SceneKeys.MotionTime(seconds, _first, _last, Loop, TimeScale, TimeOffset);
}
