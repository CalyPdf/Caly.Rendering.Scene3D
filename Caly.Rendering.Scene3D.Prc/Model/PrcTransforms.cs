using System.Numerics;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Model;

/// <summary>
/// The transformations of PRC as matrices of the scene. PRC writes matrices for column vectors
/// (<c>new = M × old</c>, translation in the last column); the scene uses the row-vector convention of
/// <see cref="Matrix4x4"/>, which is the transpose.
/// </summary>
internal static class PrcTransforms
{
    private const int Translate = 0x01, Rotate = 0x02, Mirror = 0x04, Scale = 0x08, NonUniformScale = 0x10, NonOrtho = 0x20;

    /// <summary>
    /// The matrix of a transformation as an entity keeps it: a cartesian transformation, a general one,
    /// or none (the identity). Values that are not numbers give the identity and false.
    /// </summary>
    public static bool TryGetMatrix(object? transformation, out Matrix4x4 matrix)
    {
        matrix = transformation switch
        {
            PrcMiscCartesianTransformation cartesian => Cartesian(cartesian.Transform),
            PrcMiscGeneralTransformation general => General(general.GeneralTransform),
            _ => Matrix4x4.Identity,
        };

        if (IsFinite(matrix))
        {
            return true;
        }

        matrix = Matrix4x4.Identity;
        return false;
    }

    /// <summary>
    /// Sixteen coefficients stored column by column (ISO 14739-1, PRC_TYPE_MISC_GeneralTransformation):
    /// read row by row they are the transpose, which is the scene's convention.
    /// </summary>
    private static Matrix4x4 General(double[] m)
    {
        return new Matrix4x4(
            (float)m[0], (float)m[1], (float)m[2], (float)m[3],
            (float)m[4], (float)m[5], (float)m[6], (float)m[7],
            (float)m[8], (float)m[9], (float)m[10], (float)m[11],
            (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
    }

    /// <summary>
    /// Translation, axes and scale as the behaviour bits say (ISO 14739-1, Transformation): the axes are
    /// the columns of the format's matrix, so the rows of the scene's; the third axis of a rotation is
    /// the cross product of the two stored ones, turned round for a mirror.
    /// </summary>
    private static Matrix4x4 Cartesian(PrcTransformation3D t)
    {
        int behavior = (byte)t.Behavior;
        Vector3 x = Vector3.UnitX, y = Vector3.UnitY, z = Vector3.UnitZ;
        if ((behavior & NonOrtho) != 0)
        {
            x = Vector(t.NonOrthoMatrix[0]);
            y = Vector(t.NonOrthoMatrix[1]);
            z = Vector(t.NonOrthoMatrix[2]);
        }
        else if ((behavior & Rotate) != 0)
        {
            x = Vector(t.Rotation[0]);
            y = Vector(t.Rotation[1]);
            z = (behavior & Mirror) != 0 ? Vector3.Cross(y, x) : Vector3.Cross(x, y);
        }
        else if ((behavior & Mirror) != 0)
        {
            z = -z;
        }

        if ((behavior & NonUniformScale) != 0)
        {
            x *= (float)t.NonUniformScale.X;
            y *= (float)t.NonUniformScale.Y;
            z *= (float)t.NonUniformScale.Z;
        }
        else if ((behavior & Scale) != 0)
        {
            float scale = (float)t.Scale;
            x *= scale;
            y *= scale;
            z *= scale;
        }

        Vector3 origin = (behavior & Translate) != 0 ? Vector(t.Translation) : Vector3.Zero;
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            origin.X, origin.Y, origin.Z, 1);
    }

    /// <summary>
    /// The matrix a texture definition applies to the texture coordinates (ISO 14739-1, texture
    /// transformation): the 2D transformation as a 3D one that leaves the third axis alone, after the
    /// mirrors the definition asks for; the identity when it has none or its numbers are not finite.
    /// </summary>
    public static Matrix4x4 TextureMatrix(PrcGraphTextureTransformation? texture)
    {
        if (texture is null)
        {
            return Matrix4x4.Identity;
        }

        Matrix4x4 matrix = Matrix4x4.Identity;
        if (texture.Transform is { } t)
        {
            int behavior = (byte)t.Behavior;
            Vector2 x = Vector2.UnitX, y = Vector2.UnitY;
            if ((behavior & NonOrtho) != 0 && t.NonOrthoMatrix.Length >= 2)
            {
                x = Vector(t.NonOrthoMatrix[0]);
                y = Vector(t.NonOrthoMatrix[1]);
            }
            else if ((behavior & Rotate) != 0 && t.Rotation is { } rotation)
            {
                x = Vector(rotation);
                y = new Vector2(-x.Y, x.X);
            }

            if ((behavior & NonUniformScale) != 0 && t.NonUniformScale is { } scales)
            {
                x *= (float)scales.X;
                y *= (float)scales.Y;
            }
            else if ((behavior & Scale) != 0)
            {
                x *= (float)t.Scale;
                y *= (float)t.Scale;
            }

            Vector2 origin = (behavior & Translate) != 0 && t.Translation is { } translation ? Vector(translation) : Vector2.Zero;
            matrix = new Matrix4x4(
                x.X, x.Y, 0, 0,
                y.X, y.Y, 0, 0,
                0, 0, 1, 0,
                origin.X, origin.Y, 0, 1);
        }

        if (texture.InvertS || texture.InvertT)
        {
            matrix = new Matrix4x4(
                texture.InvertS ? -1 : 1, 0, 0, 0,
                0, texture.InvertT ? -1 : 1, 0, 0,
                0, 0, 1, 0,
                texture.InvertS ? 1 : 0, texture.InvertT ? 1 : 0, 0, 1) * matrix;
        }

        return IsFinite(matrix) ? matrix : Matrix4x4.Identity;
    }

    private static Vector2 Vector(PrcVector2D v) => new((float)v.X, (float)v.Y);

    private static Vector3 Vector(PrcVector3D v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static bool IsFinite(in Matrix4x4 m)
    {
        return float.IsFinite(m.M11 + m.M12 + m.M13 + m.M14 + m.M21 + m.M22 + m.M23 + m.M24 + m.M31 + m.M32 + m.M33 + m.M34 + m.M41 + m.M42 + m.M43 + m.M44);
    }
}
