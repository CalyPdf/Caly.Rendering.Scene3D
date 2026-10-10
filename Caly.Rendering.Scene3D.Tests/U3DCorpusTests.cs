using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Real-world U3D payloads (extracted from 3D PDFs), which are not part of the repository. The folders
/// named by SCENE3D_U3D_CORPUS (';'-separated) and CALY_3D_SAMPLES are searched for *.u3d; without any,
/// the test has nothing to do and passes.
/// </summary>
public class U3DCorpusTests(ITestOutputHelper output)
{
    internal static IReadOnlyList<string> CorpusFiles()
    {
        var files = new List<string>();
        string?[] variables = [Environment.GetEnvironmentVariable("SCENE3D_U3D_CORPUS"), Environment.GetEnvironmentVariable("CALY_3D_SAMPLES")];
        foreach (string? variable in variables)
        {
            foreach (string folder in (variable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Directory.Exists(folder))
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "*.u3d", SearchOption.AllDirectories));
                }
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// How much of the file's mesh detail its base meshes hold, read straight from the declaration and base
    /// blocks: the rest is in progressive blocks, which the reader does not decode.
    /// </summary>
    private static string DetailInBase(byte[] file)
    {
        long declared = 0, inBase = 0;
        foreach (RawBlocks.Block block in RawBlocks.Enumerate(file))
        {
            if (block.Type is not (RawBlocks.MeshDeclaration or RawBlocks.BaseMesh))
            {
                continue;
            }

            // Name, chain index, then for a declaration the attributes, and the face count.
            ReadOnlySpan<byte> data = RawBlocks.Data(file, block);
            int offset = 2 + BinaryPrimitives.ReadUInt16LittleEndian(data) + 4;
            if (block.Type == RawBlocks.MeshDeclaration)
            {
                declared += BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            }
            else
            {
                inBase += BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{inBase} / {declared} ({(declared == 0 ? 100 : 100.0 * inBase / declared):F1} %)");
    }

    [Fact]
    public void EveryCorpusPayloadDecodesToAConsistentScene()
    {
        IReadOnlyList<string> files = CorpusFiles();
        if (files.Count == 0)
        {
            output.WriteLine("No corpus: set SCENE3D_U3D_CORPUS to a folder of .u3d files.");
            return;
        }

        var summary = new StringBuilder();
        summary.AppendLine("file | bytes | ms | nodes | model nodes with mesh / all | meshes | vertices | triangles | base faces / declared faces (all meshes in the file) | materials | textures | diagnostics");
        foreach (string file in files)
        {
            byte[] data = File.ReadAllBytes(file);
            var watch = Stopwatch.StartNew();
            using Scene scene = U3DReader.Read(data, cancellationToken: TestContext.Current.CancellationToken);
            watch.Stop();

            SceneInvariants.Check(scene);

            SceneNode[] nodes = scene.Nodes.ToArray();
            int models = nodes.Count(n => n.Kind == SceneNodeKind.Model);
            int withMesh = nodes.Count(n => n.Mesh >= 0);
            string diagnostics = string.Join(", ", scene.Diagnostics.Select(d => $"{d.Code} x{d.Count}"));
            summary.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileName(file)} | {data.Length} | {watch.Elapsed.TotalMilliseconds:F1} | {nodes.Length} | {withMesh} / {models} | {scene.Meshes.Length} | {scene.Positions.Length / 3} | {scene.Indices.Length / 3} | {DetailInBase(data)} | {scene.Materials.Length} | {scene.Textures.Count} | {diagnostics}"));

            // A real file shows something: geometry, placed in the scene, inside finite bounds. Files with no
            // model node at all (stubs) are the only ones exempt, and a file holding RH meshes is never one.
            bool hasRHMeshes = data.AsSpan().IndexOf("RHAdobeMeshResource"u8) >= 0;
            if (hasRHMeshes)
            {
                Assert.True(models > 0, $"{file}: holds RHAdobeMeshResource data but has no model node");
                Assert.True(scene.Meshes.Length > 0, $"{file}: holds RHAdobeMeshResource data but no mesh was decoded");
            }

            if (models > 0)
            {
                Assert.True(withMesh > 0, $"{file}: no model node has a mesh");
                SceneBounds bounds = scene.ComputeWorldBounds();
                Assert.False(bounds.IsEmpty);
                Assert.True(float.IsFinite(bounds.Min.Length()) && float.IsFinite(bounds.Max.Length()), $"{file}: world bounds are not finite");
            }
        }

        output.WriteLine(summary.ToString());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "u3d-corpus-summary.txt"), summary.ToString());
    }

    /// <summary>The fuzzing of U3DFuzzTests, over the real payloads: bigger, compressed, more varied.</summary>
    [Fact]
    public void MutatedCorpusPayloadsGiveASceneOrADeliberateError()
    {
        const int mutationsPerFile = 25;
        var failures = new List<string>();
        foreach (string file in CorpusFiles())
        {
            byte[] original = File.ReadAllBytes(file);
            U3DFuzzTests.Outcome(original, TimeSpan.MaxValue, long.MaxValue);

            for (int mutation = 0; mutation < mutationsPerFile; mutation++)
            {
                int seed = original.Length * 31 + mutation;
                byte[] mutated = U3DFuzzTests.Mutate(original, seed, out string description);
                if (U3DFuzzTests.Outcome(mutated, TimeSpan.FromSeconds(10), 1L << 30) is string problem)
                {
                    failures.Add($"{Path.GetFileName(file)} mutation {mutation} (seed {seed}, {description}): {problem}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} mutations misbehaved.\n{string.Join("\n", failures.Take(5))}");
    }
}
