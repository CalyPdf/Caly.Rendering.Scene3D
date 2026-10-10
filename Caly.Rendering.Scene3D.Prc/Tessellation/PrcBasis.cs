namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>
/// The basic calculations of compressed tessellation, as the standard gives them (ISO 14739-1, annex
/// "Basis pseudocode", and the sine and cosine of 7.8.9.4). A writer rebuilt every point and normal
/// from values it had already approximated, with these; a reader follows it by doing the same.
/// </summary>
internal static class PrcBasis
{
    /// <summary><c>FLT_EPSILON</c>: below this a length is no length.</summary>
    public const double Epsilon = 1.1920928955078125e-7;

    private const double HalfPi = Math.PI / 2;

    /// <summary>
    /// The length of a vector: the square root, by Newton's iteration, of the sum of its squares
    /// <em>stored in single precision</em>, as the annex has it. (Its listing of the iteration cannot be
    /// run as printed; this is what it sets out to do.)
    /// </summary>
    public static double Length(PrcVector v)
    {
        float sum = (float)v.LengthSquared();
        double squared = sum;
        if (!(squared > 0) || double.IsPositiveInfinity(squared))
        {
            // Nothing, or more than a float holds, or not a number.
            return squared == 0 ? 0 : squared;
        }

        // Start from the number with half the exponent.
        double x = Math.ScaleB(1.0, Math.ILogB(squared) / 2);
        for (int i = 0; i < 100; i++)
        {
            double next = 0.5 * (x + (squared / x));
            if (next == x)
            {
                break;
            }

            x = next;
        }

        return x;
    }

    /// <summary>Makes a vector of length one of it; false, and the vector untouched, when it is shorter than <see cref="Epsilon"/>.</summary>
    public static bool TryUnitize(ref PrcVector v)
    {
        double length = Length(v);
        if (!(length >= Epsilon))
        {
            return false;
        }

        v /= length;
        return true;
    }

    /// <summary>The angle between two vectors, 0 to pi; false when either is shorter than <see cref="Epsilon"/>.</summary>
    public static bool TryAngleBetween(PrcVector a, PrcVector b, out double radians)
    {
        radians = 0;
        double lengthA = Length(a), lengthB = Length(b);
        if (!(lengthA >= Epsilon) || !(lengthB >= Epsilon))
        {
            return false;
        }

        double dot = (a / lengthA).Dot(b / lengthB);
        if (dot > 1.0 - 1e-12)
        {
            dot = 1.0;
        }
        else if (dot < -1.0 + 1e-12)
        {
            dot = -1.0;
        }

        radians = Math.Acos(dot);
        return true;
    }

    /// <summary>Two axes that complete <paramref name="x"/> to a right-handed frame of unit vectors (<c>MakeOrthoRep</c>).</summary>
    public static bool TryMakeOrthoRep(PrcVector x, out PrcVector y, out PrcVector z)
    {
        y = default;
        z = default;
        if (!TryUnitize(ref x))
        {
            return false;
        }

        y = new PrcVector(0, 1, 0);
        z = x.Cross(y);
        if (!TryUnitize(ref z))
        {
            y = new PrcVector(1, 0, 0);
            z = x.Cross(y);
            if (!TryUnitize(ref z))
            {
                return false;
            }
        }

        y = z.Cross(x);
        return TryUnitize(ref y);
    }

    /// <summary>The sine of an angle of the first quarter turn: four terms of its series, taken as a cosine past an eighth of a turn.</summary>
    public static double Sin(double a) => a > Math.PI / 4 ? CosSeries(HalfPi - a) : SinSeries(a);

    /// <summary>The cosine of an angle of the first quarter turn, likewise.</summary>
    public static double Cos(double a) => a > Math.PI / 4 ? SinSeries(HalfPi - a) : CosSeries(a);

    private static double SinSeries(double a)
    {
        double a2 = a * a;
        return a * (1 - (a2 / 6) * (1 - (a2 / 20) * (1 - (a2 / 42))));
    }

    private static double CosSeries(double a)
    {
        double a2 = a * a;
        return 1 - (a2 / 2) * (1 - (a2 / 12) * (1 - (a2 / 30)));
    }
}
