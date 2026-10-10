using Caly.Rendering.Scene3D.Prc.Bits;

namespace Caly.Rendering.Scene3D.Prc.Generated
{
    /// <summary>What is kept of <c>VertexColors</c>: one colour per point of a face.</summary>
    internal sealed class PrcVertexColors
    {
        /// <summary>
        /// The colours, RGBA with red in the low byte (opaque when the file has no alpha), in the order
        /// of the face's point indices. Empty when the file has none ("optimised", which the format
        /// reserves) and for wires, whose colours are read past.
        /// </summary>
        public required uint[] Colors;
    }

    internal static partial class PrcReaders
    {
        /// <summary>
        /// Vertex colours: the first colour, then for every further point either "the same" or a colour.
        /// Their number is not in the file; it follows from the face or wire they belong to
        /// (<see cref="PrcContext.VertexColorCount"/>). Written by hand to keep them as one packed
        /// array; it reads, and reports to a trace, exactly what the description in prc.json says.
        /// </summary>
        internal static PrcVertexColors ReadVertexColors(ref PrcBitReader r, PrcContext ctx)
        {
            ctx.Enter("VertexColors", r.BitPosition);
            bool isRgba = r.ReadBoolean();
            bool isSegmentColor = ctx.InsideWire && r.ReadBoolean();
            ctx.SetWireSegmentColor(isSegmentColor);
            bool optimized = r.ReadBoolean();

            uint[] colors = [];
            if (!optimized)
            {
                string colorName = isRgba ? "ColorRGBA" : "ColorRGB";
                string remainderName = isRgba ? "ColorDataRemainderRGBA" : "ColorDataRemainderRGB";
                int bits = isRgba ? 32 : 24;
                bool keep = !ctx.InsideWire;

                // The first colour is there even when nothing has a point.
                ctx.Enter(colorName, r.BitPosition);
                uint color = Pack(r.ReadBits(bits), isRgba);
                ctx.Leave();

                int remaining = ctx.Count(ref r, ctx.VertexColorCount - 1, 1);
                if (keep)
                {
                    ctx.Charge((remaining + 1L) * sizeof(uint));
                    colors = new uint[remaining + 1];
                    colors[0] = color;
                }

                for (int i = 1; i <= remaining; i++)
                {
                    ctx.Enter(remainderName, r.BitPosition);
                    if (!r.ReadBoolean())
                    {
                        ctx.Enter(colorName, r.BitPosition);
                        color = Pack(r.ReadBits(bits), isRgba);
                        ctx.Leave();
                    }

                    ctx.Leave();
                    if (keep)
                    {
                        colors[i] = color;
                    }
                }
            }

            ctx.Leave();
            return new PrcVertexColors { Colors = colors };
        }

        /// <summary>Bytes in file order (red first) to RGBA with red in the low byte.</summary>
        private static uint Pack(uint value, bool isRgba)
        {
            return isRgba
                ? System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value)
                : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value << 8) | 0xFF000000;
        }
    }
}
