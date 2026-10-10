using Caly.Rendering.Scene3D.Prc.Brep;

namespace Caly.Rendering.Scene3D.Prc.Generated;

/// <summary>The kinds of curve a compressed body stores.</summary>
internal enum PrcCurveKind : byte
{
    Line,

    /// <summary>A circle, an arc, or a point on a circle: <see cref="PrcBrepCurveFlags"/> says which.</summary>
    Circle,

    /// <summary>A cubic Hermite spline: its control points and tangents.</summary>
    HermiteSpline,

    /// <summary>Curves one after the other.</summary>
    Composite,

    /// <summary>
    /// A curve the file does not store: an iso face deduces its third and fourth sides from the first two and the vertex they share, an iso NURBS
    /// face the line between two of its loop vertices. It has a number all the same, which other faces may refer to; <see cref="PrcBrepCurve.DataStart"/>
    /// is the face it belongs to and <see cref="PrcBrepCurve.DataCount"/> the side (0 – 3).
    /// </summary>
    Implicit,

    /// <summary>A natural boundary of an iso NURBS surface (an iso-parametric curve of it): owner face and side as for <see cref="Implicit"/>.</summary>
    IsoBoundary,

    /// <summary>An arc of an ellipse or a circle of an uncompressed body: centre, the two half-axes, the first and last angle (11 numbers).</summary>
    Conic,

    /// <summary>A part of a parametric curve of an uncompressed body: its number in <see cref="PrcTopoBrepDataCompress.CurveNurbs"/>, the first and last parameter (3).</summary>
    NurbsCurve,

    /// <summary>A polyline of an uncompressed body: its points (3 numbers each).</summary>
    Polyline,
}

[Flags]
internal enum PrcBrepCurveFlags : byte
{
    None = 0,

    /// <summary>A circle with data of its own kind (<see cref="Full"/> or a middle point), not the general form.</summary>
    Particular = 1,

    /// <summary>A whole circle: centre and normal.</summary>
    Full = 2,

    /// <summary>A general arc whose angle passes a half turn.</summary>
    OverHalfTurn = 4,

    /// <summary>A composite curve that closes on itself.</summary>
    Closed = 8,
}

/// <summary>
/// A curve of a compressed body: its kind, its end vertices (-1 when the face it trims gives them), and its numbers in
/// <see cref="PrcTopoBrepDataCompress.CurveData"/> (<c>DataStart</c>, <c>DataCount</c>) or, for a composite curve, the curves
/// it is made of in <see cref="PrcTopoBrepDataCompress.CompositeMembers"/>.
/// <para>
/// Data by kind: line — none; whole circle — centre, normal (6); circle given by its middle point — that point (3); general
/// arc — centre (3); Hermite spline — the number of points n, then n − 2 inner points and n tangents (1 + 3(n − 2) + 3n).
/// </para>
/// </summary>
internal readonly record struct PrcBrepCurve(PrcCurveKind Kind, int Start, int End, int DataStart, int DataCount, byte Flags);

/// <summary>The kinds of face a compressed body stores (the compressed entity types of its faces).</summary>
internal enum PrcFaceKind : byte
{
    IsoPlane = 2,
    IsoCylinder = 3,
    IsoTorus = 4,
    IsoSphere = 5,
    IsoCone = 6,
    IsoNurbs = 7,
    AnaPlane = 8,
    AnaCylinder = 9,
    AnaTorus = 10,
    AnaSphere = 11,
    AnaCone = 12,
    AnaNurbs = 13,
    AnaGenericFace = 14,
}

/// <summary>A loop of a face: a run of curves (<see cref="PrcTopoBrepDataCompress.Coedges"/>) and how the loop runs with the surface.</summary>
internal readonly record struct PrcBrepLoop(int FirstCoedge, int CoedgeCount, bool OrientationWithSurface);

/// <summary>
/// A face of a compressed body. An iso face has one loop of four sides; an ana face has the loops the file lists, or none when
/// the surface is not trimmed. <c>SurfaceData</c> holds the numbers the kind stores (see the table on
/// <see cref="PrcTopoBrepDataCompress.SurfaceData"/>); <c>Surface</c> indexes the NURBS surface or the generic surface of the
/// kinds that have one, else -1; <c>FirstVertex</c>, <c>VertexCount</c> index <see cref="PrcTopoBrepDataCompress.FaceVertices"/>.
/// </summary>
internal readonly record struct PrcBrepFace(
    PrcFaceKind Kind,
    int Shell,
    bool OrientationWithShell,
    bool Trimmed,
    int FirstLoop,
    int LoopCount,
    int SurfaceDataStart,
    int SurfaceDataCount,
    int FirstVertex,
    int VertexCount,
    int Surface,
    PrcVector PointOnTorus,
    bool HasPointOnTorus);

internal enum PrcGenericSurfaceKind : byte
{
    /// <summary>A surface of a kind this reader does not keep: the face is not drawn.</summary>
    Unsupported,
    Plane,
    Cylinder,
    Cone,
    Sphere,
    Torus,
}

/// <summary>
/// The uncompressed surface a generic ana face lies on, as far as it is kept: its frame, and for a cylinder or sphere its
/// radius, for a cone its radius and semi-angle, for a torus its major and minor radius (<c>Radius</c>, <c>Radius2</c>).
/// </summary>
internal readonly record struct PrcGenericSurface(PrcGenericSurfaceKind Kind, uint Type, Matrix4x4d Frame, double Radius, double Radius2);

/// <summary>
/// A compressed solid body (<c>PRC_TYPE_TOPO_BrepDataCompress</c>) as plain arrays: geometry only, no triangles. Vertices and
/// curves are numbered in the order they were stored; the references the file makes are resolved to those numbers when read.
/// (The type has the name the generated code expects of a hand-written reader's result.)
/// </summary>
internal sealed class PrcTopoBrepDataCompress
{
    /// <summary>The body's tolerance: compressed points are multiples of a hundredth of it.</summary>
    public required double Tolerance { get; init; }

    public required PrcVector[] Vertices { get; init; }

    public required PrcBrepCurve[] Curves { get; init; }

    public required double[] CurveData { get; init; }

    public required int[] CompositeMembers { get; init; }

    /// <summary>The curves of the loops, one run for each loop (<see cref="PrcBrepLoop"/>): numbers into <see cref="Curves"/>.</summary>
    public required int[] Coedges { get; init; }

    public required PrcBrepLoop[] Loops { get; init; }

    public required PrcBrepFace[] Faces { get; init; }

    /// <summary>
    /// The numbers a face stores of its surface, by kind: plane (iso or ana) — normal x, normal y, 1 when z is positive else 0;
    /// iso torus — 1 when its first circle is the major one; iso NURBS — the three booleans of the sense array; ana cylinder —
    /// a point on the axis, the axis direction (6); ana torus — centre, x axis, y axis (9); ana sphere — centre (3); ana cone —
    /// a point on the axis, the apex (6); the other kinds — nothing.
    /// </summary>
    public required double[] SurfaceData { get; init; }

    /// <summary>Vertices faces name: the one the deduced sides of an iso face share, or the loop vertices of an iso NURBS face (-1 where absent).</summary>
    public required int[] FaceVertices { get; init; }

    public required PrcGenericSurface[] GenericSurfaces { get; init; }

    /// <summary>The NURBS surfaces of the iso and ana NURBS faces (<see cref="PrcBrepFace.Surface"/> indexes them).</summary>
    public required PrcNurbs[] Nurbs { get; init; }

    /// <summary>
    /// The parametric curves of the edges of an uncompressed body (<see cref="PrcCurveKind.NurbsCurve"/>): B-splines in space, or
    /// the 2D B-splines of coedges on their faces' surfaces; none in a compressed one.
    /// </summary>
    public IPrcParametricCurve[] CurveNurbs { get; init; } = [];

    /// <summary>
    /// For each entry of <see cref="Coedges"/>, whether the loop runs along its curve against the curve's own direction (an
    /// uncompressed body's coedge orientation); null for a compressed body, which keeps no such sense.
    /// </summary>
    public bool[]? CoedgeReversed { get; init; }
}
