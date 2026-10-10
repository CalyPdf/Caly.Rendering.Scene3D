﻿using System.Buffers;
using System.Buffers.Binary;
using Caly.Rendering.Scene3D.Internal;

namespace Caly.Rendering.Scene3D.U3D.Internal;

internal sealed partial class U3DDocument
{
    private sealed class RHMeshDeclaration
    {
        public required string Name { get; init; }

        public required U3DBlock Block { get; init; }

        public required int PayloadOffset { get; init; }

        public int SceneMesh { get; set; } = NotBuilt;
    }

    private readonly record struct RHMaterial(int Layers, bool Diffuse, bool Specular, uint OriginalShadingId);

    private Dictionary<string, RHMeshDeclaration>? _rhMeshes;
    private List<uint>? _customRHMeshBlockTypes;

    private void ReadNewObjectType(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        if (string.Equals(name, "RHAdobeMeshResource", StringComparison.Ordinal))
        {
            reader.ReadU32(); // modifier type
            Span<byte> guid = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
            {
                guid[i] = reader.ReadU8();
            }

            uint declBlockType = reader.ReadU32();
            if (declBlockType != 0)
            {
                _customRHMeshBlockTypes ??= [];
                _customRHMeshBlockTypes.Add(declBlockType);
            }
        }
        else
        {
            _skippedBlocks[block.Type] = _skippedBlocks.GetValueOrDefault(block.Type) + 1;
        }
    }

    private void ReadRHAdobeMeshResource(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        int payloadOffset = reader.BytePosition;

        _rhMeshes ??= new(StringComparer.Ordinal);
        _rhMeshes[name] = new RHMeshDeclaration
        {
            Name = name,
            Block = block,
            PayloadOffset = payloadOffset
        };
    }

    private int BuildRHMesh(SceneBuilder builder, RHMeshDeclaration declaration)
    {
        ReadOnlySpan<byte> data = _data.Span.Slice(declaration.Block.DataOffset + declaration.PayloadOffset, declaration.Block.DataLength - declaration.PayloadOffset);
        int pos = 0;

        if (pos >= data.Length)
        {
            throw Truncated($"U3D RH mesh '{declaration.Name}' is truncated.");
        }

        byte chunkFlags = data[pos++];
        int version = chunkFlags & 15;
        if (version != 0)
        {
            throw Invalid($"U3D RH mesh '{declaration.Name}' has unsupported version {version}.");
        }

        if ((chunkFlags & 16) != 0)
        {
            if (pos + 6 > data.Length)
            {
                throw Truncated($"U3D RH mesh '{declaration.Name}' subchunk header is truncated.");
            }

            pos += 4; // size of mesh description block
            ushort subchunkCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos));
            pos += 2;
            if (pos + subchunkCount * 6 > data.Length)
            {
                throw Truncated($"U3D RH mesh '{declaration.Name}' subchunks header is truncated.");
            }

            pos += subchunkCount * 6;
        }

        int mt = (chunkFlags >> 6) & 3;
        int materialsCount;
        if (mt == 0)
        {
            materialsCount = 1;
        }
        else
        {
            int mtBytes = mt == 1 ? 1 : (mt == 2 ? 2 : 4);
            uint declaredMaterials = ReadVarUInt(data, ref pos, mtBytes, declaration.Name);
            if (declaredMaterials > (uint)data.Length)
            {
                throw Truncated($"U3D RH mesh '{declaration.Name}' declares {declaredMaterials} materials.");
            }

            materialsCount = (int)declaredMaterials;
        }

        if (materialsCount <= 0)
        {
            materialsCount = 1;
        }

        if ((long)materialsCount * 3 > data.Length - pos)
        {
            throw Truncated($"U3D RH mesh '{declaration.Name}' declares {materialsCount} materials but is too short to hold them.");
        }

        var mats = new RHMaterial[materialsCount];
        for (int i = 0; i < materialsCount; i++)
        {
            if (pos >= data.Length)
            {
                throw Truncated($"U3D RH mesh '{declaration.Name}' material declaration is truncated.");
            }

            byte mf = data[pos++];
            int layerBytes = ((mf >> 4) & 3) + 1;
            int shadingIdBytes = ((mf >> 2) & 3) + 1;
            uint layers = ReadVarUInt(data, ref pos, layerBytes, declaration.Name);
            uint originalShadingId = ReadVarUInt(data, ref pos, shadingIdBytes, declaration.Name);

            if (layers > 1)
            {
                int extraDimBytes = (int)((layers - 1 + 3) / 4);
                if (pos + extraDimBytes > data.Length)
                {
                    throw Truncated($"U3D RH mesh '{declaration.Name}' texture dimensions array is truncated.");
                }

                pos += extraDimBytes;
            }

            mats[i] = new RHMaterial((int)layers, (mf & 64) != 0, (mf & 128) != 0, originalShadingId);
        }

        if (pos >= data.Length)
        {
            throw Truncated($"U3D RH mesh '{declaration.Name}' values flags are missing.");
        }

        byte vf = data[pos++];
        int w = ((vf >> 6) & 3) + 1;
        uint faceCount = (vf & 1) != 0 ? ReadVarUInt(data, ref pos, w, declaration.Name) : 0;
        uint positionCount = (vf & 2) != 0 ? ReadVarUInt(data, ref pos, w, declaration.Name) : 0;
        uint normalCount = (vf & 4) != 0 ? ReadVarUInt(data, ref pos, w, declaration.Name) : 0;
        uint diffuseCount = (vf & 8) != 0 ? ReadVarUInt(data, ref pos, w, declaration.Name) : 0;
        uint specularCount = (vf & 16) != 0 ? ReadVarUInt(data, ref pos, w, declaration.Name) : 0;
        uint texCoordCount = (vf & 32) != 0 ? ReadVarUInt(data, ref pos, w, declaration.Name) : 0;

        long maxElements = Math.Min(_limits.MaxVertices, int.MaxValue / 4);
        if (normalCount > maxElements || diffuseCount > maxElements || specularCount > maxElements || texCoordCount > maxElements)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D RH mesh '{declaration.Name}' declares more normals, colors or texture coordinates than the limit allows.");
        }

        if (faceCount == 0 || positionCount < 3)
        {
            if (faceCount > 0)
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "mesh-data-missing", $"U3D RH mesh '{declaration.Name}' declares {faceCount} faces but has fewer than 3 positions.");
            }
            else
            {
                builder.Report(SceneDiagnosticSeverity.Info, "mesh-empty", $"U3D RH mesh '{declaration.Name}' has no faces.");
            }

            return -1;
        }

        if (faceCount * 3L > Math.Min(_limits.MaxIndices, int.MaxValue))
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D RH mesh '{declaration.Name}' has {faceCount} faces, more than the limit allows.");
        }

        if (positionCount > Math.Min(_limits.MaxVertices, int.MaxValue))
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D RH mesh '{declaration.Name}' has {positionCount} positions, more than the limit allows.");
        }

        float[]? rentedPositions = null, rentedNormals = null, rentedColors = null, rentedTexCoords = null;
        uint[]? rentedFaceMaterials = null;
        uint[]? rentedPosIndices = null, rentedNormIndices = null, rentedDiffIndices = null, rentedTexIndices = null;

        try
        {
            float[] posArray = ArrayPool<float>.Shared.Rent((int)positionCount * 3);
            rentedPositions = posArray;
            Span<float> positions = posArray.AsSpan(0, (int)positionCount * 3);
            DecodeFloats(data, ref pos, positions, (int)positionCount, comps: 3, "positions", declaration.Name);

            ReadOnlySpan<float> normals = default;
            if (normalCount > 0)
            {
                float[] normArray = ArrayPool<float>.Shared.Rent((int)normalCount * 3);
                rentedNormals = normArray;
                Span<float> normSpan = normArray.AsSpan(0, (int)normalCount * 3);
                DecodeFloats(data, ref pos, normSpan, (int)normalCount, comps: 3, "normals", declaration.Name);
                normals = normSpan;
            }

            ReadOnlySpan<float> colors = default;
            if (diffuseCount > 0)
            {
                float[] colArray = ArrayPool<float>.Shared.Rent((int)diffuseCount * 4);
                rentedColors = colArray;
                Span<float> colSpan = colArray.AsSpan(0, (int)diffuseCount * 4);
                DecodeFloats(data, ref pos, colSpan, (int)diffuseCount, comps: 4, "diffuse", declaration.Name);
                colors = colSpan;
            }

            if (specularCount > 0)
            {
                float[] specArray = ArrayPool<float>.Shared.Rent((int)specularCount * 4);
                try
                {
                    DecodeFloats(data, ref pos, specArray.AsSpan(0, (int)specularCount * 4), (int)specularCount, comps: 4, "specular", declaration.Name);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(specArray);
                }
            }

            ReadOnlySpan<float> texCoords = default;
            if (texCoordCount > 0)
            {
                float[] texArray = ArrayPool<float>.Shared.Rent((int)texCoordCount * 4);
                rentedTexCoords = texArray;
                Span<float> texSpan = texArray.AsSpan(0, (int)texCoordCount * 4);
                DecodeFloats(data, ref pos, texSpan, (int)texCoordCount, comps: 4, "texcoords", declaration.Name);
                texCoords = texSpan;
            }

            uint[] faceMats = ArrayPool<uint>.Shared.Rent((int)faceCount);
            rentedFaceMaterials = faceMats;
            Span<uint> faceMaterialIds = faceMats.AsSpan(0, (int)faceCount);
            if (materialsCount > 1)
            {
                DecodeInts(data, ref pos, faceMaterialIds, (int)faceCount, vertical: 0, bound: (uint)materialsCount, isFloat: false, declaration.Name);
            }
            else
            {
                faceMaterialIds.Clear();
            }

            uint[] posIdxs = ArrayPool<uint>.Shared.Rent((int)faceCount * 3);
            rentedPosIndices = posIdxs;
            Span<uint> positionIndices = posIdxs.AsSpan(0, (int)faceCount * 3);
            DecodeInts(data, ref pos, positionIndices, (int)faceCount * 3, vertical: 3, bound: positionCount, isFloat: false, declaration.Name);

            Span<uint> normalIndices = default;
            if (normalCount > 0)
            {
                uint[] normIdxs = ArrayPool<uint>.Shared.Rent((int)faceCount * 3);
                rentedNormIndices = normIdxs;
                normalIndices = normIdxs.AsSpan(0, (int)faceCount * 3);
                DecodeInts(data, ref pos, normalIndices, (int)faceCount * 3, vertical: 3, bound: normalCount, isFloat: false, declaration.Name);
            }

            long nd = 0;
            for (int f = 0; f < (int)faceCount; f++)
            {
                uint m = faceMaterialIds[f];
                if (m < (uint)materialsCount && mats[m].Diffuse)
                {
                    nd++;
                }
            }

            Span<uint> diffuseIndices = default;
            if (nd > 0 && diffuseCount > 0)
            {
                uint[] diffIdxs = ArrayPool<uint>.Shared.Rent((int)nd * 3);
                rentedDiffIndices = diffIdxs;
                diffuseIndices = diffIdxs.AsSpan(0, (int)nd * 3);
                DecodeInts(data, ref pos, diffuseIndices, (int)nd * 3, vertical: 3, bound: diffuseCount, isFloat: false, declaration.Name);
            }

            long ns = 0;
            for (int f = 0; f < (int)faceCount; f++)
            {
                uint m = faceMaterialIds[f];
                if (m < (uint)materialsCount && mats[m].Specular)
                {
                    ns++;
                }
            }

            if (ns > 0 && specularCount > 0)
            {
                uint[] specIdxs = ArrayPool<uint>.Shared.Rent((int)ns * 3);
                try
                {
                    DecodeInts(data, ref pos, specIdxs.AsSpan(0, (int)ns * 3), (int)ns * 3, vertical: 3, bound: specularCount, isFloat: false, declaration.Name);
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(specIdxs);
                }
            }

            long nt = 0;
            for (int f = 0; f < (int)faceCount; f++)
            {
                uint m = faceMaterialIds[f];
                if (m < (uint)materialsCount)
                {
                    nt += (uint)mats[m].Layers;
                }
            }

            if (nt * 3 > Math.Min(_limits.MaxIndices, int.MaxValue))
            {
                throw new Scene3DException(Scene3DError.LimitExceeded, $"U3D RH mesh '{declaration.Name}' has more texture layers than the limit allows.");
            }

            Span<uint> texCoordIndices = default;
            if (nt > 0 && texCoordCount > 0)
            {
                uint[] texIdxs = ArrayPool<uint>.Shared.Rent((int)nt * 3);
                rentedTexIndices = texIdxs;
                texCoordIndices = texIdxs.AsSpan(0, (int)nt * 3);
                DecodeInts(data, ref pos, texCoordIndices, (int)nt * 3, vertical: 3, bound: texCoordCount, isFloat: false, declaration.Name);
            }

            bool anyDiffuse = false, anyTexCoords = false;
            foreach (RHMaterial mat in mats)
            {
                anyDiffuse |= mat.Diffuse;
                anyTexCoords |= mat.Layers > 0;
            }

            bool useNormals = normalCount > 0;
            bool useColors = anyDiffuse && diffuseCount > 0;
            bool useTexCoords = anyTexCoords && texCoordCount > 0;

            SceneMeshFlags flags = SceneMeshFlags.None;
            flags |= useNormals ? SceneMeshFlags.HasNormals : 0;
            flags |= useColors ? SceneMeshFlags.HasColors : 0;
            flags |= useTexCoords ? SceneMeshFlags.HasTexCoords : 0;

            var unifier = new VertexUnifier((int)Math.Max(positionCount, Math.Max(normalCount, texCoordCount)), _limits.MaxVertices);
            try
            {
                builder.BeginMesh(builder.AddName(declaration.Name), flags);
                Span<uint> indices = builder.AllocateIndices((int)faceCount * 3);

                int diffFaceIdx = 0;
                int texLayerIdx = 0;
                int corner = 0;

                for (int face = 0; face < (int)faceCount; face++)
                {
                    if ((face & (CancellationInterval - 1)) == 0)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                    }

                    uint matIdx = faceMaterialIds[face];
                    if (matIdx >= (uint)materialsCount)
                    {
                        throw Invalid($"U3D RH mesh '{declaration.Name}' face {face} has invalid material index {matIdx}; mesh has {materialsCount}.");
                    }

                    RHMaterial mat = mats[matIdx];
                    for (int k = 0; k < 3; k++)
                    {
                        uint posIdx = positionIndices[face * 3 + k];
                        if (posIdx >= positionCount)
                        {
                            throw Invalid($"U3D RH mesh '{declaration.Name}' face {face} references position {posIdx} of {positionCount}.");
                        }

                        int normIdx = -1;
                        if (useNormals)
                        {
                            uint nIdx = normalIndices[face * 3 + k];
                            if (nIdx >= normalCount)
                            {
                                throw Invalid($"U3D RH mesh '{declaration.Name}' face {face} references normal {nIdx} of {normalCount}.");
                            }

                            normIdx = (int)nIdx;
                        }

                        int colorIdx = -1;
                        if (mat.Diffuse && useColors && !diffuseIndices.IsEmpty)
                        {
                            uint cIdx = diffuseIndices[diffFaceIdx * 3 + k];
                            if (cIdx >= diffuseCount)
                            {
                                throw Invalid($"U3D RH mesh '{declaration.Name}' face {face} references diffuse color {cIdx} of {diffuseCount}.");
                            }

                            colorIdx = (int)cIdx;
                        }

                        int texIdx = -1;
                        if (mat.Layers > 0 && useTexCoords && !texCoordIndices.IsEmpty)
                        {
                            for (int l = 0; l < mat.Layers; l++)
                            {
                                uint tIdx = texCoordIndices[(texLayerIdx + l) * 3 + k];
                                if (l == 0)
                                {
                                    if (tIdx >= texCoordCount)
                                    {
                                        throw Invalid($"U3D RH mesh '{declaration.Name}' face {face} references texCoord {tIdx} of {texCoordCount}.");
                                    }

                                    texIdx = (int)tIdx;
                                }
                            }
                        }

                        indices[corner++] = (uint)unifier.GetOrAdd((int)posIdx, normIdx, colorIdx, texIdx);
                    }

                    if (mat.Diffuse && diffuseCount > 0)
                    {
                        diffFaceIdx++;
                    }

                    if (mat.Layers > 0 && texCoordCount > 0)
                    {
                        texLayerIdx += mat.Layers;
                    }
                }

                int nonFinite = FillVertices(builder.AllocateVertices(unifier.Count), unifier.Tuples, positions, normals, colors, texCoords);

                int[]? faceShadings = null;
                ReadOnlySpan<int> shadingOfFace;
                if (materialsCount > 1)
                {
                    faceShadings = ArrayPool<int>.Shared.Rent((int)faceCount);
                    Span<int> fsh = faceShadings.AsSpan(0, (int)faceCount);
                    for (int i = 0; i < (int)faceCount; i++)
                    {
                        fsh[i] = (int)faceMaterialIds[i];
                    }

                    shadingOfFace = fsh;
                }
                else
                {
                    shadingOfFace = default;
                }

                try
                {
                    AddParts(builder, indices, shadingOfFace, materialsCount, grouped: false);
                }
                finally
                {
                    if (faceShadings is not null)
                    {
                        ArrayPool<int>.Shared.Return(faceShadings);
                    }
                }

                int mesh = builder.EndMesh();
                if (nonFinite > 0)
                {
                    builder.Report(SceneDiagnosticSeverity.Warning, "non-finite-geometry", $"U3D RH mesh '{declaration.Name}' has {nonFinite} vertices with a NaN or infinite coordinate, set to zero.");
                }

                return mesh;
            }
            finally
            {
                unifier.Dispose();
            }
        }
        finally
        {
            if (rentedPositions is not null) ArrayPool<float>.Shared.Return(rentedPositions);
            if (rentedNormals is not null) ArrayPool<float>.Shared.Return(rentedNormals);
            if (rentedColors is not null) ArrayPool<float>.Shared.Return(rentedColors);
            if (rentedTexCoords is not null) ArrayPool<float>.Shared.Return(rentedTexCoords);
            if (rentedFaceMaterials is not null) ArrayPool<uint>.Shared.Return(rentedFaceMaterials);
            if (rentedPosIndices is not null) ArrayPool<uint>.Shared.Return(rentedPosIndices);
            if (rentedNormIndices is not null) ArrayPool<uint>.Shared.Return(rentedNormIndices);
            if (rentedDiffIndices is not null) ArrayPool<uint>.Shared.Return(rentedDiffIndices);
            if (rentedTexIndices is not null) ArrayPool<uint>.Shared.Return(rentedTexIndices);
        }
    }

    private void DecodeFloats(ReadOnlySpan<byte> data, ref int pos, Span<float> destination, int count, int comps, string what, string meshName)
    {
        if (count == 0) return;
        if (pos >= data.Length) throw Truncated($"U3D RH mesh '{meshName}' {what} float header is truncated.");

        byte type = data[pos++];
        switch (type)
        {
            case 0: // Uncompressed F32
            {
                int totalFloats = count * comps;
                int byteSize = totalFloats * 4;
                if (pos + byteSize > data.Length)
                {
                    throw Truncated($"U3D RH mesh '{meshName}' {what} float data is truncated.");
                }

                ReadOnlySpan<byte> floatBytes = data.Slice(pos, byteSize);
                pos += byteSize;

                for (int c = 0; c < comps; c++)
                {
                    int compByteOffset = c * count * 4;
                    for (int i = 0; i < count; i++)
                    {
                        destination[i * comps + c] = BinaryPrimitives.ReadSingleLittleEndian(floatBytes.Slice(compByteOffset + i * 4));
                    }
                }
                break;
            }

            case 2: // Quantized floats
            {
                for (int c = 0; c < comps; c++)
                {
                    if (pos + 8 > data.Length)
                    {
                        throw Truncated($"U3D RH mesh '{meshName}' {what} float min/max is truncated.");
                    }

                    float min = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(pos));
                    float max = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(pos + 4));
                    pos += 8;

                    if (min == max)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            destination[i * comps + c] = min;
                        }
                    }
                    else
                    {
                        if (pos + 4 > data.Length)
                        {
                            throw Truncated($"U3D RH mesh '{meshName}' {what} numQuants is truncated.");
                        }

                        uint numQuants = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
                        pos += 4;
                        if (numQuants == 0)
                        {
                            throw Invalid($"U3D RH mesh '{meshName}' {what} numQuants is zero.");
                        }

                        uint[] qRented = ArrayPool<uint>.Shared.Rent(count);
                        try
                        {
                            Span<uint> qSpan = qRented.AsSpan(0, count);
                            DecodeInts(data, ref pos, qSpan, count, vertical: 0, bound: numQuants, isFloat: true, meshName);
                            float scale = (max - min) / numQuants;
                            for (int i = 0; i < count; i++)
                            {
                                destination[i * comps + c] = min + qSpan[i] * scale;
                            }
                        }
                        finally
                        {
                            ArrayPool<uint>.Shared.Return(qRented);
                        }
                    }
                }
                break;
            }

            case 3: // Quantized alpha-theta normals
            {
                if (comps != 3)
                {
                    throw Invalid($"U3D RH mesh '{meshName}' {what} float type 3 is only valid for 3D normals.");
                }

                if (pos + 4 > data.Length)
                {
                    throw Truncated($"U3D RH mesh '{meshName}' {what} numQuants is truncated.");
                }

                uint numQuants = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
                pos += 4;
                if (numQuants == 0)
                {
                    throw Invalid($"U3D RH mesh '{meshName}' {what} numQuants is zero.");
                }

                uint[] qRented = ArrayPool<uint>.Shared.Rent(count * 2);
                try
                {
                    Span<uint> qSpan = qRented.AsSpan(0, count * 2);
                    DecodeInts(data, ref pos, qSpan, count * 2, vertical: 0, bound: numQuants, isFloat: true, meshName);
                    double k = (2.0 * Math.PI) / numQuants;
                    for (int i = 0; i < count; i++)
                    {
                        double a = qSpan[i] * k;
                        double th = qSpan[count + i] * k;
                        float sinTh = (float)Math.Sin(th);
                        destination[i * 3 + 0] = sinTh * (float)Math.Cos(a);
                        destination[i * 3 + 1] = sinTh * (float)Math.Sin(a);
                        destination[i * 3 + 2] = (float)Math.Cos(th);
                    }
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(qRented);
                }
                break;
            }

            default:
                throw Invalid($"U3D RH mesh '{meshName}' {what} unknown float encoding type {type}.");
        }
    }

    private void DecodeInts(ReadOnlySpan<byte> data, ref int pos, Span<uint> destination, int count, int vertical, uint bound, bool isFloat, string meshName)
    {
        if (count == 0) return;
        if (pos >= data.Length) throw Truncated($"U3D RH mesh '{meshName}' int header is truncated.");

        byte h = data[pos++];
        int type = h & 0x3F;
        int sizeBytes = ((h >> 6) & 3) + 1;

        if (type == 0) // All zero
        {
            ReadVarUInt(data, ref pos, sizeBytes, meshName);
            destination.Clear();
            return;
        }

        if (type == 1) // Replicate U32
        {
            ReadVarUInt(data, ref pos, sizeBytes, meshName);
            if (pos + 4 > data.Length) throw Truncated($"U3D RH mesh '{meshName}' replicate value is truncated.");
            uint val = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
            pos += 4;
            destination.Fill(val);
            return;
        }

        uint size = ReadVarUInt(data, ref pos, sizeBytes, meshName);
        if (pos + size > data.Length)
        {
            throw Truncated($"U3D RH mesh '{meshName}' int data of {size} bytes is truncated.");
        }

        ReadOnlySpan<byte> slice = data.Slice(pos, (int)size);
        pos += (int)size;

        long needed = type switch
        {
            2 => ((long)count + 1) / 2,
            3 => count,
            4 => count * 2L,
            5 => count * 3L,
            6 => count * 4L,
            _ => 0
        };

        if (slice.Length < needed)
        {
            throw Truncated($"U3D RH mesh '{meshName}' int data of type {type} holds {slice.Length} bytes, {needed} are needed.");
        }

        switch (type)
        {
            case 2: // U4
            {
                for (int i = 0; i < count; i++)
                {
                    byte b = slice[i >> 1];
                    destination[i] = (uint)((i & 1) != 0 ? (b >> 4) : (b & 0x0F));
                }
                break;
            }

            case 3: // U8
            {
                for (int i = 0; i < count; i++)
                {
                    destination[i] = slice[i];
                }
                break;
            }

            case 4: // U16
            {
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(i * 2));
                }
                break;
            }

            case 5: // U24
            {
                for (int i = 0; i < count; i++)
                {
                    int off = i * 3;
                    destination[i] = (uint)(slice[off] | (slice[off + 1] << 8) | (slice[off + 2] << 16));
                }
                break;
            }

            case 6: // U32
            {
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(i * 4));
                }
                break;
            }

            case 7: // RH39
            {
                DecodeRH39(slice, destination, count, meshName);
                break;
            }

            case 8: // Arithmetic Static
            {
                uint context = U3DConstants.StaticFull + bound + (isFloat ? 1u : 0u);
                _contexts.Reset();
                var bitReader = new U3DBitReader(slice, _contexts, noCompression: false);
                for (int i = 0; i < count; i++)
                {
                    destination[i] = bitReader.ReadCompressedU32(context);
                }
                break;
            }

            case 9: // Arithmetic Dynamic
            {
                uint context = 1;
                int width = bound <= 255 ? 8 : (bound <= 65535 ? 16 : 32);
                _contexts.Reset();
                var bitReader = new U3DBitReader(slice, _contexts, noCompression: false);
                if (width == 8)
                {
                    for (int i = 0; i < count; i++)
                    {
                        destination[i] = bitReader.ReadCompressedU8(context);
                    }
                }
                else if (width == 16)
                {
                    for (int i = 0; i < count; i++)
                    {
                        destination[i] = bitReader.ReadCompressedU16(context);
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        destination[i] = bitReader.ReadCompressedU32(context);
                    }
                }
                break;
            }

            case 10: // UIC1
            {
                DecodeUIC1(slice, destination, count, meshName);
                break;
            }

            default:
                throw Invalid($"U3D RH mesh '{meshName}' unknown int encoding type {type}.");
        }
    }

    private static void DecodeRH39(ReadOnlySpan<byte> slice, Span<uint> destination, int count, string meshName)
    {
        int pos = 0;
        uint offset = 0;
        int outIdx = 0;

        while (outIdx < count)
        {
            if (pos >= slice.Length)
            {
                while (outIdx < count) destination[outIdx++] = offset;
                break;
            }

            byte b = slice[pos++];
            int op = (b >> 4) & 7;
            int len = b & 15;
            if ((b & 0x80) != 0)
            {
                if (pos >= slice.Length)
                {
                    throw Truncated($"U3D RH mesh '{meshName}' RH39 length is truncated.");
                }

                len = 17 + (len << 8) + slice[pos++];
            }
            else
            {
                len++;
            }

            if (op == 7) // Offset
            {
                offset = ReadVarUInt(slice, ref pos, len, meshName);
                continue;
            }

            byte b4 = 0;
            int bytesPerItem = op switch { 6 or 2 => 1, 4 or 3 => 2, 0 => 4, 1 => 0, _ => 0 };
            for (int i = 0; i < len && outIdx < count; i++)
            {
                Require(slice, pos, op == 1 ? ((i & 1) != 0 ? 0 : 1) : bytesPerItem, meshName);
                uint val;
                switch (op)
                {
                    case 6: // Raw U8
                        val = slice[pos++];
                        break;
                    case 4: // Raw U16
                        val = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(pos));
                        pos += 2;
                        break;
                    case 0: // Raw U32
                        val = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(pos));
                        pos += 4;
                        break;
                    case 2: // Offset + U8
                        val = offset + slice[pos++];
                        break;
                    case 3: // Offset + U16
                        val = offset + BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(pos));
                        pos += 2;
                        break;
                    case 5: // Offset
                        val = offset;
                        break;
                    default: // op == 1: Offset + U4
                        if ((i & 1) != 0)
                        {
                            val = offset + (uint)(b4 >> 4);
                        }
                        else
                        {
                            b4 = slice[pos++];
                            val = offset + (uint)(b4 & 0x0F);
                        }
                        break;
                }

                destination[outIdx++] = val;
            }
        }
    }

    private static void DecodeUIC1(ReadOnlySpan<byte> slice, Span<uint> destination, int count, string meshName)
    {
        Span<uint> prev = stackalloc uint[32];
        for (int i = 0; i < 32; i++)
        {
            prev[i] = (uint)((32 - i) % 32);
        }

        Span<int> deltas = stackalloc int[32];
        deltas.Clear();
        int cv = 0, cd = 0;
        int queue = 0;
        int qv = 0;
        uint value = 0;
        int pos = 0;
        int outIdx = 0;

        while (outIdx < count)
        {
            uint pv = value;
            uint vert = outIdx >= 3 ? destination[outIdx - 3] : 0;
            if (queue > 0)
            {
                queue--;
                value = prev[(cv - qv) & 31];
            }
            else
            {
                if (pos >= slice.Length)
                {
                    while (outIdx < count) destination[outIdx++] = value;
                    break;
                }

                byte cmd = slice[pos++];
                int op = cmd >> 4;
                int c = cmd & 15;
                switch (c)
                {
                    case 0:
                        value = prev[(cv - op) & 31];
                        break;
                    case 5:
                        value = prev[(cv - 16 - op) & 31];
                        break;
                    case 13:
                        value = prev[(cv - (op >> 2)) & 31];
                        qv = op & 3;
                        queue = 1;
                        break;
                    case 14:
                        value = prev[cv & 31];
                        qv = 0;
                        queue = op + 2;
                        break;
                    case 6:
                        value = (uint)(value + deltas[(cd - op) & 31]);
                        break;
                    case 1:
                        value = (uint)(value + ((cmd & 16) != 0 ? ((cmd >> 5) + 1) : -((cmd >> 5) + 1)));
                        break;
                    case 3:
                        value = (uint)(vert + op + 1);
                        break;
                    case 4:
                        value = (uint)(vert - op - 1);
                        break;
                    case 7:
                        value = (uint)(vert + op + 17);
                        break;
                    case 8:
                        value = (uint)(vert - op - 17);
                        break;
                    case 9:
                        Require(slice, pos, 1, meshName);
                        value = (uint)(value + op + (slice[pos++] << 4) + 3);
                        break;
                    case 10:
                        Require(slice, pos, 1, meshName);
                        value = (uint)(value - (op + (slice[pos++] << 4) + 3));
                        break;
                    case 11:
                        Require(slice, pos, 1, meshName);
                        value = (uint)(op + (slice[pos++] << 4));
                        break;
                    case 12:
                        Require(slice, pos, 2, meshName);
                        value = (uint)(op + (BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(pos)) << 4));
                        pos += 2;
                        break;
                    case 15:
                        if (op <= 4)
                        {
                            value = (uint)op;
                        }
                        else if (op <= 10)
                        {
                            value = op switch
                            {
                                5 => 0x000000FF,
                                6 => 0x0000FF00,
                                7 => 0x00FF0000,
                                8 => 0xFF000000,
                                9 => 0x00FFFFFF,
                                _ => 0xFFFFFFFF
                            };
                        }
                        else if (op == 13)
                        {
                            Require(slice, pos, 2, meshName);
                            qv = 0;
                            queue = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(pos)) + 36;
                            pos += 2;
                            value = prev[cv & 31];
                        }
                        else if (op == 11)
                        {
                            Require(slice, pos, 3, meshName);
                            byte bl = slice[pos++];
                            ushort hi = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(pos));
                            pos += 2;
                            value = ((uint)hi << 8) | bl;
                        }
                        else if (op == 12)
                        {
                            Require(slice, pos, 4, meshName);
                            value = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(pos));
                            pos += 4;
                        }
                        else
                        {
                            throw new Scene3DException(Scene3DError.InvalidValue, $"UIC1 invalid extended command {op}.");
                        }
                        break;
                    default:
                        throw new Scene3DException(Scene3DError.InvalidValue, $"UIC1 invalid command {c}.");
                }
            }

            cv = (cv + 1) & 31;
            prev[cv] = value;
            if (value != pv)
            {
                int d = unchecked((int)(value - pv));
                if (d > -int.MaxValue && d < int.MaxValue)
                {
                    cd = (cd + 1) & 31;
                    deltas[cd] = d;
                }
            }

            destination[outIdx++] = value;
        }
    }

    private static void Require(ReadOnlySpan<byte> slice, int pos, int bytes, string meshName)
    {
        if (pos + bytes > slice.Length)
        {
            throw Truncated($"U3D RH mesh '{meshName}' int data is truncated.");
        }
    }

    private static uint ReadVarUInt(ReadOnlySpan<byte> data, ref int pos, int bytes, string meshName)
    {
        if (pos + bytes > data.Length)
        {
            throw Truncated($"U3D RH mesh '{meshName}' is truncated.");
        }

        uint value = bytes switch
        {
            1 => data[pos],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos)),
            3 => (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos)),
            _ => throw Invalid($"U3D RH mesh '{meshName}' invalid variable byte length {bytes}.")
        };

        pos += bytes;
        return value;
    }
}
