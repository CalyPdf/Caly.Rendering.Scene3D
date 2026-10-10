using System.Buffers.Binary;
using Caly.Rendering.Scene3D.Prc.Container;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The outer layout of a PRC file: headers, offsets, compressed sections and the limit on inflation.
/// </summary>
public class PrcContainerTests(ITestOutputHelper output)
{
    private static byte[] Pattern(int length, int seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + seed) ^ (i >> 3));
        }

        return bytes;
    }

    private static PrcTestContainer TwoStructures()
    {
        var container = new PrcTestContainer { VersionForRead = 7094, AuthoringVersion = 15216, ModelFile = Pattern(40, 9) };
        var first = new PrcTestContainer.Structure { Id = [10, 11, 12, 13], Globals = Pattern(300, 1), Tree = Pattern(5000, 2), Tessellation = Pattern(70000, 3) };
        first.Files.Add([0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]);
        first.Files.Add([]);
        container.Structures.Add(first);
        container.Structures.Add(new PrcTestContainer.Structure { Id = [20, 21, 22, 23], Globals = Pattern(17, 4), Tree = Pattern(1, 5), Tessellation = Pattern(2, 6) });
        return container;
    }

    [Fact]
    public void TheHeadersAndEverySectionComeBack()
    {
        using PrcContainer container = PrcContainer.Open(TwoStructures().Build());

        Assert.Equal(7094u, container.VersionForRead);
        Assert.Equal(15216u, container.AuthoringVersion);
        Assert.Equal(2, container.FileStructureCount);
        Assert.True(container[0].HasId(10, 11, 12, 13));
        Assert.True(container[1].HasId(20, 21, 22, 23));
        Assert.False(container[1].HasId(10, 11, 12, 13));
        Assert.Equal(15216u, container[0].AuthoringVersion);
        Assert.Equal(2, container[0].Files.Length);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 }, container[0].Files[0].ToArray());
        Assert.True(container[0].Files[1].IsEmpty);
        Assert.Empty(container[1].Files);

        // Nothing is inflated until asked for.
        Assert.Equal(0, container.InflatedBytes);
        Assert.Equal(Pattern(40, 9), container.ModelFile.ToArray());
        Assert.Equal(Pattern(300, 1), container.Section(0, PrcSection.Globals).ToArray());
        Assert.Equal(Pattern(5000, 2), container.Section(0, PrcSection.Tree).ToArray());
        Assert.Equal(Pattern(70000, 3), container.Section(0, PrcSection.Tessellation).ToArray());
        Assert.Equal(Pattern(17, 4), container.Section(1, PrcSection.Globals).ToArray());
        Assert.Equal(Pattern(1, 5), container.Section(1, PrcSection.Tree).ToArray());
        Assert.Equal(Pattern(2, 6), container.Section(1, PrcSection.Tessellation).ToArray());
        long inflated = container.InflatedBytes;
        Assert.Equal(40 + 300 + 5000 + 70000 + 17 + 1 + 2, inflated);

        // Asking again inflates nothing; releasing and asking again does.
        Assert.Equal(Pattern(5000, 2), container.Section(0, PrcSection.Tree).ToArray());
        Assert.Equal(inflated, container.InflatedBytes);
        container.Release(0, PrcSection.Tree);
        Assert.Equal(Pattern(5000, 2), container.Section(0, PrcSection.Tree).ToArray());
        Assert.Equal(inflated + 5000, container.InflatedBytes);
    }

    [Fact]
    public void WhatIsNotPrcIsRefused()
    {
        Assert.False(PrcContainer.IsPrc("U3D\0"u8));
        Assert.False(PrcContainer.IsPrc("PR"u8));
        Assert.True(PrcContainer.IsPrc("PRC"u8));

        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => PrcContainer.Open("U3D\0 and more bytes than a header needs, by far"u8.ToArray())).Error);
        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => PrcContainer.Open(Array.Empty<byte>())).Error);
    }

    [Fact]
    public void AFileCutAnywhereFailsCleanly()
    {
        byte[] whole = TwoStructures().Build();
        for (int length = 0; length < whole.Length; length += length < 300 ? 1 : 97)
        {
            byte[] cut = whole[..length];
            try
            {
                using PrcContainer container = PrcContainer.Open(cut);
                _ = container.ModelFile.Length;
                for (int s = 0; s < container.FileStructureCount; s++)
                {
                    _ = container.Section(s, PrcSection.Globals).Length;
                    _ = container.Section(s, PrcSection.Tree).Length;
                    _ = container.Section(s, PrcSection.Tessellation).Length;
                }

                Assert.Fail($"{length} of {whole.Length} bytes opened and inflated completely.");
            }
            catch (Scene3DException)
            {
            }
        }
    }

    [Theory]
    [InlineData(24, 0xFFFFFFFFu)]   // structure header: after its own globals
    [InlineData(28, 0u)]            // globals: before the structure header
    [InlineData(32, 5u)]            // tree: before the globals
    [InlineData(36, 0xFFFFFFF0u)]   // tessellation: after the geometry
    [InlineData(40, 0xFFFFFFFFu)]   // geometry: outside the file
    [InlineData(44, 0xFFFFFFFFu)]   // extra geometry: outside the file
    [InlineData(44, 1u)]            // extra geometry: before the geometry
    [InlineData(20, 7u)]            // six sections expected
    public void OffsetsThatContradictTheFileAreRefused(int fieldOffsetInDescription, uint value)
    {
        byte[] file = TwoStructures().Build(out int[] descriptions);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(descriptions[1] + fieldOffsetInDescription), value);

        var e = Assert.Throws<Scene3DException>(() => PrcContainer.Open(file));
        Assert.Contains(e.Error, new[] { Scene3DError.InvalidValue, Scene3DError.Truncated });
    }

    [Fact]
    public void ACountOfStructuresTheFileCannotHoldIsRefused()
    {
        byte[] file = TwoStructures().Build();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(3 + 4 + 4 + 32), 0x40000000);

        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => PrcContainer.Open(file)).Error);
    }

    [Fact]
    public void ADamagedSectionFailsWhenItIsAskedForAndOnlyThen()
    {
        var builder = new PrcTestContainer { Compress = (structure, section, data) => section == "tree" ? [0x78, 0x9C, 1, 2, 3, 4, 5, 6, 7, 8] : PrcTestContainer.Zlib(data) };
        builder.Structures.Add(new PrcTestContainer.Structure { Globals = Pattern(50, 1) });
        using PrcContainer container = PrcContainer.Open(builder.Build());

        Assert.Equal(Pattern(50, 1), container.Section(0, PrcSection.Globals).ToArray());
        var e = Assert.Throws<Scene3DException>(() => container.Section(0, PrcSection.Tree).Length);
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    [Fact]
    public void TheExactGeometrySectionComesBackAndIsInflatedOnlyWhenAsked()
    {
        PrcTestContainer builder = TwoStructures();
        builder.Structures[0] = new PrcTestContainer.Structure { Id = [10, 11, 12, 13], Globals = Pattern(300, 1), Tree = Pattern(50, 2), Tessellation = Pattern(70, 3), Geometry = Pattern(900, 7) };
        using PrcContainer container = PrcContainer.Open(builder.Build());

        Assert.Equal(0, container.InflatedBytes);
        Assert.Equal(Pattern(900, 7), container.Section(0, PrcSection.ExactGeometry).ToArray());
        Assert.Equal(900, container.InflatedBytes);
        Assert.Equal(Pattern(70, 3), container.Section(0, PrcSection.Tessellation).ToArray());
        Assert.Equal(970, container.InflatedBytes);
    }

    [Fact]
    public void TheExactGeometrySectionCountsAgainstTheLimit()
    {
        var builder = new PrcTestContainer();
        builder.Structures.Add(new PrcTestContainer.Structure { Geometry = new byte[8 << 20] });
        using PrcContainer container = PrcContainer.Open(builder.Build(), new SceneLimits { MaxInflatedBytes = 1 << 20 });

        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => container.Section(0, PrcSection.ExactGeometry).Length).Error);
    }

    /// <summary>Review focus: a small file that inflates to a great deal.</summary>
    [Fact]
    public void InflationStopsAtTheLimit()
    {
        var builder = new PrcTestContainer();
        builder.Structures.Add(new PrcTestContainer.Structure { Globals = new byte[1000], Tree = new byte[64 << 20], Tessellation = new byte[1000] });
        byte[] file = builder.Build();
        Assert.True(file.Length < 1_000_000, "64 MB of zeros should compress to very little.");

        // Exactly enough is enough.
        using (PrcContainer container = PrcContainer.Open(file, new SceneLimits { MaxInflatedBytes = 1000 + (64 << 20) }))
        {
            Assert.Equal(1000, container.Section(0, PrcSection.Globals).Length);
            Assert.Equal(64 << 20, container.Section(0, PrcSection.Tree).Length);
            Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => container.Section(0, PrcSection.Tessellation).Length).Error);
        }

        // One megabyte allowed: the tree is refused long before 64 MB were inflated.
        using (PrcContainer container = PrcContainer.Open(file, new SceneLimits { MaxInflatedBytes = 1 << 20 }))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var e = Assert.Throws<Scene3DException>(() => container.Section(0, PrcSection.Tree).Length);
            Assert.Equal(Scene3DError.LimitExceeded, e.Error);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 << 20);
            Assert.Equal(1000, container.Section(0, PrcSection.Globals).Length);
        }
    }

    internal static IReadOnlyList<string> CorpusFiles()
    {
        var files = new List<string>();
        string?[] variables = [Environment.GetEnvironmentVariable("SCENE3D_PRC_CORPUS"), Environment.GetEnvironmentVariable("CALY_3D_SAMPLES")];
        foreach (string? variable in variables)
        {
            foreach (string folder in (variable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Directory.Exists(folder))
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "*.prc", SearchOption.AllDirectories));
                }
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Real PRC payloads (extracted from 3D PDFs), which are not part of the repository: the folders named
    /// by SCENE3D_PRC_CORPUS (';'-separated). Without any, the test has nothing to do and passes.
    /// </summary>
    [Fact]
    public void EveryPayloadOfTheCorpusOpensAndInflates()
    {
        foreach (string path in CorpusFiles())
        {
            byte[] file = File.ReadAllBytes(path);
            using PrcContainer container = PrcContainer.Open(file);
            long globals = 0, tree = 0, tessellation = 0;
            int files = 0;
            for (int s = 0; s < container.FileStructureCount; s++)
            {
                globals += container.Section(s, PrcSection.Globals).Length;
                tree += container.Section(s, PrcSection.Tree).Length;
                tessellation += container.Section(s, PrcSection.Tessellation).Length;
                files += container[s].Files.Length;
                Assert.True(container.Section(s, PrcSection.Globals).Length > 0);
                container.Release(s, PrcSection.Globals);
                container.Release(s, PrcSection.Tree);
                container.Release(s, PrcSection.Tessellation);
            }

            Assert.True(container.ModelFile.Length > 0);
            output.WriteLine($"{Path.GetFileName(path)}: {file.Length} bytes, read {container.VersionForRead} / authored {container.AuthoringVersion}, {container.FileStructureCount} structures, {files} files; inflated: model {container.ModelFile.Length}, globals {globals}, tree {tree}, tessellation {tessellation}");
        }
    }
}
