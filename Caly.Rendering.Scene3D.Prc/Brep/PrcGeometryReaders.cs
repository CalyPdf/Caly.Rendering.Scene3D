using Caly.Rendering.Scene3D.Prc.Bits;

namespace Caly.Rendering.Scene3D.Prc.Generated;

/// <summary>The readers of the exact-geometry section that are written by hand.</summary>
internal static partial class PrcReaders
{
    /// <summary>
    /// The blend surface of type 4: the reference reader does not read it either (prc.json: "not implemented"), and no sample has one.
    /// A file with one cannot be followed to the end of its geometry section.
    /// </summary>
    internal static void ReadSurfBlend04(ref PrcBitReader r, PrcContext ctx)
    {
        ctx.Enter("PRC_TYPE_SURF_Blend04", r.BitPosition);
        throw new Scene3DException(Scene3DError.Unsupported, "A PRC blend surface of type 4 is not one this reader can read.");
    }

    /// <summary>
    /// A single wire body in compressed form (<c>PRC_TYPE_TOPO_SingleWireBodyCompress</c>): a content body, the tolerance, a
    /// flag nobody has explained (prc.json: "unknown") and one curve. Read past, to keep in step: a wire is not drawn.
    /// </summary>
    internal static void ReadTopoSingleWireBodyCompress(ref PrcBitReader r, PrcContext ctx)
    {
        ctx.Enter("PRC_TYPE_TOPO_SingleWireBodyCompress", r.BitPosition);
        ctx.Expect(r.ReadUnsignedInteger(), PrcType.PRC_TYPE_TOPO_SingleWireBodyCompress, "PRC_TYPE_TOPO_SingleWireBodyCompress");
        ReadContentBody(ref r, ctx);
        double tolerance = r.ReadDouble();
        r.ReadBoolean();
        var reader = new Brep.PrcCompressedBrepReader(ctx, tolerance, 0, 0, 0) { CurveTrimmingFace = false };
        reader.ReadCurve(ref r);
        ctx.Leave();
    }
    /// <summary>
    /// A compressed solid body (<c>PRC_TYPE_TOPO_BrepDataCompress</c>, ISO 14739-1 draft 7.9.20): a content body, the tolerance, the
    /// width of a reference and the numbers of vertices and curves that belong to the iso faces, then the shell or the connex
    /// entities with their faces, and a <c>BaseTopology</c> for every face.
    /// </summary>
    internal static PrcTopoBrepDataCompress ReadTopoBrepDataCompress(ref PrcBitReader r, PrcContext ctx)
    {
        ctx.Enter("PRC_TYPE_TOPO_BrepDataCompress", r.BitPosition);
        ctx.Expect(r.ReadUnsignedInteger(), PrcType.PRC_TYPE_TOPO_BrepDataCompress, "PRC_TYPE_TOPO_BrepDataCompress");
        ReadContentBody(ref r, ctx);
        double tolerance = r.ReadDouble();
        int referenceBits = (int)r.ReadNumberOfBitsThenUnsignedInteger();
        uint isoVertices = r.ReadUnsignedIntegerWithVariableBitNumber(referenceBits);
        uint isoEdges = r.ReadUnsignedIntegerWithVariableBitNumber(referenceBits);
        var reader = new Brep.PrcCompressedBrepReader(ctx, tolerance, referenceBits, isoVertices, isoEdges);
        int faces = r.ReadBoolean() ? reader.ReadShell(ref r) : reader.ReadConnexes(ref r);
        if (ctx.Count(ref r, faces, 1) != reader.FaceCount)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, "A compressed PRC body counts its faces two ways.");
        }

        for (int i = 0; i < faces; i++)
        {
            ReadBaseTopology(ref r, ctx);
        }

        ctx.Leave();
        return reader.Result();
    }
}