using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// Reads the parts of a compressed solid body (<c>PRC_TYPE_TOPO_BrepDataCompress</c>, ISO 14739-1 draft 7.9.20) and keeps what
/// they hold as plain arrays. The structures are those of prc.json (prc-rs, MIT; see THIRD-PARTY-NOTICES.md), with what the
/// PDF Association issues correct of the draft: the widths and tolerances of compressed points (#706), where the type of a
/// circle is left out (#582), that a composite curve always has its ends (#583), the sentinels that end the loops of an ana face
/// (#696), and the two caches that vertices and curves are numbered in (#705, #847).
/// <para>
/// A vertex or curve is stored by the first face that needs it and referred to by its index afterwards. The index counts in the
/// cache of the kind of face that stored the entity: the vertices stored by iso faces are numbered from 0, those stored by ana
/// faces from the number of iso vertices the body's header gives (<c>number_vertex_references</c>), and so the same for curves.
/// The reader numbers what it keeps in the order it was stored and resolves each reference to that number when it reads it.
/// </para>
/// <para>
/// Every structure traces itself as the reference reader does (<see cref="PrcContext.Enter"/> and <see cref="PrcContext.Mark"/>),
/// so that a reading can be compared with another reader's line by line.
/// </para>
/// </summary>
internal sealed partial class PrcCompressedBrepReader
{
    private readonly PrcContext _ctx;
    private readonly double _pointTolerance;
    private readonly int _referenceBits;
    private readonly uint _isoVertexCount;
    private readonly uint _isoEdgeCount;

    private readonly List<PrcVector> _vertices = [];
    private readonly List<int> _isoVertexIds = [];
    private readonly List<int> _anaVertexIds = [];
    private readonly List<PrcBrepCurve> _curves = [];
    private readonly List<int> _isoCurveIds = [];
    private readonly List<int> _anaCurveIds = [];
    private readonly List<double> _curveData = [];
    private readonly List<int> _compositeMembers = [];

    private bool _isoSpline;

    /// <param name="ctx">The reading context (its trace, its limits).</param>
    /// <param name="tolerance">The body's <c>brep_data_compressed_tolerance</c>; it must be a positive number.</param>
    /// <param name="referenceBits">The width of an index: <c>number_of_bits_to_store_reference</c>.</param>
    /// <param name="isoVertexCount">The body's <c>number_vertex_references</c>: where the numbers of the vertices of ana faces start.</param>
    /// <param name="isoEdgeCount">The body's <c>number_edge_references</c>: the same for curves.</param>
    public PrcCompressedBrepReader(PrcContext ctx, double tolerance, int referenceBits, uint isoVertexCount, uint isoEdgeCount)
    {
        if (!(tolerance > 0) || double.IsInfinity(tolerance))
        {
            throw new Scene3DException(Scene3DError.InvalidValue, "A compressed PRC body has a tolerance that is not a positive number.");
        }

        if ((uint)referenceBits > 32)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC body claims {referenceBits} bits for its references.");
        }

        _ctx = ctx;
        Tolerance = tolerance;
        _pointTolerance = tolerance / 100;
        _referenceBits = referenceBits;
        _isoVertexCount = isoVertexCount;
        _isoEdgeCount = isoEdgeCount;
    }

    /// <summary>The body's tolerance.</summary>
    public double Tolerance { get; }

    /// <summary>Whether what is stored now belongs to the caches of iso faces (otherwise to those of ana faces).</summary>
    public bool IsoFace { get; set; }

    /// <summary>
    /// Whether the curves read are the trims of a face (their ends are vertices) or those of a wire body (their ends are points).
    /// </summary>
    public bool CurveTrimmingFace { get; init; } = true;

    /// <summary>What has been read so far, as arrays.</summary>
    public PrcTopoBrepDataCompress Result()
    {
        _ctx.Charge((long)_vertices.Count * 24 + (long)_curves.Count * 24 + (long)_curveData.Count * 8 + (long)_compositeMembers.Count * 4
            + (long)_coedges.Count * 8 + (long)_loops.Count * 12 + (long)_faces.Count * 84 + (long)_surfaceData.Count * 8 + (long)_faceVertices.Count * 4
            + (long)_genericSurfaces.Count * 120);
        return new PrcTopoBrepDataCompress
        {
            Tolerance = Tolerance,
            Vertices = [.. _vertices],
            Curves = [.. _curves],
            CurveData = [.. _curveData],
            CompositeMembers = [.. _compositeMembers],
            Coedges = [.. _coedges],
            Loops = [.. _loops],
            Faces = [.. _faces],
            FacesInShellOrder = _shellOrderKnown && _facesInShellOrder.Count == _faces.Count ? [.. _facesInShellOrder] : null,
            SurfaceData = [.. _surfaceData],
            FaceVertices = [.. _faceVertices],
            GenericSurfaces = [.. _genericSurfaces],
            Nurbs = [.. _nurbs],
        };
    }

    // ---- points and vertices

    /// <summary>A compressed point, traced as the reference does.</summary>
    private PrcVector ReadPoint(ref PrcBitReader r)
    {
        _ctx.Mark("CompressedPoint", r.BitPosition);
        long start = r.BitPosition;
        uint width = r.ReadBits(6);
        r.Seek(start);
        if (width is >= 1 and <= 30)
        {
            _ctx.Mark("Point3DWithVariableBitNumber", start + 6);
        }

        return r.ReadCompressedPoint(_pointTolerance);
    }

    /// <summary>
    /// A vertex (<c>CompressedVertex</c>): a point stored now, or the index of one stored before. Returns its number.
    /// </summary>
    public int ReadVertex(ref PrcBitReader r)
    {
        _ctx.Enter("CompressedVertex", r.BitPosition);
        int id;
        if (r.ReadBoolean())
        {
            // The field is called "already stored" and is true when the point is NOT yet stored (prc.json; the draft's table).
            id = _vertices.Count;
            _vertices.Add(ReadPoint(ref r));
            (IsoFace ? _isoVertexIds : _anaVertexIds).Add(id);
        }
        else
        {
            id = Resolve(r.ReadBits(_referenceBits), _isoVertexCount, _isoVertexIds, _anaVertexIds, "vertex");
        }

        _ctx.Leave();
        return id;
    }

    private static int Resolve(uint index, uint isoCount, List<int> iso, List<int> ana, string what)
    {
        if (index < isoCount)
        {
            if (index < (uint)iso.Count)
            {
                return iso[(int)index];
            }
        }
        else if (index - isoCount < (uint)ana.Count)
        {
            return ana[(int)(index - isoCount)];
        }

        throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC body refers to {what} {index}, which is not stored ({iso.Count} stored by iso faces of {isoCount} announced, {ana.Count} by ana faces).");
    }

    /// <summary>
    /// The start and end of a curve (<c>StartEndData</c>): two vertices for the trim of a face; two points for a wire body, whose\n    /// curves have no vertices (their numbers are then -1).
    /// </summary>
    public (int Start, int End) ReadStartEndData(ref PrcBitReader r)
    {
        _ctx.Enter("StartEndData", r.BitPosition);
        int start, end;
        if (CurveTrimmingFace)
        {
            start = ReadVertex(ref r);
            end = ReadVertex(ref r);
        }
        else
        {
            ReadPoint(ref r);
            ReadPoint(ref r);
            start = end = -1;
        }

        _ctx.Leave();
        return (start, end);
    }

    // ---- curves

    private PrcCompressedEntity PeekEntity(ref PrcBitReader r)
    {
        long position = r.BitPosition;
        PrcCompressedEntity entity = r.ReadCompressedEntityType();
        r.Seek(position);
        return entity;
    }

    /// <summary>The type token of a structure that starts with one, traced as the reference does.</summary>
    private PrcCompressedEntity ReadType(ref PrcBitReader r)
    {
        _ctx.Mark("CompressedEntityType", r.BitPosition);
        return r.ReadCompressedEntityType();
    }

    /// <summary>
    /// A curve of a loop (<c>RefOrCompressedCurve</c>): a curve stored now, or the index of one stored before. Returns its number.
    /// </summary>
    public int ReadRefOrCurve(ref PrcBitReader r)
    {
        _ctx.Enter("RefOrCompressedCurve", r.BitPosition);
        int id = r.ReadBoolean()
            ? ReadCurve(ref r)
            : Resolve(r.ReadBits(_referenceBits), _isoEdgeCount, _isoCurveIds, _anaCurveIds, "curve");
        _ctx.Leave();
        return id;
    }

    /// <summary>A curve stored now (<c>CompressedCurve</c>): its type, then the record of that type. Returns its number.</summary>
    public int ReadCurve(ref PrcBitReader r)
    {
        _ctx.Enter("CompressedCurve", r.BitPosition);
        _ctx.Mark("CompressedEntityType", r.BitPosition);
        PrcCompressedEntity type = PeekEntity(ref r);
        if (!type.IsCurve)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC face type, {type.Value}, stands where a curve is expected.");
        }

        int id = type.Value switch
        {
            0 => ReadLine(ref r),
            1 => ReadCircle(ref r),
            2 => ReadHermiteCurve(ref r),
            13 => ReadCompositeCurve(ref r),
            _ => throw new Scene3DException(Scene3DError.Unsupported, "A compressed PRC ellipse is not one this reader can read (the format reserves it)."),
        };
        _ctx.Leave();
        return id;
    }

    /// <summary>A curve the file does not store, for the face that will be number <see cref="FaceCount"/> (see <see cref="PrcCurveKind.Implicit"/>).</summary>
    private int AddImplicitCurve(PrcCurveKind kind, int side) => AddCurve(kind, -1, -1, _faces.Count, side, PrcBrepCurveFlags.None);

    private int AddCurve(PrcCurveKind kind, int start, int end, int dataStart, int dataCount, PrcBrepCurveFlags flags)
    {
        int id = _curves.Count;
        _curves.Add(new PrcBrepCurve(kind, start, end, dataStart, dataCount, (byte)flags));
        (IsoFace ? _isoCurveIds : _anaCurveIds).Add(id);
        return id;
    }

    private int ReadLine(ref PrcBitReader r)
    {
        _ctx.Enter("PRC_HCG_Line", r.BitPosition);
        ReadType(ref r);
        (int start, int end) = ReadStartEndData(ref r);
        _ctx.Leave();
        return AddCurve(PrcCurveKind.Line, start, end, _curveData.Count, 0, PrcBrepCurveFlags.None);
    }

    /// <summary>
    /// The circle that trims an iso NURBS face (<c>compressed_iso_spline</c>): the structure is read without its type token and
    /// without its ends, which the face's own vertices give (issue #582).
    /// </summary>
    public int ReadIsoSplineCircle(ref PrcBitReader r)
    {
        _isoSpline = true;
        try
        {
            return ReadCircle(ref r);
        }
        finally
        {
            _isoSpline = false;
        }
    }

    private int ReadCircle(ref PrcBitReader r)
    {
        _ctx.Enter("PRC_HCG_Circle", r.BitPosition);
        if (!_isoSpline)
        {
            ReadType(ref r);
        }

        int start = -1, end = -1;
        int dataStart = _curveData.Count;
        var flags = PrcBrepCurveFlags.None;
        if (r.ReadBoolean())
        {
            flags |= PrcBrepCurveFlags.Particular;
            _ctx.Enter("ParticularCircle", r.BitPosition);
            bool full = r.ReadBoolean();
            if (!_isoSpline)
            {
                (start, end) = ReadStartEndData(ref r);
            }

            if (full)
            {
                flags |= PrcBrepCurveFlags.Full;
                AddPoint(ReadPoint(ref r));    // centre
                AddPoint(ReadPoint(ref r));    // normal
            }
            else
            {
                AddPoint(ReadPoint(ref r));    // a point in the middle of the arc
            }

            _ctx.Leave();
        }
        else
        {
            _ctx.Enter("GeneralCircle", r.BitPosition);
            if (!_isoSpline)
            {
                (start, end) = ReadStartEndData(ref r);
            }

            AddPoint(ReadPoint(ref r));        // centre
            if (r.ReadBoolean())
            {
                flags |= PrcBrepCurveFlags.OverHalfTurn;
            }

            _ctx.Leave();
        }

        _ctx.Leave();
        return AddCurve(PrcCurveKind.Circle, start, end, dataStart, _curveData.Count - dataStart, flags);
    }

    private void AddPoint(PrcVector p)
    {
        _curveData.Add(p.X);
        _curveData.Add(p.Y);
        _curveData.Add(p.Z);
    }

    private int ReadHermiteCurve(ref PrcBitReader r)
    {
        _ctx.Enter("PRC_HCG_BSplineHermiteCurve", r.BitPosition);
        ReadType(ref r);
        (int start, int end) = ReadStartEndData(ref r);       // always there, whatever the context (issue #583)
        int numberBits = (int)r.ReadBits(4);
        uint numberPoints = r.ReadBits(numberBits);
        if (numberPoints < 2)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A compressed PRC Hermite curve has {numberPoints} points; it needs two at least.");
        }

        // A point or tangent takes three bits at the least: nothing is made on the announced count alone.
        _ctx.Count(ref r, 2L * numberPoints - 2, 3);
        int dataStart = _curveData.Count;
        _curveData.Add(numberPoints);
        ReadVectors(ref r, (int)numberPoints - 2);
        ReadVectors(ref r, (int)numberPoints);
        _ctx.Leave();
        return AddCurve(PrcCurveKind.HermiteSpline, start, end, dataStart, _curveData.Count - dataStart, PrcBrepCurveFlags.None);
    }

    /// <summary>A width of six bits, then vectors of that many bits a number at the body's tolerance, or plain vectors above 30.</summary>
    private void ReadVectors(ref PrcBitReader r, int count)
    {
        uint width = r.ReadBits(6);
        if (width == 0)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, "A compressed PRC Hermite curve has points of no bits.");
        }

        for (int i = 0; i < count; i++)
        {
            if (width > 30)
            {
                PrcVector3D v = PrcReaders.ReadVector3D(ref r, _ctx);
                AddPoint(new PrcVector(v.X, v.Y, v.Z));
            }
            else
            {
                _ctx.Mark("Point3DWithVariableBitNumber", r.BitPosition);
                // Half the body's tolerance, not the whole one prc.json gives: with it the two halves of a circle of radius 2.5 mm close on
                // their own vertices (face 485 of the LandingGear sample); with the whole tolerance each is twice as long as its ends are apart.
                AddPoint(r.ReadPoint3DWithVariableBitNumber((int)width, Tolerance / 2));
            }
        }
    }

    private int ReadCompositeCurve(ref PrcBitReader r)
    {
        _ctx.Enter("PRC_HCG_CompositeCurve", r.BitPosition);
        ReadType(ref r);
        (int start, int end) = ReadStartEndData(ref r);
        r.ReadUnsignedInteger();                                // dimension: 2 or 3
        bool closed = r.ReadBoolean();
        int count = _ctx.Count(ref r, r.ReadUnsignedInteger(), 1);
        var members = new int[count];
        for (int i = 0; i < members.Length; i++)
        {
            members[i] = ReadRefOrCurve(ref r);
        }

        // The members are stored before the curve they make up: it comes after them.
        int dataStart = _compositeMembers.Count;
        _compositeMembers.AddRange(members);
        _ctx.Leave();
        return AddCurve(PrcCurveKind.Composite, start, end, dataStart, members.Length, closed ? PrcBrepCurveFlags.Closed : PrcBrepCurveFlags.None);
    }
}
