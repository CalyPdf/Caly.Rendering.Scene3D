namespace Caly.Rendering.Scene3D.Prc.Bits;

/// <summary>
/// A compressed entity type (ISO 14739-1 draft 7.9.20.5.2): a curve (line 0, circle 1, B-spline 2, ellipse 12, composite 13) or,
/// when <paramref name="IsCurve"/> is false, a face or loop marker (0 – 14: new loop, end loop, the iso faces, the ana faces).
/// </summary>
internal readonly record struct PrcCompressedEntity(bool IsCurve, byte Value);
