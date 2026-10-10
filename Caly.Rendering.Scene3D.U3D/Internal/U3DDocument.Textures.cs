namespace Caly.Rendering.Scene3D.U3D.Internal;

internal sealed partial class U3DDocument
{
    /// <summary>One continuation image of a texture, as declared.</summary>
    private sealed class ImageDeclaration
    {
        public required SceneImageEncoding Encoding { get; init; }

        public required SceneTextureChannels Channels { get; init; }

        /// <summary>The image lives in another file, which is never fetched.</summary>
        public required bool External { get; init; }

        /// <summary>Where the image's bytes are in the file, in file order.</summary>
        public List<(int Offset, int Length)>? Chunks { get; set; }
    }

    private sealed class TextureDeclaration
    {
        public required string Name { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }

        public required SceneTextureChannels Channels { get; init; }

        public required ImageDeclaration[] Images { get; init; }
    }

    private const ushort ImageIsExternal = 0x1;

    // A continuation image format costs at least two type bytes, its attributes and a count.
    private const int MinimumImageFormatBytes = 8;

    private readonly Dictionary<string, TextureDeclaration> _textures = new(StringComparer.Ordinal);
    private readonly List<string> _textureOrder = [];
    private readonly List<(string Texture, uint Image, int Offset, int Length)> _textureChunks = [];
    private long _textureChunkBytes;

    // Scene texture of each declared texture, filled by BuildTextures.
    private readonly Dictionary<string, int> _textureIndices = new(StringComparer.Ordinal);

    /// <summary>ECMA-363 9.8.5.1. A later declaration of the same name replaces the earlier one.</summary>
    private void ReadTextureDeclaration(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        uint height = reader.ReadU32();
        uint width = reader.ReadU32();
        byte type = reader.ReadU8();
        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw Invalid($"U3D texture '{name}' declares an impossible size of {width} x {height}.");
        }

        uint imageCount = reader.ReadU32();
        if (imageCount > (uint)(reader.RemainingBytes / MinimumImageFormatBytes))
        {
            throw Truncated($"U3D texture '{name}' declares {imageCount} images, more than its block can hold.");
        }

        var images = new ImageDeclaration[imageCount];
        for (int i = 0; i < images.Length; i++)
        {
            byte compression = reader.ReadU8();
            byte channels = reader.ReadU8();
            ushort attributes = reader.ReadU16();
            bool external = (attributes & ImageIsExternal) != 0;
            if (external)
            {
                uint urlCount = reader.ReadU32();
                if (urlCount > (uint)(reader.RemainingBytes / 2))
                {
                    throw Truncated($"U3D texture '{name}' declares {urlCount} image locations, more than its block can hold.");
                }

                for (uint url = 0; url < urlCount; url++)
                {
                    reader.SkipString();
                }
            }
            else
            {
                // The byte count is advisory; the continuation blocks say how much data there is.
                reader.ReadU32();
            }

            images[i] = new ImageDeclaration
            {
                Encoding = compression is >= 1 and <= 4 ? (SceneImageEncoding)compression : SceneImageEncoding.Unknown,
                Channels = (SceneTextureChannels)(channels & 0x1F),
                External = external
            };
        }

        if (!_textures.ContainsKey(name))
        {
            _textureOrder.Add(name);
        }

        _textures[name] = new TextureDeclaration
        {
            Name = name,
            Width = (int)width,
            Height = (int)height,
            Channels = type switch
            {
                0x01 => SceneTextureChannels.Alpha,
                0x0E => SceneTextureChannels.Rgb,
                0x0F => SceneTextureChannels.Rgba,
                0x10 => SceneTextureChannels.Luminance,
                0x11 => SceneTextureChannels.Luminance | SceneTextureChannels.Alpha,
                _ => SceneTextureChannels.None
            },
            Images = images
        };
    }

    /// <summary>
    /// ECMA-363 9.8.5.2. Only notes where the image bytes are: the declaration they belong to may be
    /// replaced by a later one, so they are attached when the scene is built.
    /// </summary>
    private void ReadTextureContinuation(U3DBlock block)
    {
        U3DBitReader reader = OpenBlock(block);
        string name = ReadName(ref reader);
        uint image = reader.ReadU32();

        // Image data is never arithmetic-coded: it is the rest of the block as it stands.
        int consumed = reader.BytePosition;
        int length = block.DataLength - consumed;
        if (length <= 0)
        {
            return;
        }

        _textureChunkBytes += length;
        if (_textureChunkBytes > _limits.MaxTextureBytes)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The U3D file's texture images exceed {_limits.MaxTextureBytes} bytes.");
        }

        _textureChunks.Add((name, image, block.DataOffset + consumed, length));
    }

    private void BuildTextures(SceneBuilder builder)
    {
        foreach ((string textureName, uint image, int offset, int length) in _textureChunks)
        {
            if (_textures.TryGetValue(textureName, out TextureDeclaration? owner) && image < (uint)owner.Images.Length && !owner.Images[image].External)
            {
                (owner.Images[image].Chunks ??= []).Add((offset, length));
            }
            else
            {
                builder.Report(SceneDiagnosticSeverity.Warning, "texture-data-orphan", $"U3D image data for image {image} of texture '{textureName}' matches no declared image.");
            }
        }

        foreach (string textureName in _textureOrder)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            TextureDeclaration texture = _textures[textureName];

            var images = new SceneTextureImage[texture.Images.Length];
            for (int i = 0; i < images.Length; i++)
            {
                ImageDeclaration image = texture.Images[i];
                if (image.External)
                {
                    builder.Report(SceneDiagnosticSeverity.Warning, "texture-external", $"An image of U3D texture '{texture.Name}' is in an external file, which is not fetched.");
                }
                else if (image.Chunks is null)
                {
                    builder.Report(SceneDiagnosticSeverity.Warning, "texture-data-missing", $"U3D texture '{texture.Name}' has no data for image {i}.");
                }

                images[i] = new SceneTextureImage(image.Encoding, image.Channels, JoinChunks(image.Chunks));
            }

            _textureIndices[texture.Name] = builder.AddTexture(new SceneTexture(texture.Name, texture.Width, texture.Height, texture.Channels, images));
        }
    }

    /// <summary>An image in one block stays a slice of the file; one spread over several is copied together.</summary>
    private ReadOnlyMemory<byte> JoinChunks(List<(int Offset, int Length)>? chunks)
    {
        if (chunks is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (chunks.Count == 1)
        {
            return _data.Slice(chunks[0].Offset, chunks[0].Length);
        }

        long total = 0;
        foreach ((_, int length) in chunks)
        {
            total += length;
        }

        // Bounded by MaxTextureBytes when the chunks were noted, and by the file's own size.
        byte[] joined = GC.AllocateUninitializedArray<byte>(checked((int)total));
        int position = 0;
        foreach ((int offset, int length) in chunks)
        {
            _data.Span.Slice(offset, length).CopyTo(joined.AsSpan(position));
            position += length;
        }

        return joined;
    }
}
