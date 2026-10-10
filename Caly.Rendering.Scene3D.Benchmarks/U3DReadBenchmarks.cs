using BenchmarkDotNet.Attributes;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Benchmarks;

/// <summary>
/// <see cref="U3DReader.Read"/> over whole files: the sample scene with the largest base mesh, and every
/// *.u3d found in the folders named by SCENE3D_U3D_CORPUS (';'-separated; real payloads are not part of
/// the repository). The scene is disposed inside the measurement, so its pooled buffers are reused and
/// "Allocated" is what reading costs besides them.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class U3DReadBenchmarks
{
    private static readonly Dictionary<string, string> Paths = FindFiles();

    private byte[] _data = [];

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    public static IEnumerable<string> Files() => Paths.Keys.Order(StringComparer.OrdinalIgnoreCase);

    [GlobalSetup]
    public void Setup()
    {
        _data = System.IO.File.ReadAllBytes(Paths[File]);
    }

    [Benchmark]
    public int Read()
    {
        using Scene scene = U3DReader.Read(_data);
        return scene.Indices.Length;
    }

    private static Dictionary<string, string> FindFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The sample scenes live in the test project; find the repository from wherever this runs.
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string sample = Path.Combine(folder.FullName, "Caly.Rendering.Scene3D.Tests", "TestData", "TestScenes", "plane64x64_base.u3d");
            if (System.IO.File.Exists(sample))
            {
                files[Path.GetFileName(sample)] = sample;
                break;
            }
        }

        foreach (string folder in (Environment.GetEnvironmentVariable("SCENE3D_U3D_CORPUS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(folder))
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*.u3d", SearchOption.AllDirectories))
                {
                    files.TryAdd(Path.GetFileName(path), path);
                }
            }
        }

        return files;
    }
}
