using System.Buffers;
using Caly.Rendering.Scene3D.Internal;

namespace Caly.Rendering.Scene3D.U3D.Internal;

internal sealed partial class U3DDocument
{
    /// <summary>What the faces using one shading description carry per corner besides position and normal.</summary>
    private readonly record struct MeshShading(bool Diffuse, bool Specular, int Layers);

    private readonly record struct BoneDeclaration(string Name, string Parent, float Length, System.Numerics.Vector3 Displacement, System.Numerics.Quaternion Rotation);

    /// <summary>A CLOD mesh generator as declared; its geometry is decoded when a node first needs it.</summary>
    private sealed class MeshDeclaration
    {
        public required string Name { get; init; }

        /// <summary>Faces carry no normal index.</summary>
        public required bool ExcludeNormals { get; init; }

        /// <summary>Faces the mesh declares for its full detail.</summary>
        public required uint FaceCount { get; init; }

        public required MeshShading[] Shadings { get; init; }

        // What a step of a quantised difference is worth: positions, normals, texture coordinates, diffuse colours.
        public float PositionQuant { get; init; }

        public float NormalQuant { get; init; }

        public float TexCoordQuant { get; init; }

        public float DiffuseQuant { get; init; }

        /// <summary>The skeleton description (ECMA-363 9.6.1.1.6); empty for a mesh without bones.</summary>
        public BoneDeclaration[] Bones { get; init; } = [];

        /// <summary>Index of the mesh's skeleton in the scene once it is added; -1 before.</summary>
        public int SkeletonIndex { get; set; } = -1;

        /// <summary>Index of the decoded mesh in the scene, -1 when it has no geometry, or <see cref="NotBuilt"/>.</summary>
        public int SceneMesh { get; set; } = NotBuilt;
    }

    private const int NotBuilt = -2;

    private const uint MeshExcludesNormals = 0x1;
    private const uint ShadingHasDiffuse = 0x1;
    private const uint ShadingHasSpecular = 0x2;

    /// <summary>A shader has eight channels, so a face can carry at most eight texture coordinates per corner.</summary>
    private const uint MaxTextureLayers = 8;

    // A shading description costs at least its attributes, layer count and original id.
    private const int MinimumShadingBytes = 12;

    private const int CancellationInterval = 4096;

    private readonly Dictionary<string, MeshDeclaration> _meshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, U3DBlock> _baseMeshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<U3DBlock>> _progressiveMeshes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingResources = new(StringComparer.Ordinal);

    /// <summary>ECMA-363 9.6.1.1, up to the shading descriptions: all the base mesh needs.</summary>
    private void ReadMeshDeclaration(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        reader.ReadU32(); // chain index
        uint attributes = reader.ReadU32();
        uint faceCount = reader.ReadU32();
        reader.ReadU32(); // position count
        reader.ReadU32(); // normal count
        reader.ReadU32(); // diffuse colour count
        reader.ReadU32(); // specular colour count
        reader.ReadU32(); // texture coordinate count

        uint shadingCount = reader.ReadU32();
        if (shadingCount > (uint)(reader.RemainingBytes / MinimumShadingBytes))
        {
            throw Truncated($"U3D mesh '{name}' declares {shadingCount} shading descriptions, more than its block can hold.");
        }

        var shadings = new MeshShading[shadingCount];
        for (int i = 0; i < shadings.Length; i++)
        {
            uint shadingAttributes = reader.ReadU32();
            uint layers = reader.ReadU32();
            if (layers > MaxTextureLayers)
            {
                throw Invalid($"U3D mesh '{name}' declares {layers} texture layers for one shading; the format has {MaxTextureLayers}.");
            }

            for (uint layer = 0; layer < layers; layer++)
            {
                reader.ReadU32(); // texture coordinate dimensions; the first two are used
            }

            reader.ReadU32(); // original shading id
            shadings[i] = new MeshShading((shadingAttributes & ShadingHasDiffuse) != 0, (shadingAttributes & ShadingHasSpecular) != 0, (int)layers);
        }

        // The range of resolutions (two values), three quality factors, then what the quantised
        // differences of progressive detail are multiplied by. A declaration cut short has no detail.
        float positionQuant = 0, normalQuant = 0, texCoordQuant = 0, diffuseQuant = 0;
        if (reader.RemainingBytes >= 36)
        {
            reader.ReadU32(); // minimum resolution
            reader.ReadU32(); // final maximum resolution
            reader.ReadU32(); // position quality factor
            reader.ReadU32(); // normal quality factor
            reader.ReadU32(); // texture coordinate quality factor
            positionQuant = reader.ReadF32();
            normalQuant = reader.ReadF32();
            texCoordQuant = reader.ReadF32();
            diffuseQuant = reader.ReadF32();
        }

        // The specular quantisation and the normal parameters follow, then the skeleton.
        BoneDeclaration[] bones = [];
        if (reader.RemainingBytes >= 16 + 4)
        {
            reader.ReadF32();
            reader.ReadF32();
            reader.ReadF32();
            reader.ReadF32();
            bones = ReadBones(ref reader, name);
        }

        _meshes[name] = new MeshDeclaration
        {
            Name = name,
            ExcludeNormals = (attributes & MeshExcludesNormals) != 0,
            FaceCount = faceCount,
            Shadings = shadings,
            PositionQuant = positionQuant,
            NormalQuant = normalQuant,
            TexCoordQuant = texCoordQuant,
            DiffuseQuant = diffuseQuant,
            Bones = bones
        };
    }

    /// <summary>
    /// The skeleton description: for each bone its name and its parent's, then (as the reference decoder
    /// reads them) attributes, length, displacement and orientation, links and joints if the attributes say
    /// so, and six rotation limits that are always there.
    /// </summary>
    private BoneDeclaration[] ReadBones(ref U3DBitReader reader, string mesh)
    {
        uint count = reader.ReadU32();

        // A bone is at least two names, attributes, a length, a displacement, an orientation and the limits.
        if (count > (uint)(reader.RemainingBytes / 64))
        {
            throw Truncated($"U3D mesh '{mesh}' declares {count} bones, more than its block can hold.");
        }

        var bones = new BoneDeclaration[count];
        for (int i = 0; i < bones.Length; i++)
        {
            string name = ReadName(ref reader);
            string parent = ReadName(ref reader);
            uint attributes = reader.ReadU32();
            float length = reader.ReadF32();
            var displacement = new System.Numerics.Vector3(reader.ReadF32(), reader.ReadF32(), reader.ReadF32());
            float w = reader.ReadF32(), x = reader.ReadF32(), y = reader.ReadF32(), z = reader.ReadF32();
            if ((attributes & 0x1) != 0)
            {
                reader.ReadU32(); // link count
                reader.ReadF32(); // link length
            }

            if ((attributes & 0x2) != 0)
            {
                for (int k = 0; k < 8; k++)
                {
                    reader.ReadF32(); // start and end joints: a centre and a scale each
                }
            }

            for (int k = 0; k < 6; k++)
            {
                reader.ReadF32(); // rotation limits
            }

            bones[i] = new BoneDeclaration(name, parent, length, displacement, new System.Numerics.Quaternion(x, y, z, w));
        }

        return bones;
    }

    /// <summary>Notes where a mesh's base is; it is decoded if a node of the scene uses the mesh.</summary>
    private void ReadBaseMeshContinuation(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        _baseMeshes[ReadName(ref reader)] = block;
    }

    /// <summary>Notes where a mesh's progressive detail is; it is decoded if a node of the scene uses the mesh.</summary>
    private void NoteProgressiveMesh(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        if (!_progressiveMeshes.TryGetValue(name, out List<U3DBlock>? blocks))
        {
            _progressiveMeshes[name] = blocks = [];
        }

        blocks.Add(block);
    }


    private void ReportStrayBaseMeshes(SceneBuilder builder)
    {
        foreach (string name in _baseMeshes.Keys)
        {
            if (!_meshes.ContainsKey(name))
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "mesh-undeclared", $"U3D base mesh '{name}' has no mesh declaration and is ignored.");
            }
        }
    }

    /// <summary>The scene mesh of the model resource a node uses, or -1 when there is nothing to draw.</summary>
    private int GetMesh(SceneBuilder builder, NodeDeclaration node)
    {
        if (_rhMeshes is not null && _rhMeshes.TryGetValue(node.Resource, out RHMeshDeclaration? rhDeclaration))
        {
            if (rhDeclaration.SceneMesh == NotBuilt)
            {
                rhDeclaration.SceneMesh = BuildRHMesh(builder, rhDeclaration);
            }

            return rhDeclaration.SceneMesh;
        }

        if (!_meshes.TryGetValue(node.Resource, out MeshDeclaration? declaration))
        {
            if (_sets.TryGetValue(node.Resource, out SetDeclaration? set))
            {
                return GetSet(builder, set);
            }

            // The empty name is the default model resource, an empty mesh.
            if (node.Resource.Length != 0 && _missingResources.Add(node.Resource))
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "model-resource-missing", $"U3D node '{node.Name}' names a model resource '{node.Resource}' that does not exist.");
            }

            return -1;
        }

        // A node with bone weights over a mesh with a skeleton has a mesh of its own: its vertices follow its bones.
        if (_nodeBoneWeights.Count > 0 || _resourceBoneWeights.Count > 0)
        {
            // A node declared under several parents is several scene nodes; they share the one mesh.
            if (_skinnedMeshes.TryGetValue(node.Name, out int skinned))
            {
                return skinned;
            }

            if (TryGetSkinSource(builder, node, declaration, out SkinSource? source))
            {
                _currentSkin = source;
                try
                {
                    skinned = BuildMesh(builder, declaration);
                }
                finally
                {
                    _currentSkin = null;
                }

                _skinnedMeshes[node.Name] = skinned;
                return skinned;
            }
        }

        if (declaration.SceneMesh == NotBuilt)
        {
            declaration.SceneMesh = BuildMesh(builder, declaration);
        }

        return declaration.SceneMesh;
    }

    private int NoGeometry(SceneBuilder builder, MeshDeclaration declaration)
    {
        if (_progressiveMeshes.ContainsKey(declaration.Name))
        {
            builder.Report(SceneDiagnosticSeverity.Warning, "mesh-progressive-only", $"U3D mesh '{declaration.Name}' keeps its {declaration.FaceCount} faces in progressive blocks, which are not read; it is not shown.");
        }
        else if (declaration.FaceCount > 0)
        {
            builder.Report(SceneDiagnosticSeverity.Warning, "mesh-data-missing", $"U3D mesh '{declaration.Name}' declares {declaration.FaceCount} faces but the file holds none of them.");
        }
        else
        {
            builder.Report(SceneDiagnosticSeverity.Info, "mesh-empty", $"U3D mesh '{declaration.Name}' has no faces.");
        }

        return -1;
    }

    /// <summary>
    /// Decodes a CLOD base mesh (ECMA-363 9.6.1.2) into the scene: the vertex arrays as they stand in the
    /// block, then the faces, whose corners index each array separately and are unified into vertices.
    /// </summary>
    private int BuildMesh(SceneBuilder builder, MeshDeclaration declaration)
    {
        if (_progressiveMeshes.TryGetValue(declaration.Name, out List<U3DBlock>? detail))
        {
            try
            {
                return BuildDetailedMesh(builder, declaration, detail);
            }
            catch (Scene3DException e) when (e.Error is Scene3DError.InvalidValue or Scene3DError.Truncated)
            {
                // What can be shown without it is shown: the base, if there is one.
                builder.Report(SceneDiagnosticSeverity.Warning, "progressive-mesh-unreadable", $"The progressive detail of U3D mesh '{declaration.Name}' cannot be followed: {e.Message}");
            }
        }

        if (!_baseMeshes.TryGetValue(declaration.Name, out U3DBlock block))
        {
            return NoGeometry(builder, declaration);
        }

        U3DBitReader reader = OpenBlock(block);
        reader.SkipString();
        reader.ReadU32(); // chain index
        uint faceCount = reader.ReadU32();
        uint positionCount = reader.ReadU32();
        uint normalCount = reader.ReadU32();
        uint diffuseCount = reader.ReadU32();
        uint specularCount = reader.ReadU32();
        uint texCoordCount = reader.ReadU32();

        // The arrays are stored whole, and a face costs more than four bits (three position indices, each
        // at least log2(3) bits), so the block's size bounds every count before anything is allocated.
        long arrayBytes = positionCount * 12L + normalCount * 12L + diffuseCount * 16L + specularCount * 16L + texCoordCount * 16L;
        if (arrayBytes > reader.RemainingBytes)
        {
            throw Truncated($"U3D base mesh '{declaration.Name}' declares more vertex data than its block holds.");
        }

        if (faceCount * 4L > (reader.RemainingBytes - arrayBytes) * 8L + 32)
        {
            throw Truncated($"U3D base mesh '{declaration.Name}' declares {faceCount} faces, more than its block can hold.");
        }

        if (faceCount == 0 || positionCount < 3)
        {
            return NoGeometry(builder, declaration);
        }

        if (faceCount * 3L > Math.Min(_limits.MaxIndices, int.MaxValue))
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D base mesh '{declaration.Name}' has {faceCount} faces, more than the limit allows.");
        }

        MeshShading[] shadings = declaration.Shadings;
        bool anyDiffuse = false, anyTexCoords = false;
        foreach (MeshShading shading in shadings)
        {
            anyDiffuse |= shading.Diffuse;
            anyTexCoords |= shading.Layers > 0;
        }

        bool hasNormalIndex = !declaration.ExcludeNormals;
        bool useNormals = hasNormalIndex && normalCount > 0;
        bool useColors = anyDiffuse && diffuseCount > 0;
        bool useTexCoords = anyTexCoords && texCoordCount > 0;

        // The base is only part of the mesh when progressive blocks add to it. The declared counts do not
        // say: files in circulation declare twice the faces their base, and whole mesh, has.
        bool partial = _progressiveMeshes.ContainsKey(declaration.Name);

        SceneMeshFlags flags = SceneMeshFlags.None;
        flags |= useNormals ? SceneMeshFlags.HasNormals : 0;
        flags |= useColors ? SceneMeshFlags.HasColors : 0;
        flags |= useTexCoords ? SceneMeshFlags.HasTexCoords : 0;
        flags |= partial ? SceneMeshFlags.PartialDetail : 0;

        float[]? rentedPositions = null, rentedNormals = null, rentedColors = null, rentedTexCoords = null;
        int[]? faceShadings = null;
        var unifier = new VertexUnifier((int)Math.Max(positionCount, Math.Max(normalCount, texCoordCount)), _limits.MaxVertices);
        try
        {
            ReadOnlySpan<float> positions = TakeFloats(ref reader, (int)positionCount * 3, ref rentedPositions);
            ReadOnlySpan<float> normals = TakeFloats(ref reader, (int)normalCount * 3, ref rentedNormals);
            ReadOnlySpan<float> colors = TakeFloats(ref reader, (int)diffuseCount * 4, ref rentedColors);
            SkipFloats(ref reader, (int)specularCount * 4);
            ReadOnlySpan<float> texCoords = TakeFloats(ref reader, (int)texCoordCount * 4, ref rentedTexCoords);

            builder.BeginMesh(builder.AddName(declaration.Name), flags);
            Span<uint> indices = builder.AllocateIndices((int)faceCount * 3);

            // Faces come in any order of shading; note each face's so they can be grouped into parts.
            Span<int> shadingOfFace = default;
            if (shadings.Length > 1)
            {
                faceShadings = ArrayPool<int>.Shared.Rent((int)faceCount);
                shadingOfFace = faceShadings.AsSpan(0, (int)faceCount);
            }

            // The counts are bounded by the block's size, far below where these sums could wrap.
            uint positionContext = U3DConstants.StaticFull + positionCount;
            uint normalContext = U3DConstants.StaticFull + normalCount;
            uint diffuseContext = U3DConstants.StaticFull + diffuseCount;
            uint specularContext = U3DConstants.StaticFull + specularCount;
            uint texCoordContext = U3DConstants.StaticFull + texCoordCount;

            bool grouped = true;
            uint previousShading = 0;
            int corner = 0;
            for (int face = 0; face < (int)faceCount; face++)
            {
                if ((face & (CancellationInterval - 1)) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                uint shadingId = reader.ReadCompressedU32(U3DConstants.ContextBaseShadingId);
                if (shadingId >= (uint)shadings.Length)
                {
                    throw Invalid($"U3D base mesh '{declaration.Name}' has a face with shading {shadingId}; the mesh declares {shadings.Length}.");
                }

                MeshShading shading = shadings[shadingId];
                if (!shadingOfFace.IsEmpty)
                {
                    shadingOfFace[face] = (int)shadingId;
                    grouped &= shadingId >= previousShading;
                    previousShading = shadingId;
                }

                for (int k = 0; k < 3; k++)
                {
                    uint position = reader.ReadCompressedU32(positionContext);
                    if (position >= positionCount)
                    {
                        throw Invalid($"U3D base mesh '{declaration.Name}' has a face using position {position} of {positionCount}.");
                    }

                    // An index into an array the mesh does not have is read and means nothing.
                    int normal = -1;
                    if (hasNormalIndex)
                    {
                        uint value = reader.ReadCompressedU32(normalContext);
                        if (useNormals)
                        {
                            if (value >= normalCount)
                            {
                                throw Invalid($"U3D base mesh '{declaration.Name}' has a face using normal {value} of {normalCount}.");
                            }

                            normal = (int)value;
                        }
                    }

                    int color = -1;
                    if (shading.Diffuse)
                    {
                        uint value = reader.ReadCompressedU32(diffuseContext);
                        if (useColors)
                        {
                            if (value >= diffuseCount)
                            {
                                throw Invalid($"U3D base mesh '{declaration.Name}' has a face using diffuse colour {value} of {diffuseCount}.");
                            }

                            color = (int)value;
                        }
                    }

                    if (shading.Specular)
                    {
                        reader.ReadCompressedU32(specularContext);
                    }

                    int texCoord = -1;
                    for (int layer = 0; layer < shading.Layers; layer++)
                    {
                        uint value = reader.ReadCompressedU32(texCoordContext);
                        if (layer == 0 && useTexCoords)
                        {
                            if (value >= texCoordCount)
                            {
                                throw Invalid($"U3D base mesh '{declaration.Name}' has a face using texture coordinate {value} of {texCoordCount}.");
                            }

                            texCoord = (int)value;
                        }
                    }

                    indices[corner++] = (uint)unifier.GetOrAdd((int)position, normal, color, texCoord);
                }
            }

            int nonFinite = FillVertices(builder.AllocateVertices(unifier.Count), unifier.Tuples, positions, normals, colors, texCoords);
            AddParts(builder, indices, shadingOfFace, shadings.Length, grouped);
            AttachSkin(builder, unifier.Tuples, unifier.Count);
            int mesh = builder.EndMesh();

            if (partial)
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "mesh-partial-detail", $"U3D mesh '{declaration.Name}' is shown at its base level of detail, {faceCount} faces; the rest is in progressive blocks, which are not read.");
            }

            if (nonFinite > 0)
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "non-finite-geometry", $"U3D mesh '{declaration.Name}' has {nonFinite} vertices with a NaN or infinite coordinate, set to zero.");
            }

            return mesh;
        }
        finally
        {
            unifier.Dispose();
            Return(rentedPositions);
            Return(rentedNormals);
            Return(rentedColors);
            Return(rentedTexCoords);
            if (faceShadings is not null)
            {
                ArrayPool<int>.Shared.Return(faceShadings);
            }
        }

        static void Return(float[]? rented)
        {
            if (rented is not null)
            {
                ArrayPool<float>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// The next <paramref name="count"/> floats of the block: a view of the block where they are stored
    /// plainly (always, in practice: the arrays precede the block's first compressed value), else decoded
    /// into a rented array.
    /// </summary>
    private static ReadOnlySpan<float> TakeFloats(ref U3DBitReader reader, int count, ref float[]? rented)
    {
        if (reader.TryTakeF32(count, out ReadOnlySpan<float> values))
        {
            return values;
        }

        rented = ArrayPool<float>.Shared.Rent(count);
        Span<float> decoded = rented.AsSpan(0, count);
        reader.ReadF32(decoded);
        return decoded;
    }

    private static void SkipFloats(ref U3DBitReader reader, int count)
    {
        if (!reader.TryTakeF32(count, out _))
        {
            for (int i = 0; i < count; i++)
            {
                reader.ReadF32();
            }
        }
    }

    /// <summary>
    /// Writes each unified vertex from the arrays its indices point into. Returns how many vertices had a
    /// coordinate that is not a finite number; those are written as zero, so that bounds stay usable.
    /// </summary>
    private int FillVertices(SceneVertexSpans vertices, ReadOnlySpan<int> tuples, ReadOnlySpan<float> positions, ReadOnlySpan<float> normals, ReadOnlySpan<float> colors, ReadOnlySpan<float> texCoords)
    {
        Span<float> outPositions = vertices.Positions;
        Span<float> outNormals = vertices.Normals;
        Span<float> outTexCoords = vertices.TexCoords;
        Span<uint> outColors = vertices.Colors;

        int nonFinite = 0;
        int count = tuples.Length / VertexUnifier.Stride;
        for (int vertex = 0; vertex < count; vertex++)
        {
            if ((vertex & (CancellationInterval - 1)) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<int> tuple = tuples.Slice(vertex * VertexUnifier.Stride, VertexUnifier.Stride);
            bool finite = CopyFinite(positions.Slice(tuple[0] * 3, 3), outPositions.Slice(vertex * 3, 3));

            if (!outNormals.IsEmpty)
            {
                // A corner that progressive detail left without a normal has none: a consumer shades it flat.
                if (tuple[1] < 0)
                {
                    outNormals.Slice(vertex * 3, 3).Clear();
                }
                else
                {
                    finite &= CopyFinite(normals.Slice(tuple[1] * 3, 3), outNormals.Slice(vertex * 3, 3));
                }
            }

            if (!outColors.IsEmpty)
            {
                // A face whose shading has no colours is white, so that it shows its material unchanged.
                outColors[vertex] = tuple[2] < 0 ? 0xFFFFFFFF : PackColor(colors.Slice(tuple[2] * 4, 4));
            }

            if (!outTexCoords.IsEmpty)
            {
                Span<float> uv = outTexCoords.Slice(vertex * 2, 2);
                if (tuple[3] < 0)
                {
                    uv.Clear();
                }
                else
                {
                    finite &= CopyFinite(texCoords.Slice(tuple[3] * 4, 2), uv);
                }
            }

            if (!finite)
            {
                nonFinite++;
            }
        }

        return nonFinite;
    }

    private static bool CopyFinite(ReadOnlySpan<float> source, Span<float> destination)
    {
        bool finite = true;
        for (int i = 0; i < source.Length; i++)
        {
            finite &= float.IsFinite(source[i]);
        }

        if (finite)
        {
            source.CopyTo(destination);
        }
        else
        {
            destination.Clear();
        }

        return finite;
    }

    /// <summary>RGBA floats to bytes, red in the low byte; values are clamped to 0 – 1, NaN reads as 0.</summary>
    private static uint PackColor(ReadOnlySpan<float> rgba)
    {
        return Channel(rgba[0]) | (Channel(rgba[1]) << 8) | (Channel(rgba[2]) << 16) | (Channel(rgba[3]) << 24);

        static uint Channel(float value) => value > 0 ? (value < 1 ? (uint)(value * 255f + 0.5f) : 255u) : 0u;
    }

    /// <summary>
    /// Declares the mesh's parts: one run of triangles per shading in use, in ascending order. The indices
    /// are in face order; unless the faces already came grouped, they are regrouped by a counting sort.
    /// </summary>
    private static void AddParts(SceneBuilder builder, Span<uint> indices, ReadOnlySpan<int> shadingOfFace, int shadingCount, bool grouped, int perFace = 3)
    {
        if (shadingOfFace.IsEmpty)
        {
            builder.AddPart(indices.Length, 0);
            return;
        }

        int[] starts = ArrayPool<int>.Shared.Rent(shadingCount + 1);
        uint[]? copy = null;
        try
        {
            // starts[s + 1] counts the faces of shading s, then becomes the first face of shading s + 1.
            Span<int> start = starts.AsSpan(0, shadingCount + 1);
            start.Clear();
            foreach (int shading in shadingOfFace)
            {
                start[shading + 1]++;
            }

            for (int s = 0; s < shadingCount; s++)
            {
                if (start[s + 1] > 0)
                {
                    builder.AddPart(start[s + 1] * perFace, s);
                }

                start[s + 1] += start[s];
            }

            if (grouped)
            {
                return;
            }

            copy = ArrayPool<uint>.Shared.Rent(indices.Length);
            Span<uint> source = copy.AsSpan(0, indices.Length);
            indices.CopyTo(source);
            for (int face = 0; face < shadingOfFace.Length; face++)
            {
                int target = start[shadingOfFace[face]]++;
                source.Slice(face * perFace, perFace).CopyTo(indices.Slice(target * perFace, perFace));
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(starts);
            if (copy is not null)
            {
                ArrayPool<uint>.Shared.Return(copy);
            }
        }
    }
}
