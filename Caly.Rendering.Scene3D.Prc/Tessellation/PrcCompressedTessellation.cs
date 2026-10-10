using System.Buffers;
using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Prc.Generated;

internal static partial class PrcReaders
{
    /// <summary>
    /// Reads a compressed tessellation (<c>PRC_TYPE_TESS_3D_Compressed</c>, "highly compressed") and
    /// keeps what it holds; the mesh is rebuilt by the compressed mesher. The entity
    /// has no length, and three of its arrays size later ones: the number of triangles, the number of
    /// faces (the highest face index a triangle names, plus one) and the number of points that refer to
    /// another point.
    /// <para>
    /// The order and encoding of the fields is that of prc.json (prc-rs, MIT; see
    /// THIRD-PARTY-NOTICES.md), including what it corrects of the ISO text (PDF Association issues
    /// #427, #436, #705, #727).
    /// </para>
    /// </summary>
    internal static PrcTess3DCompressed ReadTess3DCompressed(ref PrcBitReader r, PrcContext ctx)
    {
        ctx.Enter("PRC_TYPE_TESS_3D_Compressed", r.BitPosition);
        ctx.Expect(r.ReadUnsignedInteger(), PrcType.PRC_TYPE_TESS_3D_Compressed, "PRC_TYPE_TESS_3D_Compressed");
        bool isCalculated = r.ReadBoolean();
        bool hasFaces = r.ReadBoolean();
        double tolerance = r.ReadDouble();
        bool hasOrigin = ctx.AuthoringVersion >= 7031;
        float originX = 0, originY = 0, originZ = 0;
        if (hasOrigin)
        {
            originX = r.ReadFloatAsBytes();
            originY = r.ReadFloatAsBytes();
            originZ = r.ReadFloatAsBytes();
        }

        // The points: three integers for each one stored.
        ctx.Mark("CompressedIntegerArray", r.BitPosition);
        int[] points = Keep(ctx, PrcHuffman.ReadIntegerArray(ref r, out int[] rentedPoints), rentedPoints);

        // Per triangle, how it joins its neighbours: two bits a value.
        ctx.Mark("CharacterArray", r.BitPosition);
        byte[] edgeStatus = KeepBytes(ctx, PrcHuffman.ReadElements(ref r, true, 2, true, false, false, out short[] rentedStatus), rentedStatus);

        // Per triangle, the face it belongs to.
        ctx.Mark("CompressedIndiceArray", r.BitPosition);
        int[] triangleFaces = Keep(ctx, PrcHuffman.ReadIndiceArray(ref r, hasCompressedBit: true, compressedByDefault: true, out int[] rentedFaces), rentedFaces);
        int triangleCount = triangleFaces.Length;
        long faceCount = 0;
        foreach (int face in triangleFaces)
        {
            faceCount = Math.Max(faceCount, face + 1L);
        }

        // Per point, whether it refers to an earlier one; then those references, Huffman-coded when
        // there are more than three (the file has no bit to say so: issue #427).
        uint referenceArraySize = r.ReadUnsignedInteger();
        ctx.Mark("UncompressedBoolArray", r.BitPosition);
        PrcBits pointIsReference = PrcBits.Read(ref r, ctx, referenceArraySize);
        int referencedPoints = pointIsReference.CountSet();
        ctx.Mark("CompressedIndiceArrayWithoutBit", r.BitPosition);
        int[] references = Keep(ctx, PrcHuffman.ReadIndiceArray(ref r, hasCompressedBit: false, compressedByDefault: referencedPoints > 3, out int[] rentedReferences), rentedReferences);

        var tess = new PrcTess3DCompressed
        {
            IsCalculated = isCalculated,
            HasFaces = hasFaces,
            Tolerance = tolerance,
            HasOrigin = hasOrigin,
            OriginX = originX,
            OriginY = originY,
            OriginZ = originZ,
            Points = points,
            EdgeStatus = edgeStatus,
            TriangleFaces = triangleFaces,
            FaceCount = (int)Math.Min(faceCount, int.MaxValue),
            PointIsReference = pointIsReference,
            PointReferences = references,
            MustRecalculateNormals = r.ReadBoolean(),
            LineAttributes = [],
        };

        if (tess.MustRecalculateNormals)
        {
            ctx.Mark("UncompressedBoolArray", r.BitPosition);
            tess.NormalIsReversed = PrcBits.Read(ref r, ctx, triangleCount);
            tess.CreaseAngle = r.ReadDouble();
            r.ReadCharacter();                            // normal recalculation flags: not used
        }
        else
        {
            tess.NormalAngleBits = (byte)r.ReadCharacter();
            uint normalBinaryDataSize = r.ReadUnsignedInteger();
            ctx.Mark("UncompressedBoolArray", r.BitPosition);
            tess.NormalBits = PrcBits.Read(ref r, ctx, normalBinaryDataSize);
            ctx.Mark("ShortArray", r.BitPosition);
            tess.NormalAngles = KeepShorts(ctx, PrcHuffman.ReadElements(ref r, true, Math.Clamp(tess.NormalAngleBits, 1, 16), true, false, true, out short[] rentedAngles), rentedAngles);
            ctx.Mark("UncompressedBoolArray", r.BitPosition);
            tess.IsFacePlanar = PrcBits.Read(ref r, ctx, faceCount);
        }

        long perFace = hasFaces ? faceCount : 1;
        if (r.ReadBoolean())                              // colours on points
        {
            ctx.Mark("UncompressedBoolArray", r.BitPosition);
            tess.IsPointColorOnFace = PrcBits.Read(ref r, ctx, perFace);
            ctx.Mark("CharacterArray", r.BitPosition);
            tess.PointColors = KeepBytes(ctx, PrcHuffman.ReadElements(ref r, true, 8, true, false, false, out short[] rentedColors), rentedColors);
        }

        if (r.ReadBoolean())                              // several line attributes on a face
        {
            ctx.Mark("UncompressedBoolArray", r.BitPosition);
            tess.IsMultipleLineAttributeOnFace = PrcBits.Read(ref r, ctx, perFace);
        }

        ctx.Mark("ShortArray", r.BitPosition);
        tess.LineAttributes = KeepShorts(ctx, PrcHuffman.ReadElements(ref r, true, 16, true, false, true, out short[] rentedAttributes), rentedAttributes);

        if (!r.ReadBoolean())                             // no_texture
        {
            tess.Texture = ReadCompressedTexture(ref r, ctx);
            if (!r.ReadBoolean())                         // all_faces_have_texture
            {
                ctx.Mark("UncompressedBoolArray", r.BitPosition);
                tess.FaceHasTexture = PrcBits.Read(ref r, ctx, faceCount);
            }
        }

        if (r.ReadBoolean())                              // behaviours
        {
            ctx.Mark("CharacterArray", r.BitPosition);
            tess.Behaviors = KeepBytes(ctx, PrcHuffman.ReadElements(ref r, true, 8, true, false, false, out short[] rentedBehaviors), rentedBehaviors);
        }

        ctx.Schema.Evaluate(ref r, PrcType.PRC_TYPE_TESS_3D_Compressed);
        ctx.Leave();
        return tess;
    }

    /// <summary>
    /// The texture parameters of a compressed tessellation, kept: the same fields in the same order as
    /// the generated <c>ReadCompressedTextureParameter</c>, which reads past them.
    /// </summary>
    private static PrcCompressedTexture ReadCompressedTexture(ref PrcBitReader r, PrcContext ctx)
    {
        ctx.Enter("CompressedTextureParameter", r.BitPosition);
        ctx.Enter("BinaryTextureData", r.BitPosition);
        uint wordCount = r.ReadUnsignedInteger();
        int words = PrcContext.CheckCount(ref r, wordCount, 32);
        ctx.Charge(words * 4L);
        var data = new uint[words];
        for (int i = 0; i < words; i++)
        {
            // The field is written as bytes, and its first bit is the lowest of its first byte
            // (7.8.9.9; seen in files: only this order makes the bits set as many as the references).
            // Each byte is turned round, so that the bits are kept first to last.
            uint w = r.ReadBits(32);
            w = ((w & 0xF0F0F0F0u) >> 4) | ((w & 0x0F0F0F0Fu) << 4);
            w = ((w & 0xCCCCCCCCu) >> 2) | ((w & 0x33333333u) << 2);
            data[i] = ((w & 0xAAAAAAAAu) >> 1) | ((w & 0x55555555u) << 1);
        }

        // Of the last word only so many bits count.
        uint used = r.ReadUnsignedInteger();
        long bitCount = words == 0 ? 0 : (words - 1) * 32L + Math.Min(used, 32);
        ctx.Leave();

        uint referenceCount = r.ReadUnsignedInteger();
        uint[] references = PrcArrays.ReadBitCountedUnsignedIntegers(ref r, ctx, referenceCount);
        r.ReadDouble();                                   // tolerance: reserved
        uint parameterCount = r.ReadUnsignedInteger();
        float[] parameters = PrcArrays.ReadFloats(ref r, ctx, parameterCount);
        ctx.Leave();
        return new PrcCompressedTexture { Bits = new PrcBits(data, (int)Math.Min(bitCount, int.MaxValue)), References = references, Parameters = parameters };
    }

    private static int[] Keep(PrcContext ctx, int count, int[] rented)
    {
        try
        {
            ctx.Charge(count * 4L);
            return rented.AsSpan(0, count).ToArray();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static byte[] KeepBytes(PrcContext ctx, int count, short[] rented)
    {
        try
        {
            ctx.Charge(count);
            var kept = new byte[count];
            for (int i = 0; i < count; i++)
            {
                kept[i] = (byte)rented[i];
            }

            return kept;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(rented);
        }
    }

    private static ushort[] KeepShorts(PrcContext ctx, int count, short[] rented)
    {
        try
        {
            ctx.Charge(count * 2L);
            return System.Runtime.InteropServices.MemoryMarshal.Cast<short, ushort>(rented.AsSpan(0, count)).ToArray();
        }
        finally
        {
            ArrayPool<short>.Shared.Return(rented);
        }
    }

    private static void SkipBits(ref PrcBitReader r, long count)
    {
        if (count > r.BitsLeft)
        {
            throw new Scene3DException(Scene3DError.Truncated, $"A PRC array of {count} bits does not fit in the {r.BitsLeft} bits left.");
        }

        r.Skip(count);
    }

    /// <summary>Reads an array of bits and returns how many are set.</summary>
    private static long CountBits(ref PrcBitReader r, long count)
    {
        if (count > r.BitsLeft)
        {
            throw new Scene3DException(Scene3DError.Truncated, $"A PRC array of {count} bits does not fit in the {r.BitsLeft} bits left.");
        }

        long set = 0;
        for (; count >= 32; count -= 32)
        {
            set += System.Numerics.BitOperations.PopCount(r.ReadBits(32));
        }

        return set + System.Numerics.BitOperations.PopCount(r.ReadBits((int)count));
    }
}
