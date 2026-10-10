using Caly.Rendering.Scene3D.Geometry;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>What cutting a body into triangles gave: counts for the diagnostics.</summary>
/// <param name="Faces">Faces of the body.</param>
/// <param name="FacesDropped">Faces left out because they could not be cut (their loops do not close, a surface does not fit its curves, the triangulation failed).</param>
/// <param name="FacesUnsupported">Faces left out because they lie on a kind of surface that is not read.</param>
/// <param name="Capped">Whether an edge or a face needed more than the caps allow, and was cut coarser.</param>
/// <param name="Triangles">Triangles made.</param>
internal readonly record struct PrcBrepTessellation(int Faces, int FacesDropped, int FacesUnsupported, bool Capped, int Triangles);

/// <summary>
/// Cuts a compressed body into triangles (decoding note, spec §4). Faces are taken in the order the file stores them, so that a
/// side an iso face deduces has been given its points before another face refers to it. Every edge is sampled once
/// (<see cref="PrcEdgeSampler"/>), and every face that uses it takes those points exactly, so that neighbouring faces meet without
/// cracks. A face is cut in the parameters of its surface: the loops are taken there, points of a grid fine enough for the chord
/// tolerance and the angle cap are added inside, and the polygon with its holes and those points is triangulated
/// (<see cref="ConstrainedTriangulator"/>); the corners are the points of the edges and the surface evaluated at the others, with
/// the normals of the surface turned as the face says.
/// </summary>
internal static class PrcBrepTessellator
{
    /// <summary>
    /// Adds the body's mesh (one part, material slot 0) to the scene being built: index -1 in the result when no face gave a triangle.
    /// <paramref name="scale"/> is the scale its topological context gives its bodies (the coordinates of the body times it are the file's).
    /// </summary>
    public static PrcMesh AddMesh(SceneBuilder builder, PrcTopoBrepDataCompress brep, int name, out PrcBrepTessellation result, double scale = 1, CancellationToken cancellation = default)
    {
        var work = new Work(brep, builder.Limits.MaxIndices, cancellation);
        work.Run();
        result = work.Result;
        int triangles = result.Triangles;
        if (triangles == 0)
        {
            return new PrcMesh(-1, [], 0, false);
        }

        if ((long)triangles * 3 > builder.Limits.MaxIndices)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {builder.Limits.MaxIndices} indices.");
        }

        double[] points = work.Points;
        if (scale != 1)
        {
            for (int i = 0; i < points.Length; i++)
            {
                points[i] *= scale;
            }
        }

        int[] corners = work.Corners;
        int[] none = new int[corners.Length];
        none.AsSpan().Fill(-1);
        return PrcMesher.Emit(
            builder,
            name,
            SceneMeshFlags.HasNormals,
            [],
            points,
            work.Normals,
            default,
            corners,
            corners,
            none,
            none,
            new int[triangles],
            triangles,
            missingNormals: false,
            creaseAngle: 0,
            anyColor: false,
            cancellation);
    }

    /// <summary>Drops every other line (the second, fourth, ...), by position: equal values are different lines.</summary>
    internal static void Thin(List<double> lines)
    {
        int kept = 0;
        for (int i = 0; i < lines.Count; i += 2)
        {
            lines[kept++] = lines[i];
        }

        lines.RemoveRange(kept, lines.Count - kept);
    }

    private const int MaxSidesPerLoop = 20_000;

    private enum Status
    {
        Done,
        Dropped,
        Unsupported,
    }

    /// <summary>A loop of a face: its points in 3D, in the parameters of the surface (continuous along the loop), and how many turns it makes.</summary>
    private sealed class Loop
    {
        public List<PrcVector> Points { get; } = [];

        public List<double> U { get; } = [];

        public List<double> V { get; } = [];

        public int WindingU { get; set; }

        public int WindingV { get; set; }

        /// <summary>A loop that is a point (a vertex loop): it bounds nothing.</summary>
        public bool Degenerate { get; set; }
    }


    private sealed class Work
    {
        private readonly PrcTopoBrepDataCompress _body;
        private PrcEdgeSampler _edges;
        private readonly double _chord;
        private readonly CancellationToken _cancellation;
        private readonly long _maxIndices;
        private readonly Dictionary<PrcNurbs, (int[] U, int[] V)> _steps = new(ReferenceEqualityComparer.Instance);
        private readonly List<double> _points = [];
        private readonly List<double> _normals = [];
        private readonly List<int> _corners = [];
        private readonly ConstrainedTriangulator _triangulator = new();
        private int _faces, _dropped, _unsupported;
        private bool _capped;

        public Work(PrcTopoBrepDataCompress body, long maxIndices, CancellationToken cancellation)
        {
            _maxIndices = maxIndices;
            _body = body;
            _cancellation = cancellation;
            _chord = PrcBrepTolerance.Chord(body);
            _edges = new PrcEdgeSampler(body, _chord);
        }

        public double[] Points => [.. _points];

        public double[] Normals => [.. _normals];

        public int[] Corners => [.. _corners];

        public PrcBrepTessellation Result => new(_faces, _dropped, _unsupported, _capped || _edges.Capped, _corners.Count / 3);

        public void Run()
        {
            // A face may name a side that an iso face later in the body deduces: such a face is cut again once every face has been.
            var waiting = new List<int>();
            for (int pass = 0; pass < 2; pass++)
            {
                int count = pass == 0 ? _body.Faces.Length : waiting.Count;
                for (int k = 0; k < count; k++)
                {
                    int f = pass == 0 ? k : waiting[k];
                    _cancellation.ThrowIfCancellationRequested();
                    if (pass == 0)
                    {
                        _faces++;
                    }

                    Status status = Attempt(f, out bool unresolved);
                    if (pass == 0 && unresolved)
                    {
                        waiting.Add(f);
                        continue;
                    }

                    if (status == Status.Dropped)
                    {
                        _dropped++;
                    }
                    else if (status == Status.Unsupported)
                    {
                        _unsupported++;
                    }
                }
            }
        }

        /// <summary>One face, what it began taken back if it fails; <paramref name="unresolved"/> when it named a deduced side nothing deduced yet.</summary>
        private Status Attempt(int f, out bool unresolved)
        {
            unresolved = false;
            Status status;
            int points = _points.Count, normals = _normals.Count, corners = _corners.Count;
            try
            {
                status = Face(f);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException or ArithmeticException)
            {
                // A curve a face refers to has no points (nothing deduced it before), or its numbers do not make a surface (knots that
                // run backwards, a degree beyond the points): the face cannot be cut, and what it began is taken back.
                _points.RemoveRange(points, _points.Count - points);
                _normals.RemoveRange(normals, _normals.Count - normals);
                _corners.RemoveRange(corners, _corners.Count - corners);
                unresolved = e is InvalidOperationException && e.Message.StartsWith("A curve without ends", StringComparison.Ordinal);
                status = Status.Dropped;
            }

            if ((long)_corners.Count > _maxIndices)
            {
                throw new Scene3DException(Scene3DError.LimitExceeded, $"The scene has more than {_maxIndices} indices.");
            }

            return status;
        }

        // ---- a face

        private bool HasUnresolvedSide(PrcBrepFace face)
        {
            for (int l = 0; l < face.LoopCount; l++)
            {
                PrcBrepLoop loop = _body.Loops[face.FirstLoop + l];
                foreach (int curve in _body.Coedges.AsSpan(loop.FirstCoedge, loop.CoedgeCount))
                {
                    if (_body.Curves[curve].Kind is PrcCurveKind.Implicit or PrcCurveKind.IsoBoundary && !_edges.IsResolved(curve))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private Status Face(int f)
        {
            PrcBrepFace face = _body.Faces[f];
            if (face.Kind == PrcFaceKind.AnaGenericFace && face.Surface >= 0 && face.Surface < _body.GenericSurfaces.Length && _body.GenericSurfaces[face.Surface].Kind == PrcGenericSurfaceKind.Unsupported)
            {
                return Status.Unsupported;
            }

            // The sides an iso NURBS face does not store come from the patch itself; the rest are resolved once the surface is known.
            if (!PrcSurfaces.TryBuild(_body, f, _edges, out PrcSurface surface))
            {
                // An iso face whose surface comes from a side another face deduces, not deduced yet: tried again after the others.
                if (HasUnresolvedSide(face))
                {
                    throw new InvalidOperationException("A curve without ends of its own is not deduced yet.");
                }

                return Status.Dropped;
            }

            if (Cut(face, surface))
            {
                return Status.Done;
            }

            // A loop near a pole of a sphere is a tangle in the parameters (every direction is one there): the pole is moved to the other
            // side of the sphere from the face, where nothing is near it, and the face cut again.
            if (surface.Kind == PrcSurfaceKind.Sphere && TryTurnSphere(face, surface, out PrcSurface turned) && Cut(face, turned))
            {
                return Status.Done;
            }

            // A swept sheet whose sweep was lost (its two rows of control points one): swept again along the way the face lies off it.
            if (surface.Kind == PrcSurfaceKind.Nurbs && TrySweep(face, surface.Nurbs!, out PrcSurface swept) && Cut(face, swept))
            {
                return Status.Done;
            }

            // Two curves that touch (a hole tangent to the outline, an edge running along another) can cross between their samples: the
            // face is cut once more from its curves sampled finer, for it alone (its edges then meet its neighbours' to the chord).
            PrcEdgeSampler shared = _edges;
            _edges = shared.Finer(8);
            try
            {
                if (Cut(face, surface))
                {
                    return Status.Done;
                }
            }
            finally
            {
                _capped |= _edges.Capped;
                _edges = shared;
            }

            return Status.Dropped;
        }

        private bool Cut(PrcBrepFace face, PrcSurface surface)
        {
            var loops = new List<Loop>();
            return BuildLoops(face, surface, loops) && Cut(face, surface, loops);
        }

        /// <summary>
        /// A spline of degree 1 and two rows in v whose rows are one (to the chord): a sheet swept from a profile, stored by the profile
        /// and the step to the far end, the step lost (a sheet 20,000 long in one sample, stored at its far end). The face's points lie
        /// off the profile along one direction: the sheet is the profile swept along it, over the stretch the points cover.
        /// </summary>
        private bool TrySweep(PrcBrepFace face, PrcNurbs n, out PrcSurface swept)
        {
            swept = default;
            if (n.DegreeV != 1 || n.CountV != 2)
            {
                return false;
            }

            for (int i = 0; i < n.CountU; i++)
            {
                if (Distance(n.ControlPoints[2 * i], n.ControlPoints[(2 * i) + 1]) > _chord)
                {
                    return false;           // a real ruled sheet
                }
            }

            // How far each point of the face's edges is from the profile, and which way.
            var profile = new PrcSurface(n);
            var offsets = new List<PrcVector>();
            double hint = PrcNurbsSurface.MinU(n);
            for (int l = 0; l < face.LoopCount; l++)
            {
                PrcBrepLoop loop = _body.Loops[face.FirstLoop + l];
                foreach (int curve in _body.Coedges.AsSpan(loop.FirstCoedge, loop.CoedgeCount))
                {
                    if (!_edges.IsResolved(curve) && _body.Curves[curve].Start < 0)
                    {
                        continue;
                    }

                    foreach (PrcVector p in _edges.Samples(curve))
                    {
                        if (profile.TryInvert(p, hint, 0, double.PositiveInfinity, out double u, out double v))
                        {
                            offsets.Add(p - profile.Evaluate(u, v, out _, out _));
                            hint = u;
                        }
                    }
                }
            }

            PrcVector longest = default;
            foreach (PrcVector o in offsets)
            {
                if (o.LengthSquared() > longest.LengthSquared())
                {
                    longest = o;
                }
            }

            double reach = Math.Sqrt(longest.LengthSquared());
            if (!(reach > _chord))
            {
                return false;
            }

            PrcVector way = longest / reach;
            double near = double.MaxValue, far = double.MinValue;
            foreach (PrcVector o in offsets)
            {
                double along = o.Dot(way);
                if (Distance(o, way * along) > 5 * _chord)
                {
                    return false;           // the points are not off the profile along one direction
                }

                near = Math.Min(near, along);
                far = Math.Max(far, along);
            }

            near -= _chord;
            far += _chord;
            var points = new PrcVector[2 * n.CountU];
            double[]? weights = n.Weights is null ? null : new double[2 * n.CountU];
            for (int i = 0; i < n.CountU; i++)
            {
                PrcVector p = n.ControlPoints[2 * i];
                points[2 * i] = p + (way * near);
                points[(2 * i) + 1] = p + (way * far);
                if (weights is not null)
                {
                    weights[2 * i] = n.Weights![2 * i];
                    weights[(2 * i) + 1] = n.Weights[2 * i];
                }
            }

            swept = new PrcSurface(new PrcNurbs
            {
                DegreeU = n.DegreeU,
                DegreeV = 1,
                KnotsU = n.KnotsU,
                KnotsV = [0, 0, 1, 1],
                CountU = n.CountU,
                CountV = 2,
                ControlPoints = points,
                Weights = weights,
                ClosedU = n.ClosedU,
            });
            return true;
        }

        /// <summary>The sphere with its axis square to the direction of the face's loops from the centre, so that the face lies about <c>(0, 0)</c>.</summary>
        private bool TryTurnSphere(PrcBrepFace face, PrcSurface surface, out PrcSurface turned)
        {
            turned = default;
            PrcVector centre = surface.Frame.Origin, sum = default;
            int count = 0;
            for (int l = 0; l < face.LoopCount; l++)
            {
                PrcBrepLoop loop = _body.Loops[face.FirstLoop + l];
                foreach (int curve in _body.Coedges.AsSpan(loop.FirstCoedge, loop.CoedgeCount))
                {
                    if (!_edges.IsResolved(curve) && _body.Curves[curve].Start < 0)
                    {
                        continue;
                    }

                    foreach (PrcVector p in _edges.Samples(curve))
                    {
                        PrcVector d = p - centre;
                        double length = Math.Sqrt(d.LengthSquared());
                        if (length > 0)
                        {
                            sum += d / length;
                            count++;
                        }
                    }
                }
            }

            double mean = Math.Sqrt(sum.LengthSquared());
            if (count == 0 || mean < 0.2 * count)
            {
                return false;           // spread over the sphere: no side to put the pole on
            }

            PrcVector toFace = sum / mean;
            PrcVector other = Math.Abs(toFace.X) < 0.6 ? new PrcVector(1, 0, 0) : new PrcVector(0, 1, 0);
            turned = new PrcSurface(PrcSurfaceKind.Sphere, PrcSurfaces.FrameAt(centre, toFace.Cross(other), toFace), surface.Radius, 0);
            return true;
        }

        // ---- loops

        private bool BuildLoops(PrcBrepFace face, PrcSurface surface, List<Loop> loops)
        {
            if (face.LoopCount == 0)
            {
                return true;
            }

            for (int l = 0; l < face.LoopCount; l++)
            {
                PrcBrepLoop loop = _body.Loops[face.FirstLoop + l];
                ReadOnlySpan<int> curves = _body.Coedges.AsSpan(loop.FirstCoedge, loop.CoedgeCount);
                if (face.Kind == PrcFaceKind.IsoNurbs)
                {
                    ResolveIsoNurbsSides(face, surface, curves);
                }
                else if (face.Kind <= PrcFaceKind.IsoCone)
                {
                    if (!ResolveIsoSides(face, surface, curves))
                    {
                        return false;
                    }
                }

                if (!Chain(curves, loop.FirstCoedge, out Loop chained))
                {
                    return false;
                }

                if (!MapToParameters(surface, chained))
                {
                    return false;
                }

                loops.Add(chained);
            }

            return true;
        }

        private static double Distance(PrcVector a, PrcVector b) => Math.Sqrt((a - b).LengthSquared());

        /// <summary>
        /// The sides of a loop joined end to end: each side's points turned the way that continues the one before (the file stores a
        /// curve with the direction of the loop that stored it first). False when the loop does not close.
        /// </summary>
        private bool Chain(ReadOnlySpan<int> curves, int firstCoedge, out Loop loop)
        {
            loop = new Loop();
            if (curves.Length == 0 || curves.Length > MaxSidesPerLoop)
            {
                return false;           // (chaining is quadratic in the sides: a loop of tens of thousands is not a real one)
            }

            // The sides of a loop are listed in no particular order (a face's sides may share a vertex before the loop reaches it): the
            // loop is followed from the first one, each time to the unused side that starts (or ends) nearest to where it has got to.
            double closeTolerance = 10 * _chord;
            bool[] used = new bool[curves.Length];
            used[0] = true;
            ReadOnlySpan<PrcVector> first = _edges.Samples(curves[0]);
            bool forward = true;
            if (curves.Length > 1)
            {
                forward = NearestUnused(curves, used, first[^1], out _, out _) <= NearestUnused(curves, used, first[0], out _, out _);
            }

            // A side that starts and ends at one point (a whole circle) says nothing in its points of which way round it goes. An
            // uncompressed body's coedge says it. A compressed body keeps no sense: in a loop of two whole circles (the wall of a cylinder:
            // round the bottom, up a generator, round the top, down it) they go opposite ways, or the loop would wind once round the
            // surface instead of enclosing the wall.
            bool[]? reversed = _body.CoedgeReversed is { } r && r.Length == _body.Coedges.Length ? r : null;
            if (reversed is not null && IsClosedSide(curves[0]))
            {
                forward = !reversed[firstCoedge];
            }

            bool seam = reversed is null && CountClosedSides(curves) >= 2;
            bool turn = true;
            if (seam && IsClosedSide(curves[0]))
            {
                turn = false;       // the first whole circle runs forward, the next one back
            }

            for (int i = 0; i < first.Length; i++)
            {
                loop.Points.Add(forward ? first[i] : first[first.Length - 1 - i]);
            }

            for (int step = 1; step < curves.Length; step++)
            {
                double gap = NearestUnused(curves, used, loop.Points[^1], out int next, out bool along);
                if (gap > closeTolerance)
                {
                    return false;
                }

                used[next] = true;
                ReadOnlySpan<PrcVector> side = _edges.Samples(curves[next]);
                if (reversed is not null && IsClosedSide(curves[next]))
                {
                    along = !reversed[firstCoedge + next];
                }
                else if (seam && IsClosedSide(curves[next]))
                {
                    along = turn;
                    turn = !turn;
                }

                for (int i = 1; i < side.Length; i++)
                {
                    loop.Points.Add(along ? side[i] : side[side.Length - 1 - i]);
                }
            }

            if (Distance(loop.Points[^1], loop.Points[0]) > closeTolerance)
            {
                return false;
            }
            // The loop closes on its first point, which the last side's end is a copy of (a vertex quantised twice differs from itself
            // by a nanometre or so): not repeated, or the loop would double back on itself there.
            if (loop.Points.Count > 1)
            {
                loop.Points.RemoveAt(loop.Points.Count - 1);
            }

            double extent = 0;
            foreach (PrcVector p in loop.Points)
            {
                extent = Math.Max(extent, Distance(p, loop.Points[0]));
            }

            loop.Degenerate = loop.Points.Count < 3 || extent <= 1e-9 * Math.Max(1, _chord);
            return true;
        }

        private int CountClosedSides(ReadOnlySpan<int> curves)
        {
            int closed = 0;
            foreach (int curve in curves)
            {
                closed += IsClosedSide(curve) ? 1 : 0;
            }

            return closed;
        }

        /// <summary>Whether a side starts where it ends (a whole circle).</summary>
        private bool IsClosedSide(int curve)
        {
            ReadOnlySpan<PrcVector> side = _edges.Samples(curve);
            return side.Length > 2 && Distance(side[0], side[^1]) <= 1e-9 * Math.Max(1, _chord);
        }

        /// <summary>The distance from a point to the nearest end of a side not used yet, which side, and whether it is reached at its start.</summary>
        private double NearestUnused(ReadOnlySpan<int> curves, bool[] used, PrcVector from, out int which, out bool atStart)
        {
            double best = double.MaxValue;
            which = -1;
            atStart = true;
            for (int k = 0; k < curves.Length; k++)
            {
                if (used[k])
                {
                    continue;
                }

                ReadOnlySpan<PrcVector> side = _edges.Samples(curves[k]);
                double toStart = Distance(side[0], from), toEnd = Distance(side[^1], from);
                if (toStart < best)
                {
                    best = toStart;
                    which = k;
                    atStart = true;
                }

                if (toEnd < best)
                {
                    best = toEnd;
                    which = k;
                    atStart = false;
                }
            }

            return best;
        }
        /// <summary>The loop's points in the parameters of the surface, each as near the one before as an angle can be.</summary>
        private bool MapToParameters(PrcSurface surface, Loop loop)
        {
            if (loop.Degenerate)
            {
                return true;
            }

            double maxDistance = 50 * _chord;
            double hintU = 0, hintV = 0;
            for (int i = 0; i < loop.Points.Count; i++)
            {
                if (!surface.TryInvert(loop.Points[i], hintU, hintV, maxDistance, out double u, out double v))
                {
                    return false;
                }

                PrcVector on = surface.Evaluate(u, v, out _, out _);
                if (Distance(on, loop.Points[i]) > maxDistance)
                {
                    return false;
                }

                // A closed spline is followed across its seam: the parameter nearest the point before, whichever turn the inversion gave.
                if (surface.Kind == PrcSurfaceKind.Nurbs && i > 0)
                {
                    if (surface.PeriodicU && surface.PeriodU > 0)
                    {
                        u += surface.PeriodU * Math.Round((hintU - u) / surface.PeriodU);
                    }

                    if (surface.PeriodicV && surface.PeriodV > 0)
                    {
                        v += surface.PeriodV * Math.Round((hintV - v) / surface.PeriodV);
                    }
                }

                loop.U.Add(u);
                loop.V.Add(v);
                hintU = u;
                hintV = v;
            }

            if (surface.Kind is PrcSurfaceKind.Sphere or PrcSurfaceKind.Cone)
            {
                ExpandPoles(surface, loop);
            }
            else if (surface.Kind == PrcSurfaceKind.Nurbs)
            {
                ExpandCollapsed(surface, loop);
            }

            // Back to the first point: how many turns the loop made.
            if (surface.TryInvert(loop.Points[0], hintU, hintV, maxDistance, out double closeU, out double closeV))
            {
                if (surface.PeriodicU)
                {
                    loop.WindingU = (int)Math.Round((closeU - loop.U[0]) / surface.PeriodU);
                }

                if (surface.PeriodicV)
                {
                    loop.WindingV = (int)Math.Round((closeV - loop.V[0]) / surface.PeriodV);
                }
            }

            return true;
        }

        /// <summary>
        /// A loop that passes through a pole (the apex of a cone) has no angle there: every direction is one. In the parameters the pole
        /// is the whole line of its <c>v</c>, and the loop comes to it at the angle it came by and leaves at the angle it leaves by: the
        /// point is two, one on each.
        /// </summary>
        private void ExpandPoles(PrcSurface surface, Loop loop)
        {
            int n = loop.Points.Count;
            bool IsPole(double v) => surface.Kind == PrcSurfaceKind.Sphere ? Math.Abs(Math.Abs(v) - (Math.PI / 2)) < 1e-6 : Math.Abs(v) <= 1e-3 * _chord;
            bool any = false;
            for (int i = 0; i < n && !any; i++)
            {
                any = IsPole(loop.V[i]);
            }

            if (!any)
            {
                return;
            }

            var points = new List<PrcVector>(n + 2);
            var us = new List<double>(n + 2);
            var vs = new List<double>(n + 2);
            for (int i = 0; i < n; i++)
            {
                if (IsPole(loop.V[i]))
                {
                    points.Add(loop.Points[i]);
                    us.Add(loop.U[(i + n - 1) % n]);
                    vs.Add(loop.V[i]);
                    points.Add(loop.Points[i]);
                    us.Add(loop.U[(i + 1) % n]);
                    vs.Add(loop.V[i]);
                }
                else
                {
                    points.Add(loop.Points[i]);
                    us.Add(loop.U[i]);
                    vs.Add(loop.V[i]);
                }
            }

            loop.Points.Clear();
            loop.U.Clear();
            loop.V.Clear();
            loop.Points.AddRange(points);
            loop.U.AddRange(us);
            loop.V.AddRange(vs);
        }

        /// <summary>
        /// A spline may collapse a whole row of its parameters to one point (the pole of a surface of revolution): a loop point there has
        /// no u (or no v) of its own, and the inversion gives any. Like a sphere's pole, the point is two in the parameters: at the
        /// value the loop came by and the one it leaves by.
        /// </summary>
        private void ExpandCollapsed(PrcSurface surface, Loop loop)
        {
            int n = loop.Points.Count;
            if (n < 3)
            {
                return;
            }

            // 0: an ordinary point; 1: no u of its own; 2: no v of its own.
            Span<byte> kind = n <= 1024 ? stackalloc byte[n] : new byte[n];
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                surface.Evaluate(loop.U[i], loop.V[i], out PrcVector du, out PrcVector dv);
                double lu = Math.Sqrt(du.LengthSquared()), lv = Math.Sqrt(dv.LengthSquared());
                kind[i] = (byte)(lu <= 1e-9 * lv ? 1 : lv <= 1e-9 * lu ? 2 : 0);
                any |= kind[i] != 0;
            }

            if (!any)
            {
                return;
            }

            var points = new List<PrcVector>(n + 8);
            var us = new List<double>(n + 8);
            var vs = new List<double>(n + 8);
            for (int i = 0; i < n; i++)
            {
                int before = (i + n - 1) % n, after = (i + 1) % n;
                if (kind[i] == 0 || kind[before] == kind[i] && kind[after] == kind[i])
                {
                    points.Add(loop.Points[i]);
                    us.Add(loop.U[i]);
                    vs.Add(loop.V[i]);
                    continue;
                }

                bool noU = kind[i] == 1;
                points.Add(loop.Points[i]);
                us.Add(noU ? loop.U[before] : loop.U[i]);
                vs.Add(noU ? loop.V[i] : loop.V[before]);
                points.Add(loop.Points[i]);
                us.Add(noU ? loop.U[after] : loop.U[i]);
                vs.Add(noU ? loop.V[i] : loop.V[after]);
            }

            loop.Points.Clear();
            loop.U.Clear();
            loop.V.Clear();
            loop.Points.AddRange(points);
            loop.U.AddRange(us);
            loop.V.AddRange(vs);
        }

        // ---- sides that are not stored

        /// <summary>
        /// The third and fourth side of an iso face that the file deduces, from the first two, the vertex they share and the surface: the
        /// curves of constant parameter that close the rectangle. Their points are registered, so that the faces that refer to them get
        /// the same.
        /// </summary>
        private bool ResolveIsoSides(PrcBrepFace face, PrcSurface surface, ReadOnlySpan<int> curves)
        {
            if (curves.Length != 4)
            {
                return false;
            }

            bool thirdOpen = _body.Curves[curves[2]].Kind == PrcCurveKind.Implicit && !_edges.IsResolved(curves[2]);
            bool fourthOpen = _body.Curves[curves[3]].Kind == PrcCurveKind.Implicit && !_edges.IsResolved(curves[3]);
            if (!thirdOpen && !fourthOpen)
            {
                return true;
            }

            ReadOnlySpan<PrcVector> s1 = _edges.Samples(curves[0]), s2 = _edges.Samples(curves[1]);
            // A, B, C: the first side from A to B, the second from B to C.
            bool firstForward = Math.Min(Distance(s1[^1], s2[0]), Distance(s1[^1], s2[^1])) <= Math.Min(Distance(s1[0], s2[0]), Distance(s1[0], s2[^1]));
            PrcVector a = firstForward ? s1[0] : s1[^1], b = firstForward ? s1[^1] : s1[0];
            PrcVector c = Distance(s2[0], b) <= Distance(s2[^1], b) ? s2[^1] : s2[0];

            PrcVector d;
            if (face.VertexCount > 0 && _body.FaceVertices[face.FirstVertex] >= 0)
            {
                d = _body.Vertices[_body.FaceVertices[face.FirstVertex]];
            }
            else if (!fourthOpen)
            {
                ReadOnlySpan<PrcVector> s4 = _edges.Samples(curves[3]);
                d = Distance(s4[0], a) <= Distance(s4[^1], a) ? s4[^1] : s4[0];
            }
            else if (!thirdOpen)
            {
                ReadOnlySpan<PrcVector> s3 = _edges.Samples(curves[2]);
                d = Distance(s3[0], c) <= Distance(s3[^1], c) ? s3[^1] : s3[0];
            }
            else
            {
                return false;
            }

            // The face is a rectangle in the parameters: the first side goes by (du1, dv1) from A to B, the second by (du2, dv2) from B to C,
            // the third by minus the first from C to D, the fourth by minus the second back to A. Which way round a circle goes that is
            // exactly half a turn is not in the points, but it is here (a half cylinder: the top arc runs the same half as the bottom one).
            if (!surface.TryInvert(a, 0, 0, double.PositiveInfinity, out double ua, out double va))
            {
                return false;
            }

            double ub = ua, vb = va;
            Walk(surface, s1, firstForward, ref ub, ref vb);
            double uc = ub, vc = vb;
            Walk(surface, s2, Distance(s2[0], b) <= Distance(s2[^1], b), ref uc, ref vc);
            double du1 = ub - ua, dv1 = vb - va, du2 = uc - ub, dv2 = vc - vb;
            double ud = uc - du1, vd = vc - dv1;

            if (thirdOpen)
            {
                _edges.Register(curves[2], ParameterLine(surface, c, d, uc, vc, ud, vd));
                DescribeArc(curves[2], surface, c, d, uc, vc, ud, vd);
            }

            if (fourthOpen)
            {
                _edges.Register(curves[3], ParameterLine(surface, d, a, ud, vd, ud - du2, vd - dv2));
                DescribeArc(curves[3], surface, d, a, ud, vd, ud - du2, vd - dv2);
            }

            return true;
        }

        /// <summary>
        /// A line of the parameters of a cylinder, cone, sphere or torus that turns is an arc of a circle: said to the sampler, from the
        /// surface's own point halfway, since a short one has two samples and a face built from it (a torus from its meridian) needs the circle.
        /// </summary>
        private void DescribeArc(int curve, PrcSurface surface, PrcVector from, PrcVector to, double u0, double v0, double u1, double v1)
        {
            if (surface.Kind is not (PrcSurfaceKind.Cylinder or PrcSurfaceKind.Cone or PrcSurfaceKind.Sphere or PrcSurfaceKind.Torus))
            {
                return;
            }

            // A line of one parameter, the other's change a thousandth of it on the surface (walking a side leaves a little of the
            // other), and of an angle: u on all four, v on a sphere or torus. A cylinder's or cone's line along v is straight.
            surface.Evaluate(u0, v0, out PrcVector du, out PrcVector dv);
            double alongU = Math.Abs(u1 - u0) * Math.Sqrt(du.LengthSquared()), alongV = Math.Abs(v1 - v0) * Math.Sqrt(dv.LengthSquared());
            bool turnsU = alongV <= 1e-3 * alongU;
            bool turnsV = alongU <= 1e-3 * alongV && surface.Kind is PrcSurfaceKind.Sphere or PrcSurfaceKind.Torus;
            if (!turnsU && !turnsV)
            {
                return;
            }

            PrcVector middle = turnsU ? surface.Evaluate((u0 + u1) / 2, v0, out _, out _) : surface.Evaluate(u0, (v0 + v1) / 2, out _, out _);
            if (PrcBrepCurves.TryFitArc(from, middle, to, out PrcBrepCurves.Arc arc))
            {
                _edges.Describe(curve, arc);
            }
        }

        /// <summary>Follows a side from its first point (at <paramref name="u"/>, <paramref name="v"/>) to its last, in the parameters, a step at a time (or from its last to its first).</summary>
        private static void Walk(PrcSurface surface, ReadOnlySpan<PrcVector> side, bool forward, ref double u, ref double v)
        {
            for (int i = 1; i < side.Length; i++)
            {
                if (surface.TryInvert(forward ? side[i] : side[side.Length - 1 - i], u, v, double.PositiveInfinity, out double nu, out double nv))
                {
                    u = nu;
                    v = nv;
                }
            }
        }

        /// <summary>The points of the line of constant parameter between two points of the surface: a straight line where the surface is straight, else enough points for the tolerance.</summary>
        private PrcVector[] ParameterLine(PrcSurface surface, PrcVector from, PrcVector to, double u0, double v0, double u1, double v1)
        {
            (double stepU, double stepV) = surface.Steps(_chord, Math.Min(v0, v1), Math.Max(v0, v1));
            double byU = double.IsInfinity(stepU) ? 1 : Math.Ceiling(Math.Abs(u1 - u0) / stepU);
            double byV = double.IsInfinity(stepV) ? 1 : Math.Ceiling(Math.Abs(v1 - v0) / stepV);
            double wanted = Math.Max(byU, byV);
            int segments = wanted > 1 ? (int)Math.Min(wanted, PrcBrepTolerance.MaxSamplesPerEdge) : 1;      // not a NaN's to cast
            if (wanted > PrcBrepTolerance.MaxSamplesPerEdge)
            {
                _capped = true;
            }

            var samples = new PrcVector[segments + 1];
            for (int i = 1; i < segments; i++)
            {
                double t = (double)i / segments;
                samples[i] = surface.Evaluate(u0 + ((u1 - u0) * t), v0 + ((v1 - v0) * t), out _, out _);
            }

            samples[0] = from;
            samples[segments] = to;
            return samples;
        }

        /// <summary>
        /// The four sides of an iso NURBS face: the natural boundaries of the patch, in the order the sense array gives. Sides that no
        /// face has made yet (deduced ones, boundaries, a stored circle with no ends of its own) are cut from the patch and registered.
        /// </summary>
        private void ResolveIsoNurbsSides(PrcBrepFace face, PrcSurface surface, ReadOnlySpan<int> curves)
        {
            PrcNurbs n = surface.Nurbs!;
            ReadOnlySpan<double> sense = _body.SurfaceData.AsSpan(face.SurfaceDataStart, face.SurfaceDataCount);
            // Boundaries: 0 = Umin, 1 = Vmin, 2 = Umax, 3 = Vmax; the first curve is on the one the first two numbers name, the rest follow.
            // Read off the corpus (ExploreIsoNurbsSides: sides that have vertices of their own, matched to the corners of the patch).
            int first = (sense[0] != 0 ? 1 : 0) + (sense[1] != 0 ? 2 : 0);
            int[] cycle = sense[2] == 0 ? [0, 3, 2, 1] : [0, 1, 2, 3];
            int at = Array.IndexOf(cycle, first);
            for (int k = 0; k < curves.Length && k < 4; k++)
            {
                PrcBrepCurve curve = _body.Curves[curves[k]];
                bool ownEnds = curve.Start >= 0 && curve.End >= 0;
                if (_edges.IsResolved(curves[k]) || ownEnds)
                {
                    continue;
                }

                _edges.Register(curves[k], Boundary(n, cycle[(at + k) % 4]));
            }
        }

        /// <summary>The steps of each knot span of a surface at the body's tolerance: measured once however many of its sides and faces ask.</summary>
        private (int[] U, int[] V) SpanSteps(PrcNurbs n)
        {
            if (!_steps.TryGetValue(n, out (int[] U, int[] V) steps))
            {
                steps = (new int[PrcNurbsSurface.SpanCountU(n)], new int[PrcNurbsSurface.SpanCountV(n)]);
                PrcNurbsSurface.SpanSteps(n, _chord, steps.U, steps.V);
                _steps.Add(n, steps);
            }

            return steps;
        }

        private PrcVector[] Boundary(PrcNurbs n, int location)
        {
            bool alongV = location is 0 or 2;                 // on Umin or Umax the boundary runs along v
            double[] edges = alongV ? PrcNurbsSurface.SpanEdges(n.KnotsV, n.DegreeV, n.CountV) : PrcNurbsSurface.SpanEdges(n.KnotsU, n.DegreeU, n.CountU);
            double fixedAt = location switch { 0 => PrcNurbsSurface.MinU(n), 1 => PrcNurbsSurface.MinV(n), 2 => PrcNurbsSurface.MaxU(n), _ => PrcNurbsSurface.MaxV(n) };
            (int[] stepsU, int[] stepsV) = SpanSteps(n);
            int[] steps = alongV ? stepsV : stepsU;
            var points = new List<PrcVector>();
            for (int s = 0; s + 1 < edges.Length; s++)
            {
                int count = Math.Min(steps[s], PrcBrepTolerance.MaxSamplesPerEdge);
                for (int i = (s == 0 ? 0 : 1); i <= count; i++)
                {
                    double t = edges[s] + ((edges[s + 1] - edges[s]) * i / count);
                    points.Add(alongV ? PrcNurbsSurface.Evaluate(n, fixedAt, t, out _, out _) : PrcNurbsSurface.Evaluate(n, t, fixedAt, out _, out _));
                }
            }

            return [.. points];
        }

        // ---- cutting the face

        private bool Cut(PrcBrepFace face, PrcSurface surface, List<Loop> loops)
        {
            var real = loops.Where(l => !l.Degenerate).ToList();
            if (real.Count == 0)
            {
                return CutWholeSurface(face, surface);
            }

            if (real.Any(l => l.WindingU != 0 || l.WindingV != 0))
            {
                // Two loops that each go once round the surface, in u (a cylinder wall between two circles) or in v (a torus between two
                // circles round its tube), and holes or none: a strip.
                List<Loop> round = real.FindAll(l => l.WindingU != 0 || l.WindingV != 0), holes = real.FindAll(l => l.WindingU == 0 && l.WindingV == 0);
                if (round.Count == 2 && round.All(l => Math.Abs(l.WindingU) == 1 && l.WindingV == 0) && surface.PeriodicU)
                {
                    return CutStrip(face, surface, round[0], round[1], holes, alongV: false);
                }

                if (round.Count == 2 && round.All(l => Math.Abs(l.WindingV) == 1 && l.WindingU == 0) && surface.PeriodicV)
                {
                    return CutStrip(face, surface, round[0], round[1], holes, alongV: true);
                }

                // One loop that goes once round: a cap, from the loop to the pole it encloses.
                if (real.Count == 1 && Math.Abs(real[0].WindingU) == 1 && real[0].WindingV == 0 && surface.Kind is PrcSurfaceKind.Sphere or PrcSurfaceKind.Cone)
                {
                    return CutCap(face, surface, real[0]);
                }

                return false;
            }

            return CutPolygon(face, surface, real);
        }

        /// <summary>A surface that closes on itself and has no boundary (a sphere, a torus), or a whole spline patch, in a grid.</summary>
        private bool CutWholeSurface(PrcBrepFace face, PrcSurface surface)
        {
            double[] us, vs;
            switch (surface.Kind)
            {
                case PrcSurfaceKind.Sphere:
                case PrcSurfaceKind.Torus:
                {
                    double v0 = surface.Kind == PrcSurfaceKind.Sphere ? -Math.PI / 2 : 0, v1 = surface.Kind == PrcSurfaceKind.Sphere ? Math.PI / 2 : 2 * Math.PI;
                    (double du, double dv) = surface.Steps(_chord, v0, v1);
                    int nu = Cells(2 * Math.PI / du), nv = Cells((v1 - v0) / dv);
                    LimitCells(ref nu, ref nv);
                    us = Spaced(0, 2 * Math.PI, nu);
                    vs = Spaced(v0, v1, nv);
                    break;
                }

                case PrcSurfaceKind.Nurbs:
                {
                    PrcNurbs n = surface.Nurbs!;
                    double minU = PrcNurbsSurface.MinU(n), maxU = PrcNurbsSurface.MaxU(n), minV = PrcNurbsSurface.MinV(n), maxV = PrcNurbsSurface.MaxV(n);
                    var gridU = new List<double>();
                    var gridV = new List<double>();
                    NurbsGrid(n, minU, maxU, minV, maxV, gridU, gridV);
                    us = [minU, .. gridU, maxU];
                    vs = [minV, .. gridV, maxV];
                    break;
                }

                default:
                    return false;
            }

            int w = us.Length;
            var index = new int[w * vs.Length];
            for (int j = 0; j < vs.Length; j++)
            {
                for (int i = 0; i < us.Length; i++)
                {
                    index[(j * w) + i] = AddVertex(face, surface, surface.Evaluate(us[i], vs[j], out _, out _), us[i], vs[j]);
                }
            }

            for (int j = 0; j + 1 < vs.Length; j++)
            {
                for (int i = 0; i + 1 < us.Length; i++)
                {
                    int a = index[(j * w) + i], b = index[(j * w) + i + 1], c = index[((j + 1) * w) + i + 1], d = index[((j + 1) * w) + i];
                    AddTriangle(a, b, c);
                    AddTriangle(a, c, d);
                }
            }

            return true;
        }

        private static double[] Spaced(double from, double to, int cells)
        {
            var values = new double[cells + 1];
            for (int i = 0; i <= cells; i++)
            {
                values[i] = from + ((to - from) * i / cells);
            }

            return values;
        }
        private static int Cells(double steps)
        {
            double cells = Math.Ceiling(steps);
            return cells >= 1 ? (int)Math.Min(cells, 1 << 20) : 1;      // a NaN is one cell (casting it is not the same on every runtime)
        }

        /// <summary>Keeps a grid within the cap on triangles of a face: coarser in both directions by the same factor.</summary>
        private void LimitCells(ref int nu, ref int nv)
        {
            long cells = (long)nu * nv;
            long allowed = PrcBrepTolerance.MaxTrianglesPerFace / 2;
            if (cells > allowed)
            {
                _capped = true;
                double scale = Math.Sqrt((double)allowed / cells);
                nu = Math.Max(1, (int)(nu * scale));
                nv = Math.Max(1, (int)(nv * scale));
            }
        }

        /// <summary>A repeatable value in (-0.0005, 0.0005) for the grid point (i, j).</summary>
        private static double Nudge(int i, int j, uint salt)
        {
            uint h = ((uint)i * 73856093u) ^ ((uint)j * 19349663u) ^ salt;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return ((h & 1023u) / 1024.0 - 0.5) * 1e-3;
        }

        /// <summary>
        /// A face between two loops that each make one turn round the surface in <c>u</c>: the surface is cut open along the line of constant
        /// <c>u</c> at the first loop's start, and the strip is one polygon. The first loop is one lap; the second, cut where it crosses that
        /// line, is the other lap back; the line itself, on both sides, is cut into steps for the surface. Holes are moved by whole turns
        /// into the strip. Loops that turn in <c>v</c> (<paramref name="alongV"/>) are the same with the parameters swapped.
        /// </summary>
        private bool CutStrip(PrcBrepFace face, PrcSurface surface, Loop first, Loop second, List<Loop> holes, bool alongV)
        {
            if (alongV)
            {
                first = Transposed(first);
                second = Transposed(second);
                holes = holes.ConvertAll(Transposed);
            }

            PrcVector At(double around, double across) => alongV ? surface.Evaluate(across, around, out _, out _) : surface.Evaluate(around, across, out _, out _);
            double period = alongV ? surface.PeriodV : surface.PeriodU;
            (List<PrcVector> pa, List<double> ua, List<double> va) = Lap(first, period);
            (List<PrcVector> pb, List<double> ub, List<double> vb) = Lap(second, period);
            double seam = ua[0];

            // The second lap twice over, shifted so that the seam lies inside the first of them; then from the crossing, one lap.
            int n = pb.Count - 1;
            double shift = Math.Floor((seam - ub[0]) / period) * period;
            var qp = new List<PrcVector>(2 * n);
            var qu = new List<double>(2 * n);
            var qv = new List<double>(2 * n);
            for (int lap = 0; lap < 2; lap++)
            {
                for (int i = lap == 0 ? 0 : 1; i <= n; i++)
                {
                    qp.Add(pb[i]);
                    qu.Add(ub[i] + shift + (lap * period));
                    qv.Add(vb[i]);
                }
            }

            int at = -1;
            for (int i = 0; i + 1 < qu.Count; i++)
            {
                if (qu[i] <= seam && seam < qu[i + 1])
                {
                    at = i;
                    break;
                }
            }

            if (at < 0)
            {
                return false;
            }

            double t = (seam - qu[at]) / (qu[at + 1] - qu[at]);
            PrcVector crossing = qp[at] + ((qp[at + 1] - qp[at]) * t);
            double crossingV = qv[at] + ((qv[at + 1] - qv[at]) * t);
            var lap2 = new List<(PrcVector P, double U, double V)> { (crossing, seam, crossingV) };
            for (int i = at + 1; i < qu.Count && qu[i] < seam + period; i++)
            {
                lap2.Add((qp[i], qu[i], qv[i]));
            }

            lap2.Add((crossing, seam + period, crossingV));

            // The polygon: the first lap forward, up the seam, the second lap backward, down the seam.
            var polygon = new Loop();
            for (int i = 0; i < pa.Count; i++)
            {
                Append(polygon, pa[i], ua[i], va[i]);
            }

            // The step across: along v of the surface, or along u when the loops turn in v (a step in u depends on where in v, over all of a turn).
            double step = alongV
                ? surface.Steps(_chord, ua.Min(), ua.Max()).Du
                : surface.Steps(_chord, Math.Min(va[0], crossingV), Math.Max(va[0], crossingV)).Dv;
            int across = double.IsInfinity(step) ? 1 : Cells(Math.Abs(crossingV - va[0]) / step);
            across = Math.Min(across, PrcBrepTolerance.MaxSamplesPerEdge);
            for (int k = 1; k < across; k++)
            {
                double v = va[0] + ((crossingV - va[0]) * k / across);
                Append(polygon, At(seam + period, v), seam + period, v);
            }

            for (int i = lap2.Count - 1; i >= 0; i--)
            {
                Append(polygon, lap2[i].P, lap2[i].U, lap2[i].V);
            }

            for (int k = across - 1; k >= 1; k--)
            {
                double v = va[0] + ((crossingV - va[0]) * k / across);
                Append(polygon, At(seam, v), seam, v);
            }

            var loops = new List<Loop>(1 + holes.Count) { polygon };
            foreach (Loop hole in holes)
            {
                // Into the strip, by whole turns.
                double turns = Math.Floor((hole.U.Average() - seam) / period) * period;
                var moved = new Loop();
                for (int i = 0; i < hole.Points.Count; i++)
                {
                    Append(moved, hole.Points[i], hole.U[i] - turns, hole.V[i]);
                }

                loops.Add(moved);
            }

            return CutPolygon(face, surface, alongV ? loops.ConvertAll(Transposed) : loops);
        }

        /// <summary>A loop with its parameters swapped (and its turns).</summary>
        private static Loop Transposed(Loop loop)
        {
            var t = new Loop { WindingU = loop.WindingV, WindingV = loop.WindingU, Degenerate = loop.Degenerate };
            t.Points.AddRange(loop.Points);
            t.U.AddRange(loop.V);
            t.V.AddRange(loop.U);
            return t;
        }

        /// <summary>
        /// A face bounded by one loop that goes once round a sphere or a cone: the loop and the pole (the apex of the cone) on the side
        /// the face is. The polygon is the lap, the line of constant <c>u</c> up to the pole, the pole's row of points (the
        /// same point in space, a row in the parameters) back, and the line down.
        /// </summary>
        private bool CutCap(PrcBrepFace face, PrcSurface surface, Loop loop)
        {
            double period = surface.PeriodU;
            Loop path = loop;
            double poleV;
            if (surface.Kind == PrcSurfaceKind.Cone)
            {
                poleV = 0;
                // The loop goes out to the apex and back along the same line: the apex is no part of the lap.
                path = new Loop { WindingU = loop.WindingU };
                double scale = 1e-6 * Math.Max(1, loop.V.Max(Math.Abs));
                for (int i = 0; i < loop.Points.Count; i++)
                {
                    if (Math.Abs(loop.V[i] - poleV) > scale)
                    {
                        Append(path, loop.Points[i], loop.U[i], loop.V[i]);
                    }
                }

                if (path.Points.Count < 3)
                {
                    return false;
                }
            }
            else
            {
                // Which side of the loop the face is on is not in the loop's direction (the file keeps no sense for a curve): a cap is
                // taken to be the smaller part, round the pole the loop lies nearer to; a loop on the equator goes by its direction.
                double mean = loop.V.Average();
                bool north = Math.Abs(mean) > 0.1 ? mean > 0 : (loop.WindingU > 0) == face.OrientationWithShell;
                poleV = north ? Math.PI / 2 : -Math.PI / 2;
            }

            (List<PrcVector> pa, List<double> ua, List<double> va) = Lap(path, period);
            var polygon = new Loop();
            for (int i = 0; i < pa.Count; i++)
            {
                Append(polygon, pa[i], ua[i], va[i]);
            }

            (double stepU, double stepV) = surface.Steps(_chord, Math.Min(va[^1], poleV), Math.Max(va[^1], poleV));
            _ = stepU;
            int across = double.IsInfinity(stepV) ? 1 : Cells(Math.Abs(poleV - va[^1]) / stepV);
            across = Math.Min(across, PrcBrepTolerance.MaxSamplesPerEdge);
            for (int k = 1; k < across; k++)
            {
                double v = va[^1] + ((poleV - va[^1]) * k / across);
                Append(polygon, surface.Evaluate(ua[^1], v, out _, out _), ua[^1], v);
            }

            for (int i = pa.Count - 1; i >= 0; i--)
            {
                Append(polygon, surface.Evaluate(ua[i], poleV, out _, out _), ua[i], poleV);
            }

            for (int k = across - 1; k >= 1; k--)
            {
                double v = va[0] + ((poleV - va[0]) * k / across);
                Append(polygon, surface.Evaluate(ua[0], v, out _, out _), ua[0], v);
            }

            return CutPolygon(face, surface, [polygon]);
        }

        private static void Append(Loop loop, PrcVector point, double u, double v)
        {
            loop.Points.Add(point);
            loop.U.Add(u);
            loop.V.Add(v);
        }

        /// <summary>One lap of a loop that winds once in <c>u</c>, running with <c>u</c> growing, with its first point again at the end, a period on.</summary>
        private static (List<PrcVector> Points, List<double> U, List<double> V) Lap(Loop loop, double period)
        {
            var points = new List<PrcVector>(loop.Points.Count + 1);
            var us = new List<double>(loop.Points.Count + 1);
            var vs = new List<double>(loop.Points.Count + 1);
            bool backward = loop.WindingU < 0;
            for (int k = 0; k < loop.Points.Count; k++)
            {
                int i = backward ? loop.Points.Count - 1 - k : k;
                points.Add(loop.Points[i]);
                us.Add(loop.U[i]);
                vs.Add(loop.V[i]);
            }

            // Closing: the first point once more, one period on (the loop's own first, or the reversed loop's, is where the lap began).
            points.Add(points[0]);
            us.Add(us[0] + period);
            vs.Add(vs[0]);
            return (points, us, vs);
        }

        /// <summary>Cuts out the slivers of the loops that cross themselves; false when there is none, or one is too big to be a sliver.</summary>
        private static bool Untangle(List<Loop> loops)
        {
            bool changed = false;
            foreach (Loop l in loops)
            {
                if (l.Degenerate || !PrcLoopRepair.HasCrossing(l.U, l.V))
                {
                    continue;
                }

                if (!PrcLoopRepair.TryUntangle(l.U, l.V, l.Points, 0.10))
                {
                    return false;
                }

                changed = true;
            }

            return changed;
        }

        private bool CutPolygon(PrcBrepFace face, PrcSurface surface, List<Loop> loops, bool untangled = false)
        {
            // The grid inside: lines of the parameters at the steps the surface needs, within the box of the loops.
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (Loop l in loops)
            {
                for (int i = 0; i < l.U.Count; i++)
                {
                    minU = Math.Min(minU, l.U[i]);
                    maxU = Math.Max(maxU, l.U[i]);
                    minV = Math.Min(minV, l.V[i]);
                    maxV = Math.Max(maxV, l.V[i]);
                }
            }

            // The parameters in a common scale: a unit of u and a unit of v are about the same length on the surface.
            PrcVector mid = surface.Evaluate((minU + maxU) / 2, (minV + maxV) / 2, out PrcVector du0, out PrcVector dv0);
            _ = mid;
            double scaleU = Math.Max(Math.Sqrt(du0.LengthSquared()), 1e-12), scaleV = Math.Max(Math.Sqrt(dv0.LengthSquared()), 1e-12);
            if (surface.Kind == PrcSurfaceKind.Sphere || surface.Kind == PrcSurfaceKind.Torus || surface.Kind == PrcSurfaceKind.Cylinder || surface.Kind == PrcSurfaceKind.Cone)
            {
                // An angle's length grows with the radius: take it where it is greatest in the loop's box (its ends, its middle, and the equator
                // of a sphere, if the box crosses it: at a pole, where the loop may end, a turn has no length at all).
                scaleU = 0;
                foreach (double v in new[] { minV, maxV, (minV + maxV) / 2, Math.Clamp(0, minV, maxV) })
                {
                    surface.Evaluate((minU + maxU) / 2, v, out PrcVector a, out _);
                    scaleU = Math.Max(scaleU, Math.Sqrt(a.LengthSquared()));
                }

                scaleU = Math.Max(scaleU, 1e-12);
            }

            double width = maxU - minU, height = maxV - minV;
            var gridU = new List<double>();
            var gridV = new List<double>();
            if (surface.Kind == PrcSurfaceKind.Nurbs)
            {
                NurbsGrid(surface.Nurbs!, minU, maxU, minV, maxV, gridU, gridV);
            }
            else
            {
                (double stepU, double stepV) = surface.Steps(_chord, minV, maxV);
                int nu = double.IsInfinity(stepU) ? 1 : Cells(width / stepU), nv = double.IsInfinity(stepV) ? 1 : Cells(height / stepV);
                LimitCells(ref nu, ref nv);
                for (int i = 1; i < nu; i++)
                {
                    gridU.Add(minU + (width * i / nu));
                }

                for (int j = 1; j < nv; j++)
                {
                    gridV.Add(minV + (height * j / nv));
                }
            }

            // The points of the loops first (the triangulator takes them as the polygon), then the grid points inside.
            var xy = new List<double>();
            var loopStarts = new List<int>();
            var vertexOfPoint = new List<int>();
            foreach (Loop l in loops)
            {
                loopStarts.Add(xy.Count / 2);
                for (int i = 0; i < l.U.Count; i++)
                {
                    xy.Add(l.U[i] * scaleU);
                    xy.Add(l.V[i] * scaleV);
                }
            }

            int boundaryPoints = xy.Count / 2;
            loopStarts.Add(boundaryPoints);
            double cellU = gridU.Count > 0 ? width / (gridU.Count + 1) : width, cellV = gridV.Count > 0 ? height / (gridV.Count + 1) : height;
            double margin = 0.3 * Math.Min(cellU * scaleU, cellV * scaleV);
            var interior = new List<(double U, double V)>();
            for (int i = 0; i < gridU.Count; i++)
            {
                for (int j = 0; j < gridV.Count; j++)
                {
                    // A grid is full of points on one circle, which the triangulator can only tell apart with exact arithmetic (slow): a
                    // nudge of a thousandth of a cell, the same every time, makes every circle test plain.
                    double gu = gridU[i] + (cellU * Nudge(i, j, 0x9E3779B1u)), gv = gridV[j] + (cellV * Nudge(i, j, 0x85EBCA6Bu));
                    if (InsideWithMargin(loops, gu * scaleU, gv * scaleV, scaleU, scaleV, margin))
                    {
                        interior.Add((gu, gv));
                        xy.Add(gu * scaleU);
                        xy.Add(gv * scaleV);
                    }
                }
            }

            var triangles = new List<int>();
            int pointCount = xy.Count / 2;
            if (!_triangulator.Triangulate([.. xy], [.. loopStarts], loops.Count, pointCount, (int)Math.Min(400L * pointCount + 100_000, int.MaxValue), triangles))
            {
                // A loop that folds over itself by a sliver (an edge just off its patch): the sliver cut out, once.
                return !untangled && Untangle(loops) && CutPolygon(face, surface, loops, untangled: true);
            }

            // The corners: the points of the loops are the points of the edges themselves.
            var vertex = new int[pointCount];
            Array.Fill(vertex, -1);
            int Vertex(int index)
            {
                if (vertex[index] >= 0)
                {
                    return vertex[index];
                }

                int at = 0;
                foreach (Loop l in loops)
                {
                    if (index < at + l.Points.Count)
                    {
                        int i = index - at;
                        return vertex[index] = AddVertex(face, surface, l.Points[i], l.U[i], l.V[i]);
                    }

                    at += l.Points.Count;
                }

                (double u, double v) = interior[index - boundaryPoints];
                return vertex[index] = AddVertex(face, surface, surface.Evaluate(u, v, out _, out _), u, v);
            }

            for (int t = 0; t < triangles.Count; t += 3)
            {
                AddTriangle(Vertex(triangles[t]), Vertex(triangles[t + 1]), Vertex(triangles[t + 2]));
            }

            return true;
        }

        private void NurbsGrid(PrcNurbs n, double minU, double maxU, double minV, double maxV, List<double> gridU, List<double> gridV)
        {
            double[] edgesU = PrcNurbsSurface.SpanEdges(n.KnotsU, n.DegreeU, n.CountU), edgesV = PrcNurbsSurface.SpanEdges(n.KnotsV, n.DegreeV, n.CountV);
            (int[] stepsU, int[] stepsV) = SpanSteps(n);
            Lines(edgesU, stepsU, minU, maxU, gridU);
            Lines(edgesV, stepsV, minV, maxV, gridV);
            long cells = (long)(gridU.Count + 1) * (gridV.Count + 1);
            if (cells > PrcBrepTolerance.MaxTrianglesPerFace / 2)
            {
                // Every other line, until it fits.
                _capped = true;
                while ((long)(gridU.Count + 1) * (gridV.Count + 1) > PrcBrepTolerance.MaxTrianglesPerFace / 2 && (gridU.Count > 0 || gridV.Count > 0))
                {
                    Thin(gridU);
                    Thin(gridV);
                }
            }
        }

        /// <summary>The lines of a parameter inside (min, max): the spans' edges, each span cut into its steps.</summary>
        private static void Lines(double[] edges, int[] steps, double min, double max, List<double> lines)
        {
            for (int s = 0; s + 1 < edges.Length && s < steps.Length; s++)
            {
                for (int i = 0; i < steps[s]; i++)
                {
                    double t = edges[s] + ((edges[s + 1] - edges[s]) * i / steps[s]);
                    if (t > min + (1e-9 * (max - min)) && t < max - (1e-9 * (max - min)))
                    {
                        lines.Add(t);
                    }
                }
            }
        }

        /// <summary>Whether the point is inside the loops (an odd number of them) and at least <paramref name="margin"/> from their segments.</summary>
        private static bool InsideWithMargin(List<Loop> loops, double x, double y, double scaleU, double scaleV, double margin)
        {
            bool inside = false;
            double margin2 = margin * margin;
            foreach (Loop l in loops)
            {
                int n = l.U.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = l.U[i] * scaleU, yi = l.V[i] * scaleV, xj = l.U[j] * scaleU, yj = l.V[j] * scaleV;
                    if ((yi > y) != (yj > y) && x < (((xj - xi) * (y - yi)) / (yj - yi)) + xi)
                    {
                        inside = !inside;
                    }

                    // Distance from the segment.
                    double dx = xj - xi, dy = yj - yi, len2 = (dx * dx) + (dy * dy);
                    double t = len2 > 0 ? Math.Clamp((((x - xi) * dx) + ((y - yi) * dy)) / len2, 0, 1) : 0;
                    double px = xi + (t * dx) - x, py = yi + (t * dy) - y;
                    if ((px * px) + (py * py) < margin2)
                    {
                        return false;
                    }
                }
            }

            return inside;
        }

        // ---- the mesh

        /// <summary>Adds a corner point and its normal (the surface's, turned as the face says), returns its number.</summary>
        private int AddVertex(PrcBrepFace face, PrcSurface surface, PrcVector position, double u, double v)
        {
            PrcVector normal = surface.Normal(u, v);
            if (!face.OrientationWithShell)
            {
                normal = -normal;
            }

            int index = _points.Count / 3;
            _points.Add(position.X);
            _points.Add(position.Y);
            _points.Add(position.Z);
            _normals.Add(normal.X);
            _normals.Add(normal.Y);
            _normals.Add(normal.Z);
            return index;
        }

        /// <summary>Adds a triangle turned so that it runs with its normals.</summary>
        private void AddTriangle(int a, int b, int c)
        {
            if (a == b || b == c || a == c)
            {
                return;
            }

            PrcVector pa = Point(a), pb = Point(b), pc = Point(c);
            PrcVector geometric = (pb - pa).Cross(pc - pa);
            PrcVector normal = Normal(a) + Normal(b) + Normal(c);
            if (geometric.Dot(normal) < 0)
            {
                (b, c) = (c, b);
            }

            _corners.Add(a);
            _corners.Add(b);
            _corners.Add(c);
        }

        private PrcVector Point(int i) => new(_points[3 * i], _points[(3 * i) + 1], _points[(3 * i) + 2]);

        private PrcVector Normal(int i) => new(_normals[3 * i], _normals[(3 * i) + 1], _normals[(3 * i) + 2]);
    }
}