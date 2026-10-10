using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>A placement in double precision: an origin and three axes (the frames of PRC's analytic surfaces).</summary>
internal readonly record struct Matrix4x4d(PrcVector Origin, PrcVector X, PrcVector Y, PrcVector Z)
{
    public static Matrix4x4d Identity { get; } = new(default, new PrcVector(1, 0, 0), new PrcVector(0, 1, 0), new PrcVector(0, 0, 1));

    public PrcVector Transform(PrcVector p) => Origin + (X * p.X) + (Y * p.Y) + (Z * p.Z);

    public PrcVector TransformDirection(PrcVector d) => (X * d.X) + (Y * d.Y) + (Z * d.Z);
}

/// <summary>Helpers of the geometry of compressed bodies.</summary>
internal static class PrcBrepMath
{
    private const int Translate = 0x01, Rotate = 0x02, Mirror = 0x04, Scale = 0x08, NonUniformScale = 0x10, NonOrtho = 0x20;

    /// <summary>
    /// The frame a transformation stands for, in double precision (as <c>PrcTransforms</c> reads it into the scene's single
    /// precision matrices): the axes with their scale, the origin; none is the identity.
    /// </summary>
    public static Matrix4x4d Frame(PrcTransformation3D? t)
    {
        if (t is null)
        {
            return Matrix4x4d.Identity;
        }

        int behavior = (byte)t.Behavior;
        var x = new PrcVector(1, 0, 0);
        var y = new PrcVector(0, 1, 0);
        var z = new PrcVector(0, 0, 1);
        if ((behavior & NonOrtho) != 0 && t.NonOrthoMatrix.Length >= 3)
        {
            x = V(t.NonOrthoMatrix[0]);
            y = V(t.NonOrthoMatrix[1]);
            z = V(t.NonOrthoMatrix[2]);
        }
        else if ((behavior & Rotate) != 0 && t.Rotation.Length >= 2)
        {
            x = V(t.Rotation[0]);
            y = V(t.Rotation[1]);
            z = (behavior & Mirror) != 0 ? y.Cross(x) : x.Cross(y);
        }
        else if ((behavior & Mirror) != 0)
        {
            z = -z;
        }

        if ((behavior & NonUniformScale) != 0)
        {
            x *= t.NonUniformScale.X;
            y *= t.NonUniformScale.Y;
            z *= t.NonUniformScale.Z;
        }
        else if ((behavior & Scale) != 0)
        {
            x *= t.Scale;
            y *= t.Scale;
            z *= t.Scale;
        }

        PrcVector origin = (behavior & Translate) != 0 ? V(t.Translation) : default;
        return new Matrix4x4d(origin, x, y, z);
    }

    private static PrcVector V(PrcVector3D v) => new(v.X, v.Y, v.Z);
}
