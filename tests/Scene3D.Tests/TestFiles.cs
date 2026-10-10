namespace Scene3D.Tests;

/// <summary>
/// Access to the U3D sample scenes (`.u3d` with its `.idtf` text source) copied next to the test assembly.
/// </summary>
internal static class TestFiles
{
    public static string ScenesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "TestData", "TestScenes");

    public static string ScenePath(string fileName) => Path.Combine(ScenesDirectory, fileName);

    public static byte[] Scene(string fileName) => File.ReadAllBytes(ScenePath(fileName));

    /// <summary>Names of all sample scenes, without extension, in a stable order.</summary>
    public static IReadOnlyList<string> SceneNames { get; } = Directory.Exists(ScenesDirectory)
        ? Directory.EnumerateFiles(ScenesDirectory, "*.u3d").Select(p => Path.GetFileNameWithoutExtension(p)!).Order(StringComparer.Ordinal).ToArray()
        : [];

    public static IEnumerable<object[]> AllScenes() => SceneNames.Select(n => new object[] { n });

    /// <summary>Folder of real-world 3D PDFs and extracted payloads, or null when CALY_3D_SAMPLES is not set.</summary>
    public static string? CorpusDirectory
    {
        get
        {
            string? root = Environment.GetEnvironmentVariable("CALY_3D_SAMPLES");
            return !string.IsNullOrEmpty(root) && Directory.Exists(root) ? root : null;
        }
    }
}