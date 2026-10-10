namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// B-spline surfaces (<see cref="PrcNurbs"/>): evaluation with the first derivatives by de Boor's recursion in the form of Piegl and
/// Tiller, "The NURBS Book" (A2.1, A2.2, A2.3), rational surfaces by the quotient rule; the point of a surface nearest to a point;
/// and how many steps each knot span has to be cut into.
/// </summary>
internal static class PrcNurbsSurface
{
    public static double MinU(PrcNurbs n) => n.KnotsU[n.DegreeU];

    public static double MaxU(PrcNurbs n) => n.KnotsU[n.CountU];

    public static double MinV(PrcNurbs n) => n.KnotsV[n.DegreeV];

    public static double MaxV(PrcNurbs n) => n.KnotsV[n.CountV];

    /// <summary>The index of the knot span (with a width) that holds the parameter.</summary>
    private static int FindSpan(int highest, int degree, double u, double[] knots)
    {
        // At an end, the last (first) span that has a width: writers repeat an end knot once more than a clamped end needs, which
        // leaves a span [1, 1) there.
        if (u >= knots[highest + 1])
        {
            int last = highest;
            while (last > degree && !(knots[last] < knots[last + 1]))
            {
                last--;
            }

            return last;
        }

        if (u <= knots[degree])
        {
            int first = degree;
            while (first < highest && !(knots[first] < knots[first + 1]))
            {
                first++;
            }

            return first;
        }

        int low = degree, high = highest + 1, mid = (low + high) / 2;
        while (u < knots[mid] || u >= knots[mid + 1])
        {
            if (u < knots[mid])
            {
                high = mid;
            }
            else
            {
                low = mid;
            }

            mid = (low + high) / 2;
        }

        return mid;
    }

    /// <summary>The <c>degree + 1</c> basis functions that are not zero at <paramref name="u"/> and their first derivatives.</summary>
    private static void Basis(int span, double u, int degree, double[] knots, Span<double> basis, Span<double> derivative)
    {
        int w = degree + 1;
        Span<double> ndu = stackalloc double[w * w];
        Span<double> left = stackalloc double[w];
        Span<double> right = stackalloc double[w];
        ndu[0] = 1;
        for (int j = 1; j <= degree; j++)
        {
            left[j] = u - knots[span + 1 - j];
            right[j] = knots[span + j] - u;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                ndu[(j * w) + r] = right[r + 1] + left[j - r];
                double temp = ndu[(r * w) + j - 1] / ndu[(j * w) + r];
                ndu[(r * w) + j] = saved + (right[r + 1] * temp);
                saved = left[j - r] * temp;
            }

            ndu[(j * w) + j] = saved;
        }

        for (int j = 0; j <= degree; j++)
        {
            basis[j] = ndu[(j * w) + degree];
        }

        for (int r = 0; r <= degree; r++)
        {
            double d = 0;
            if (degree > 0)
            {
                if (r >= 1)
                {
                    d += ndu[((r - 1) * w) + degree - 1] / ndu[(degree * w) + r - 1];
                }

                if (r <= degree - 1)
                {
                    d -= ndu[(r * w) + degree - 1] / ndu[(degree * w) + r];
                }

                d *= degree;
            }

            derivative[r] = d;
        }
    }

    /// <summary>
    /// How far beyond its domain an open direction of a patch is followed, as a part of the domain, and never further than its end
    /// span is wide (an end piece followed further may turn back): writers trim faces with edges that run a little past the end of
    /// the patch, on the surface its end pieces go on to make (its natural extension).
    /// </summary>
    internal const double Extension = 0.25;

    /// <summary>The lowest parameter of a direction followed: the domain's start, less the extension when the direction is open.</summary>
    private static double Low(double[] knots, int degree, int count, bool closed)
    {
        double min = knots[degree], max = knots[count];
        if (closed)
        {
            return min;
        }

        int first = degree;
        while (first < count - 1 && !(knots[first] < knots[first + 1]))
        {
            first++;
        }

        return min - Math.Min(Extension * (max - min), knots[first + 1] - knots[first]);
    }

    /// <summary>The highest parameter of a direction followed: the domain's end, and the extension when the direction is open.</summary>
    private static double High(double[] knots, int degree, int count, bool closed)
    {
        double min = knots[degree], max = knots[count];
        if (closed)
        {
            return max;
        }

        int last = count - 1;
        while (last > degree && !(knots[last] < knots[last + 1]))
        {
            last--;
        }

        return max + Math.Min(Extension * (max - min), knots[last + 1] - knots[last]);
    }

    /// <summary>The parameter taken into the domain: wrapped when the surface is closed in that direction, else clamped to the domain and its extension.</summary>
    private static double Fold(double t, double[] knots, int degree, int count, bool closed)
    {
        double min = knots[degree], max = knots[count];
        if (closed && max > min)
        {
            double range = max - min;
            t = min + ((t - min) % range);
            if (t < min)
            {
                t += range;
            }

            return t;
        }

        return Math.Clamp(t, Low(knots, degree, count, closed), High(knots, degree, count, closed));
    }

    /// <summary>The point of the surface at a parameter and its derivatives in <c>u</c> and <c>v</c>.</summary>
        public static PrcVector Evaluate(PrcNurbs n, double u, double v, out PrcVector du, out PrcVector dv)
    {
        u = Fold(u, n.KnotsU, n.DegreeU, n.CountU, n.ClosedU);
        v = Fold(v, n.KnotsV, n.DegreeV, n.CountV, n.ClosedV);
        int pu = n.DegreeU, pv = n.DegreeV;
        int su = FindSpan(n.CountU - 1, pu, u, n.KnotsU), sv = FindSpan(n.CountV - 1, pv, v, n.KnotsV);
        Span<double> nu = stackalloc double[pu + 1], dnu = stackalloc double[pu + 1], nv = stackalloc double[pv + 1], dnv = stackalloc double[pv + 1];
        Basis(su, u, pu, n.KnotsU, nu, dnu);
        Basis(sv, v, pv, n.KnotsV, nv, dnv);

        PrcVector a = default, au = default, av = default;
        double w = 0, wu = 0, wv = 0;
        for (int k = 0; k <= pu; k++)
        {
            for (int l = 0; l <= pv; l++)
            {
                int index = ((su - pu + k) * n.CountV) + (sv - pv + l);
                PrcVector p = n.ControlPoints[index];
                double weight = n.Weights is null ? 1 : n.Weights[index];
                double b = nu[k] * nv[l] * weight, bu = dnu[k] * nv[l] * weight, bv = nu[k] * dnv[l] * weight;
                a += p * b;
                au += p * bu;
                av += p * bv;
                w += b;
                wu += bu;
                wv += bv;
            }
        }

        PrcVector s = a / w;
        du = (au - (s * wu)) / w;
        dv = (av - (s * wv)) / w;
        return s;
    }

    /// <summary>
    /// The parameters of the point of the surface nearest to a point, by Newton's method on the squared distance (with the second
    /// derivatives left out: the point is on the surface or near it) from the hint, or from the nearest of a grid of points if the hint
    /// does not lead to it. Parameters of a closed direction are returned as near the hint as they can be, even beyond the domain.
    /// False when the nearest point is further than <paramref name="maxDistance"/>.
    /// </summary>
    public static bool TryInvert(PrcNurbs n, PrcVector p, double hintU, double hintV, double maxDistance, out double u, out double v)
    {
        // A point on the surface (to within a fiftieth of what is allowed) is found from the hint; a point that is merely near is
        // looked for again from the grid, since Newton's method may have settled on a local minimum far along the surface.
        bool near = Solve(n, p, hintU, hintV, out u, out v, out double distance);
        if (near && distance <= maxDistance / 500)
        {
            return true;
        }

        double hintedU = u, hintedV = v, hintedDistance = near ? distance : double.MaxValue;
        // From the nearest of a grid of points (the same points for every point asked of this surface).
        double minU = MinU(n), maxU = MaxU(n), minV = MinV(n), maxV = MaxV(n);
        PrcVector[] grid = Grids.GetValue(n, static surface => SampleGrid(surface));
        double best = double.MaxValue, bestU = minU, bestV = minV;
        for (int i = 0; i <= Grid; i++)
        {
            for (int j = 0; j <= Grid; j++)
            {
                double d = (grid[(i * (Grid + 1)) + j] - p).LengthSquared();
                if (d < best)
                {
                    best = d;
                    bestU = minU + ((maxU - minU) * i / Grid);
                    bestV = minV + ((maxV - minV) * j / Grid);
                }
            }
        }

        bool ok = Solve(n, p, bestU, bestV, out u, out v, out distance) && distance <= maxDistance;
        if (!ok || distance > hintedDistance)
        {
            // The hint's answer was as good: take it, if it is within what is allowed.
            if (hintedDistance <= maxDistance)
            {
                u = hintedU;
                v = hintedV;
                return true;
            }
        }

        if (ok)
        {
            ToHintsSide(n, hintU, hintV, ref u, ref v);
            return true;
        }

        // Too far from the patch: on its extension, if the point is (an edge trimmed a little past the end of the patch), from the
        // nearest point found on its border. Only then: a point the patch itself has keeps the patch's parameters.
        if (!(n.ClosedU && n.ClosedV))
        {
            double fromU = hintedDistance < distance ? hintedU : u, fromV = hintedDistance < distance ? hintedV : v;
            if (Solve(n, p, fromU, fromV, out double beyondU, out double beyondV, out double beyond, extend: true) && beyond <= maxDistance)
            {
                u = beyondU;
                v = beyondV;
                ToHintsSide(n, hintU, hintV, ref u, ref v);
                return true;
            }
        }

        return false;
    }

    /// <summary>Back to the hint's side of a seam: a closed direction's parameter moved by whole turns as near the hint as it goes.</summary>
    private static void ToHintsSide(PrcNurbs n, double hintU, double hintV, ref double u, ref double v)
    {
        if (n.ClosedU && MaxU(n) > MinU(n))
        {
            double range = MaxU(n) - MinU(n);
            u += range * Math.Round((hintU - u) / range);
        }

        if (n.ClosedV && MaxV(n) > MinV(n))
        {
            double range = MaxV(n) - MinV(n);
            v += range * Math.Round((hintV - v) / range);
        }
    }

    private const int Grid = 12;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PrcNurbs, PrcVector[]> Grids = new();

    /// <summary>The surface at a grid of <c>(Grid + 1)²</c> parameters, <c>u</c> major.</summary>
    private static PrcVector[] SampleGrid(PrcNurbs n)
    {
        double minU = MinU(n), maxU = MaxU(n), minV = MinV(n), maxV = MaxV(n);
        var points = new PrcVector[(Grid + 1) * (Grid + 1)];
        for (int i = 0; i <= Grid; i++)
        {
            for (int j = 0; j <= Grid; j++)
            {
                points[(i * (Grid + 1)) + j] = Evaluate(n, minU + ((maxU - minU) * i / Grid), minV + ((maxV - minV) * j / Grid), out _, out _);
            }
        }

        return points;
    }

    private static bool Solve(PrcNurbs n, PrcVector p, double u, double v, out double resultU, out double resultV, out double distance, bool extend = false)
    {
        double rangeU = MaxU(n) - MinU(n), rangeV = MaxV(n) - MinV(n);
        // Held within the domain, or the domain and its extension.
        double minU = extend ? Low(n.KnotsU, n.DegreeU, n.CountU, n.ClosedU) : MinU(n), maxU = extend ? High(n.KnotsU, n.DegreeU, n.CountU, n.ClosedU) : MaxU(n);
        double minV = extend ? Low(n.KnotsV, n.DegreeV, n.CountV, n.ClosedV) : MinV(n), maxV = extend ? High(n.KnotsV, n.DegreeV, n.CountV, n.ClosedV) : MaxV(n);
        distance = double.MaxValue;
        resultU = u;
        resultV = v;
        for (int iteration = 0; iteration < 40; iteration++)
        {
            PrcVector s = Evaluate(n, u, v, out PrcVector su, out PrcVector sv);
            PrcVector r = s - p;
            distance = Math.Sqrt(r.LengthSquared());
            double fu = su.Dot(r), fv = sv.Dot(r);
            double a = su.Dot(su), b = su.Dot(sv), c = sv.Dot(sv);
            double det = (a * c) - (b * b);
            double stepU, stepV;
            if (Math.Abs(det) > 1e-18 * (a * c + 1e-300))
            {
                stepU = -((c * fu) - (b * fv)) / det;
                stepV = -((a * fv) - (b * fu)) / det;
            }
            else
            {
                stepU = a > 0 ? -fu / a : 0;
                stepV = c > 0 ? -fv / c : 0;
            }

            double nextU = u + stepU, nextV = v + stepV;
            bool outU = !n.ClosedU && (nextU < minU || nextU > maxU), outV = !n.ClosedV && (nextV < minV || nextV > maxV);
            if (outU && !outV && c > 0)
            {
                // Held at the edge in u, the step in v is the one-dimensional one (the full step took v where the edge was not).
                nextU = Math.Clamp(nextU, minU, maxU);
                nextV = v - (fv / c);
            }
            else if (outV && !outU && a > 0)
            {
                nextV = Math.Clamp(nextV, minV, maxV);
                nextU = u - (fu / a);
            }

            if (!n.ClosedU)
            {
                nextU = Math.Clamp(nextU, minU, maxU);
            }

            if (!n.ClosedV)
            {
                nextV = Math.Clamp(nextV, minV, maxV);
            }

            if (double.IsNaN(nextU + nextV))
            {
                return false;
            }

            bool done = Math.Abs(nextU - u) <= 1e-13 * Math.Max(rangeU, 1e-12) && Math.Abs(nextV - v) <= 1e-13 * Math.Max(rangeV, 1e-12);
            u = nextU;
            v = nextV;
            resultU = u;
            resultV = v;
            if (done)
            {
                break;
            }
        }

        distance = Math.Sqrt((Evaluate(n, u, v, out _, out _) - p).LengthSquared());
        return true;
    }

    /// <summary>The number of knot spans (intervals with a width) inside the domain in <c>u</c>.</summary>
    public static int SpanCountU(PrcNurbs n) => SpanCount(n.KnotsU, n.DegreeU, n.CountU);

    public static int SpanCountV(PrcNurbs n) => SpanCount(n.KnotsV, n.DegreeV, n.CountV);

    private static int SpanCount(double[] knots, int degree, int count)
    {
        int spans = 0;
        for (int k = degree; k < count; k++)
        {
            if (knots[k + 1] > knots[k])
            {
                spans++;
            }
        }

        return spans;
    }

    /// <summary>The start parameter of each span of a direction, then the end of the last.</summary>
    public static double[] SpanEdges(double[] knots, int degree, int count)
    {
        var edges = new List<double>();
        for (int k = degree; k < count; k++)
        {
            if (knots[k + 1] > knots[k])
            {
                if (edges.Count == 0)
                {
                    edges.Add(knots[k]);
                }

                edges.Add(knots[k + 1]);
            }
        }

        return [.. edges];
    }

    /// <summary>
    /// The number of steps to cut each knot span into, in <c>u</c> and in <c>v</c>, so that chords are within
    /// <paramref name="chord"/> of the surface and the direction of the surface turns no more than the angle cap from one step to the
    /// next. It is measured: the surface is sampled across each span along lines of the other parameter at every knot and between
    /// them, and the sagitta and the turn of the largest are scaled to the tolerance.
    /// </summary>
    public static void SpanSteps(PrcNurbs n, double chord, Span<int> stepsU, Span<int> stepsV)
    {
        double[] edgesU = SpanEdges(n.KnotsU, n.DegreeU, n.CountU), edgesV = SpanEdges(n.KnotsV, n.DegreeV, n.CountV);
        Directional(n, chord, edgesU, edgesV, alongU: true, stepsU);
        Directional(n, chord, edgesV, edgesU, alongU: false, stepsV);
    }

    private static void Directional(PrcNurbs n, double chord, double[] edges, double[] across, bool alongU, Span<int> steps)
    {
        // The other parameter: at each knot and in the middle of each span.
        var probes = new List<double>();
        for (int i = 0; i < across.Length; i++)
        {
            probes.Add(across[i]);
            if (i + 1 < across.Length)
            {
                probes.Add((across[i] + across[i + 1]) / 2);
            }
        }

        for (int span = 0; span + 1 < edges.Length && span < steps.Length; span++)
        {
            double a = edges[span], b = edges[span + 1];
            double worstSag = 0, worstTurn = 0;
            foreach (double probe in probes)
            {
                PrcVector p0 = Point(n, a, probe, alongU, out PrcVector t0);
                PrcVector p1 = Point(n, (a + b) / 2, probe, alongU, out PrcVector tm);
                PrcVector p2 = Point(n, b, probe, alongU, out PrcVector t1);
                worstSag = Math.Max(worstSag, Math.Sqrt((p1 - ((p0 + p2) / 2)).LengthSquared()));
                worstTurn = Math.Max(worstTurn, Math.Max(Angle(t0, tm), Angle(tm, t1)) * 2);
                worstTurn = Math.Max(worstTurn, Angle(t0, t1));
            }

            // A sagitta of s over the whole span: a part of it, f, has f² s; it is to be within the tolerance, with a margin for the
            // parameterisation not being even.
            int bySag = (int)Math.Ceiling(1.25 * Math.Sqrt(worstSag / Math.Max(chord, 1e-300)));
            int byTurn = (int)Math.Ceiling(worstTurn / PrcBrepTolerance.MaxTurnRadians);
            steps[span] = Math.Clamp(Math.Max(Math.Max(bySag, byTurn), 1), 1, PrcBrepTolerance.MaxSamplesPerEdge);
        }
    }

    private static PrcVector Point(PrcNurbs n, double along, double across, bool alongU, out PrcVector tangent)
    {
        PrcVector p = alongU ? Evaluate(n, along, across, out tangent, out _) : Evaluate(n, across, along, out _, out tangent);
        return p;
    }

    private static double Angle(PrcVector a, PrcVector b)
    {
        double la = Math.Sqrt(a.LengthSquared()), lb = Math.Sqrt(b.LengthSquared());
        if (!(la > 1e-300) || !(lb > 1e-300))
        {
            return 0;
        }

        return Math.Acos(Math.Clamp(a.Dot(b) / (la * lb), -1, 1));
    }
}
