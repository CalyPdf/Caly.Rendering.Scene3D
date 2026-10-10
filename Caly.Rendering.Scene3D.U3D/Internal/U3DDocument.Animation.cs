using System.Buffers;
using System.Numerics;
using Caly.Rendering.Scene3D.Internal;

namespace Caly.Rendering.Scene3D.U3D.Internal;

/// <summary>
/// Motion resources, animation modifiers and bone weights (ECMA-363 9.7.3, 9.7.4, 9.8.6). A motion moves a
/// node, or the bones of a skeleton, through keys of location, rotation and scale; the animation modifier
/// of the node's chain says which motion, whether it loops, and how fast. A node whose mesh has a skeleton
/// and bone weights is animated by moving its bones (the mesh follows, see <see cref="AttachSkin"/>);
/// any other node is moved as a whole, by a motion of one track.
/// </summary>
internal sealed partial class U3DDocument
{
    /// <summary>A motion read from its block.</summary>
    private sealed class MotionDeclaration
    {
        public required string Name { get; init; }

        public bool Unreadable { get; init; }

        public string? Problem { get; init; }

        /// <summary>The tracks by the name of the bone or node they move, keys in ascending time.</summary>
        public List<(string Name, SceneKeyFrame[] Keys)> Tracks { get; init; } = [];
    }

    private readonly record struct MotionUse(string Motion, uint Attributes, float TimeOffset, float TimeScale);

    private sealed class AnimationModifierDeclaration
    {
        public required string Name { get; init; }

        /// <summary>0 for a node's chain, 1 for a model resource's.</summary>
        public required int ChainType { get; init; }

        public required uint Attributes { get; init; }

        public required float TimeScale { get; init; }

        public required MotionUse[] Motions { get; init; }

        /// <summary>Set once the modifier moves the bones of a skin; it then does not move a node.</summary>
        public bool MovesBones { get; set; }
    }

    /// <summary>The bone weights of a node's (or a model resource's) chain: for each position of the mesh, its bones and weights, packed.</summary>
    private sealed class BoneWeightsDeclaration
    {
        public required string Name { get; init; }

        public uint[] Indices { get; init; } = [];

        public uint[] Weights { get; init; } = [];

        public string? Problem { get; init; }

        public bool Reported { get; set; }
    }

    /// <summary>What a skinned mesh takes from the chains of its node while it is built.</summary>
    private sealed class SkinSource
    {
        public required int Skeleton { get; init; }

        public required int BoneCount { get; init; }

        public required BoneWeightsDeclaration Weights { get; init; }

        public SceneBoneMotion? Motion { get; init; }
    }

    private const uint AnimationStarts = 0x1;
    private const uint AnimationRootLocked = 0x2;
    private const uint MotionLoops = 0x1;

    /// <summary>A track's keys beyond this are not read (a motion of that length is not an animation).</summary>
    private const int MaxKeyFrames = 1_000_000;

    /// <summary>Most bones one position can name; a position cannot have more influences than the skeleton has bones.</summary>
    private const uint MaxInfluences = 65_535;

    private readonly Dictionary<string, MotionDeclaration> _motions = new(StringComparer.Ordinal);
    private readonly List<AnimationModifierDeclaration> _animationModifiers = [];
    private readonly Dictionary<string, BoneWeightsDeclaration> _nodeBoneWeights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoneWeightsDeclaration> _resourceBoneWeights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _skinnedMeshes = new(StringComparer.Ordinal);

    /// <summary>The skin a mesh being built is to take; null while building a mesh that has none.</summary>
    private SkinSource? _currentSkin;

    // ---- motion resources ----

    private void ReadMotionResource(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        MotionDeclaration motion;
        try
        {
            motion = ReadMotion(ref reader, name);
        }
        catch (Scene3DException e) when (e.Error is Scene3DError.InvalidValue or Scene3DError.Truncated)
        {
            motion = new MotionDeclaration { Name = name, Unreadable = true, Problem = e.Message };
        }

        _motions[name] = motion;
    }

    private MotionDeclaration ReadMotion(ref U3DBitReader reader, string name)
    {
        uint trackCount = reader.ReadU32();
        float timeQuant = reader.ReadF32();
        float rotationQuant = reader.ReadF32();

        // A track is at least a name, a count and two quantisers, and a first and a last key.
        if (trackCount > (uint)reader.RemainingBytes / 14)
        {
            throw Truncated($"U3D motion '{name}' declares {trackCount} tracks, more than its block can hold.");
        }

        var tracks = new List<(string, SceneKeyFrame[])>((int)Math.Min(trackCount, 64));
        for (uint t = 0; t < trackCount; t++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string trackName = ReadName(ref reader);
            tracks.Add((trackName, ReadTrack(ref reader, name, timeQuant, rotationQuant)));
        }

        return new MotionDeclaration { Name = name, Tracks = tracks };
    }

    private SceneKeyFrame[] ReadTrack(ref U3DBitReader reader, string motion, float timeQuant, float rotationQuant)
    {
        uint keyCount = reader.ReadU32();
        float displacementQuant = reader.ReadF32();
        float scaleQuant = reader.ReadF32();
        if (keyCount > MaxKeyFrames)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D motion '{motion}' has {keyCount} keys in a track, more than the limit allows.");
        }

        // The count is a claim of the file, not yet a fact: keys are added as they are read.
        var keys = new List<SceneKeyFrame>((int)Math.Min(keyCount, 256));
        float time = 0;
        Vector3 location = Vector3.Zero, scale = Vector3.One;
        Quaternion rotation = Quaternion.Identity;
        for (uint i = 0; i < keyCount; i++)
        {
            if ((i & (CancellationInterval - 1)) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            // The first and the last key are written in full; each is still a difference from the key before
            // in time, location and scale (the first from the identity), and a rotation as it is.
            bool full = i == 0 || i == keyCount - 1;
            if (full)
            {
                time += reader.ReadF32();
                location += new Vector3(reader.ReadF32(), reader.ReadF32(), reader.ReadF32());
                float w = reader.ReadF32(), x = reader.ReadF32(), y = reader.ReadF32(), z = reader.ReadF32();
                rotation = new Quaternion(x, y, z, w);
                scale += new Vector3(reader.ReadF32(), reader.ReadF32(), reader.ReadF32());
            }
            else
            {
                byte timeSign = reader.ReadCompressedU8(U3DConstants.ContextMotionSignTime);
                float timeStep = timeQuant * reader.ReadCompressedU32(U3DConstants.ContextMotionDiffTime);
                time += (timeSign & 1) != 0 ? -timeStep : timeStep;

                location += ReadDifference(ref reader, U3DConstants.ContextMotionSignDisplacement, U3DConstants.ContextMotionDiffDisplacement, displacementQuant);

                byte rotationSign = reader.ReadCompressedU8(U3DConstants.ContextMotionSignRotation);
                float b = Math.Min(rotationQuant * reader.ReadCompressedU32(U3DConstants.ContextMotionDiffRotation), 1f);
                float c = Math.Min(rotationQuant * reader.ReadCompressedU32(U3DConstants.ContextMotionDiffRotation), 1f);
                float d = Math.Min(rotationQuant * reader.ReadCompressedU32(U3DConstants.ContextMotionDiffRotation), 1f);
                float a = (float)Math.Sqrt(Math.Abs(1.0 - (double)b * b - (double)c * c - (double)d * d));
                var difference = new Quaternion((rotationSign & 2) != 0 ? -b : b, (rotationSign & 4) != 0 ? -c : c, (rotationSign & 8) != 0 ? -d : d, (rotationSign & 1) != 0 ? -a : a);
                rotation = Quaternion.Multiply(rotation, difference);

                scale += ReadDifference(ref reader, U3DConstants.ContextMotionSignScale, U3DConstants.ContextMotionDiffScale, scaleQuant);
            }

            keys.Add(new SceneKeyFrame(time, location, rotation, scale));
        }

        return SortedByTime(keys);
    }

    private static Vector3 ReadDifference(ref U3DBitReader reader, uint signContext, uint differenceContext, float quant)
    {
        byte signs = reader.ReadCompressedU8(signContext);
        float x = quant * reader.ReadCompressedU32(differenceContext);
        float y = quant * reader.ReadCompressedU32(differenceContext);
        float z = quant * reader.ReadCompressedU32(differenceContext);
        return new Vector3((signs & 1) != 0 ? -x : x, (signs & 2) != 0 ? -y : y, (signs & 4) != 0 ? -z : z);
    }

    /// <summary>The keys in ascending time; they come so, and a sampler that searches them needs it.</summary>
    private static SceneKeyFrame[] SortedByTime(List<SceneKeyFrame> keys)
    {
        SceneKeyFrame[] array = keys.ToArray();
        bool sorted = true;
        for (int i = 1; i < array.Length && sorted; i++)
        {
            sorted = !(array[i].Time < array[i - 1].Time);
        }

        if (!sorted)
        {
            // A stable order: keys of the same time stay as they were.
            array = array.Select((key, index) => (key, index)).OrderBy(pair => pair.key.Time).ThenBy(pair => pair.index).Select(pair => pair.key).ToArray();
        }

        return array;
    }

    // ---- animation modifiers ----

    private void ReadAnimationModifier(U3DBlock block, int chainType)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        reader.ReadU32(); // chain index
        uint attributes = reader.ReadU32();
        float timeScale = reader.ReadF32();
        uint motionCount = reader.ReadU32();

        // A motion use is a name, attributes and two numbers.
        if (motionCount > (uint)reader.RemainingBytes / 14)
        {
            throw Truncated($"U3D animation modifier '{name}' declares {motionCount} motions, more than its block can hold.");
        }

        var motions = new MotionUse[motionCount];
        for (int i = 0; i < motions.Length; i++)
        {
            string motion = ReadName(ref reader);
            uint motionAttributes = reader.ReadU32();
            float offset = reader.ReadF32();
            float scale = reader.ReadF32();
            motions[i] = new MotionUse(motion, motionAttributes, offset, scale);
        }

        _animationModifiers.Add(new AnimationModifierDeclaration { Name = name, ChainType = chainType, Attributes = attributes, TimeScale = timeScale, Motions = motions });
    }

    // ---- bone weights ----

    private void ReadBoneWeights(U3DBlock block, int chainType)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        BoneWeightsDeclaration weights;
        try
        {
            weights = ReadWeights(ref reader, name);
        }
        catch (Scene3DException e) when (e.Error is Scene3DError.InvalidValue or Scene3DError.Truncated)
        {
            weights = new BoneWeightsDeclaration { Name = name, Problem = e.Message };
        }

        (chainType == 1 ? _resourceBoneWeights : _nodeBoneWeights)[name] = weights;
    }

    private BoneWeightsDeclaration ReadWeights(ref U3DBitReader reader, string name)
    {
        reader.ReadU32(); // chain index
        reader.ReadU32(); // attributes: the kinds of geometry the weights are for
        float inverseQuant = reader.ReadF32();
        uint positionCount = reader.ReadU32();

        // The count is a claim of the file: positions are added as they are read.
        int capacity = (int)Math.Min(positionCount, 4096);
        var indices = new List<uint>(capacity);
        var weights = new List<uint>(capacity);
        (int Bone, float Weight)[] influences = ArrayPool<(int, float)>.Shared.Rent(16);
        try
        {
            for (uint p = 0; p < positionCount; p++)
            {
                if ((p & (CancellationInterval - 1)) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                uint count = reader.ReadCompressedU32(U3DConstants.ContextBoneWeightCount);
                if (count == 0)
                {
                    indices.Add(0);
                    weights.Add(0);
                    continue;
                }

                if (count > MaxInfluences)
                {
                    throw Invalid($"U3D bone weights '{name}' give a position {count} bones.");
                }

                if (count > influences.Length)
                {
                    ArrayPool<(int, float)>.Shared.Return(influences);
                    influences = ArrayPool<(int, float)>.Shared.Rent((int)count);
                }

                for (int i = 0; i < count; i++)
                {
                    influences[i].Bone = (int)Math.Min(reader.ReadCompressedU32(U3DConstants.ContextBoneWeightBoneId), int.MaxValue);
                }

                // The last weight is not written: it is what the others leave of one.
                float sum = 0;
                for (int i = 0; i < count - 1; i++)
                {
                    float weight = reader.ReadCompressedU32(U3DConstants.ContextBoneWeightWeight) * inverseQuant;
                    influences[i].Weight = weight;
                    sum += weight;
                }

                influences[count - 1].Weight = 1f - sum;

                (uint packedIndices, uint packedWeights) = SceneSkin.Pack(influences.AsSpan(0, (int)count));
                indices.Add(packedIndices);
                weights.Add(packedWeights);
            }
        }
        finally
        {
            ArrayPool<(int, float)>.Shared.Return(influences);
        }

        return new BoneWeightsDeclaration { Name = name, Indices = indices.ToArray(), Weights = weights.ToArray() };
    }

    // ---- skinning a mesh while it is built ----

    /// <summary>
    /// The skin a node's mesh takes, when the node has bone weights and the mesh a skeleton: the weights of
    /// its chain, and the motion of its animation modifier on the bones. False for any other mesh.
    /// </summary>
    private bool TryGetSkinSource(SceneBuilder builder, NodeDeclaration node, MeshDeclaration declaration, out SkinSource? source)
    {
        source = null;
        if (!_nodeBoneWeights.TryGetValue(node.Name, out BoneWeightsDeclaration? weights) && !_resourceBoneWeights.TryGetValue(node.Resource, out weights))
        {
            return false;
        }

        if (declaration.Bones.Length == 0)
        {
            ReportOnce(builder, weights, SceneDiagnosticSeverity.Warning, "bone-weights-ignored", $"U3D bone weights '{weights.Name}' are for a mesh '{declaration.Name}' that has no skeleton.");
            return false;
        }

        if (weights.Problem is not null)
        {
            ReportOnce(builder, weights, SceneDiagnosticSeverity.Warning, "bone-weights-unreadable", $"U3D bone weights '{weights.Name}' cannot be read: {weights.Problem}");
            return false;
        }

        if (declaration.SkeletonIndex < 0)
        {
            declaration.SkeletonIndex = builder.AddSkeleton(BuildSkeleton(builder, declaration));
        }

        source = new SkinSource
        {
            Skeleton = declaration.SkeletonIndex,
            BoneCount = declaration.Bones.Length,
            Weights = weights,
            Motion = BuildBoneMotion(builder, node, declaration)
        };
        return true;
    }

    private static void ReportOnce(SceneBuilder builder, BoneWeightsDeclaration weights, SceneDiagnosticSeverity severity, string code, string message)
    {
        if (!weights.Reported)
        {
            weights.Reported = true;
            builder.Report(severity, code, message);
        }
    }

    private static SceneSkeleton BuildSkeleton(SceneBuilder builder, MeshDeclaration declaration)
    {
        BoneDeclaration[] bones = declaration.Bones;
        var result = new SceneBone[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            // The parent is the bone of that name; one that comes later or is not declared makes a root.
            int parent = -1;
            if (bones[i].Parent.Length != 0)
            {
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(bones[j].Name, bones[i].Parent, StringComparison.Ordinal))
                    {
                        parent = j;
                        break;
                    }
                }
            }

            result[i] = new SceneBone(builder.AddName(bones[i].Name), parent, bones[i].Length, bones[i].Displacement, bones[i].Rotation);
        }

        return new SceneSkeleton(result);
    }

    /// <summary>The motion of the animation modifier of a node's chain (or of its mesh's), on the bones of its skeleton, by bone name.</summary>
    private SceneBoneMotion? BuildBoneMotion(SceneBuilder builder, NodeDeclaration node, MeshDeclaration declaration)
    {
        AnimationModifierDeclaration? modifier = null;
        foreach (AnimationModifierDeclaration candidate in _animationModifiers)
        {
            if ((candidate.ChainType == 0 && candidate.Name == node.Name) || (candidate.ChainType == 1 && candidate.Name == node.Resource))
            {
                modifier = candidate;
                if (candidate.ChainType == 0)
                {
                    break;
                }
            }
        }

        if (modifier is null || modifier.Motions.Length == 0)
        {
            return null;
        }

        MotionUse use = modifier.Motions[0];
        if (modifier.Motions.Length > 1)
        {
            builder.Report(SceneDiagnosticSeverity.Info, "animation-partial", $"U3D animation of '{modifier.Name}' queues {modifier.Motions.Length} motions; only the first is played.");
        }

        if (!_motions.TryGetValue(use.Motion, out MotionDeclaration? motion))
        {
            builder.Report(SceneDiagnosticSeverity.Warning, "motion-missing", $"U3D animation of '{modifier.Name}' names a motion '{use.Motion}' that does not exist.");
            return null;
        }

        if (motion.Unreadable)
        {
            builder.Report(SceneDiagnosticSeverity.Warning, "motion-unreadable", $"U3D motion '{motion.Name}' cannot be read: {motion.Problem}");
            return null;
        }

        modifier.MovesBones = true;
        builder.AutoPlayAnimations |= (modifier.Attributes & AnimationStarts) != 0;

        var tracks = new SceneKeyFrame[]?[declaration.Bones.Length];
        foreach ((string trackName, SceneKeyFrame[] keys) in motion.Tracks)
        {
            int bone = Array.FindIndex(declaration.Bones, b => string.Equals(b.Name, trackName, StringComparison.Ordinal));
            if (bone < 0)
            {
                builder.Report(SceneDiagnosticSeverity.Info, "bone-track-unknown", $"U3D motion '{motion.Name}' has a track '{trackName}' for a bone that does not exist.");
            }
            else if (tracks[bone] is null && keys.Length > 0)
            {
                tracks[bone] = keys;
            }
        }

        // The overall rate is the modifier's times the motion's, as the reference player has it.
        return new SceneBoneMotion(motion.Name, tracks, (use.Attributes & MotionLoops) != 0, modifier.TimeScale * use.TimeScale, use.TimeOffset, (modifier.Attributes & AnimationRootLocked) != 0);
    }

    /// <summary>Gives the mesh being built the skin of <see cref="_currentSkin"/>, if there is one: per unified vertex, the bones of its position.</summary>
    private void AttachSkin(SceneBuilder builder, ReadOnlySpan<int> tuples, int vertexCount)
    {
        if (_currentSkin is not { } source)
        {
            return;
        }

        uint[] positionIndices = source.Weights.Indices, positionWeights = source.Weights.Weights;
        var indices = new uint[vertexCount];
        var weights = new uint[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            int position = tuples[v * VertexUnifier.Stride];
            if ((uint)position >= (uint)positionIndices.Length)
            {
                continue;
            }

            (indices[v], weights[v]) = WithinTheSkeleton(positionIndices[position], positionWeights[position], source.BoneCount);
        }

        builder.SetMeshSkin(builder.AddSkin(new SceneSkin(source.Skeleton, source.Motion, indices, weights)));
    }

    /// <summary>The influences of a position without the bones the skeleton does not have, the rest scaled to one.</summary>
    private static (uint Indices, uint Weights) WithinTheSkeleton(uint indices, uint weights, int boneCount)
    {
        bool outside = false;
        for (int i = 0; i < 4 && !outside; i++)
        {
            outside = ((weights >> (8 * i)) & 0xFF) != 0 && (int)((indices >> (8 * i)) & 0xFF) >= boneCount;
        }

        if (!outside)
        {
            return (indices, weights);
        }

        Span<(int Bone, float Weight)> kept = stackalloc (int, float)[4];
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            int bone = (int)((indices >> (8 * i)) & 0xFF);
            float weight = ((weights >> (8 * i)) & 0xFF) / 255f;
            if (weight > 0 && bone < boneCount)
            {
                kept[count++] = (bone, weight);
            }
        }

        return SceneSkin.Pack(kept[..count]);
    }

    // ---- node animations, and what could not be played ----

    /// <summary>Hands the scene the animations of nodes it has, and says what could not be.</summary>
    private void BuildAnimations(SceneBuilder builder)
    {
        foreach (AnimationModifierDeclaration modifier in _animationModifiers)
        {
            if (modifier.MovesBones)
            {
                continue;
            }

            if (modifier.ChainType != 0)
            {
                // The chain of a model resource: its bones move through the nodes that use it, if any has weights.
                builder.Report(SceneDiagnosticSeverity.Info, "animation-skipped", $"U3D animation of the model resource '{modifier.Name}' moves no skinned mesh; it is not played.");
                continue;
            }

            if (!_nodes.ContainsKey(modifier.Name))
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "animated-node-missing", $"U3D animation modifier names a node '{modifier.Name}' that does not exist.");
                continue;
            }

            if (modifier.Motions.Length == 0)
            {
                builder.Report(SceneDiagnosticSeverity.Info, "animation-skipped", $"U3D animation of '{modifier.Name}' names no motion.");
                continue;
            }

            // The first motion plays; the others would be queued behind it by a player that blends.
            MotionUse use = modifier.Motions[0];
            if (modifier.Motions.Length > 1)
            {
                builder.Report(SceneDiagnosticSeverity.Info, "animation-partial", $"U3D animation of '{modifier.Name}' queues {modifier.Motions.Length} motions; only the first is played.");
            }

            if (!_motions.TryGetValue(use.Motion, out MotionDeclaration? motion))
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "motion-missing", $"U3D animation of '{modifier.Name}' names a motion '{use.Motion}' that does not exist.");
                continue;
            }

            if (motion.Unreadable)
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "motion-unreadable", $"U3D motion '{motion.Name}' cannot be read: {motion.Problem}");
                continue;
            }

            if (motion.Tracks.Count != 1)
            {
                builder.Report(SceneDiagnosticSeverity.Info, "animation-skipped", $"U3D motion '{motion.Name}' has {motion.Tracks.Count} tracks, which move the bones of a skeleton the node does not have; it is not played.");
                continue;
            }

            SceneKeyFrame[] keys = motion.Tracks[0].Keys;
            if (keys.Length == 0)
            {
                continue;
            }

            // The overall rate is the modifier's times the motion's, as the reference player has it.
            builder.AddAnimation(new SceneAnimation(motion.Name, builder.AddName(modifier.Name), keys, (use.Attributes & MotionLoops) != 0, modifier.TimeScale * use.TimeScale, use.TimeOffset));
            builder.AutoPlayAnimations |= (modifier.Attributes & AnimationStarts) != 0;
        }
    }
}
