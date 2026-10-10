using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The arithmetic the PRC standard gives for compressed tessellation (ISO 14739-1, annex "Basis
/// pseudocode"): a writer rebuilt every point and normal from values it had already approximated, with
/// these functions; a reader that uses the same ones follows it.
/// </summary>
public class PrcBasisTests
{
    [Fact]
    public void TheLengthIsTheRootOfTheSquaresRoundedToSinglePrecision()
    {
        Assert.Equal(13, PrcBasis.Length(new PrcVector(3, 4, 12)));
        Assert.Equal(0, PrcBasis.Length(default));

        // The annex takes the root of the sum of squares after storing it in a float.
        var random = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            double scale = Math.Pow(10, random.NextDouble() * 12 - 6);
            var v = new PrcVector((random.NextDouble() - 0.5) * scale, (random.NextDouble() - 0.5) * scale, (random.NextDouble() - 0.5) * scale);
            double expected = Math.Sqrt((float)v.LengthSquared());
            Assert.True(Math.Abs(PrcBasis.Length(v) - expected) <= 4e-16 * expected, $"{v}: {PrcBasis.Length(v):R} against {expected:R}");
        }
    }

    [Fact]
    public void AVectorShorterThanEpsilonHasNoDirection()
    {
        var tiny = new PrcVector(PrcBasis.Epsilon / 2, 0, 0);
        Assert.False(PrcBasis.TryUnitize(ref tiny));

        var v = new PrcVector(0, -3, 4);
        Assert.True(PrcBasis.TryUnitize(ref v));
        Assert.Equal(1, Math.Sqrt(v.LengthSquared()), 14);
        Assert.Equal(new PrcVector(0, -0.6, 0.8), v);
    }

    [Fact]
    public void TheAngleBetweenTwoVectorsIsClampedAtItsEnds()
    {
        Assert.True(PrcBasis.TryAngleBetween(new PrcVector(1, 0, 0), new PrcVector(0, 5, 0), out double right));
        Assert.Equal(Math.PI / 2, right, 15);

        Assert.True(PrcBasis.TryAngleBetween(new PrcVector(2, 2, 2), new PrcVector(7, 7, 7), out double none));
        Assert.Equal(0, none);
        Assert.True(PrcBasis.TryAngleBetween(new PrcVector(2, 2, 2), new PrcVector(-7, -7, -7), out double opposite));
        Assert.Equal(Math.PI, opposite);

        Assert.False(PrcBasis.TryAngleBetween(default, new PrcVector(1, 0, 0), out _));
        Assert.False(PrcBasis.TryAngleBetween(new PrcVector(1, 0, 0), new PrcVector(1e-9, 0, 0), out _));
    }

    [Fact]
    public void AnAxisIsCompletedToAFrame()
    {
        Assert.True(PrcBasis.TryMakeOrthoRep(new PrcVector(1, 0, 0), out PrcVector y, out PrcVector z));
        Assert.Equal((new PrcVector(0, 1, 0), new PrcVector(0, 0, 1)), (y, z));

        // Along Y the first helper axis fails and the second is taken.
        Assert.True(PrcBasis.TryMakeOrthoRep(new PrcVector(0, 2, 0), out y, out z));
        Assert.Equal((new PrcVector(1, 0, 0), new PrcVector(0, 0, -1)), (y, z));

        var random = new Random(11);
        for (int i = 0; i < 200; i++)
        {
            var x = new PrcVector(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            Assert.True(PrcBasis.TryMakeOrthoRep(x, out y, out z));
            PrcVector unit = x;
            Assert.True(PrcBasis.TryUnitize(ref unit));
            Assert.Equal(0, unit.Dot(y), 6);
            Assert.Equal(0, unit.Dot(z), 6);
            Assert.Equal(0, y.Dot(z), 6);
            PrcVector cross = unit.Cross(y);
            Assert.Equal(1, cross.Dot(z), 6);          // right-handed: X ^ Y = Z
        }

        Assert.False(PrcBasis.TryMakeOrthoRep(default, out _, out _));
    }

    [Fact]
    public void SineAndCosineAreTheFourTermSeriesFoldedAtAnEighthOfATurn()
    {
        for (int i = 0; i <= 1000; i++)
        {
            double a = i * (Math.PI / 2) / 1000;
            // Four terms are good to a few millionths over an eighth of a turn.
            Assert.InRange(PrcBasis.Sin(a) - Math.Sin(a), -5e-6, 5e-6);
            Assert.InRange(PrcBasis.Cos(a) - Math.Cos(a), -5e-6, 5e-6);
        }

        // Below the fold: the series themselves.
        const double small = 0.5;
        Assert.Equal(small - Math.Pow(small, 3) / 6 + Math.Pow(small, 5) / 120 - Math.Pow(small, 7) / 5040, PrcBasis.Sin(small), 15);
        Assert.Equal(1 - small * small / 2 + Math.Pow(small, 4) / 24 - Math.Pow(small, 6) / 720, PrcBasis.Cos(small), 15);

        // Above it: each is the other of the complement.
        const double large = 1.2;
        Assert.Equal(PrcBasis.Cos(Math.PI / 2 - large), PrcBasis.Sin(large));
        Assert.Equal(PrcBasis.Sin(Math.PI / 2 - large), PrcBasis.Cos(large));
    }
}
