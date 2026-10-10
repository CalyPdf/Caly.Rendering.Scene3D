namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// A B-spline curve of an uncompressed body (<c>PRC_TYPE_CRV_NURBS</c>, draft 7.10.5): its degree, its knots (each as often as
/// its multiplicity), its control points and, when rational, their weights. Evaluated by de Boor's algorithm on the homogeneous
/// points.
/// </summary>
internal sealed class PrcNurbsCurve(int degree, double[] knots, PrcVector[] points, double[]? weights) : IPrcParametricCurve
{
    public int Degree { get; } = degree;

    public double[] Knots { get; } = knots;

    public PrcVector[] Points { get; } = points;

    public double[]? Weights { get; } = weights;

    /// <summary>The parameters the curve is defined on: from the knot at the degree to the one at the number of points.</summary>
    public double Min => Knots[Degree];

    public double Max => Knots[Points.Length];

    /// <summary>Whether the numbers make a curve: a degree of 1 or more, knots enough and in order, weights that are positive and finite.</summary>
    public bool IsValid()
    {
        if (Degree < 1 || Points.Length < Degree + 1 || Knots.Length != Points.Length + Degree + 1 || (Weights is not null && Weights.Length != Points.Length))
        {
            return false;
        }

        for (int i = 1; i < Knots.Length; i++)
        {
            if (!(Knots[i] >= Knots[i - 1]))
            {
                return false;
            }
        }

        if (!(Max > Min))
        {
            return false;
        }

        if (Weights is not null)
        {
            foreach (double w in Weights)
            {
                if (!(w > 0) || !double.IsFinite(w))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The point at a parameter (taken into the curve's interval).</summary>
    public PrcVector Evaluate(double t)
    {
        int p = Degree;
        t = Math.Clamp(t, Min, Max);
        int span = p;
        while (span < Points.Length - 1 && t >= Knots[span + 1])
        {
            span++;
        }

        Span<double> x = stackalloc double[p + 1], y = stackalloc double[p + 1], z = stackalloc double[p + 1], w = stackalloc double[p + 1];
        for (int j = 0; j <= p; j++)
        {
            PrcVector c = Points[span - p + j];
            double weight = Weights is null ? 1 : Weights[span - p + j];
            x[j] = c.X * weight;
            y[j] = c.Y * weight;
            z[j] = c.Z * weight;
            w[j] = weight;
        }

        for (int r = 1; r <= p; r++)
        {
            for (int j = p; j >= r; j--)
            {
                double left = Knots[span - p + j], right = Knots[span + 1 + j - r];
                double alpha = right > left ? (t - left) / (right - left) : 0;
                x[j] = ((1 - alpha) * x[j - 1]) + (alpha * x[j]);
                y[j] = ((1 - alpha) * y[j - 1]) + (alpha * y[j]);
                z[j] = ((1 - alpha) * z[j - 1]) + (alpha * z[j]);
                w[j] = ((1 - alpha) * w[j - 1]) + (alpha * w[j]);
            }
        }

        return new PrcVector(x[p] / w[p], y[p] / w[p], z[p] / w[p]);
    }
}
