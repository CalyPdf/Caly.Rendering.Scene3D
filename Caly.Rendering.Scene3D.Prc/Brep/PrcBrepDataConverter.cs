using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// An uncompressed body (<c>PRC_TYPE_TOPO_BrepData</c>, draft 7.9.18) as the body model the compressed reader gives
/// (<see cref="PrcTopoBrepDataCompress"/>), so that the same tessellator draws it: its vertices, its edges as curves, its faces
/// as ana faces on the surfaces they name (the analytic kinds as generic surfaces, NURBS as spline surfaces, any other kind
/// unsupported), its loops as runs of those curves.
/// <para>
/// An entity is stored where it is first used and named by its number after that (draft 7.4.7.2). The numbers, read off the
/// corpus (phase B ledger): topology from 1, in file order with an entity before what it holds, counted across the bodies of a
/// topological context, null pointers not counted; surfaces likewise, on a count of their own. <see cref="Index"/> makes those
/// tables once per context.
/// </para>
/// </summary>
internal static class PrcBrepDataConverter
{
    /// <summary>
    /// The coordinates of a rational NURBS's control points are stored multiplied by their weights ("all the coordinates x, y, z
    /// are weighted", draft 7.10.5): read so, the starts of the 1,143 rational edge curves of the corpus are on their vertices, read
    /// as they stand 100 are not (ledger, phase B Task 4).
    /// </summary>
    private const bool WeightedCoordinates = true;

    /// <summary>
    /// A NURBS surface's control points are stored for each u a run of v (the order <see cref="PrcNurbs"/> keeps them in): so, 7,132
    /// edge vertices of the corpus lie on their faces' surfaces to 5 chords and 879 do not; the other way round, 4,649 and 3,362.
    /// </summary>
    private const bool UMajor = true;

    /// <summary>The entities of a topological context by the numbers the file names them by.</summary>
    internal sealed class Tables
    {
        public List<PrcTopo> Topology { get; } = [];

        public List<PrcSurf> Surfaces { get; } = [];
    }

    /// <summary>Numbers every entity of a context's uncompressed bodies, in the order the file stores them.</summary>
    public static Tables Index(PrcTopoContext context)
    {
        var tables = new Tables();
        foreach (PrcTopoBody body in context.Bodies)
        {
            if (body.Id is PrcTopoBrepData data)
            {
                foreach (PrcPtrTopology connex in data.Connex)
                {
                    Walk(tables, connex, 0);
                }
            }
        }

        return tables;
    }

    private static void Walk(Tables tables, PrcPtrTopology pointer, int depth)
    {
        if (pointer.IsStored || pointer.Topo is null)
        {
            return;
        }

        if (depth > 16)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, "The topology of a PRC body is nested deeper than it can be.");
        }

        PrcTopo topo = pointer.Topo;
        if (topo.IdType != PrcType.PRC_TYPE_ROOT)
        {
            tables.Topology.Add(topo);
        }

        switch (topo.Id)
        {
            case PrcTopoConnex connex:
                foreach (PrcPtrTopology shell in connex.Shells)
                {
                    Walk(tables, shell, depth + 1);
                }

                break;
            case PrcTopoShell shell:
                foreach (PrcFacesInShell face in shell.Faces)
                {
                    Walk(tables, face.Face, depth + 1);
                }

                break;
            case PrcTopoFace face:
                Number(tables, face.SurfaceGeometry);

                foreach (PrcPtrTopology loop in face.Loops)
                {
                    Walk(tables, loop, depth + 1);
                }

                break;
            case PrcTopoLoop loop:
                foreach (PrcCoedgeInLoop coedge in loop.Coedge)
                {
                    Walk(tables, coedge.NextCoedge, depth + 1);
                }

                break;
            case PrcTopoCoEdge coedge:
                Walk(tables, coedge.PtrTopology, depth + 1);
                break;
            case PrcTopoEdge edge:
                // The edge's curve comes before its vertices in the file; an intersection curve holds two surfaces of its own.
                if (!edge.WireEdge.PtrCurve.IsReferenced && edge.WireEdge.PtrCurve.Curve?.Id is PrcCrvIntersection intersection)
                {
                    Number(tables, intersection.Surface1);
                    Number(tables, intersection.Surface2);
                }

                Walk(tables, edge.StartVertex, depth + 1);
                Walk(tables, edge.EndVertex, depth + 1);
                break;
        }
    }

    private static void Number(Tables tables, PrcPtrSurface pointer)
    {
        if (!pointer.IsReferenced && pointer.Surface is { } surface && surface.IdType != PrcType.PRC_TYPE_ROOT)
        {
            tables.Surfaces.Add(surface);
        }
    }

    /// <summary>The body model of one uncompressed body; false (and why) when a reference does not name what it must.</summary>
    public static bool TryConvert(PrcTopoBrepData data, Tables tables, double tolerance, out PrcTopoBrepDataCompress body, out string? error)
    {
        var work = new Work(tables);
        try
        {
            foreach (PrcPtrTopology connex in data.Connex)
            {
                if (work.Resolve(connex) is PrcTopoConnex c)
                {
                    work.Connex(c);
                }
                else
                {
                    throw new InvalidDataException("A body's connex is not a connex.");
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or ArithmeticException)
        {
            // A reference that does not name what it must, or numbers that make no curve or surface where one is followed: the body
            // fails alone.
            body = null!;
            error = e.Message;
            return false;
        }

        body = work.Build(double.IsFinite(tolerance) && tolerance > 0 ? tolerance : 0);
        error = null;
        return true;
    }

    private sealed class Work(Tables tables)
    {
        private readonly List<PrcVector> _vertices = [];
        private readonly List<PrcBrepCurve> _curves = [];
        private readonly List<double> _curveData = [];
        private readonly List<int> _members = [];
        private readonly List<int> _coedges = [];
        private readonly List<bool> _reversed = [];
        private readonly List<PrcBrepLoop> _loops = [];
        private readonly List<PrcBrepFace> _faces = [];
        private readonly List<PrcGenericSurface> _generic = [];
        private readonly List<PrcNurbs> _nurbs = [];
        private readonly List<IPrcParametricCurve> _curveNurbs = [];
        private readonly Dictionary<object, int> _vertexOf = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> _curveOf = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, (int Kind, int Index)> _surfaceOf = new(ReferenceEqualityComparer.Instance);
        private int _shell;
        private readonly HashSet<object> _facesSeen = new(ReferenceEqualityComparer.Instance);

        /// <summary>Most coedges of one body: a file can name stored loops again and again for a few bytes each.</summary>
        private const int MaxCoedges = 1 << 24;

        public PrcTopoBrepDataCompress Build(double tolerance) => new()
        {
            Tolerance = tolerance,
            Vertices = [.. _vertices],
            Curves = [.. _curves],
            CurveData = [.. _curveData],
            CompositeMembers = [.. _members],
            Coedges = [.. _coedges],
            CoedgeReversed = [.. _reversed],
            Loops = [.. _loops],
            Faces = [.. _faces],
            SurfaceData = [],
            FaceVertices = [],
            GenericSurfaces = [.. _generic],
            Nurbs = [.. _nurbs],
            CurveNurbs = [.. _curveNurbs],
        };

        /// <summary>The entity a pointer names: the one stored there, or the one stored earlier under that number; null for a null pointer.</summary>
        public object? Resolve(PrcPtrTopology pointer)
        {
            if (!pointer.IsStored)
            {
                return pointer.Topo?.Id;
            }

            long index = (long)pointer.TopoIdentifier - 1;
            if (index < 0 || index >= tables.Topology.Count)
            {
                throw new InvalidDataException($"A topological entity is named by a number ({pointer.TopoIdentifier}) that was not given.");
            }

            return tables.Topology[(int)index].Id;
        }

        public void Connex(PrcTopoConnex connex)
        {
            foreach (PrcPtrTopology pointer in connex.Shells)
            {
                if (Resolve(pointer) is not PrcTopoShell shell)
                {
                    throw new InvalidDataException("A connex's shell is not a shell.");
                }

                foreach (PrcFacesInShell entry in shell.Faces)
                {
                    if (Resolve(entry.Face) is not PrcTopoFace face)
                    {
                        throw new InvalidDataException("A shell's face is not a face.");
                    }

                    // A face named again (by its number) is the same face: drawn once.
                    if (_facesSeen.Add(face))
                    {
                        Face(face, entry.Orientation != 0);
                    }
                }

                _shell++;
            }
        }

        private void Face(PrcTopoFace face, bool withShell)
        {
            (PrcFaceKind kind, int surface) = Surface(face.SurfaceGeometry);
            PrcNativeSurface? native = PrcNativeSurface.From(Stored(face.SurfaceGeometry), kind == PrcFaceKind.AnaNurbs ? _nurbs[surface] : null);
            int firstLoop = _loops.Count;
            foreach (PrcPtrTopology pointer in face.Loops)
            {
                if (Resolve(pointer) is not PrcTopoLoop loop)
                {
                    throw new InvalidDataException("A face's loop is not a loop.");
                }

                int first = _coedges.Count;
                if ((long)_coedges.Count + loop.Coedge.Length > MaxCoedges)
                {
                    throw new InvalidDataException("A body has more coedges than can be drawn.");
                }

                foreach (PrcCoedgeInLoop entry in loop.Coedge)
                {
                    object? coedge = Resolve(entry.NextCoedge);
                    PrcTopoCoEdge c = coedge as PrcTopoCoEdge ?? throw new InvalidDataException("A loop's coedge is not a coedge.");
                    _coedges.Add(Curve(Resolve(c.PtrTopology), c.PtrCurves.IsReferenced ? null : c.PtrCurves.Curve, native));
                    _reversed.Add(c.CoedgeOrientation == 0);      // 0 against the edge, 1 with it, 2 not known (taken as with it)
                }

                _loops.Add(new PrcBrepLoop(first, _coedges.Count - first, loop.LoopOrientation != 0));
            }

            if (_loops.Count == firstLoop && kind == PrcFaceKind.AnaGenericFace && native is not null && (face.IsTrimmed ? face.TrimmedSurface : native.Domain) is { } domain)
            {
                DomainLoop(native, domain);
            }

            int loops = _loops.Count - firstLoop;
            _faces.Add(new PrcBrepFace(kind, _shell, withShell, loops > 0, firstLoop, loops, 0, 0, 0, 0, surface, default, false));
        }

        /// <summary>Points along each side of a face's domain: enough for a quarter turn of a cylinder to be within a hundredth of its radius.</summary>
        private const int DomainSideSamples = 48;

        /// <summary>
        /// A face with no loop is the part of its surface over its domain (draft 7.9.11): the domain's four sides, through the surface,
        /// as one loop (a side of no length, at a pole, is a run of one point).
        /// </summary>
        private void DomainLoop(PrcNativeSurface surface, PrcDomain domain)
        {
            double u0 = domain.MinUv.X, v0 = domain.MinUv.Y, u1 = domain.MaxUv.X, v1 = domain.MaxUv.Y;
            const double Far = 1e7;
            if (!(u1 > u0) || !(v1 > v0) || Math.Abs(u0) > Far || Math.Abs(u1) > Far || Math.Abs(v0) > Far || Math.Abs(v1) > Far)
            {
                return;
            }

            (double U, double V)[] corners = [(u0, v0), (u1, v0), (u1, v1), (u0, v1)];
            int[] at = [.. corners.Select(c => AddVertex(surface.At(c.U, c.V)))];
            int first = _coedges.Count;
            for (int side = 0; side < 4; side++)
            {
                (double ua, double va) = corners[side];
                (double ub, double vb) = corners[(side + 1) % 4];
                int dataAt = _curveData.Count;
                for (int i = 0; i <= DomainSideSamples; i++)
                {
                    double t = (double)i / DomainSideSamples;
                    PrcVector p = surface.At(ua + ((ub - ua) * t), va + ((vb - va) * t));
                    _curveData.AddRange([p.X, p.Y, p.Z]);
                }

                _curves.Add(new PrcBrepCurve(PrcCurveKind.Polyline, at[side], at[(side + 1) % 4], dataAt, _curveData.Count - dataAt, 0));
                _coedges.Add(_curves.Count - 1);
                _reversed.Add(false);
            }

            _loops.Add(new PrcBrepLoop(first, 4, true));
        }

        private int AddVertex(PrcVector p)
        {
            _vertices.Add(p);
            return _vertices.Count - 1;
        }

        /// <summary>The surface a pointer names: the one stored there, or the one stored earlier under that number (null when there is none).</summary>
        private PrcSurf? Stored(PrcPtrSurface pointer)
        {
            if (!pointer.IsReferenced)
            {
                return pointer.Surface;
            }

            long index = (long)pointer.SurfaceIdentifier - 1;
            return index >= 0 && index < tables.Surfaces.Count ? tables.Surfaces[(int)index] : null;
        }

        /// <summary>The face kind and surface number of a face's surface: generic for the analytic kinds and any kind not drawn, spline for NURBS.</summary>
        private (PrcFaceKind Kind, int Index) Surface(PrcPtrSurface pointer)
        {
            PrcSurf? surface = Stored(pointer);

            if (surface?.Id is null)
            {
                _generic.Add(new PrcGenericSurface(PrcGenericSurfaceKind.Unsupported, surface?.IdType ?? 0, Matrix4x4d.Identity, 0, 0));
                return (PrcFaceKind.AnaGenericFace, _generic.Count - 1);
            }

            if (_surfaceOf.TryGetValue(surface.Id, out (int Kind, int Index) known))
            {
                return ((PrcFaceKind)known.Kind, known.Index);
            }

            (PrcFaceKind Kind, int Index) result;
            if (surface.Id is PrcSurfNurbs n && Nurbs(n) is { } nurbs)
            {
                _nurbs.Add(nurbs);
                result = (PrcFaceKind.AnaNurbs, _nurbs.Count - 1);
            }
            else
            {
                PrcGenericSurface generic = surface.Id is PrcSurfNurbs ? new PrcGenericSurface(PrcGenericSurfaceKind.Unsupported, surface.IdType, Matrix4x4d.Identity, 0, 0) : PrcCompressedBrepReader.ToGeneric(surface);
                _generic.Add(generic);
                result = (PrcFaceKind.AnaGenericFace, _generic.Count - 1);
            }

            _surfaceOf.Add(surface.Id, ((int)result.Kind, result.Index));
            return result;
        }

        private static PrcNurbs? Nurbs(PrcSurfNurbs n)
        {
            long countU = (long)n.HighestIndexOfControlPointsInU + 1, countV = (long)n.HighestIndexOfControlPointsInV + 1;
            int degreeU = (int)Math.Min(n.Du, 64), degreeV = (int)Math.Min(n.Dv, 64);
            long count = countU * countV;
            int stored = n.IsRational ? n.P4.Length : n.P3.Length;
            if (degreeU < 1 || degreeV < 1 || countU <= degreeU || countV <= degreeV || count != stored || n.Ku.Length != countU + degreeU + 1 || n.Kv.Length != countV + degreeV + 1)
            {
                return null;
            }

            var points = new PrcVector[count];
            double[]? weights = n.IsRational ? new double[count] : null;
            for (long k = 0; k < count; k++)
            {
                // The order the model keeps them in: for each u, a run of v.
                long u = UMajor ? k / countV : k % countU, v = UMajor ? k % countV : k / countU;
                long at = (u * countV) + v;
                if (n.IsRational)
                {
                    PrcControlPointsNurbsSurf4 p = n.P4[k];
                    double w = p.W;
                    if (!(w > 0) || !double.IsFinite(w))
                    {
                        return null;
                    }

                    points[at] = WeightedCoordinates ? new PrcVector(p.X / w, p.Y / w, p.Z / w) : new PrcVector(p.X, p.Y, p.Z);
                    weights![at] = w;
                }
                else
                {
                    PrcControlPointsNurbsSurf3 p = n.P3[k];
                    points[at] = new PrcVector(p.X, p.Y, p.Z);
                }
            }

            if (!Ordered(n.Ku) || !Ordered(n.Kv))
            {
                return null;
            }

            return new PrcNurbs
            {
                DegreeU = degreeU,
                DegreeV = degreeV,
                KnotsU = n.Ku,
                KnotsV = n.Kv,
                CountU = (int)countU,
                CountV = (int)countV,
                ControlPoints = points,
                Weights = weights,
            };
        }

        private static bool Ordered(double[] knots)
        {
            for (int i = 1; i < knots.Length; i++)
            {
                if (!(knots[i] >= knots[i - 1]))
                {
                    return false;
                }
            }

            return knots.Length > 1 && knots[^1] > knots[0];
        }

        // ---- edges

        /// <summary>
        /// The curve number of an edge, made the first time the edge is met. An edge with no curve in space takes the 2D curve of the
        /// coedge on its face's surface, at the first coedge that has one.
        /// </summary>
        private int Curve(object? edge, PrcCrv? uv, PrcNativeSurface? surface)
        {
            if (edge is null)
            {
                throw new InvalidDataException("A coedge has no edge.");
            }

            if (_curveOf.TryGetValue(edge, out int known))
            {
                if (_curves[known].Kind == PrcCurveKind.Implicit && OnSurface(edge, uv, surface) is { } better)
                {
                    _curves[known] = better;
                }

                return known;
            }

            PrcContentWireEdge wire;
            object? start = null, end = null;
            switch (edge)
            {
                case PrcTopoEdge e:
                    wire = e.WireEdge;
                    start = Resolve(e.StartVertex);
                    end = Resolve(e.EndVertex);
                    break;
                case PrcTopoWireEdge w:
                    wire = w.Curve;
                    break;
                default:
                    throw new InvalidDataException("A coedge's edge is not an edge.");
            }

            int index = FromCurve(wire.PtrCurve.IsReferenced ? null : wire.PtrCurve.Curve, wire.IsTrimmed ? wire.TrimInterval : null, start, end);
            if (_curves[index].Kind == PrcCurveKind.Implicit && OnSurface(edge, uv, surface) is { } onSurface)
            {
                _curves[index] = onSurface;
            }

            _curveOf.Add(edge, index);
            return index;
        }

        /// <summary>
        /// An edge as the 2D curve of a coedge on its face's surface, turned to run from the edge's start vertex to its end; null when
        /// there is no such curve.
        /// </summary>
        private PrcBrepCurve? OnSurface(object edge, PrcCrv? uv, PrcNativeSurface? surface)
        {
            if (surface is null || uv?.Id is not PrcCrvNurbs n || NurbsCurve(n, plane: true) is not { } curve || edge is not PrcTopoEdge e)
            {
                return null;
            }

            var onSurface = new PrcCurveOnSurface(curve, surface);
            PrcVector first = onSurface.Evaluate(curve.Min), last = onSurface.Evaluate(curve.Max);
            int s = Vertex(Resolve(e.StartVertex), first), end = Vertex(Resolve(e.EndVertex), last);
            if (s < 0 || end < 0)
            {
                return null;
            }

            PrcVector a = _vertices[s], b = _vertices[end];
            bool forward = (first - a).LengthSquared() + (last - b).LengthSquared() <= (first - b).LengthSquared() + (last - a).LengthSquared();
            _curveNurbs.Add(onSurface);
            int at = _curveData.Count;
            _curveData.AddRange([_curveNurbs.Count - 1, forward ? curve.Min : curve.Max, forward ? curve.Max : curve.Min]);
            return new PrcBrepCurve(PrcCurveKind.NurbsCurve, s, end, at, 3, 0);
        }

        private int Vertex(object? vertex, PrcVector? fallback)
        {
            if (vertex is not null && _vertexOf.TryGetValue(vertex, out int known))
            {
                return known;
            }

            PrcVector? p = vertex switch
            {
                PrcTopoUniqueVertex u => V(u.Vertex),
                PrcTopoMultipleVertex m when m.Points.Length > 0 => V(m.Points[0]),
                null => fallback,
                _ => throw new InvalidDataException("An edge's vertex is not a vertex."),
            };

            if (p is not { } point)
            {
                return -1;
            }

            _vertices.Add(point);
            if (vertex is not null)
            {
                _vertexOf.Add(vertex, _vertices.Count - 1);
            }

            return _vertices.Count - 1;
        }

        /// <summary>A curve of the body for a stored curve between two vertices (made from the curve's ends where the edge has none).</summary>
        private int FromCurve(PrcCrv? stored, PrcInterval? trim, object? start, object? end)
        {
            Geometry g = Describe(stored, trim);
            int s = Vertex(start, g.Start), e = Vertex(end, g.End);
            if (g.Kind == PrcCurveKind.Implicit || s < 0 || e < 0)
            {
                // A kind of curve this reader does not follow: the faces it bounds cannot be closed, and are dropped.
                _curves.Add(new PrcBrepCurve(PrcCurveKind.Implicit, -1, -1, -1, 0, 0));
                return _curves.Count - 1;
            }

            if (g.Kind == PrcCurveKind.Composite)
            {
                int first = _members.Count;
                // Each member between its own ends; the sampler turns each to continue the one before.
                foreach ((PrcCrv? member, _) in g.Members!)
                {
                    _members.Add(FromCurve(member, null, null, null));
                }

                _curves.Add(new PrcBrepCurve(PrcCurveKind.Composite, s, e, first, _members.Count - first, 0));
                return _curves.Count - 1;
            }

            double[] data = g.Data ?? [];
            if (g.Kind == PrcCurveKind.Polyline && data.Length >= 6)
            {
                // A polyline runs as its points were stored, which need not be from the edge's start vertex to its end.
                PrcVector first = new(data[0], data[1], data[2]), last = new(data[^3], data[^2], data[^1]);
                PrcVector a = _vertices[s], b = _vertices[e];
                if ((first - b).LengthSquared() + (last - a).LengthSquared() < (first - a).LengthSquared() + (last - b).LengthSquared())
                {
                    var reversed = new double[data.Length];
                    for (int k = 0; k < data.Length; k += 3)
                    {
                        reversed[data.Length - 3 - k] = data[k];
                        reversed[data.Length - 2 - k] = data[k + 1];
                        reversed[data.Length - 1 - k] = data[k + 2];
                    }

                    data = reversed;
                }
            }

            int at = _curveData.Count;
            _curveData.AddRange(data);
            _curves.Add(new PrcBrepCurve(g.Kind, s, e, at, _curveData.Count - at, 0));
            return _curves.Count - 1;
        }

        private sealed record Geometry(PrcCurveKind Kind, PrcVector? Start, PrcVector? End, double[]? Data, (PrcCrv? Curve, bool Sense)[]? Members = null);

        private static readonly Geometry None = new(PrcCurveKind.Implicit, null, null, null);

        /// <summary>What a stored curve is over an interval (the edge's, else the curve's own): its kind for the body model, its ends, its numbers.</summary>
        private Geometry Describe(PrcCrv? stored, PrcInterval? trim)
        {
            switch (stored?.Id)
            {
                case PrcCrvLine line:
                {
                    // The line along x, at the implicit parameter (coeff_a · t + coeff_b, draft 7.10.3).
                    Matrix4x4d frame = PrcBrepMath.Frame(line.Transform3d?.Transformation);
                    (double t0, double t1) = Range(line.Parameterization, trim);
                    double x0 = (line.Parameterization.CoeffA * t0) + line.Parameterization.CoeffB, x1 = (line.Parameterization.CoeffA * t1) + line.Parameterization.CoeffB;
                    return new Geometry(PrcCurveKind.Line, frame.Transform(new PrcVector(x0, 0, 0)), frame.Transform(new PrcVector(x1, 0, 0)), []);
                }

                case PrcCrvCircle circle:
                    return Conic(circle.Transform3d, circle.Parameterization, trim, circle.Radius, circle.Radius);
                case PrcCrvEllipse ellipse:
                    return Conic(ellipse.Transform3d, ellipse.Parameterization, trim, ellipse.Rx, ellipse.Ry);
                case PrcCrvNurbs nurbs:
                {
                    PrcNurbsCurve? curve = NurbsCurve(nurbs);
                    if (curve is null)
                    {
                        return None;
                    }

                    double t0 = trim?.MinValue ?? curve.Min, t1 = trim?.MaxValue ?? curve.Max;
                    if (!double.IsFinite(t0) || !double.IsFinite(t1))
                    {
                        return None;
                    }

                    _curveNurbs.Add(curve);
                    return new Geometry(PrcCurveKind.NurbsCurve, curve.Evaluate(t0), curve.Evaluate(t1), [_curveNurbs.Count - 1, t0, t1]);
                }

                case PrcCrvPolyLine polyline when polyline.Points3d.Length >= 2:
                {
                    Matrix4x4d frame = PrcBrepMath.Frame(polyline.Transform3d?.Transformation);
                    var data = new double[3 * polyline.Points3d.Length];
                    for (int i = 0; i < polyline.Points3d.Length; i++)
                    {
                        PrcVector p = frame.Transform(V(polyline.Points3d[i]));
                        data[3 * i] = p.X;
                        data[(3 * i) + 1] = p.Y;
                        data[(3 * i) + 2] = p.Z;
                    }

                    return new Geometry(PrcCurveKind.Polyline, new PrcVector(data[0], data[1], data[2]), new PrcVector(data[^3], data[^2], data[^1]), data);
                }

                case PrcCrvIntersection intersection when intersection.Crossings.Length >= 2:
                {
                    // The points the curve was found through (draft 7.10.13), in order of their parameter: a polyline to the chord the
                    // writer kept; a trim keeps the points inside it.
                    Matrix4x4d frame = PrcBrepMath.Frame(intersection.Transform3d?.Transformation);
                    var data = new List<double>(3 * intersection.Crossings.Length);
                    foreach (PrcCrossingPointsCrvIntersection c in intersection.Crossings.OrderBy(c => c.Parameter))
                    {
                        if (trim is { } range && (c.Parameter < Math.Min(range.MinValue, range.MaxValue) || c.Parameter > Math.Max(range.MinValue, range.MaxValue)))
                        {
                            continue;
                        }

                        PrcVector p = frame.Transform(V(c.Position));
                        data.AddRange([p.X, p.Y, p.Z]);
                    }

                    return data.Count < 6 ? None : new Geometry(PrcCurveKind.Polyline, new PrcVector(data[0], data[1], data[2]), new PrcVector(data[^3], data[^2], data[^1]), [.. data]);
                }

                case PrcCrvComposite composite when composite.Subcurves.Length > 0 && trim is null:
                {
                    var members = composite.Subcurves.Select(s => (s.Subcurve.IsReferenced ? null : s.Subcurve.Curve, s.Sense)).ToArray();
                    Geometry first = Describe(members[0].Item1, null), last = Describe(members[^1].Item1, null);
                    return new Geometry(PrcCurveKind.Composite, first.Start, last.End, null, members);
                }

                default:
                    return None;
            }
        }

        private static Geometry Conic(PrcTransformation3DWithBit? transform, PrcParameterization parameterization, PrcInterval? trim, double rx, double ry)
        {
            Matrix4x4d frame = PrcBrepMath.Frame(transform?.Transformation);
            (double t0, double t1) = Range(parameterization, trim);
            double a0 = (parameterization.CoeffA * t0) + parameterization.CoeffB, a1 = (parameterization.CoeffA * t1) + parameterization.CoeffB;
            PrcVector centre = frame.Origin, a = frame.X * rx, b = frame.Y * ry;
            if (!double.IsFinite(a0) || !double.IsFinite(a1) || !(Math.Abs(rx) > 0) || !(Math.Abs(ry) > 0))
            {
                return None;
            }

            PrcVector start = centre + (a * Math.Cos(a0)) + (b * Math.Sin(a0)), end = centre + (a * Math.Cos(a1)) + (b * Math.Sin(a1));
            return new Geometry(PrcCurveKind.Conic, start, end, [centre.X, centre.Y, centre.Z, a.X, a.Y, a.Z, b.X, b.Y, b.Z, a0, a1]);
        }

        /// <summary>The parameters of a curve's part: the edge's trim, else the curve's own interval.</summary>
        private static (double, double) Range(PrcParameterization parameterization, PrcInterval? trim)
        {
            PrcInterval interval = trim ?? parameterization.TrimInterval;
            return (interval.MinValue, interval.MaxValue);
        }

        /// <summary>A B-spline curve in space, or (<paramref name="plane"/>) a 2D one, its points in x and y.</summary>
        private static PrcNurbsCurve? NurbsCurve(PrcCrvNurbs n, bool plane = false)
        {
            if (n.CurveData.Is3dFlag == plane || n.P.Length == 0 || n.Degree is < 1 or > 64)
            {
                return null;
            }

            var points = new PrcVector[n.P.Length];
            double[]? weights = n.IsRational ? new double[n.P.Length] : null;
            for (int i = 0; i < points.Length; i++)
            {
                PrcControlPointsNurbsCrv p = n.P[i];
                double w = n.IsRational ? p.W : 1;
                points[i] = n.IsRational && WeightedCoordinates && w != 0 ? new PrcVector(p.X / w, p.Y / w, p.Z / w) : new PrcVector(p.X, p.Y, p.Z);
                if (weights is not null)
                {
                    weights[i] = w;
                }
            }

            var curve = new PrcNurbsCurve((int)n.Degree, n.U, points, weights);
            return curve.IsValid() ? curve : null;
        }

        private static PrcVector V(PrcVector3D v) => new(v.X, v.Y, v.Z);
    }
}
