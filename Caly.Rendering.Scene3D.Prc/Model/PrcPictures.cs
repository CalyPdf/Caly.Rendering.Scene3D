using System.Buffers.Binary;

namespace Caly.Rendering.Scene3D.Prc.Model;

/// <summary>
/// The pictures of textures. A PRC picture is a file embedded in the header of its file structure: a PNG
/// or a JPEG as it is, or deflated pixels. Only the first two are carried into a scene, undecoded; what
/// the scene needs to know of them (size, whether there is alpha) is read from their own headers, since
/// the size the PRC entity gives is not meant for them (ISO 14739-1, PRC_TYPE_GRAPH_Picture).
/// </summary>
internal static class PrcPictures
{
    /// <summary>EPRCPictureDataFormat: 0 PNG, 1 JPEG, 2 to 5 deflated pixels.</summary>
    public const uint FormatPng = 0, FormatJpeg = 1;

    public static bool TryDescribe(uint format, ReadOnlySpan<byte> file, out SceneImageEncoding encoding, out SceneTextureChannels channels, out int width, out int height)
    {
        encoding = SceneImageEncoding.Unknown;
        channels = SceneTextureChannels.None;
        width = 0;
        height = 0;
        return format switch
        {
            FormatPng => TryPng(file, out encoding, out channels, out width, out height),
            FormatJpeg => TryJpeg(file, out encoding, out channels, out width, out height),
            _ => false,
        };
    }

    private static bool TryPng(ReadOnlySpan<byte> file, out SceneImageEncoding encoding, out SceneTextureChannels channels, out int width, out int height)
    {
        encoding = SceneImageEncoding.Png;
        channels = SceneTextureChannels.Rgba;
        width = 0;
        height = 0;

        // Signature, then the IHDR chunk: length, "IHDR", width, height, bit depth, colour type.
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (file.Length < 26 || !file[..8].SequenceEqual(signature) || !file.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        uint w = BinaryPrimitives.ReadUInt32BigEndian(file[16..]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(file[20..]);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
        {
            return false;
        }

        // Colour types 4 and 6 have alpha; a palette (3) may have it through a tRNS chunk.
        channels = file[25] is 0 or 2 ? SceneTextureChannels.Rgb : SceneTextureChannels.Rgba;
        width = (int)w;
        height = (int)h;
        return true;
    }

    private static bool TryJpeg(ReadOnlySpan<byte> file, out SceneImageEncoding encoding, out SceneTextureChannels channels, out int width, out int height)
    {
        encoding = SceneImageEncoding.Jpeg24;
        channels = SceneTextureChannels.Rgb;
        width = 0;
        height = 0;
        if (file.Length < 4 || file[0] != 0xFF || file[1] != 0xD8)
        {
            return false;
        }

        // Segments: FF, a marker, a two-byte length that includes itself. The frame header (SOF0 to
        // SOF15, but not DHT, JPG or DAC) holds precision, height, width, number of components.
        int at = 2;
        while (at + 4 <= file.Length)
        {
            if (file[at] != 0xFF)
            {
                return false;
            }

            byte marker = file[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2;
                continue;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(file[(at + 2)..]);
            if (length < 2 || marker == 0xDA || marker == 0xD9)
            {
                return false;
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 8 || at + 10 > file.Length)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(file[(at + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(file[(at + 7)..]);
                if (file[at + 9] == 1)
                {
                    encoding = SceneImageEncoding.Jpeg8;
                    channels = SceneTextureChannels.Luminance;
                }

                return width > 0 && height > 0;
            }

            at += 2 + length;
        }

        return false;
    }
}
