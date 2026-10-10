using System.Numerics;

namespace Caly.Rendering.Scene3D;

/// <summary>
/// How a mesh follows a skeleton: for each of the mesh's vertices up to four bones, with weights that
/// sum to one, packed a byte each. A vertex is moved to the weighted mix of where its bones put it.
/// </summary>
public sealed class SceneSkin
{
    private uint[] _indices;
    private uint[] _weights;

    /// <param name="skeleton">Index of the skeleton in <see cref="Scene.Skeletons"/>.</param>
    /// <param name="motion">What moves the skeleton, or null for a skeleton that stays in its reference pose.</param>
    /// <param name="indices">Per vertex, four bone indices, the first in the low byte (<see cref="PackIndices"/>).</param>
    /// <param name="weights">Per vertex, four weights, byte for byte with the indices (<see cref="PackWeights(float, float, float, float)"/>).</param>
    public SceneSkin(int skeleton, SceneBoneMotion? motion, uint[] indices, uint[] weights)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(weights);
        if (indices.Length != weights.Length)
        {
            throw new ArgumentException("There must be as many weights as indices.", nameof(weights));
        }

        Skeleton = skeleton;
        Motion = motion;
        VertexCount = indices.Length;
        _indices = indices;
        _weights = weights;
    }

    /// <summary>Index of the skeleton in <see cref="Scene.Skeletons"/>.</summary>
    public int Skeleton { get; }

    /// <summary>What moves the skeleton, or null.</summary>
    public SceneBoneMotion? Motion { get; }

    /// <summary>Number of vertices of the mesh.</summary>
    public int VertexCount { get; }

    /// <summary>Per vertex, four bone indices, the first in the low byte; empty once the geometry was released.</summary>
    public ReadOnlySpan<uint> Indices => _indices;

    /// <summary>Per vertex, four weights of a byte each, matching <see cref="Indices"/>; empty once the geometry was released.</summary>
    public ReadOnlySpan<uint> Weights => _weights;

    internal void Release()
    {
        _indices = [];
        _weights = [];
    }

    /// <summary>Four bone indices, <paramref name="b0"/> in the low byte.</summary>
    public static uint PackIndices(int b0, int b1, int b2, int b3) => (uint)(b0 & 0xFF) | (uint)(b1 & 0xFF) << 8 | (uint)(b2 & 0xFF) << 16 | (uint)(b3 & 0xFF) << 24;

    /// <summary>Four weights, a byte each (255 is 1), scaled to sum to 255 if they sum to anything.</summary>
    public static uint PackWeights(float w0, float w1, float w2, float w3)
    {
        Span<float> w = [w0, w1, w2, w3];
        return PackWeights(w);
    }

    private static uint PackWeights(Span<float> w)
    {
        float total = 0;
        for (int i = 0; i < 4; i++)
        {
            w[i] = float.IsFinite(w[i]) && w[i] > 0 ? w[i] : 0;
            total += w[i];
        }

        if (!(total > 0))
        {
            return 0;
        }

        Span<int> bytes = stackalloc int[4];
        int sum = 0, largest = 0;
        for (int i = 0; i < 4; i++)
        {
            bytes[i] = (int)MathF.Round(w[i] / total * 255f);
            sum += bytes[i];
            if (bytes[i] > bytes[largest])
            {
                largest = i;
            }
        }

        // Rounding leaves the sum a little off; the strongest bone takes the difference.
        bytes[largest] = Math.Clamp(bytes[largest] + 255 - sum, 0, 255);
        return (uint)bytes[0] | (uint)bytes[1] << 8 | (uint)bytes[2] << 16 | (uint)bytes[3] << 24;
    }

    /// <summary>
    /// Packs any number of influences into the four strongest (bones of index above 255 and weights of
    /// zero or less are left out), their weights scaled to sum to one, strongest first.
    /// </summary>
    public static (uint Indices, uint Weights) Pack(ReadOnlySpan<(int Bone, float Weight)> influences)
    {
        Span<int> bone = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        int kept = 0;
        foreach ((int b, float w) in influences)
        {
            if ((uint)b > 255 || !(w > 0) || !float.IsFinite(w))
            {
                continue;
            }

            // Insert in order of weight; a fifth, weaker than all four, falls off the end.
            int at = kept;
            while (at > 0 && weight[at - 1] < w)
            {
                at--;
            }

            if (at >= 4)
            {
                continue;
            }

            for (int i = Math.Min(kept, 3); i > at; i--)
            {
                bone[i] = bone[i - 1];
                weight[i] = weight[i - 1];
            }

            bone[at] = b;
            weight[at] = w;
            kept = Math.Min(kept + 1, 4);
        }

        for (int i = kept; i < 4; i++)
        {
            bone[i] = 0;
            weight[i] = 0;
        }

        return (PackIndices(bone[0], bone[1], bone[2], bone[3]), PackWeights(weight));
    }

    /// <summary>Where a vertex goes under the skin matrices of <see cref="SceneSkeleton.ComputeSkinMatrices"/>; the same place for a vertex without weights.</summary>
    public static Vector3 TransformPosition(Vector3 position, uint indices, uint weights, ReadOnlySpan<Matrix4x4> skin)
    {
        Vector3 sum = Vector3.Zero;
        float used = 0;
        for (int i = 0; i < 4; i++)
        {
            float w = ((weights >> (8 * i)) & 0xFF) / 255f;
            int bone = (int)((indices >> (8 * i)) & 0xFF);
            if (w > 0 && bone < skin.Length)
            {
                sum += Vector3.Transform(position, skin[bone]) * w;
                used += w;
            }
        }

        return used > 0 ? sum / used : position;
    }
}
