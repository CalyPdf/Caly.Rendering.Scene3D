using System.Numerics;

namespace Caly.Rendering.Scene3D;

/// <summary>One key of an animation: where a node is, and how it is turned and scaled, at a time.</summary>
/// <param name="Time">Seconds on the animation's own clock.</param>
/// <param name="Location">Translation.</param>
/// <param name="Rotation">Rotation; need not be of unit length, it is normalised when used.</param>
/// <param name="Scale">Scale along each axis.</param>
public readonly record struct SceneKeyFrame(float Time, Vector3 Location, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// A motion that moves one node (every node of that name) over time: keys in ascending time, and how the
/// scene's clock maps to theirs.
/// <para>
/// The motion is a change <em>to</em> the node's own transform, not a replacement of it: the node stands
/// where the scene puts it at the first key (which authoring tools give the identity), and a sample is
/// the matrix to apply to the node's local transform, see <see cref="Sample"/>.
/// </para>
/// </summary>
public sealed class SceneAnimation
{
    private readonly SceneKeyFrame[] _keys;

    /// <param name="name">The motion's name.</param>
    /// <param name="nodeName">Index of the animated node's name, for <see cref="Scene.GetName"/>.</param>
    /// <param name="keys">Keys, in ascending time.</param>
    /// <param name="loop">Whether the motion repeats; otherwise it holds its last key.</param>
    /// <param name="timeScale">Motion seconds per scene second.</param>
    /// <param name="timeOffset">Scene seconds before the motion starts.</param>
    public SceneAnimation(string name, int nodeName, ReadOnlySpan<SceneKeyFrame> keys, bool loop, float timeScale, float timeOffset)
    {
        Name = name;
        NodeName = nodeName;
        _keys = keys.ToArray();
        Loop = loop;
        TimeScale = float.IsFinite(timeScale) ? timeScale : 1;
        TimeOffset = float.IsFinite(timeOffset) ? timeOffset : 0;
    }

    /// <summary>The motion's name.</summary>
    public string Name { get; }

    /// <summary>Index of the animated node's name, for <see cref="Scene.GetName"/>.</summary>
    public int NodeName { get; }

    /// <summary>Whether the motion repeats; otherwise it holds its last key.</summary>
    public bool Loop { get; }

    /// <summary>Motion seconds per scene second.</summary>
    public float TimeScale { get; }

    /// <summary>Scene seconds before the motion starts.</summary>
    public float TimeOffset { get; }

    /// <summary>The keys, in ascending time.</summary>
    public ReadOnlySpan<SceneKeyFrame> Keys => _keys;

    /// <summary>Seconds from the first key to the last, on the motion's clock.</summary>
    public float Duration => _keys.Length < 2 ? 0 : _keys[^1].Time - _keys[0].Time;

    /// <summary>
    /// The transform of the motion at <paramref name="seconds"/> of the scene's clock, in the row-vector
    /// convention of <see cref="Matrix4x4"/>: scale, then rotation, then translation. The node's local
    /// transform is <c>Sample(t) × Local</c> while the motion plays: the motion happens in the node's own
    /// space, before its placement.
    /// </summary>
    public Matrix4x4 Sample(double seconds)
    {
        SceneKeyFrame[] keys = _keys;
        if (keys.Length == 0)
        {
            return Matrix4x4.Identity;
        }

        double time = SceneKeys.MotionTime(seconds, keys[0].Time, keys[^1].Time, Loop, TimeScale, TimeOffset);
        SceneKeys.Interpolate(keys, time, out Vector3 location, out Quaternion rotation, out Vector3 scale);
        return SceneKeys.Compose(scale, rotation, location);
    }
}

/// <summary>What animations of nodes and of bones share: mapping the clock to a motion's time, and reading keys.</summary>
internal static class SceneKeys
{
    /// <summary>
    /// The motion's own time at <paramref name="seconds"/> of the scene's clock, within [first, last]
    /// if it loops (wrapped) and unbounded otherwise (the keys hold their ends).
    /// </summary>
    public static double MotionTime(double seconds, double first, double last, bool loop, float timeScale, float timeOffset)
    {
        double time = first;
        if (double.IsFinite(seconds))
        {
            time = (seconds - timeOffset) * timeScale + first;
            if (!double.IsFinite(time))
            {
                time = first;
            }
        }

        if (loop && last > first)
        {
            double span = last - first;
            time = first + ((time - first) % span + span) % span;
        }

        return time;
    }

    /// <summary>The state between the keys at <paramref name="time"/>; the first or last key outside them. Keys must be in ascending time.</summary>
    public static void Interpolate(ReadOnlySpan<SceneKeyFrame> keys, double time, out Vector3 location, out Quaternion rotation, out Vector3 scale)
    {
        if (keys.Length == 1 || time <= keys[0].Time)
        {
            Take(keys[0], out location, out rotation, out scale);
            return;
        }

        if (time >= keys[^1].Time)
        {
            Take(keys[^1], out location, out rotation, out scale);
            return;
        }

        // The first key after the time; keys are few per motion, a binary search is plenty.
        int lo = 1, hi = keys.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (keys[mid].Time > time)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        SceneKeyFrame a = keys[lo - 1], b = keys[lo];
        double gap = b.Time - a.Time;
        float t = gap > 0 ? (float)((time - a.Time) / gap) : 1f;

        rotation = Quaternion.Slerp(SafeNormalize(a.Rotation), SafeNormalize(b.Rotation), t);
        location = Vector3.Lerp(a.Location, b.Location, t);
        scale = Vector3.Lerp(a.Scale, b.Scale, t);
    }

    private static void Take(in SceneKeyFrame key, out Vector3 location, out Quaternion rotation, out Vector3 scale)
    {
        location = key.Location;
        rotation = SafeNormalize(key.Rotation);
        scale = key.Scale;
    }

    public static Matrix4x4 Compose(Vector3 scale, Quaternion rotation, Vector3 location)
    {
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(location);
    }

    public static Quaternion SafeNormalize(Quaternion q)
    {
        float length = q.Length();
        return length > 1e-12f && float.IsFinite(length) ? Quaternion.Normalize(q) : Quaternion.Identity;
    }
}
