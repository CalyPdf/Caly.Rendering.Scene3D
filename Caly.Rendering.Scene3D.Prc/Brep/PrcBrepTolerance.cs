using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// How finely a compressed body is cut into triangles (spec §4): one chord tolerance for the whole body, so that faces that share
/// an edge agree on its points, and the caps that keep a tiny tolerance on a huge body from making a mesh of billions.
/// </summary>
internal static class PrcBrepTolerance
{
    /// <summary>The chord tolerance as a part of the body's diagonal.</summary>
    public const double RelativeChord = 1.0 / 1000;

    /// <summary>The largest angle between neighbouring normals (and between neighbouring chords of a curve): 15 degrees.</summary>
    public const double MaxTurnRadians = 15 * Math.PI / 180;

    /// <summary>Most segments of one edge.</summary>
    public const int MaxSamplesPerEdge = 1024;

    /// <summary>Most triangles of one face.</summary>
    public const int MaxTrianglesPerFace = 65536;

    /// <summary>
    /// The chord tolerance of a body: a thousandth of the diagonal of the box of its vertices, or of the control points of its
    /// spline surfaces when it has none (a body of untrimmed patches), never finer than the tolerance the file gives (finer than
    /// the body's own accuracy only adds triangles).
    /// </summary>
    public static double Chord(PrcTopoBrepDataCompress brep)
    {
        bool any = false;
        PrcVector min = default, max = default;
        void Add(PrcVector v)
        {
            if (!double.IsFinite(v.X + v.Y + v.Z))
            {
                return;                 // a point of nothing (a wide compressed point holds plain doubles)
            }

            if (!any)
            {
                min = max = v;
                any = true;
                return;
            }

            min = new PrcVector(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));
            max = new PrcVector(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
        }

        foreach (PrcVector v in brep.Vertices)
        {
            Add(v);
        }

        // The control points only for a body that has no vertex (untrimmed patches): a net may reach far past its faces (a sheet
        // 20,000 long trimmed to a few millimetres, in one sample), which would make the whole body coarse.
        if (!any || min == max)
        {
            foreach (PrcNurbs n in brep.Nurbs)
            {
                foreach (PrcVector v in n.ControlPoints)
                {
                    Add(v);
                }
            }
        }

        if (!any)
        {
            return brep.Tolerance;
        }

        double diagonal = Math.Sqrt((max - min).LengthSquared());
        return Math.Max(diagonal * RelativeChord, brep.Tolerance);
    }
}