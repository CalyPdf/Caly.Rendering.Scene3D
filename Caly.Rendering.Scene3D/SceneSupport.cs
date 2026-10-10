namespace Caly.Rendering.Scene3D;

/// <summary>Why a 3D file could not be read.</summary>
public enum Scene3DError
{
    /// <summary>The data ends before a structure is complete.</summary>
    Truncated = 0,

    /// <summary>A value is outside what the format allows, or structures contradict each other.</summary>
    InvalidValue = 1,

    /// <summary>The data is well formed but exceeds a <see cref="SceneLimits"/> value.</summary>
    LimitExceeded = 2,

    /// <summary>The data uses a feature the reader does not handle and cannot skip.</summary>
    Unsupported = 3
}

/// <summary>
/// The exception a reader throws for input it cannot turn into a <see cref="Scene"/>. No other exception
/// type is thrown for bad input; cancellation throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class Scene3DException : Exception
{
    /// <summary>The kind of failure.</summary>
    public Scene3DError Error { get; }

    /// <summary>Creates the exception.</summary>
    public Scene3DException(Scene3DError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Creates the exception with an inner exception.</summary>
    public Scene3DException(Scene3DError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }
}

/// <summary>
/// Upper bounds a reader enforces before allocating, so that hostile input cannot exhaust memory or time.
/// The defaults are far above anything found in real documents.
/// </summary>
public sealed record SceneLimits
{
    /// <summary>The default limits.</summary>
    public static SceneLimits Default { get; } = new();

    /// <summary>Most vertices in a scene.</summary>
    public long MaxVertices { get; init; } = 50_000_000;

    /// <summary>Most indices in a scene.</summary>
    public long MaxIndices { get; init; } = 150_000_000;

    /// <summary>Most nodes, after expanding nodes with several parents.</summary>
    public int MaxNodes { get; init; } = 1_000_000;

    /// <summary>Most meshes.</summary>
    public int MaxMeshes { get; init; } = 1_000_000;

    /// <summary>Most materials.</summary>
    public int MaxMaterials { get; init; } = 1_000_000;

    /// <summary>Most textures.</summary>
    public int MaxTextures { get; init; } = 100_000;

    /// <summary>Most bytes of encoded texture images.</summary>
    public long MaxTextureBytes { get; init; } = 1L << 30;

    /// <summary>Most bytes of scene buffers overall.</summary>
    public long MaxTotalBytes { get; init; } = 4L << 30;

    /// <summary>Deepest nesting of containers and of the node tree.</summary>
    public int MaxNestingDepth { get; init; } = 256;

    /// <summary>Longest name, in bytes.</summary>
    public int MaxStringLength { get; init; } = 65_535;

    /// <summary>
    /// Most bytes a file's compressed parts may inflate to, all together. A reader of a format with
    /// compressed sections stops inflating when this is reached.
    /// </summary>
    public long MaxInflatedBytes { get; init; } = 1L << 30;
}

/// <summary>How serious a <see cref="SceneDiagnostic"/> is.</summary>
public enum SceneDiagnosticSeverity
{
    /// <summary>Something worth knowing that loses nothing visible.</summary>
    Info = 0,

    /// <summary>Part of the source was skipped or approximated.</summary>
    Warning = 1
}

/// <summary>Something a reader skipped or approximated while building a scene.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">Stable identifier of the kind of event, for example <c>skipped-block</c>.</param>
/// <param name="Message">Detail of the first occurrence.</param>
/// <param name="Count">How many times it happened.</param>
public readonly record struct SceneDiagnostic(SceneDiagnosticSeverity Severity, string Code, string Message, int Count);

/// <summary>How the bytes of a <see cref="SceneTextureImage"/> are encoded.</summary>
public enum SceneImageEncoding : byte
{
    /// <summary>Not known.</summary>
    Unknown = 0,

    /// <summary>JPEG, 24-bit colour.</summary>
    Jpeg24 = 1,

    /// <summary>PNG.</summary>
    Png = 2,

    /// <summary>JPEG, 8-bit greyscale.</summary>
    Jpeg8 = 3,

    /// <summary>TIFF.</summary>
    Tiff = 4
}

/// <summary>The channels of a texture, or those one of its images supplies.</summary>
[Flags]
public enum SceneTextureChannels : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Alpha.</summary>
    Alpha = 0x01,

    /// <summary>Blue.</summary>
    Blue = 0x02,

    /// <summary>Green.</summary>
    Green = 0x04,

    /// <summary>Red.</summary>
    Red = 0x08,

    /// <summary>Luminance (red, green and blue together).</summary>
    Luminance = 0x10,

    /// <summary>Red, green and blue.</summary>
    Rgb = Red | Green | Blue,

    /// <summary>Red, green, blue and alpha.</summary>
    Rgba = Rgb | Alpha
}

/// <summary>One encoded image contributing channels to a texture. The bytes are not decoded.</summary>
public readonly struct SceneTextureImage
{
    /// <summary>The encoding of <see cref="Data"/>.</summary>
    public SceneImageEncoding Encoding { get; }

    /// <summary>The texture channels this image supplies.</summary>
    public SceneTextureChannels Channels { get; }

    /// <summary>
    /// The encoded image file. Empty when the source refers to an external file, which is never fetched.
    /// May be a slice of the buffer the scene was read from, which the scene therefore keeps alive.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Creates the image.</summary>
    public SceneTextureImage(SceneImageEncoding encoding, SceneTextureChannels channels, ReadOnlyMemory<byte> data)
    {
        Encoding = encoding;
        Channels = channels;
        Data = data;
    }
}

/// <summary>A texture: its size, its channels and the encoded images that compose it.</summary>
public sealed class SceneTexture
{
    /// <summary>The texture's name.</summary>
    public string Name { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The channels of the composed texture.</summary>
    public SceneTextureChannels Channels { get; }

    /// <summary>The encoded images, each supplying some of the channels.</summary>
    public IReadOnlyList<SceneTextureImage> Images { get; }

    /// <summary>Creates the texture.</summary>
    public SceneTexture(string name, int width, int height, SceneTextureChannels channels, IReadOnlyList<SceneTextureImage> images)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Width = width;
        Height = height;
        Channels = channels;
        Images = images ?? throw new ArgumentNullException(nameof(images));
    }
}
