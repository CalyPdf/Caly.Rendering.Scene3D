using System.Buffers;
using System.Numerics;
using Caly.Rendering.Scene3D.Internal;

namespace Caly.Rendering.Scene3D.U3D.Internal;

/// <summary>
/// A mesh at full detail: its base, if it has one, and then its progressive blocks (ECMA-363 9.6.1.3),
/// each of which undoes one step of the simplification that made the base: a position is split in two,
/// some of the faces around it move to the new position, new faces fill the gap.
/// <para>
/// What a resolution update stores is counted against the neighbourhood of the split position: an index
/// "among the positions around it", a colour "among those used there". The standard names those sets and
/// not their order, and leaves the predictions of normals and colours to a sentence each. What is here
/// follows the Universal 3D Sample Software (CIFXAuthorCLODDecoder, CIFXSetAdjacencyX, CIFXSetX), which
/// wrote the files: every set is ordered from its largest member down, and the sets are taken at the very
/// moments it takes them (some before the faces have moved, some after).
/// </para>
/// </summary>
internal sealed partial class U3DDocument
{
    private const byte OrientationLeft = 1, OrientationRight = 2;
    private const byte ThirdIndexLocal = 1;
    private const byte UpdateChange = 1, UpdateNew = 1, UpdateLocal = 2;
    private const byte DupSplit = 1, DupUpdate = 2, DupThird = 4;
    private const uint PredictMove = 1, PredictStay = 2, PredictMove2 = 3, PredictStay2 = 4;

    /// <summary>A set of numbers kept from the largest down, as the sample software keeps its sets.</summary>
    private sealed class DescendingSet
    {
        private int[] _members = new int[16];

        public int Count { get; private set; }

        public int this[int index] => _members[index];

        public void Clear() => Count = 0;

        public bool Contains(int member)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_members[i] == member)
                {
                    return true;
                }
            }

            return false;
        }

        public void Add(int member)
        {
            int at = 0;
            while (at < Count && _members[at] > member)
            {
                at++;
            }

            if (at < Count && _members[at] == member)
            {
                return;
            }

            if (Count == _members.Length)
            {
                Array.Resize(ref _members, Count * 2);
            }

            Array.Copy(_members, at, _members, at + 1, Count - at);
            _members[at] = member;
            Count++;
        }

        public void Remove(int member)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_members[i] == member)
                {
                    Array.Copy(_members, i + 1, _members, i, Count - i - 1);
                    Count--;
                    return;
                }
            }
        }
    }

    /// <summary>The mesh while it grows: pools of values, faces as indices into them, and which faces meet at each position.</summary>
    private sealed class DetailMesh : IDisposable
    {
        public float[] Positions = [], Normals = [], Diffuse = [], TexCoords = [];
        public int PositionCount, NormalCount, DiffuseCount, SpecularCount, TexCoordCount;

        // Three indices a face; -1 where a face has none.
        public int[] FacePosition = [], FaceNormal = [], FaceDiffuse = [], FaceShading = [];
        public readonly int[]?[] FaceTex = new int[MaxTextureLayers][];
        public int FaceCount, FaceCapacity;

        // The faces at each position, as lists from the largest face down.
        private int[] _head = [], _entryFace = [], _entryNext = [];
        private int _headCount, _entryCount, _freeEntry = -1;

        public void EnsureFaces(int count, int layers)
        {
            if (count > FaceCapacity)
            {
                int capacity = Math.Max(count, Math.Max(FaceCapacity * 2, 64));
                Grow(ref FacePosition, FaceCapacity * 3, capacity * 3);
                Grow(ref FaceNormal, FaceCapacity * 3, capacity * 3);
                Grow(ref FaceDiffuse, FaceCapacity * 3, capacity * 3);
                Grow(ref FaceShading, FaceCapacity, capacity);
                for (int layer = 0; layer < FaceTex.Length; layer++)
                {
                    if (FaceTex[layer] is { } tex)
                    {
                        Grow(ref tex, FaceCapacity * 3, capacity * 3);
                        FaceTex[layer] = tex;
                    }
                }

                FaceCapacity = capacity;
            }

            for (int layer = 0; layer < layers; layer++)
            {
                if (FaceTex[layer] is null)
                {
                    int[] tex = ArrayPool<int>.Shared.Rent(Math.Max(FaceCapacity * 3, 3));
                    tex.AsSpan().Fill(-1);
                    FaceTex[layer] = tex;
                }
            }
        }

        public void EnsurePositions(int count)
        {
            Grow(ref Positions, PositionCount * 3, count * 3);
            if (count > _head.Length)
            {
                int old = _headCount;
                Grow(ref _head, old, Math.Max(count, _head.Length * 2));
            }

            if (count > _headCount)
            {
                _head.AsSpan(_headCount, count - _headCount).Fill(-1);
                _headCount = count;
            }
        }

        /// <summary>What the mesh may hold while it grows, in bytes: the limit of the scene it is read for.</summary>
        public long MaxBytes = long.MaxValue;

        private long _bytes;

        /// <summary>Makes room for <paramref name="needed"/> values, keeping the first <paramref name="used"/>.</summary>
        public void Grow<T>(ref T[] array, int used, int needed)
            where T : unmanaged
        {
            if (needed <= array.Length)
            {
                return;
            }

            // A few bytes of a file can stand for any number of coded values: what they make the mesh
            // hold is bounded here, not by the size of the file.
            int length = Math.Max(needed, array.Length * 2);
            _bytes += (long)(length - array.Length) * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
            if (_bytes > MaxBytes)
            {
                throw new Scene3DException(Scene3DError.LimitExceeded, "The progressive detail of a U3D mesh needs more memory than the limit allows.");
            }

            T[] larger = ArrayPool<T>.Shared.Rent(length);
            array.AsSpan(0, used).CopyTo(larger);
            if (array.Length > 0)
            {
                ArrayPool<T>.Shared.Return(array);
            }

            array = larger;
        }

        public int FirstEntry(int position) => (uint)position < (uint)_headCount ? _head[position] : -1;

        public int EntryFace(int entry) => _entryFace[entry];

        public int NextEntry(int entry) => _entryNext[entry];

        public void AddFaceAt(int position, int face)
        {
            int previous = -1, entry = _head[position];
            while (entry >= 0 && _entryFace[entry] > face)
            {
                previous = entry;
                entry = _entryNext[entry];
            }

            if (entry >= 0 && _entryFace[entry] == face)
            {
                return;
            }

            int added;
            if (_freeEntry >= 0)
            {
                added = _freeEntry;
                _freeEntry = _entryNext[added];
            }
            else
            {
                Grow(ref _entryFace, _entryCount, _entryCount + 1);
                Grow(ref _entryNext, _entryCount, _entryCount + 1);
                added = _entryCount++;
            }

            _entryFace[added] = face;
            _entryNext[added] = entry;
            if (previous < 0)
            {
                _head[position] = added;
            }
            else
            {
                _entryNext[previous] = added;
            }
        }

        public void RemoveFaceAt(int position, int face)
        {
            int previous = -1, entry = _head[position];
            while (entry >= 0 && _entryFace[entry] != face)
            {
                previous = entry;
                entry = _entryNext[entry];
            }

            if (entry < 0)
            {
                return;
            }

            if (previous < 0)
            {
                _head[position] = _entryNext[entry];
            }
            else
            {
                _entryNext[previous] = _entryNext[entry];
            }

            _entryNext[entry] = _freeEntry;
            _freeEntry = entry;
        }

        public void Dispose()
        {
            Return(ref Positions);
            Return(ref Normals);
            Return(ref Diffuse);
            Return(ref TexCoords);
            Return(ref FacePosition);
            Return(ref FaceNormal);
            Return(ref FaceDiffuse);
            Return(ref FaceShading);
            Return(ref _head);
            Return(ref _entryFace);
            Return(ref _entryNext);
            for (int layer = 0; layer < FaceTex.Length; layer++)
            {
                if (FaceTex[layer] is { } tex)
                {
                    ArrayPool<int>.Shared.Return(tex);
                    FaceTex[layer] = null;
                }
            }
        }

        private static void Return<T>(ref T[] array)
        {
            if (array.Length > 0)
            {
                ArrayPool<T>.Shared.Return(array);
                array = [];
            }
        }
    }

    /// <summary>The sets one update works with, kept between updates so that nothing is allocated for each.</summary>
    private sealed class UpdateSets
    {
        public readonly DescendingSet SplitFaces = new(), Around = new(), Left = new(), Right = new(), MoveFaces = new(), MovePositions = new(), StayPositions = new(), NewPositions = new(), Values = new(), ThirdValues = new(), Faces = new();
        public readonly List<(int Slot, int[] Array, int Value)> Pending = [];
        public Vector3[] FaceNormals = new Vector3[16], Predicted = new Vector3[16];
        public int[] Contributions = new int[16];
        public int PreviousSplitDiffuse, PreviousUpdateDiffuse, PreviousThirdDiffuse, PreviousSplitTex, PreviousUpdateTex, PreviousThirdTex;
    }

    /// <summary>The mesh at full detail, or an exception of bad data when its progressive blocks cannot be followed.</summary>
    private int BuildDetailedMesh(SceneBuilder builder, MeshDeclaration declaration, List<U3DBlock> blocks)
    {
        MeshShading[] shadings = declaration.Shadings;
        long maxFaces = Math.Min(_limits.MaxIndices, int.MaxValue - 16) / 3;
        using var mesh = new DetailMesh { MaxBytes = _limits.MaxTotalBytes };
        int layers = 0;
        bool anyDiffuse = false;
        foreach (MeshShading shading in shadings)
        {
            layers = Math.Max(layers, shading.Layers);
            anyDiffuse |= shading.Diffuse;
        }

        if (_baseMeshes.TryGetValue(declaration.Name, out U3DBlock baseBlock))
        {
            ReadBaseInto(mesh, declaration, baseBlock, layers, maxFaces);
        }

        var sets = new UpdateSets();
        foreach (U3DBlock block in blocks)
        {
            U3DBitReader reader = OpenBlock(block);
            reader.SkipString();
            reader.ReadU32(); // chain index
            uint start = reader.ReadU32(), end = reader.ReadU32();
            if (start != (uint)mesh.PositionCount || end < start)
            {
                throw Invalid($"U3D mesh '{declaration.Name}' has progressive detail from position {start}, where {mesh.PositionCount} was expected.");
            }

            if (end > Math.Min(_limits.MaxVertices, int.MaxValue / 4))
            {
                throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D mesh '{declaration.Name}' has {end} positions, more than the limit allows.");
            }

            for (int current = (int)start; current < (int)end; current++)
            {
                if ((current & 1023) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                ReadResolutionUpdate(ref reader, mesh, sets, declaration, current, layers, maxFaces);
            }
        }

        if (mesh.FaceCount == 0 || mesh.PositionCount < 3)
        {
            return NoGeometry(builder, declaration);
        }

        for (int corner = 0; corner < mesh.FaceCount * 3; corner++)
        {
            if ((uint)mesh.FacePosition[corner] >= (uint)mesh.PositionCount)
            {
                throw Invalid($"U3D mesh '{declaration.Name}' has a face using position {mesh.FacePosition[corner]} of {mesh.PositionCount}.");
            }
        }

        // Out of the pools and the faces, vertices that are the same in everything they have.
        bool useNormals = !declaration.ExcludeNormals && mesh.NormalCount > 0;
        bool useColors = anyDiffuse && mesh.DiffuseCount > 0;
        bool useTexCoords = layers > 0 && mesh.TexCoordCount > 0;
        SceneMeshFlags flags = (useNormals ? SceneMeshFlags.HasNormals : 0) | (useColors ? SceneMeshFlags.HasColors : 0) | (useTexCoords ? SceneMeshFlags.HasTexCoords : 0);
        int[]? tex = mesh.FaceTex[0];
        var unifier = new VertexUnifier(mesh.PositionCount, _limits.MaxVertices);
        int[]? faceShadings = null;
        try
        {
            builder.BeginMesh(builder.AddName(declaration.Name), flags);
            Span<uint> indices = builder.AllocateIndices(mesh.FaceCount * 3);
            Span<int> shadingOfFace = default;
            if (shadings.Length > 1)
            {
                faceShadings = ArrayPool<int>.Shared.Rent(mesh.FaceCount);
                shadingOfFace = faceShadings.AsSpan(0, mesh.FaceCount);
            }

            bool grouped = true;
            for (int face = 0; face < mesh.FaceCount; face++)
            {
                MeshShading shading = shadings[mesh.FaceShading[face]];
                if (!shadingOfFace.IsEmpty)
                {
                    shadingOfFace[face] = mesh.FaceShading[face];
                    grouped &= face == 0 || mesh.FaceShading[face] >= mesh.FaceShading[face - 1];
                }

                for (int k = 0; k < 3; k++)
                {
                    int corner = (face * 3) + k;
                    int position = mesh.FacePosition[corner];

                    // An index into a pool that has no such entry means nothing, as in a base mesh.
                    int normal = useNormals && (uint)mesh.FaceNormal[corner] < (uint)mesh.NormalCount ? mesh.FaceNormal[corner] : -1;
                    int color = useColors && shading.Diffuse && (uint)mesh.FaceDiffuse[corner] < (uint)mesh.DiffuseCount ? mesh.FaceDiffuse[corner] : -1;
                    int texCoord = useTexCoords && shading.Layers > 0 && tex is not null && (uint)tex[corner] < (uint)mesh.TexCoordCount ? tex[corner] : -1;
                    indices[corner] = (uint)unifier.GetOrAdd(position, normal, color, texCoord);
                }
            }

            int nonFinite = FillVertices(
                builder.AllocateVertices(unifier.Count),
                unifier.Tuples,
                mesh.Positions.AsSpan(0, mesh.PositionCount * 3),
                mesh.Normals.AsSpan(0, mesh.NormalCount * 3),
                mesh.Diffuse.AsSpan(0, mesh.DiffuseCount * 4),
                mesh.TexCoords.AsSpan(0, mesh.TexCoordCount * 4));
            AddParts(builder, indices, shadingOfFace, shadings.Length, grouped);
            AttachSkin(builder, unifier.Tuples, unifier.Count);
            int sceneMesh = builder.EndMesh();
            if (nonFinite > 0)
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "non-finite-geometry", $"U3D mesh '{declaration.Name}' has {nonFinite} vertices with a NaN or infinite coordinate, set to zero.");
            }

            return sceneMesh;
        }
        finally
        {
            unifier.Dispose();
            if (faceShadings is not null)
            {
                ArrayPool<int>.Shared.Return(faceShadings);
            }
        }
    }

    /// <summary>The base mesh (9.6.1.2) as pools and faces, for the progressive blocks to build on.</summary>
    private void ReadBaseInto(DetailMesh mesh, MeshDeclaration declaration, U3DBlock block, int layers, long maxFaces)
    {
        U3DBitReader reader = OpenBlock(block);
        reader.SkipString();
        reader.ReadU32(); // chain index
        uint faceCount = reader.ReadU32();
        uint positionCount = reader.ReadU32();
        uint normalCount = reader.ReadU32();
        uint diffuseCount = reader.ReadU32();
        uint specularCount = reader.ReadU32();
        uint texCoordCount = reader.ReadU32();

        long arrayBytes = positionCount * 12L + normalCount * 12L + diffuseCount * 16L + specularCount * 16L + texCoordCount * 16L;
        if (arrayBytes > reader.RemainingBytes)
        {
            throw Truncated($"U3D base mesh '{declaration.Name}' declares more vertex data than its block holds.");
        }

        if (faceCount * 4L > (reader.RemainingBytes - arrayBytes) * 8L + 32)
        {
            throw Truncated($"U3D base mesh '{declaration.Name}' declares {faceCount} faces, more than its block can hold.");
        }

        if (faceCount > maxFaces)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D base mesh '{declaration.Name}' has {faceCount} faces, more than the limit allows.");
        }

        mesh.EnsurePositions((int)positionCount);
        reader.ReadF32(mesh.Positions.AsSpan(0, (int)positionCount * 3));
        mesh.PositionCount = (int)positionCount;
        mesh.Grow(ref mesh.Normals, 0, (int)normalCount * 3);
        reader.ReadF32(mesh.Normals.AsSpan(0, (int)normalCount * 3));
        mesh.NormalCount = (int)normalCount;
        mesh.Grow(ref mesh.Diffuse, 0, (int)diffuseCount * 4);
        reader.ReadF32(mesh.Diffuse.AsSpan(0, (int)diffuseCount * 4));
        mesh.DiffuseCount = (int)diffuseCount;
        SkipFloats(ref reader, (int)specularCount * 4);
        mesh.SpecularCount = (int)specularCount;
        mesh.Grow(ref mesh.TexCoords, 0, (int)texCoordCount * 4);
        reader.ReadF32(mesh.TexCoords.AsSpan(0, (int)texCoordCount * 4));
        mesh.TexCoordCount = (int)texCoordCount;

        MeshShading[] shadings = declaration.Shadings;
        mesh.EnsureFaces((int)faceCount, layers);
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
            mesh.FaceShading[face] = (int)shadingId;
            for (int k = 0; k < 3; k++)
            {
                int corner = (face * 3) + k;
                uint position = reader.ReadCompressedU32(U3DConstants.StaticFull + positionCount);
                if (position >= positionCount)
                {
                    throw Invalid($"U3D base mesh '{declaration.Name}' has a face using position {position} of {positionCount}.");
                }

                mesh.FacePosition[corner] = (int)position;
                mesh.FaceNormal[corner] = declaration.ExcludeNormals ? -1 : (int)reader.ReadCompressedU32(U3DConstants.StaticFull + normalCount);
                mesh.FaceDiffuse[corner] = shading.Diffuse ? (int)reader.ReadCompressedU32(U3DConstants.StaticFull + diffuseCount) : -1;
                if (shading.Specular)
                {
                    reader.ReadCompressedU32(U3DConstants.StaticFull + specularCount);
                }

                for (int layer = 0; layer < shading.Layers; layer++)
                {
                    mesh.FaceTex[layer]![corner] = (int)reader.ReadCompressedU32(U3DConstants.StaticFull + texCoordCount);
                }

                for (int layer = shading.Layers; layer < layers; layer++)
                {
                    mesh.FaceTex[layer]![corner] = -1;
                }
            }

            mesh.FaceCount = face + 1;
            for (int k = 0; k < 3; k++)
            {
                mesh.AddFaceAt(mesh.FacePosition[(face * 3) + k], face);
            }
        }
    }

    /// <summary>The corner of a face that is at a position, or -1.</summary>
    private static int CornerAt(DetailMesh mesh, int face, int position)
    {
        int at = face * 3;
        return mesh.FacePosition[at] == position ? at : mesh.FacePosition[at + 1] == position ? at + 1 : mesh.FacePosition[at + 2] == position ? at + 2 : -1;
    }

    /// <summary>The values (colours, or texture coordinates of a layer) that the faces at a position use there.</summary>
    private static void ValuesAt(DetailMesh mesh, MeshShading[] shadings, int position, int[]? faceValues, int layer, DescendingSet values)
    {
        values.Clear();
        if (faceValues is null)
        {
            return;
        }

        for (int entry = mesh.FirstEntry(position); entry >= 0; entry = mesh.NextEntry(entry))
        {
            int face = mesh.EntryFace(entry);
            MeshShading shading = shadings[mesh.FaceShading[face]];
            if (layer < 0 ? !shading.Diffuse : shading.Layers <= layer)
            {
                continue;
            }

            int corner = CornerAt(mesh, face, position);
            if (corner >= 0)
            {
                values.Add(faceValues[corner]);
            }
        }
    }

    private static int Member(DescendingSet set, uint index, string name)
    {
        if (index >= (uint)set.Count)
        {
            throw Invalid($"U3D mesh '{name}' names entry {index} of a neighbourhood of {set.Count}.");
        }

        return set[(int)index];
    }

    private void ReadResolutionUpdate(ref U3DBitReader reader, DetailMesh mesh, UpdateSets sets, MeshDeclaration declaration, int current, int layers, long maxFaces)
    {
        MeshShading[] shadings = declaration.Shadings;
        string name = declaration.Name;
        int split = (int)reader.ReadCompressedU32(U3DConstants.StaticFull + (uint)current);
        if (current > 0 && (uint)split >= (uint)current)
        {
            throw Invalid($"U3D mesh '{name}' splits position {split} to make position {current}.");
        }

        if (current == 0)
        {
            split = 0;
        }

        // The faces at the split position, and the positions around it.
        sets.SplitFaces.Clear();
        sets.Around.Clear();
        for (int entry = mesh.FirstEntry(split); entry >= 0; entry = mesh.NextEntry(entry))
        {
            int face = mesh.EntryFace(entry);
            sets.SplitFaces.Add(face);
            sets.Around.Add(mesh.FacePosition[face * 3]);
            sets.Around.Add(mesh.FacePosition[(face * 3) + 1]);
            sets.Around.Add(mesh.FacePosition[(face * 3) + 2]);
        }

        sets.Around.Remove(split);

        // New colours and texture coordinates: differences from the average of those in use at the split position.
        Span<float> predicted = stackalloc float[4];
        int newDiffuse = reader.ReadCompressedU16(U3DConstants.ContextNewDiffuseColorsCount);
        if (newDiffuse > 0)
        {
            ValuesAt(mesh, shadings, split, mesh.FaceDiffuse, -1, sets.Values);
            Average(predicted, sets.Values, mesh.Diffuse, mesh.DiffuseCount);
            mesh.Grow(ref mesh.Diffuse, mesh.DiffuseCount * 4, (mesh.DiffuseCount + newDiffuse) * 4);
            for (int j = 0; j < newDiffuse; j++)
            {
                byte signs = reader.ReadCompressedU8(U3DConstants.ContextDiffuseColorSign);
                ReadFour(ref reader, U3DConstants.ContextVertexColorDiffMagR, signs, declaration.DiffuseQuant, predicted, mesh.Diffuse.AsSpan((mesh.DiffuseCount + j) * 4, 4));
            }
        }

        int newSpecular = reader.ReadCompressedU16(U3DConstants.ContextNewSpecularColorsCount);
        for (int j = 0; j < newSpecular; j++)
        {
            reader.ReadCompressedU8(U3DConstants.ContextSpecularColorSign);
            reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagR);
            reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagG);
            reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagB);
            reader.ReadCompressedU32(U3DConstants.ContextVertexColorDiffMagA);
        }

        int newTexCoords = reader.ReadCompressedU16(U3DConstants.ContextNewTexCoordsCount);
        if (newTexCoords > 0)
        {
            ValuesAt(mesh, shadings, split, mesh.FaceTex[0], 0, sets.Values);
            Average(predicted, sets.Values, mesh.TexCoords, mesh.TexCoordCount);
            mesh.Grow(ref mesh.TexCoords, mesh.TexCoordCount * 4, (mesh.TexCoordCount + newTexCoords) * 4);
            for (int j = 0; j < newTexCoords; j++)
            {
                byte signs = reader.ReadCompressedU8(U3DConstants.ContextTexCoordSign);
                ReadFour(ref reader, U3DConstants.ContextTexCoordDiffMagU, signs, declaration.TexCoordQuant, predicted, mesh.TexCoords.AsSpan((mesh.TexCoordCount + j) * 4, 4));
            }
        }

        // New faces: the split position, the new one and a third, to the left or to the right.
        uint newFaces = reader.ReadCompressedU32(U3DConstants.ContextNumNewFaces);
        if (newFaces > maxFaces - mesh.FaceCount)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D mesh '{name}' has more faces than the limit allows.");
        }

        if (newFaces > (uint)reader.RemainingBytes * 8L + 64)
        {
            throw Truncated($"U3D mesh '{name}' adds {newFaces} faces at position {current}, more than its block can hold.");
        }

        int firstNew = mesh.FaceCount;
        mesh.EnsureFaces(firstNew + (int)newFaces, layers);
        sets.Left.Clear();
        sets.Right.Clear();
        for (int j = 0; j < (int)newFaces; j++)
        {
            int face = firstNew + j;
            uint shadingId = reader.ReadCompressedU32(U3DConstants.ContextShadingId);
            if (shadingId >= (uint)shadings.Length)
            {
                throw Invalid($"U3D mesh '{name}' has a face with shading {shadingId}; the mesh declares {shadings.Length}.");
            }

            mesh.FaceShading[face] = (int)shadingId;
            byte orientation = reader.ReadCompressedU8(U3DConstants.ContextOrientationType);
            int third;
            if (reader.ReadCompressedU8(U3DConstants.ContextThirdIndexType) == ThirdIndexLocal)
            {
                third = Member(sets.Around, reader.ReadCompressedU32(U3DConstants.ContextLocal3rdPosition), name);
            }
            else
            {
                third = (int)reader.ReadCompressedU32(U3DConstants.StaticFull + (uint)current);
                if ((uint)third >= (uint)current)
                {
                    throw Invalid($"U3D mesh '{name}' has a new face at position {third}, which does not exist yet.");
                }

                sets.Around.Add(third);
            }

            int at = face * 3;
            if (orientation == OrientationLeft)
            {
                sets.Left.Add(third);
                (mesh.FacePosition[at], mesh.FacePosition[at + 1]) = (split, current);
            }
            else if (orientation == OrientationRight)
            {
                sets.Right.Add(third);
                (mesh.FacePosition[at], mesh.FacePosition[at + 1]) = (current, split);
            }
            else
            {
                throw Invalid($"U3D mesh '{name}' has a new face that is neither to the left nor to the right.");
            }

            mesh.FacePosition[at + 2] = third;
            mesh.FaceNormal.AsSpan(at, 3).Fill(-1);
            mesh.FaceDiffuse.AsSpan(at, 3).Fill(-1);
            for (int layer = 0; layer < layers; layer++)
            {
                mesh.FaceTex[layer].AsSpan(at, 3).Fill(-1);
            }
        }

        // Which of the faces at the split position move to the new one. Each answer is guessed first, from
        // the new faces and from the answers before it, and the guess chooses the context it is read in.
        sets.MoveFaces.Clear();
        sets.MovePositions.Clear();
        sets.StayPositions.Clear();
        for (int i = 0; i < sets.SplitFaces.Count; i++)
        {
            int face = sets.SplitFaces[i];
            int a = mesh.FacePosition[face * 3], b = mesh.FacePosition[(face * 3) + 1], c = mesh.FacePosition[(face * 3) + 2];
            uint guess = 0;
            (int next, int last) = split == a ? (b, c) : split == b ? (c, a) : split == c ? (a, b) : (-1, -1);
            if (next >= 0)
            {
                guess = sets.Right.Contains(next) ? PredictMove : sets.Right.Contains(last) ? PredictStay : sets.Left.Contains(next) ? PredictStay : sets.Left.Contains(last) ? PredictMove : 0;
            }

            if (guess == 0 && (sets.MovePositions.Contains(a) || sets.MovePositions.Contains(b) || sets.MovePositions.Contains(c)))
            {
                guess = PredictMove2;
            }

            if (guess == 0 && (sets.StayPositions.Contains(a) || sets.StayPositions.Contains(b) || sets.StayPositions.Contains(c)))
            {
                guess = PredictStay2;
            }

            DescendingSet group = sets.StayPositions;
            if (reader.ReadCompressedU8(U3DConstants.ContextStayMove + guess) == 1)
            {
                sets.MoveFaces.Add(face);
                group = sets.MovePositions;
            }

            group.Add(a);
            group.Add(b);
            group.Add(c);
            group.Remove(split);
        }

        // What the faces that move use at their moved corner. Nothing is changed yet: the sets below are
        // those of the mesh as it was.
        sets.Pending.Clear();
        for (int i = 0; i < sets.MoveFaces.Count; i++)
        {
            int face = sets.MoveFaces[i];
            int corner = CornerAt(mesh, face, split);
            if (corner >= 0)
            {
                sets.Pending.Add((corner, mesh.FacePosition, current));
            }

            MeshShading shading = shadings[mesh.FaceShading[face]];
            if (shading.Diffuse && reader.ReadCompressedU8(U3DConstants.ContextDiffuseKeepChange) == UpdateChange)
            {
                byte type = reader.ReadCompressedU8(U3DConstants.ContextDiffuseChangeType);
                uint index = reader.ReadCompressedU32(type == UpdateNew ? U3DConstants.ContextDiffuseChangeIndexNew : type == UpdateLocal ? U3DConstants.ContextDiffuseChangeIndexLocal : U3DConstants.ContextDiffuseChangeIndexGlobal);
                int value;
                if (type == UpdateLocal)
                {
                    ValuesAt(mesh, shadings, split, mesh.FaceDiffuse, -1, sets.Values);
                    value = Member(sets.Values, index, name);
                }
                else
                {
                    value = type == UpdateNew ? (int)Math.Min(index + (long)mesh.DiffuseCount, int.MaxValue) : (int)Math.Min(index, int.MaxValue);
                }

                if (corner >= 0)
                {
                    sets.Pending.Add((corner, mesh.FaceDiffuse, value));
                }
            }

            if (shading.Specular && reader.ReadCompressedU8(U3DConstants.ContextSpecularKeepChange) == UpdateChange)
            {
                byte type = reader.ReadCompressedU8(U3DConstants.ContextSpecularChangeType);
                reader.ReadCompressedU32(type == UpdateNew ? U3DConstants.ContextSpecularChangeIndexNew : type == UpdateLocal ? U3DConstants.ContextSpecularChangeIndexLocal : U3DConstants.ContextSpecularChangeIndexGlobal);
            }

            for (int layer = 0; layer < shading.Layers; layer++)
            {
                if (reader.ReadCompressedU8(U3DConstants.ContextTexCoordKeepChange) != UpdateChange)
                {
                    continue;
                }

                byte type = reader.ReadCompressedU8(U3DConstants.ContextTexCoordChangeType);
                uint index = reader.ReadCompressedU32(type == UpdateNew ? U3DConstants.ContextTexCoordChangeIndexNew : type == UpdateLocal ? U3DConstants.ContextTexCoordChangeIndexLocal : U3DConstants.ContextTexCoordChangeIndexGlobal);
                int value;
                if (type == UpdateLocal)
                {
                    ValuesAt(mesh, shadings, split, mesh.FaceTex[layer], layer, sets.Values);
                    value = Member(sets.Values, index, name);
                }
                else
                {
                    value = type == UpdateNew ? (int)Math.Min(index + (long)mesh.TexCoordCount, int.MaxValue) : (int)Math.Min(index, int.MaxValue);
                }

                if (corner >= 0)
                {
                    sets.Pending.Add((corner, mesh.FaceTex[layer]!, value));
                }
            }
        }

        // What the new faces use at their three corners: as the face before, or one of those in use at the
        // position, or any.
        for (int j = 0; j < (int)newFaces; j++)
        {
            int face = firstNew + j, at = face * 3;
            bool left = mesh.FacePosition[at] == split;
            int splitCorner = at + (left ? 0 : 1), newCorner = at + (left ? 1 : 0), thirdCorner = at + 2;
            int third = mesh.FacePosition[thirdCorner];
            MeshShading shading = shadings[mesh.FaceShading[face]];
            if (shading.Diffuse)
            {
                byte same = reader.ReadCompressedU8(U3DConstants.ContextVertColorDupType);
                ValuesAt(mesh, shadings, split, mesh.FaceDiffuse, -1, sets.Values);
                ValuesAt(mesh, shadings, third, mesh.FaceDiffuse, -1, sets.ThirdValues);
                int atSplit = (same & DupSplit) != 0 ? sets.PreviousSplitDiffuse : ReadUsed(ref reader, sets.Values, U3DConstants.ContextVertColorSplitType, U3DConstants.ContextVertColorSplitIndexLocal, U3DConstants.ContextVertColorSplitIndexGlobal, name);
                int atNew = (same & DupUpdate) != 0 ? sets.PreviousUpdateDiffuse : ReadUsed(ref reader, sets.Values, U3DConstants.ContextVertColorSplitType, U3DConstants.ContextVertColorSplitIndexLocal, U3DConstants.ContextVertColorSplitIndexGlobal, name);
                int atThird = (same & DupThird) != 0 ? sets.PreviousThirdDiffuse : ReadUsed(ref reader, sets.ThirdValues, U3DConstants.ContextVertColorSplitType, U3DConstants.ContextVertColorSplitIndexLocal, U3DConstants.ContextVertColorSplitIndexGlobal, name);
                (mesh.FaceDiffuse[splitCorner], mesh.FaceDiffuse[newCorner], mesh.FaceDiffuse[thirdCorner]) = (atSplit, atNew, atThird);
                (sets.PreviousSplitDiffuse, sets.PreviousUpdateDiffuse, sets.PreviousThirdDiffuse) = (atSplit, atNew, atThird);
            }

            if (shading.Specular)
            {
                // Read in the contexts of the diffuse colours, as the sample software does; not kept.
                byte same = reader.ReadCompressedU8(U3DConstants.ContextVertColorDupType);
                for (byte bit = DupSplit; bit <= DupThird; bit <<= 1)
                {
                    if ((same & bit) == 0)
                    {
                        byte type = reader.ReadCompressedU8(U3DConstants.ContextVertColorSplitType);
                        reader.ReadCompressedU32(type == UpdateLocal ? U3DConstants.ContextVertColorSplitIndexLocal : U3DConstants.ContextVertColorSplitIndexGlobal);
                    }
                }
            }

            for (int layer = 0; layer < shading.Layers; layer++)
            {
                byte same = reader.ReadCompressedU8(U3DConstants.ContextTexCoordDupType);
                int[] tex = mesh.FaceTex[layer]!;
                ValuesAt(mesh, shadings, split, tex, layer, sets.Values);
                ValuesAt(mesh, shadings, third, tex, layer, sets.ThirdValues);
                int atSplit = (same & DupSplit) != 0 ? sets.PreviousSplitTex : ReadUsed(ref reader, sets.Values, U3DConstants.ContextTexCoordSplitType, U3DConstants.ContextTexCoordSplitIndexLocal, U3DConstants.ContextTexCoordSplitIndexGlobal, name);
                int atNew = (same & DupUpdate) != 0 ? sets.PreviousUpdateTex : ReadUsed(ref reader, sets.Values, U3DConstants.ContextTexCoordSplitType, U3DConstants.ContextTexCoordSplitIndexLocal, U3DConstants.ContextTexCoordSplitIndexGlobal, name);
                int atThird = (same & DupThird) != 0 ? sets.PreviousThirdTex : ReadUsed(ref reader, sets.ThirdValues, U3DConstants.ContextTexCoordSplitType, U3DConstants.ContextTexCoordSplitIndexLocal, U3DConstants.ContextTexCoordSplitIndexGlobal, name);
                (tex[splitCorner], tex[newCorner], tex[thirdCorner]) = (atSplit, atNew, atThird);
                (sets.PreviousSplitTex, sets.PreviousUpdateTex, sets.PreviousThirdTex) = (atSplit, atNew, atThird);
            }
        }

        // The faces that move now meet at the new position, and the new faces at their three.
        mesh.EnsurePositions(current + 1);
        for (int i = 0; i < sets.MoveFaces.Count; i++)
        {
            mesh.RemoveFaceAt(split, sets.MoveFaces[i]);
            mesh.AddFaceAt(current, sets.MoveFaces[i]);
        }

        for (int j = 0; j < (int)newFaces; j++)
        {
            int at = (firstNew + j) * 3;
            mesh.AddFaceAt(mesh.FacePosition[at], firstNew + j);
            mesh.AddFaceAt(mesh.FacePosition[at + 1], firstNew + j);
            mesh.AddFaceAt(mesh.FacePosition[at + 2], firstNew + j);
        }

        // The positions whose normals this update gives: those of the faces at the new position, the faces
        // that moved still counted where they were.
        sets.NewPositions.Clear();
        for (int entry = mesh.FirstEntry(current); entry >= 0; entry = mesh.NextEntry(entry))
        {
            int at = mesh.EntryFace(entry) * 3;
            sets.NewPositions.Add(mesh.FacePosition[at]);
            sets.NewPositions.Add(mesh.FacePosition[at + 1]);
            sets.NewPositions.Add(mesh.FacePosition[at + 2]);
        }

        // The new position: a difference from the split position.
        byte positionSigns = reader.ReadCompressedU8(U3DConstants.ContextPositionDiffSigns);
        uint dx = reader.ReadCompressedU32(U3DConstants.ContextPositionDiffMagX);
        uint dy = reader.ReadCompressedU32(U3DConstants.ContextPositionDiffMagY);
        uint dz = reader.ReadCompressedU32(U3DConstants.ContextPositionDiffMagZ);
        float quant = declaration.PositionQuant;
        float px = 0, py = 0, pz = 0;
        if (current > 0)
        {
            (px, py, pz) = (mesh.Positions[split * 3], mesh.Positions[(split * 3) + 1], mesh.Positions[(split * 3) + 2]);
        }

        mesh.Positions[current * 3] = px + ((positionSigns & 1) != 0 ? -quant * dx : quant * dx);
        mesh.Positions[(current * 3) + 1] = py + ((positionSigns & 2) != 0 ? -quant * dy : quant * dy);
        mesh.Positions[(current * 3) + 2] = pz + ((positionSigns & 4) != 0 ? -quant * dz : quant * dz);
        mesh.PositionCount = current + 1;

        // The mesh at its new resolution.
        foreach ((int slot, int[] array, int value) in sets.Pending)
        {
            array[slot] = value;
        }

        mesh.FaceCount = firstNew + (int)newFaces;
        mesh.DiffuseCount += newDiffuse;
        mesh.SpecularCount += newSpecular;
        mesh.TexCoordCount += newTexCoords;

        if (!declaration.ExcludeNormals)
        {
            ReadNormalUpdates(ref reader, mesh, sets, declaration);
        }
    }

    /// <summary>A value among those in use at a position, or any, as its type says.</summary>
    private static int ReadUsed(ref U3DBitReader reader, DescendingSet used, uint typeContext, uint localContext, uint globalContext, string name)
    {
        return reader.ReadCompressedU8(typeContext) == UpdateLocal
            ? Member(used, reader.ReadCompressedU32(localContext), name)
            : (int)Math.Min(reader.ReadCompressedU32(globalContext), int.MaxValue);
    }

    private static void Average(Span<float> predicted, DescendingSet used, float[] pool, int poolCount)
    {
        predicted.Clear();
        if (used.Count == 0)
        {
            return;
        }

        for (int i = 0; i < used.Count; i++)
        {
            // An index the pool does not have counts as nothing but still divides.
            if ((uint)used[i] < (uint)poolCount)
            {
                for (int k = 0; k < 4; k++)
                {
                    predicted[k] += pool[(used[i] * 4) + k];
                }
            }
        }

        for (int k = 0; k < 4; k++)
        {
            predicted[k] /= used.Count;
        }
    }

    /// <summary>Four magnitudes in four contexts that follow one another, signed by the bits of <paramref name="signs"/>, added to a prediction.</summary>
    private static void ReadFour(ref U3DBitReader reader, uint firstContext, byte signs, float quant, scoped ReadOnlySpan<float> predicted, scoped Span<float> value)
    {
        for (int k = 0; k < 4; k++)
        {
            uint magnitude = reader.ReadCompressedU32(firstContext + (uint)k);
            value[k] = predicted[k] + ((signs & (1 << k)) != 0 ? -quant * magnitude : quant * magnitude);
        }
    }

    /// <summary>
    /// The normals around the new position (9.6.1.3.4.11): for each position of the neighbourhood a number
    /// of normals, each a rotation away from a prediction, and for each face there which of them it uses.
    /// The predictions are the normals of the faces at the position, gathered into as many groups as there
    /// are normals: the first face's, then each time the one farthest from those chosen, each then turned
    /// towards the faces nearest to it.
    /// </summary>
    private void ReadNormalUpdates(ref U3DBitReader reader, DetailMesh mesh, UpdateSets sets, MeshDeclaration declaration)
    {
        float quant = declaration.NormalQuant;
        for (int p = 0; p < sets.NewPositions.Count; p++)
        {
            int position = sets.NewPositions[p];
            sets.Faces.Clear();
            for (int entry = mesh.FirstEntry(position); entry >= 0; entry = mesh.NextEntry(entry))
            {
                sets.Faces.Add(mesh.EntryFace(entry));
            }

            int faces = sets.Faces.Count;
            uint local = reader.ReadCompressedU32(U3DConstants.ContextNumLocalNormals);
            if (local > (uint)faces)
            {
                throw Invalid($"U3D mesh '{declaration.Name}' gives position {position} {local} normals for {faces} faces.");
            }

            if (faces > sets.FaceNormals.Length)
            {
                sets.FaceNormals = new Vector3[faces * 2];
                sets.Predicted = new Vector3[faces * 2];
                sets.Contributions = new int[faces * 2];
            }

            Span<Vector3> faceNormals = sets.FaceNormals.AsSpan(0, Math.Max(faces, 1));
            Span<Vector3> predicted = sets.Predicted.AsSpan(0, Math.Max(faces, 1));
            Span<int> contributions = sets.Contributions.AsSpan(0, Math.Max(faces, 1));
            faceNormals.Clear();
            predicted.Clear();
            contributions.Clear();

            int valid = 0;
            for (int k = 0; k < faces; k++)
            {
                if (TryFaceNormal(mesh, sets.Faces[k], out Vector3 normal))
                {
                    faceNormals[valid++] = normal;
                }
            }

            int groups = 0;
            if (valid > 0)
            {
                predicted[groups++] = faceNormals[0];
            }

            while (groups < (int)local)
            {
                float farthest = 1.0f;
                int chosen = 0;
                for (int k = 0; k < valid; k++)
                {
                    float nearest = -2.0f;
                    for (int l = 0; l < groups; l++)
                    {
                        nearest = MathF.Max(nearest, Vector3.Dot(faceNormals[k], predicted[l]));
                    }

                    if (nearest < farthest)
                    {
                        farthest = nearest;
                        chosen = k;
                    }
                }

                predicted[groups++] = faceNormals[chosen];
            }

            for (int k = 0; k < valid; k++)
            {
                int closest = 0;
                float nearest = -2.0f;
                for (int l = 0; l < groups; l++)
                {
                    float d = Vector3.Dot(faceNormals[k], predicted[l]);
                    if (d > nearest)
                    {
                        nearest = d;
                        closest = l;
                    }
                }

                predicted[closest] = Interpolate(1.0f / (contributions[closest] + 1), predicted[closest], faceNormals[k]);
                contributions[closest]++;
            }

            mesh.Grow(ref mesh.Normals, mesh.NormalCount * 3, (mesh.NormalCount + (int)local) * 3);
            for (int k = 0; k < (int)local; k++)
            {
                byte signs = reader.ReadCompressedU8(U3DConstants.ContextNormalDiffSigns);
                float x = quant * reader.ReadCompressedU32(U3DConstants.ContextNormalDiffMagX);
                float y = quant * reader.ReadCompressedU32(U3DConstants.ContextNormalDiffMagY);
                float z = quant * reader.ReadCompressedU32(U3DConstants.ContextNormalDiffMagZ);
                x = (signs & 2) != 0 ? -x : x;
                y = (signs & 4) != 0 ? -y : y;
                z = (signs & 8) != 0 ? -z : z;
                float w = MathF.Sqrt(1.0f - MathF.Min((x * x) + (y * y) + (z * z), 1.0f));
                w = (signs & 1) != 0 ? -w : w;

                // The prediction as a quaternion without a real part, times the difference; its vector part.
                Vector3 a = predicted[k];
                int at = (mesh.NormalCount + k) * 3;
                mesh.Normals[at] = (a.X * w) + (a.Y * z) - (a.Z * y);
                mesh.Normals[at + 1] = (a.Y * w) + (a.Z * x) - (a.X * z);
                mesh.Normals[at + 2] = (a.Z * w) + (a.X * y) - (a.Y * x);
            }

            for (int k = 0; k < faces; k++)
            {
                uint best = reader.ReadCompressedU32(U3DConstants.ContextNormalLocalIndex);
                int corner = CornerAt(mesh, sets.Faces[k], position);
                if (corner >= 0)
                {
                    mesh.FaceNormal[corner] = (int)Math.Min(mesh.NormalCount + (long)best, int.MaxValue);
                }
            }

            mesh.NormalCount += (int)local;
        }
    }

    /// <summary>The unit normal of a face; false for a face without area (the sample software's own test of it).</summary>
    private static bool TryFaceNormal(DetailMesh mesh, int face, out Vector3 normal)
    {
        const float Epsilon = 0.0000001f;
        normal = default;
        int at = face * 3;
        Vector3 a = Position(mesh, mesh.FacePosition[at]), b = Position(mesh, mesh.FacePosition[at + 1]), c = Position(mesh, mesh.FacePosition[at + 2]);
        Vector3 d1 = b - a, d2 = c - a;
        if (IsZero(d1, Epsilon) || IsZero(d2, Epsilon))
        {
            return false;
        }

        d1 *= 1.0f / d1.Length();
        d2 *= 1.0f / d2.Length();
        normal = Vector3.Cross(d1, d2);
        if (IsZero(normal, Epsilon))
        {
            return false;
        }

        normal *= 1.0f / normal.Length();
        return true;

        static bool IsZero(Vector3 v, float margin) => MathF.Abs(v.X) < margin && MathF.Abs(v.Y) < margin && MathF.Abs(v.Z) < margin;

        static Vector3 Position(DetailMesh mesh, int index) => (uint)index < (uint)mesh.PositionCount
            ? new Vector3(mesh.Positions[index * 3], mesh.Positions[(index * 3) + 1], mesh.Positions[(index * 3) + 2])
            : default;
    }

    /// <summary>Between two directions, along the arc: the sample software's quaternion interpolation, for quaternions without a real part.</summary>
    private static Vector3 Interpolate(float t, Vector3 from, Vector3 to)
    {
        float cosine = Vector3.Dot(from, to);
        if (cosine < 0.0f)
        {
            cosine = -cosine;
            to = -to;
        }

        float scale0 = 1.0f - t, scale1 = t;
        if (1.0f - cosine > 1e-6f)
        {
            float omega = MathF.Acos(cosine), sine = MathF.Sin(omega);
            scale0 = MathF.Sin((1.0f - t) * omega) / sine;
            scale1 = MathF.Sin(t * omega) / sine;
        }

        return (from * scale0) + (to * scale1);
    }
}
