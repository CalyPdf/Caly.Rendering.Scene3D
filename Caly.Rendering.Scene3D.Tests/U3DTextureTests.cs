using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Caly.Rendering.Scene3D.Tests.Idtf;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

public class U3DTextureTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Width and height stored in a PNG or JPEG file, read independently of the U3D declaration.</summary>
    private static (int Width, int Height) ImageSize(ReadOnlySpan<byte> image)
    {
        if (image.StartsWith(PngSignature))
        {
            return (BinaryPrimitives.ReadInt32BigEndian(image[16..]), BinaryPrimitives.ReadInt32BigEndian(image[20..]));
        }

        Assert.True(image.Length > 4 && image[0] == 0xFF && image[1] == 0xD8, "neither PNG nor JPEG");
        int position = 2;
        while (position + 9 < image.Length)
        {
            Assert.Equal(0xFF, image[position]);
            byte marker = image[position + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(image[(position + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(image[(position + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(image[(position + 5)..]));
            }

            position += 2 + length;
        }

        throw new InvalidDataException("JPEG without a frame header");
    }

    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void TexturesMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = U3DSceneGraphTests.Read(name);

        foreach (IdtfElement resource in idtf.Resources("TEXTURE"))
        {
            string textureName = resource.Text("RESOURCE_NAME");
            SceneTexture texture = Assert.Single(scene.Textures, t => t.Name == textureName);

            // The converter's defaults: an RGB texture in one JPEG image.
            string type = resource.TextOrNull("TEXTURE_IMAGE_TYPE") ?? "RGB";
            Assert.Equal(type == "RGBA" ? SceneTextureChannels.Rgba : SceneTextureChannels.Rgb, texture.Channels);
            Assert.Equal((int)(resource.NumberOrNull("TEXTURE_IMAGE_COUNT") ?? 1), texture.Images.Count);

            IdtfElement[] formats = resource.Find("IMAGE_FORMAT_LIST")?.All("IMAGE_FORMAT").OrderBy(f => int.Parse(f.Argument)).ToArray() ?? [];
            for (int i = 0; i < texture.Images.Count; i++)
            {
                SceneTextureImage image = texture.Images[i];
                Assert.False(image.Data.IsEmpty, $"{name}/{textureName}: image {i} has no data");

                if (i < formats.Length)
                {
                    Assert.Equal(formats[i].Text("COMPRESSION_TYPE") == "PNG" ? SceneImageEncoding.Png : SceneImageEncoding.Jpeg24, image.Encoding);

                    SceneTextureChannels channels = SceneTextureChannels.None;
                    channels |= formats[i].TextOrNull("ALPHA_CHANNEL") == "TRUE" ? SceneTextureChannels.Alpha : 0;
                    channels |= formats[i].TextOrNull("BLUE_CHANNEL") == "TRUE" ? SceneTextureChannels.Blue : 0;
                    channels |= formats[i].TextOrNull("GREEN_CHANNEL") == "TRUE" ? SceneTextureChannels.Green : 0;
                    channels |= formats[i].TextOrNull("RED_CHANNEL") == "TRUE" ? SceneTextureChannels.Red : 0;
                    Assert.Equal(channels, image.Channels);
                }

                if (image.Encoding == SceneImageEncoding.Png)
                {
                    Assert.True(image.Data.Span.StartsWith(PngSignature));
                }
                else
                {
                    Assert.Equal(SceneImageEncoding.Jpeg24, image.Encoding);
                    Assert.True(image.Data.Span.StartsWith<byte>([0xFF, 0xD8]));
                }

                Assert.Equal((texture.Width, texture.Height), ImageSize(image.Data.Span));
            }
        }

        Assert.DoesNotContain(scene.Diagnostics, d => d.Code.StartsWith("texture-", StringComparison.Ordinal) && d.Code != "texture-missing");
    }

    [Fact]
    public void AnImageInOneBlockIsASliceOfTheInput()
    {
        byte[] file = new TestU3D()
            .TextureDeclaration("T", 4, 2, 0x0F, (1, 0x0E, 5, null), (2, 0x01, 3, null))
            .TextureContinuation("T", 1, [9, 8, 7])
            .TextureContinuation("T", 0, [1, 2, 3, 4, 5])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneTexture texture = Assert.Single(scene.Textures);
        Assert.Equal("T", texture.Name);
        Assert.Equal(4, texture.Width);
        Assert.Equal(2, texture.Height);
        Assert.Equal(SceneTextureChannels.Rgba, texture.Channels);
        Assert.Equal(2, texture.Images.Count);

        Assert.Equal(SceneImageEncoding.Jpeg24, texture.Images[0].Encoding);
        Assert.Equal(SceneTextureChannels.Rgb, texture.Images[0].Channels);
        Assert.Equal<byte>([1, 2, 3, 4, 5], texture.Images[0].Data.ToArray());
        Assert.Equal(SceneImageEncoding.Png, texture.Images[1].Encoding);
        Assert.Equal(SceneTextureChannels.Alpha, texture.Images[1].Channels);
        Assert.Equal<byte>([9, 8, 7], texture.Images[1].Data.ToArray());

        Assert.True(MemoryMarshal.TryGetArray(texture.Images[0].Data, out ArraySegment<byte> segment));
        Assert.Same(file, segment.Array);
    }

    [Fact]
    public void AnImageSpreadOverSeveralBlocksIsJoinedInFileOrder()
    {
        byte[] file = new TestU3D()
            .TextureDeclaration("T", 1, 1, 0x10, (3, 0x10, 6, null))
            .TextureContinuation("T", 0, [1, 2])
            .TextureContinuation("Other", 0, [0xEE])
            .TextureContinuation("T", 0, [3])
            .TextureContinuation("T", 0, [4, 5, 6])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneTexture texture = Assert.Single(scene.Textures);
        Assert.Equal(SceneTextureChannels.Luminance, texture.Channels);
        Assert.Equal(SceneImageEncoding.Jpeg8, texture.Images[0].Encoding);
        Assert.Equal<byte>([1, 2, 3, 4, 5, 6], texture.Images[0].Data.ToArray());
        Assert.Contains(scene.Diagnostics, d => d.Code == "texture-data-orphan");
    }

    [Fact]
    public void AnExternalImageIsNeverFetched()
    {
        byte[] file = new TestU3D()
            .TextureDeclaration("T", 8, 8, 0x0E, (2, 0x0E, 0, ["http://example.invalid/a.png", "b.png"]), (2, 0x01, 2, null))
            .TextureContinuation("T", 1, [1, 2])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneTexture texture = Assert.Single(scene.Textures);
        Assert.Equal(2, texture.Images.Count);
        Assert.True(texture.Images[0].Data.IsEmpty);
        Assert.Equal<byte>([1, 2], texture.Images[1].Data.ToArray());
        Assert.Contains(scene.Diagnostics, d => d.Code == "texture-external" && d.Severity == SceneDiagnosticSeverity.Warning);
    }

    [Fact]
    public void DataForAnImageTheDeclarationLacksIsReported()
    {
        byte[] file = new TestU3D()
            .TextureDeclaration("T", 8, 8, 0x0E, (2, 0x0E, 1, null))
            .TextureContinuation("T", 5, [1])
            .ToArray();

        using Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(scene.Textures).Images[0].Data.IsEmpty);
        Assert.Contains(scene.Diagnostics, d => d.Code == "texture-data-orphan");
        Assert.Contains(scene.Diagnostics, d => d.Code == "texture-data-missing");
    }

    [Fact]
    public void TextureBytesAreBoundedByTheLimit()
    {
        byte[] file = new TestU3D()
            .TextureDeclaration("T", 8, 8, 0x0E, (2, 0x0E, 600, null))
            .TextureContinuation("T", 0, new byte[300])
            .TextureContinuation("T", 0, new byte[300])
            .ToArray();

        using (Scene scene = U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(600, scene.Textures[0].Images[0].Data.Length);
        }

        var e = Assert.Throws<Scene3DException>(() => U3DReader.Read(file, SceneLimits.Default with { MaxTextureBytes = 500 }, TestContext.Current.CancellationToken));
        Assert.Equal(Scene3DError.LimitExceeded, e.Error);
    }

    [Fact]
    public void AbsurdImageCountsAreRejectedBeforeAllocating()
    {
        byte[] images = new TestU3D()
            .Block(0xFFFFFF55, new TestU3D.Data().String("T").U32(8).U32(8).U8(0x0E).U32(0xFFFFFFF0))
            .ToArray();
        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => U3DReader.Read(images, cancellationToken: TestContext.Current.CancellationToken)).Error);

        byte[] urls = new TestU3D()
            .Block(0xFFFFFF55, new TestU3D.Data().String("T").U32(8).U32(8).U8(0x0E).U32(1).U8(2).U8(0x0E).U16(1).U32(0xFFFFFFF0))
            .ToArray();
        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => U3DReader.Read(urls, cancellationToken: TestContext.Current.CancellationToken)).Error);

        byte[] size = new TestU3D()
            .TextureDeclaration("T", 0x90000000, 8, 0x0E)
            .ToArray();
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => U3DReader.Read(size, cancellationToken: TestContext.Current.CancellationToken)).Error);
    }
}
