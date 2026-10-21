using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Prc.Brep;

/// <summary>
/// The graphics of the faces of a solid's exact geometry. A compressed face has none of its own: they are in the extra-geometry
/// section (ISO 14739-1, 8.3.9, <c>ContextGraphics</c>), one entry per face of the topological context, gathered body after body
/// and, in a body, connex after connex, shell after shell, face after face. An entry either has no graphics (the face takes its
/// item's), gives a layer, a style and behaviour bits, or says to use the current ones: the last given before it in the context
/// (the current graphics are reset before the list is read). CAD exports put the colours of single faces there: the Tetra4D
/// LandingGear sample's arms, lugs and bushings are coloured that way only.
/// </summary>
internal static class PrcFaceStyles
{
    /// <summary>
    /// The graphics of each face of a body (by its number in <see cref="PrcTopoBrepDataCompress.Faces"/>; style 0 for none), or null
    /// when no face has a style of its own or the list cannot be matched with the faces with certainty: when it does not have
    /// exactly one entry per face of the context, or when a body of the context is uncompressed (its faces are named through
    /// pointers that may repeat, which this does not count) or a compressed body's shell order is unknown.
    /// </summary>
    /// <param name="extra">The extra geometry of the file structure: one entry per topological context.</param>
    /// <param name="contexts">The topological contexts of the exact geometry.</param>
    /// <param name="context">The body's context (index into both).</param>
    /// <param name="body">The body's index in its context.</param>
    public static PrcFaceGraphics[]? Of(PrcExtraGeometry[]? extra, PrcTopoContext[] contexts, int context, int body)
    {
        if (extra is null || extra.Length != contexts.Length || (uint)context >= (uint)contexts.Length)
        {
            return null;
        }

        PrcElementInformation[]? elements = null;
        foreach (PrcGraphicsInformation information in extra[context].ContextData.TreatTypes)
        {
            if (information.ElementType == PrcType.PRC_TYPE_TOPO_Face)
            {
                elements = information.TreatTypes;
            }
        }

        PrcTopoBody[] bodies = contexts[context].Bodies;
        if (elements is null || (uint)body >= (uint)bodies.Length || bodies[body].Id is not PrcTopoBrepDataCompress target
            || target.FacesInShellOrder is not { } order)
        {
            return null;
        }

        // Where the body's faces start in the list, and whether the list has exactly the faces of the context.
        int start = 0, total = 0;
        for (int b = 0; b < bodies.Length; b++)
        {
            switch (bodies[b].Id)
            {
                case PrcTopoBrepDataCompress compressed:
                    start += b < body ? compressed.Faces.Length : 0;
                    total += compressed.Faces.Length;
                    break;
                case PrcTopoBrepData:
                    return null;
            }
        }

        if (total != elements.Length)
        {
            return null;
        }

        // The current graphics up to the body's first face, then the body's faces.
        var current = default(PrcFaceGraphics);
        var faces = new PrcFaceGraphics[target.Faces.Length];
        bool any = false;
        for (int e = 0; e < start + order.Length; e++)
        {
            PrcElementInformation element = elements[e];
            PrcFaceGraphics graphics = default;
            if (element.HasGraphics)
            {
                if (!element.GraphicBehavior.UseContext)
                {
                    current = new PrcFaceGraphics(element.GraphicBehavior.BiasedIndexOfLineStyle, element.GraphicBehavior.BehaviorBitField);
                }

                graphics = current;
            }

            if (e >= start)
            {
                faces[order[e - start]] = graphics;
                any |= graphics.Style != 0;
            }
        }

        return any ? faces : null;
    }
}
