using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

internal sealed partial class PrcCompressedBrepReader
{
    private const byte NewLoop = 0, EndLoop = 1;

    private readonly List<int> _coedges = [];
    private readonly List<PrcBrepLoop> _loops = [];
    private readonly List<PrcBrepFace> _faces = [];
    private readonly List<double> _surfaceData = [];
    private readonly List<int> _faceVertices = [];
    private readonly List<PrcGenericSurface> _genericSurfaces = [];
    private int _shellCount;

    /// <summary>What the face being read has so far (it is put together in the order the file gives its parts).</summary>
    private struct FaceParts
    {
        public bool OrientationWithShell;
        public bool Trimmed;
        public int FirstLoop;
        public int LoopCount;
        public int FirstVertex;
        public int VertexCount;
        public int SurfaceDataStart;
        public int SurfaceDataCount;
        public int Surface;
        public PrcVector PointOnTorus;
        public bool HasPointOnTorus;
    }

    /// <summary>The number of faces read so far: how many <c>BaseTopology</c> end the body.</summary>
    public int FaceCount => _faces.Count;

    // ---- shells and connex

    /// <summary>
    /// A shell (<c>CompressedShell</c>): one face, or a count and that many faces (all the iso faces first, then the others),
    /// then one flag a face saying which of the two it is, in the order the faces have in the shell. Returns the number of faces.
    /// </summary>
    public int ReadShell(ref PrcBitReader r)
    {
        _ctx.Enter("CompressedShell", r.BitPosition);
        uint announced = r.ReadBoolean() ? 1 : r.ReadNumberOfBitsThenUnsignedInteger();
        int count = _ctx.Count(ref r, announced, 6);        // a face is a type token and a bit at the least
        for (int i = 0; i < count; i++)
        {
            ReadFace(ref r);
        }

        // Which kind of face each place of the shell holds: the faces are in the same order as the shell's own only for the
        // purpose of the BaseTopology that follows the body; nothing of the geometry depends on it.
        r.Skip(count);
        _shellCount++;
        _ctx.Leave();
        return count;
    }

    /// <summary>The shells of several connex entities (<c>MultipleCompressedConnex</c>). Returns the number of faces.</summary>
    public int ReadConnexes(ref PrcBitReader r)
    {
        _ctx.Enter("MultipleCompressedConnex", r.BitPosition);
        int total = 0;
        int connex = _ctx.Count(ref r, r.ReadNumberOfBitsThenUnsignedInteger(), 5);
        for (int i = 0; i < connex; i++)
        {
            _ctx.Enter("CompressedConnex", r.BitPosition);
            int shells = _ctx.Count(ref r, r.ReadNumberOfBitsThenUnsignedInteger(), 1);
            for (int j = 0; j < shells; j++)
            {
                total += ReadShell(ref r);
            }

            _ctx.Leave();
        }

        _ctx.Leave();
        return total;
    }

    // ---- faces

    /// <summary>A face (<c>CompressedFace</c>): a type, then the record of that type. Returns its number.</summary>
    public int ReadFace(ref PrcBitReader r)
    {
        _ctx.Enter("CompressedFace", r.BitPosition);
        _ctx.Mark("CompressedEntityType", r.BitPosition);
        PrcCompressedEntity type = PeekEntity(ref r);
        if (type.IsCurve || type.Value is NewLoop or EndLoop)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC {(type.IsCurve ? "curve" : "loop marker")} stands where a face is expected.");
        }

        var kind = (PrcFaceKind)type.Value;
        IsoFace = kind <= PrcFaceKind.IsoNurbs;
        _ctx.Enter(FaceName(kind), r.BitPosition);
        _ctx.Mark("CompressedEntityType", r.BitPosition);
        r.ReadCompressedEntityType();

        var parts = new FaceParts { Surface = -1, FirstLoop = _loops.Count, FirstVertex = _faceVertices.Count, SurfaceDataStart = _surfaceData.Count };
        switch (kind)
        {
            case PrcFaceKind.IsoPlane:
            case PrcFaceKind.AnaPlane:
                _surfaceData.Add(r.ReadDouble());
                _surfaceData.Add(r.ReadDouble());
                _surfaceData.Add(r.ReadBoolean() ? 1 : 0);
                ReadContentFace(ref r, kind, ref parts);
                break;

            case PrcFaceKind.IsoTorus:
                _surfaceData.Add(r.ReadBoolean() ? 1 : 0);
                ReadContentFace(ref r, kind, ref parts);
                break;

            case PrcFaceKind.IsoCylinder:
            case PrcFaceKind.IsoSphere:
            case PrcFaceKind.IsoCone:
                ReadContentFace(ref r, kind, ref parts);
                break;

            case PrcFaceKind.AnaCylinder:
            case PrcFaceKind.AnaCone:
                ReadContentFace(ref r, kind, ref parts);
                AddPointToSurfaceData(ReadPoint(ref r));
                AddPointToSurfaceData(ReadPoint(ref r));
                break;

            case PrcFaceKind.AnaSphere:
                ReadContentFace(ref r, kind, ref parts);
                AddPointToSurfaceData(ReadPoint(ref r));
                break;

            case PrcFaceKind.AnaTorus:
                ReadContentFace(ref r, kind, ref parts);
                AddPointToSurfaceData(ReadPoint(ref r));
                AddPointToSurfaceData(ReadPoint(ref r));
                {
                    // The reference reads the second axis as a vector of three plain doubles (prc.json: the 2014 text says so, the
                    // working draft says a compressed point).
                    PrcVector3D axis = PrcReaders.ReadVector3D(ref r, _ctx);
                    AddPointToSurfaceData(new PrcVector(axis.X, axis.Y, axis.Z));
                }

                break;

            case PrcFaceKind.IsoNurbs:
                ReadIsoNurbsFace(ref r, ref parts);
                break;

            case PrcFaceKind.AnaNurbs:
                ReadContentFace(ref r, kind, ref parts);
                parts.Surface = _nurbs.Count;
                _nurbs.Add(ReadCompressedNurbs(ref r));
                break;

            case PrcFaceKind.AnaGenericFace:
                ReadContentFace(ref r, kind, ref parts);
                parts.Surface = _genericSurfaces.Count;
                _genericSurfaces.Add(ReadGenericSurface(ref r));
                break;

            default:
                throw new Scene3DException(Scene3DError.Unsupported, $"A compressed PRC face of type {kind} cannot be read yet.");
        }

        parts.SurfaceDataCount = _surfaceData.Count - parts.SurfaceDataStart;
        int id = _faces.Count;
        _faces.Add(new PrcBrepFace(
            kind, _shellCount, parts.OrientationWithShell, parts.Trimmed, parts.FirstLoop, parts.LoopCount, parts.SurfaceDataStart,
            parts.SurfaceDataCount, parts.FirstVertex, parts.VertexCount, parts.Surface, parts.PointOnTorus, parts.HasPointOnTorus));
        _ctx.Leave();
        _ctx.Leave();
        return id;
    }

    private static string FaceName(PrcFaceKind kind) => kind switch
    {
        PrcFaceKind.IsoPlane => "PRC_HCG_IsoPlane",
        PrcFaceKind.IsoCylinder => "PRC_HCG_IsoCylinder",
        PrcFaceKind.IsoTorus => "PRC_HCG_IsoTorus",
        PrcFaceKind.IsoSphere => "PRC_HCG_IsoSphere",
        PrcFaceKind.IsoCone => "PRC_HCG_IsoCone",
        PrcFaceKind.IsoNurbs => "PRC_HCG_IsoNurbs",
        PrcFaceKind.AnaPlane => "PRC_HCG_AnaPlane",
        PrcFaceKind.AnaCylinder => "PRC_HCG_AnaCylinder",
        PrcFaceKind.AnaTorus => "PRC_HCG_AnaTorus",
        PrcFaceKind.AnaSphere => "PRC_HCG_AnaSphere",
        PrcFaceKind.AnaCone => "PRC_HCG_AnaCone",
        PrcFaceKind.AnaNurbs => "PRC_HCG_AnaNurbs",
        _ => "PRC_HCG_AnaGenericFace",
    };

    private void AddPointToSurfaceData(PrcVector p)
    {
        _surfaceData.Add(p.X);
        _surfaceData.Add(p.Y);
        _surfaceData.Add(p.Z);
    }

    /// <summary>The boundary of a face (<c>ContentCompressedFace</c>): how the surface runs with the shell, then an iso face's four sides or an ana face's loops.</summary>
    private void ReadContentFace(ref PrcBitReader r, PrcFaceKind kind, ref FaceParts parts)
    {
        _ctx.Enter("ContentCompressedFace", r.BitPosition);
        parts.OrientationWithShell = r.ReadBoolean();
        if (kind <= PrcFaceKind.IsoNurbs)
        {
            ReadIsoFace(ref r, ref parts);
        }
        else
        {
            ReadAnaFace(ref r, kind, ref parts);
        }

        _ctx.Leave();
    }

    /// <summary>
    /// An iso face's boundary (<c>ContentCompressedIsoFace</c>): two sides stored or referred to; the third and fourth as an index
    /// of a curve stored before or, when not yet saved, deduced from the first two and the vertex they share.
    /// </summary>
    private void ReadIsoFace(ref PrcBitReader r, ref FaceParts parts)
    {
        _ctx.Enter("ContentCompressedIsoFace", r.BitPosition);
        bool orientation = r.ReadBoolean();
        int first = ReadRefOrCurve(ref r);
        int second = ReadRefOrCurve(ref r);
        bool thirdDeduced = r.ReadBoolean();
        int third = thirdDeduced ? -1 : ResolveCurve(r.ReadBits(_referenceBits));
        bool fourthDeduced = r.ReadBoolean();
        int fourth = fourthDeduced ? -1 : ResolveCurve(r.ReadBits(_referenceBits));
        if (thirdDeduced && fourthDeduced)
        {
            _faceVertices.Add(ReadVertex(ref r));
            parts.VertexCount = 1;
        }

        // A side that is not stored is deduced all the same, and takes the next number: a neighbouring face may refer to it.
        if (thirdDeduced)
        {
            third = AddImplicitCurve(PrcCurveKind.Implicit, 2);
        }

        if (fourthDeduced)
        {
            fourth = AddImplicitCurve(PrcCurveKind.Implicit, 3);
        }

        int firstCoedge = _coedges.Count;
        _coedges.Add(first);
        _coedges.Add(second);
        _coedges.Add(third);
        _coedges.Add(fourth);
        _loops.Add(new PrcBrepLoop(firstCoedge, 4, orientation));        parts.Trimmed = true;
        parts.LoopCount = 1;
        _ctx.Leave();
    }

    private int ResolveCurve(uint index) => Resolve(index, _isoEdgeCount, _isoCurveIds, _anaCurveIds, "curve");

    /// <summary>
    /// An ana face's boundary (<c>ContentCompressedAnaFace</c>): whether the surface is trimmed; if so its loops, each its
    /// orientation and curves, ended by an entity that says another loop follows or that this was the last (issue #696); and for a
    /// torus that is trimmed by vertex loops only, a point that says which side of the torus the face is.
    /// </summary>
    private void ReadAnaFace(ref PrcBitReader r, PrcFaceKind kind, ref FaceParts parts)
    {
        _ctx.Enter("ContentCompressedAnaFace", r.BitPosition);
        parts.Trimmed = r.ReadBoolean();
        bool onlyVertexLoops = true;
        if (parts.Trimmed)
        {
            // The reference reads the loops with code of its own, which nests what it traces one level deeper without a line.
            _ctx.Descend();
            bool another;
            do
            {
                bool loopIsAVertex = ReadAnaLoop(ref r, out another);
                onlyVertexLoops &= loopIsAVertex;
                parts.LoopCount++;
            }
            while (another);
            _ctx.Leave();
        }

        if (parts.Trimmed && kind == PrcFaceKind.AnaTorus && onlyVertexLoops)
        {
            parts.PointOnTorus = ReadPoint(ref r);
            parts.HasPointOnTorus = true;
        }

        _ctx.Leave();
    }

    /// <summary>
    /// One loop of an ana face, up to the entity that ends it. Returns whether it is a vertex loop (a single line whose two ends
    /// are the same point to within a hundredth of the tolerance), and in <paramref name="another"/> whether a loop follows.
    /// </summary>
    private bool ReadAnaLoop(ref PrcBitReader r, out bool another)
    {
        bool orientation = r.ReadBoolean();
        int firstCoedge = _coedges.Count;
        int sides = 0;
        bool vertexLoop = true;
        while (true)
        {
            if (!r.ReadBoolean())
            {
                // A curve stored before.
                _coedges.Add(ResolveCurve(r.ReadBits(_referenceBits)));
                sides++;
                vertexLoop = false;
                continue;
            }

            _ctx.Mark("CompressedEntityType", r.BitPosition);
            PrcCompressedEntity type = PeekEntity(ref r);
            if (!type.IsCurve)
            {
                if (type.Value is not (NewLoop or EndLoop))
                {
                    throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC face type, {type.Value}, stands inside the loop of a face.");
                }

                // The reference reads the marker a second time to consume it, and traces both reads.
                _ctx.Mark("CompressedEntityType", r.BitPosition);
                r.ReadCompressedEntityType();
                another = type.Value == NewLoop;
                break;
            }

            int curve = ReadCurve(ref r);
            _coedges.Add(curve);
            sides++;
            vertexLoop &= sides == 1 && IsDegenerateLine(curve);
        }

        _loops.Add(new PrcBrepLoop(firstCoedge, sides, orientation));
        return vertexLoop;
    }

    private bool IsDegenerateLine(int curve)
    {
        PrcBrepCurve c = _curves[curve];
        if (c.Kind != PrcCurveKind.Line)
        {
            return false;
        }

        PrcVector d = _vertices[c.End] - _vertices[c.Start];
        return Math.Sqrt(d.LengthSquared()) < _pointTolerance;
    }

    // ---- the surface of a generic face

    /// <summary>
    /// The uncompressed surface of a generic ana face: the analytic kinds are kept with their frame and radii; any other kind is
    /// read (to stay in step) and kept as unsupported.
    /// </summary>
    private PrcGenericSurface ReadGenericSurface(ref PrcBitReader r)
    {
        return ToGeneric(PrcReaders.ReadSurf(ref r, _ctx));
    }

    /// <summary>An uncompressed surface as a generic one: the analytic kinds with their frame and radii, any other kind unsupported.</summary>
    internal static PrcGenericSurface ToGeneric(PrcSurf surface)
    {
        return surface.Id switch
        {
            PrcSurfPlane plane => new PrcGenericSurface(PrcGenericSurfaceKind.Plane, surface.IdType, PrcBrepMath.Frame(plane.Transform), 0, 0),
            PrcSurfCylinder cylinder => new PrcGenericSurface(PrcGenericSurfaceKind.Cylinder, surface.IdType, PrcBrepMath.Frame(cylinder.Transform.Transformation), cylinder.Radius, 0),
            PrcSurfCone cone => new PrcGenericSurface(PrcGenericSurfaceKind.Cone, surface.IdType, PrcBrepMath.Frame(cone.Transform.Transformation), cone.Radius, cone.SemiAngle),
            PrcSurfSphere sphere => new PrcGenericSurface(PrcGenericSurfaceKind.Sphere, surface.IdType, PrcBrepMath.Frame(sphere.Transform.Transformation), sphere.Radius, 0),
            PrcSurfTorus torus => new PrcGenericSurface(PrcGenericSurfaceKind.Torus, surface.IdType, PrcBrepMath.Frame(torus.Transform.Transformation), torus.MajorRadius, torus.MinorRadius),
            _ => new PrcGenericSurface(PrcGenericSurfaceKind.Unsupported, surface.IdType, Matrix4x4d.Identity, 0, 0),
        };
    }
}
