using BenchmarkDotNet.Attributes;
using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Benchmarks;

/// <summary>
/// <see cref="PrcReader.Read"/> over whole files: the *.prc found in the folders named by
/// SCENE3D_PRC_CORPUS (';'-separated; real payloads are not part of the repository). SCENE3D_PRC_BENCH
/// narrows them to the names that contain one of its ';'-separated parts. The scene is disposed inside
/// the measurement, so its pooled buffers are reused and "Allocated" is what reading costs besides them.
/// <para>
/// Compressed tessellations are decoded and meshed, so the figure of a payload that has them is the whole
/// cost of showing it.
/// </para>
/// <para>
/// Exact geometry (phase 5) is cut here too. The two cases that were set targets for it: the LandingGear
/// (SCENE3D_PRC_BENCH="LandingGear-V03042024_136": 1,133 faces, nothing else in the file; under 250 ms, 188 ms measured) and
/// blender_asymptote (the Asymptote corpus, "blender_asymptote": 17,545 tiny bodies; under 2 s, 92 ms measured). Uncompressed
/// bodies (phase B), once the triangulator stopped deciding cocircular points in BigInteger: 3D_PDF_PROSTEP-Advanced_Maintenance_Manual
/// 350 ms (was 2.6 s), Service_documentation 165 ms (was 3.4 s, 1.6 GB allocated), 301-105-pro-i 230 ms.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 8)]
public class PrcReadBenchmarks
{
    private const string None = "(no corpus: set SCENE3D_PRC_CORPUS)";

    private static readonly Dictionary<string, string> Paths = FindFiles();

    private byte[] _data = [];

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    public static IEnumerable<string> Files() => Paths.Count == 0 ? [None] : Paths.Keys.Order(StringComparer.OrdinalIgnoreCase);

    [GlobalSetup]
    public void Setup()
    {
        _data = Paths.TryGetValue(File, out string? path) ? System.IO.File.ReadAllBytes(path) : [];
    }

    [Benchmark]
    public int Read()
    {
        if (_data.Length == 0)
        {
            return 0;
        }

        using Scene scene = PrcReader.Read(_data);
        return scene.Indices.Length;
    }

    private static Dictionary<string, string> FindFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] only = (Environment.GetEnvironmentVariable("SCENE3D_PRC_BENCH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string folder in (Environment.GetEnvironmentVariable("SCENE3D_PRC_CORPUS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.prc", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(path);
                if (only.Length == 0 || only.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase)))
                {
                    // Short names keep the result table readable.
                    files.TryAdd(name.Length > 40 ? name[..40] : name, path);
                }
            }
        }

        return files;
    }
}

/// <summary>
/// Decoding the largest compressed tessellation of the corpus (SCENE3D_PRC_CORPUS) on its own, then decoding and
/// meshing it: what one big CAD part costs, in triangles a second (the plan's target is 2 million).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 10)]
public class PrcCompressedDecodeBenchmarks
{
    private PrcTess3DCompressed? _largest;

    [GlobalSetup]
    public void Setup()
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("SCENE3D_PRC_CORPUS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.prc", SearchOption.AllDirectories))
            {
                using PrcContainer container = PrcContainer.Open(System.IO.File.ReadAllBytes(path));
                PrcFile file = PrcFileReader.Read(container);
                foreach (PrcFileStructureData structure in file.FileStructures)
                {
                    foreach (PrcTess tess in structure.Tessellation.Tess)
                    {
                        if (tess.Id is PrcTess3DCompressed compressed && (_largest is null || compressed.TriangleCount > _largest.TriangleCount))
                        {
                            _largest = compressed;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Triangles of the tessellation measured: divide it by the mean to get triangles a second.</summary>
    [Benchmark]
    public int Decode()
    {
        if (_largest is null)
        {
            return 0;
        }

        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(_largest);
        return geometry.TriangleCount;
    }

    [Benchmark]
    public int DecodeAndMesh()
    {
        if (_largest is null)
        {
            return 0;
        }

        using var builder = new SceneBuilder();
        int name = builder.AddName("mesh");
        PrcCompressedMesher.TryAddMesh(builder, _largest, name, out _, out _);
        return builder.MeshCount;
    }
}
/// <summary>
/// The two values PRC sections are mostly made of, decoded from prc-rs's test vectors (in the test
/// project): variable-length integers and compressed doubles. Per value, since everything else a
/// section holds is read through these.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class PrcBitReaderBenchmarks
{
    private const int IntegerRecords = 66_002, DoubleRecords = 6_002;

    private byte[] _integers = [];
    private byte[] _doubles = [];

    [GlobalSetup]
    public void Setup()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string data = Path.Combine(folder.FullName, "Caly.Rendering.Scene3D.Tests", "TestData", "Prc");
            if (Directory.Exists(data))
            {
                _integers = System.IO.File.ReadAllBytes(Path.Combine(data, "read_ints.bin"));
                _doubles = System.IO.File.ReadAllBytes(Path.Combine(data, "read_doubles.bin"));
                return;
            }
        }

        throw new InvalidOperationException("The test vectors of the PRC bit reader were not found.");
    }

    /// <summary>Per record: an unsigned integer, two integers, 32 plain bits.</summary>
    [Benchmark(OperationsPerInvoke = IntegerRecords * 4)]
    public long Integers()
    {
        var reader = new PrcBitReader(_integers);
        long sum = 0;
        for (int i = 0; i < IntegerRecords; i++)
        {
            sum += reader.ReadUnsignedInteger();
            sum += reader.ReadInteger();
            sum += reader.ReadInteger();
            sum += reader.ReadBits(32);
        }

        return sum;
    }

    /// <summary>Per record: an unsigned integer and two doubles; the figure is per value.</summary>
    [Benchmark(OperationsPerInvoke = DoubleRecords * 3)]
    public double Doubles()
    {
        var reader = new PrcBitReader(_doubles);
        double sum = 0;
        for (int i = 0; i < DoubleRecords; i++)
        {
            sum += reader.ReadUnsignedInteger();
            sum += reader.ReadDouble();
            sum += reader.ReadDouble();
        }

        return sum;
    }
}
