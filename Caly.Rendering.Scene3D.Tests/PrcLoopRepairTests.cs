using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Brep;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>Loops in the parameters that cross themselves by a sliver (points of an edge inverted just off a patch): the sliver is cut out.</summary>
public sealed class PrcLoopRepairTests
{
    private static (List<double> U, List<double> V, List<PrcVector> P) Loop(params (double U, double V)[] corners) =>
        ([.. corners.Select(c => c.U)], [.. corners.Select(c => c.V)], [.. corners.Select(c => new PrcVector(c.U, c.V, 0))]);

    [Fact]
    public void AFoldAtACornerIsCutOut()
    {
        // The unit square whose top edge, coming back from (1, 1), goes out above it and back under it: 4 -> 5 crosses 2 -> 3.
        var (u, v, p) = Loop((0, 0), (1, 0), (1, 1), (0.5, 1), (0.6, 1.05), (0.55, 0.95), (0, 1));

        bool repaired = PrcLoopRepair.TryUntangle(u, v, p, 0.25);

        Assert.True(repaired);
        Assert.False(PrcLoopRepair.HasCrossing(u, v));
        Assert.Equal(u.Count, p.Count);
        Assert.Equal(v.Count, p.Count);
        Assert.Equal(5, u.Count);                         // the two points of the sliver are gone
        Assert.Contains(new PrcVector(1, 1, 0), p);      // what is not the sliver stays
        Assert.Contains(new PrcVector(0.55, 0.95, 0), p);
    }

    [Fact]
    public void AFigureOfEightOfTwoEqualHalvesIsLeftAlone()
    {
        // Two triangles meeting at a crossing (1, 0.5): neither half is a sliver.
        var (u, v, p) = Loop((0, 0), (2, 1), (2, 0), (0, 1));
        int count = u.Count;

        bool repaired = PrcLoopRepair.TryUntangle(u, v, p, 0.25);

        Assert.False(repaired);
        Assert.Equal(count, u.Count);
    }

    [Fact]
    public void ALobeOfTwoPointsIsMeasuredFromTheCrossingAndABigOneIsLeftAlone()
    {
        // (0, 0) (2, 1) (2, 0) (0, 1) (-0.5, 0.5): the first and third segments cross at (1, 0.5). The lobe beyond the crossing has two
        // points of the loop but is the triangle (1, 0.5) (2, 1) (2, 0), area 0.5 of 1.25: no sliver.
        var (u, v, p) = Loop((0, 0), (2, 1), (2, 0), (0, 1), (-0.5, 0.5));

        bool repaired = PrcLoopRepair.TryUntangle(u, v, p, 0.10);

        Assert.False(repaired);
        Assert.Equal(5, u.Count);
    }

    [Fact]
    public void ALoopLongerThanTheSearchAllowsIsLeftAlone()
    {
        // A sliver in a loop of 2,000 points: every pair of segments is a test, and a face may not cost seconds.
        int n = 2000;
        var corners = new (double U, double V)[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            corners[i] = (Math.Cos(a), Math.Sin(a));
        }

        corners[10] = (corners[12].U * 1.001, corners[12].V * 1.001);   // a fold
        var (u, v, p) = Loop(corners);

        Assert.False(PrcLoopRepair.TryUntangle(u, v, p, 0.10));
        Assert.Equal(n, u.Count);
    }

    [Fact]
    public void APointGivenTwiceIsNotACrossing()
    {
        // Two ends of edges inverted to the same corner of the patch: the triangulator takes them as one point; both stay.
        var (u, v, p) = Loop((0, 0), (1, 0), (1, 0), (1, 1), (0, 1));

        Assert.False(PrcLoopRepair.HasCrossing(u, v));
        Assert.True(PrcLoopRepair.TryUntangle(u, v, p, 0.25));
        Assert.Equal(5, u.Count);
    }

    [Fact]
    public void ALoopThatDoesNotCrossItselfIsNotChanged()
    {
        var (u, v, p) = Loop((0, 0), (1, 0), (1, 1), (0, 1));

        Assert.False(PrcLoopRepair.HasCrossing(u, v));
        Assert.True(PrcLoopRepair.TryUntangle(u, v, p, 0.25));
        Assert.Equal(4, u.Count);
    }
}
