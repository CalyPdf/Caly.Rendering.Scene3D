using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Prc.Generated;

/// <summary>
/// A compressed tessellation (<c>PRC_TYPE_TESS_3D_Compressed</c>) as the file has it. It is turned into a
/// mesh when a body shows it.
/// </summary>
internal sealed class PrcTess3DCompressed
{
    public required bool IsCalculated;
    public required bool HasFaces;

    /// <summary>The length that one step of a stored point integer stands for.</summary>
    public required double Tolerance;

    /// <summary>Files from version 7031 on give the middle of the tessellation's bounding box.</summary>
    public required bool HasOrigin;
    public required float OriginX;
    public required float OriginY;
    public required float OriginZ;

    /// <summary>Three integers per stored point.</summary>
    public required int[] Points;

    /// <summary>Per triangle: 0x1 it has a right neighbour, 0x2 a left one.</summary>
    public required byte[] EdgeStatus;

    /// <summary>Per triangle, the face it belongs to.</summary>
    public required int[] TriangleFaces;

    /// <summary>The highest face index, plus one.</summary>
    public required int FaceCount;

    /// <summary>Per point the traversal meets: whether it is one met before.</summary>
    public required PrcBits PointIsReference;

    /// <summary>For each point that is a reference, which.</summary>
    public required int[] PointReferences;

    public required bool MustRecalculateNormals;

    /// <summary>Normals to be calculated: a bit per triangle.</summary>
    public PrcBits NormalIsReversed;
    public double CreaseAngle;

    /// <summary>Stored normals: how many bits an angle is quantised to.</summary>
    public int NormalAngleBits;

    /// <summary>Stored normals: the bits that say how each normal is stored.</summary>
    public PrcBits NormalBits;

    /// <summary>Stored normals: two quantised angles per stored normal.</summary>
    public ushort[] NormalAngles = [];

    /// <summary>Stored normals: a bit per face.</summary>
    public PrcBits IsFacePlanar;

    /// <summary>A bit per face (one in all without faces); no bits when the tessellation has no colours.</summary>
    public PrcBits IsPointColorOnFace;
    public byte[] PointColors = [];

    /// <summary>A bit per face (one in all without faces); no bits when no face has several styles.</summary>
    public PrcBits IsMultipleLineAttributeOnFace;
    public required ushort[] LineAttributes;

    public PrcCompressedTexture? Texture;

    /// <summary>With a texture: a bit per face; no bits when every face has it.</summary>
    public PrcBits FaceHasTexture;

    /// <summary>The behaviour bits of the faces; empty when none are stored.</summary>
    public byte[] Behaviors = [];

    public int TriangleCount => TriangleFaces.Length;
}
