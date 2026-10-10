using System.Buffers.Binary;
using System.IO.Compression;

namespace Caly.Rendering.Scene3D.Tool;

/// <summary>Writes 8-bit RGB images as PNG with nothing but the base class library.</summary>
internal static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="rgb">Rows top to bottom, three bytes per pixel.</param>
    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> rgb)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // bits per channel
        header[9] = 2;  // colour type: RGB
        WriteChunk(output, "IHDR", header);

        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 3;
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter: none
                zlib.Write(rgb.Slice(y * stride, stride));
            }
        }

        WriteChunk(output, "IDAT", data.GetBuffer().AsSpan(0, (int)data.Length));
        WriteChunk(output, "IEND", default);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> prefix = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(prefix, data.Length);
        for (int i = 0; i < 4; i++)
        {
            prefix[4 + i] = (byte)type[i];
        }

        output.Write(prefix);
        output.Write(data);

        uint crc = Crc(Crc(0xFFFFFFFF, prefix[4..]), data) ^ 0xFFFFFFFF;
        Span<byte> suffix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(suffix, crc);
        output.Write(suffix);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
