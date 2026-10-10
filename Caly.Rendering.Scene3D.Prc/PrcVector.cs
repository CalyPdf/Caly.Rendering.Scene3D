namespace Caly.Rendering.Scene3D.Prc;

/// <summary>A point or a direction in double precision (the standard's <c>PrcPt</c>).</summary>
internal readonly record struct PrcVector(double X, double Y, double Z)
{
    public static PrcVector operator +(PrcVector a, PrcVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static PrcVector operator -(PrcVector a, PrcVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static PrcVector operator -(PrcVector a) => new(-a.X, -a.Y, -a.Z);

    public static PrcVector operator *(PrcVector a, double d) => new(a.X * d, a.Y * d, a.Z * d);

    public static PrcVector operator /(PrcVector a, double d) => new(a.X / d, a.Y / d, a.Z / d);

    public double Dot(PrcVector other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    /// <summary>The vector product (the standard writes it as the product of two points).</summary>
    public PrcVector Cross(PrcVector other) => new((Y * other.Z) - (Z * other.Y), (Z * other.X) - (X * other.Z), (X * other.Y) - (Y * other.X));

    public double LengthSquared() => (X * X) + (Y * Y) + (Z * Z);
}
