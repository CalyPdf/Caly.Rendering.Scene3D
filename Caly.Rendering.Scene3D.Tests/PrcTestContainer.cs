using System.Buffers.Binary;
using System.IO.Compression;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Builds the outer layout of a PRC file around given section contents, for tests: file header, one
/// header and five compressed sections per file structure, and the model file.
/// </summary>
internal sealed class PrcTestContainer
{
    public sealed class Structure
    {
        public uint[] Id { get; init; } = [1, 2, 3, 4];

        public byte[] Globals { get; init; } = [0];

        public byte[] Tree { get; init; } = [0];

        public byte[] Tessellation { get; init; } = [0];

        /// <summary>The exact-geometry section (what an item without a tessellation is drawn from).</summary>
        public byte[] Geometry { get; init; } = [0];

        /// <summary>The extra-geometry section (the graphics of the faces of the exact geometry); by default a byte that is not one.</summary>
        public byte[] ExtraGeometry { get; init; } = [0];

        public List<byte[]> Files { get; } = [];
    }

    public uint VersionForRead { get; init; } = 7094;

    public uint AuthoringVersion { get; init; } = 8137;

    public List<Structure> Structures { get; } = [];

    public byte[] ModelFile { get; init; } = [0];

    /// <summary>When set, the bytes stored for a section in place of its compressed content.</summary>
    public Func<int, string, byte[], byte[]>? Compress { get; init; }

    public static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    public byte[] Build() => Build(out _);

    /// <summary>Builds the file; <paramref name="offsets"/> receives where each structure's description sits in the header.</summary>
    public byte[] Build(out int[] offsets)
    {
        byte[] Pack(int structure, string section, byte[] data) => Compress is null ? Zlib(data) : Compress(structure, section, data);

        const int fileHeader = 3 + 4 + 4 + 16 + 16 + 4;
        const int description = 16 + 4 + 4 + 6 * 4;
        int position = fileHeader + Structures.Count * description + 4 + 4 + 4;
        var body = new MemoryStream();
        var table = new List<uint[]>();
        offsets = new int[Structures.Count];
        for (int i = 0; i < Structures.Count; i++)
        {
            Structure s = Structures[i];
            offsets[i] = fileHeader + i * description;
            var starts = new uint[6];

            starts[0] = (uint)(position + body.Length);
            var header = new MemoryStream();
            header.Write("PRC"u8);
            WriteUInt32(header, VersionForRead);
            WriteUInt32(header, AuthoringVersion);
            header.Write(new byte[32]);
            WriteUInt32(header, (uint)s.Files.Count);
            foreach (byte[] file in s.Files)
            {
                WriteUInt32(header, (uint)file.Length);
                header.Write(file);
            }

            body.Write(header.ToArray());

            byte[][] sections = [Pack(i, "globals", s.Globals), Pack(i, "tree", s.Tree), Pack(i, "tessellation", s.Tessellation), Pack(i, "geometry", s.Geometry), Pack(i, "extra geometry", s.ExtraGeometry)];
            for (int k = 0; k < sections.Length; k++)
            {
                starts[k + 1] = (uint)(position + body.Length);
                body.Write(sections[k]);
            }

            table.Add(starts);
        }

        uint modelStart = (uint)(position + body.Length);
        body.Write(Pack(-1, "model", ModelFile));
        uint modelEnd = (uint)(position + body.Length);

        var file2 = new MemoryStream();
        file2.Write("PRC"u8);
        WriteUInt32(file2, VersionForRead);
        WriteUInt32(file2, AuthoringVersion);
        file2.Write(new byte[32]);
        WriteUInt32(file2, (uint)Structures.Count);
        for (int i = 0; i < Structures.Count; i++)
        {
            foreach (uint part in Structures[i].Id)
            {
                WriteUInt32(file2, part);
            }

            WriteUInt32(file2, 0);
            WriteUInt32(file2, 6);
            foreach (uint start in table[i])
            {
                WriteUInt32(file2, start);
            }
        }

        WriteUInt32(file2, modelStart);
        WriteUInt32(file2, modelEnd);
        WriteUInt32(file2, 0);
        file2.Write(body.ToArray());
        return file2.ToArray();
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
