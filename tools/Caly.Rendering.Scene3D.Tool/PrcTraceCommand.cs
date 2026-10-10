using System.Text;
using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Container;

namespace Caly.Rendering.Scene3D.Tool;

/// <summary>
/// Prints every entity of a PRC file as it is read (the exact geometry too), with the bit it starts at, in the form of the trace
/// of prc-rs's prc-describe: the two outputs are compared line by line to check that this reader walks a
/// file exactly as the reference does.
/// </summary>
internal static class PrcTraceCommand
{
    public static int Run(string file, TextWriter output)
    {
        byte[] data = File.ReadAllBytes(file);
        using PrcContainer container = PrcContainer.Open(data);
        var trace = new Writer(output);
        try
        {
            PrcFileReader.Read(container, trace: trace, geometry: PrcGeometryReading.Always);
            return 0;
        }
        catch (Scene3DException e)
        {
            trace.Flush();
            output.WriteLine($"--FAILED {e.Error}: {e.Message}--");
            return 2;
        }
        finally
        {
            trace.Flush();
        }
    }

    private sealed class Writer(TextWriter output) : PrcTrace
    {
        private static readonly string Spaces = new(' ', 512);
        private readonly StringBuilder _buffer = new(1 << 16);

        public override void Entity(string name, long bitPosition, int depth)
        {
            _buffer.Append(Spaces, 0, Math.Min(depth, Spaces.Length)).Append(name).Append("::from_reader() bp=").Append(bitPosition).Append('\n');
            if (_buffer.Length > 60_000)
            {
                Flush();
            }
        }

        public override void SectionStart(string name, long totalBits)
        {
            _buffer.Append("--").Append(name).Append(" START ").Append(totalBits).Append(" bits--\n");
        }

        public override void SectionEnd(PrcSectionRead section)
        {
            _buffer.Append("--").Append(section.Name).Append(" ENDOK remaining: ").Append(section.RemainingBits).Append(" bits, consumed: ")
                .Append(section.ConsumedBits).Append(" bits of ").Append(section.TotalBits).Append(" bits--\n");
        }

        public void Flush()
        {
            output.Write(_buffer.ToString());
            _buffer.Clear();
            output.Flush();
        }
    }
}
