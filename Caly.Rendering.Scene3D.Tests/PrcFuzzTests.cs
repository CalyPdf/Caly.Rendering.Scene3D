using System.Buffers.Binary;
using System.Diagnostics;
using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Container;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Deterministic mutation fuzzing of the PRC reader: whatever is done to a file, reading it gives a
/// consistent scene or a <see cref="Scene3DException"/> raised on purpose — never another exception (the
/// reader wraps those, and the wrapped one shows as <see cref="Exception.InnerException"/>), a hang or a
/// runaway allocation.
/// <para>
/// A PRC file is compressed sections, so a change to its bytes mostly breaks the compression and never
/// reaches the entity readers. Most mutations are therefore made to the inflated sections, which are
/// then packed into a file again: bits flipped, removed or inserted (everything after them then lands on
/// other fields, as after a wrong count), values overwritten, parts copied over others.
/// </para>
/// </summary>
public class PrcFuzzTests(ITestOutputHelper output)
{
    /// <summary>Mutations tried per file; SCENE3D_FUZZ_MUTATIONS raises it for a deeper run.</summary>
    private static readonly int Mutations = int.TryParse(Environment.GetEnvironmentVariable("SCENE3D_FUZZ_MUTATIONS"), out int n) && n > 0 ? n : 150;

    private static readonly SceneLimits Limits = SceneLimits.Default with { MaxTotalBytes = 256L << 20, MaxTextureBytes = 64L << 20, MaxInflatedBytes = 256L << 20, MaxNodes = 100_000 };

    internal static int SeedOf(string name, int mutation)
    {
        int hash = 17;
        foreach (char c in name)
        {
            hash = unchecked(hash * 31 + c);
        }

        return unchecked(hash * 1009 + mutation);
    }

    /// <summary>A file as the parts a mutation works on: its inflated sections, and what else it takes to pack them again.</summary>
    internal sealed class Unpacked
    {
        public required uint VersionForRead { get; init; }

        public required uint AuthoringVersion { get; init; }

        public required byte[] ModelFile { get; init; }

        public required List<(uint[] Id, byte[][] Files, byte[] Globals, byte[] Tree, byte[] Tessellation, byte[] Geometry)> Structures { get; init; }

        public static Unpacked Of(byte[] file)
        {
            using PrcContainer container = PrcContainer.Open(file);
            var structures = new List<(uint[], byte[][], byte[], byte[], byte[], byte[])>();
            for (int i = 0; i < container.FileStructureCount; i++)
            {
                PrcFileStructureInfo info = container[i];
                structures.Add((
                    [info.Id0, info.Id1, info.Id2, info.Id3],
                    [.. info.Files.Select(f => f.ToArray())],
                    container.Section(i, PrcSection.Globals).ToArray(),
                    container.Section(i, PrcSection.Tree).ToArray(),
                    container.Section(i, PrcSection.Tessellation).ToArray(),
                    container.Section(i, PrcSection.ExactGeometry).ToArray()));
            }

            return new Unpacked { VersionForRead = container.VersionForRead, AuthoringVersion = container.AuthoringVersion, ModelFile = container.ModelFile.ToArray(), Structures = structures };
        }

        /// <summary>Number of sections a mutation can choose from.</summary>
        public int SectionCount => Structures.Count * 4 + 1;

        /// <summary>The file again, with one section replaced.</summary>
        public byte[] Pack(int section, byte[] replacement)
        {
            var container = new PrcTestContainer { VersionForRead = VersionForRead, AuthoringVersion = AuthoringVersion, ModelFile = section == SectionCount - 1 ? replacement : ModelFile };
            for (int i = 0; i < Structures.Count; i++)
            {
                (uint[] id, byte[][] files, byte[] globals, byte[] tree, byte[] tessellation, byte[] geometry) = Structures[i];
                var structure = new PrcTestContainer.Structure
                {
                    Id = id,
                    Globals = section == i * 4 ? replacement : globals,
                    Tree = section == i * 4 + 1 ? replacement : tree,
                    Tessellation = section == i * 4 + 2 ? replacement : tessellation,
                    Geometry = section == i * 4 + 3 ? replacement : geometry,
                };
                structure.Files.AddRange(files);
                container.Structures.Add(structure);
            }

            return container.Build();
        }

        public byte[] Section(int section)
        {
            if (section == SectionCount - 1)
            {
                return ModelFile;
            }

            (_, _, byte[] globals, byte[] tree, byte[] tessellation, byte[] geometry) = Structures[section / 4];
            return (section % 4) switch { 0 => globals, 1 => tree, 2 => tessellation, _ => geometry };
        }
    }

    private static bool GetBit(byte[] data, long bit) => (data[bit >> 3] & (0x80 >> (int)(bit & 7))) != 0;

    private static void SetBit(byte[] data, long bit, bool value)
    {
        if (value)
        {
            data[bit >> 3] |= (byte)(0x80 >> (int)(bit & 7));
        }
        else
        {
            data[bit >> 3] &= (byte)~(0x80 >> (int)(bit & 7));
        }
    }

    /// <summary>A section with one thing done to it.</summary>
    internal static byte[] MutateSection(byte[] original, Random random, out string description)
    {
        if (original.Length == 0)
        {
            description = "empty section left alone";
            return original;
        }

        byte[] data = (byte[])original.Clone();
        long bits = data.Length * 8L;

        // Entities come one after the other: the start of a section is headers and counts that decide
        // everything after them, so half the mutations aim there.
        long Position() => random.Next(2) == 0 ? random.NextInt64(Math.Min(bits, 4096)) : random.NextInt64(bits);

        switch (random.Next(8))
        {
            case 0:
            {
                int flips = random.Next(1, 6);
                for (int i = 0; i < flips; i++)
                {
                    long at = Position();
                    SetBit(data, at, !GetBit(data, at));
                }

                description = $"{flips} bit flips";
                break;
            }

            case 1:
            {
                // Bits removed: what follows moves up, as after a count that was one too small.
                long at = Position();
                int count = random.Next(1, 40);
                for (long i = at; i + count < bits; i++)
                {
                    SetBit(data, i, GetBit(original, i + count));
                }

                description = $"{count} bits removed at {at}";
                break;
            }

            case 2:
            {
                // Bits inserted: what follows moves down.
                long at = Position();
                int count = random.Next(1, 40);
                for (long i = bits - 1; i >= at + count; i--)
                {
                    SetBit(data, i, GetBit(original, i - count));
                }

                for (long i = at; i < Math.Min(at + count, bits); i++)
                {
                    SetBit(data, i, random.Next(2) == 0);
                }

                description = $"{count} bits inserted at {at}";
                break;
            }

            case 3:
            {
                // A number written over the bits, in the format's own encoding: a byte at a time, each
                // announced by a set bit. Large values where a count or an index is expected.
                long at = Position();
                uint value = random.Next(4) switch { 0 => 0xFFFFFFFF, 1 => 0x7FFFFFFF, 2 => (uint)random.Next(1, 1 << 20), _ => (uint)random.Next(256) };
                long i = at;
                for (uint v = value; v != 0 && i + 9 < bits; v >>= 8)
                {
                    SetBit(data, i++, true);
                    for (int b = 7; b >= 0; b--)
                    {
                        SetBit(data, i++, ((v >> b) & 1) != 0);
                    }
                }

                if (i < bits)
                {
                    SetBit(data, i, false);
                }

                description = $"number {value} written at bit {at}";
                break;
            }

            case 4:
            {
                int length = random.Next(data.Length);
                data = data[..length];
                description = $"cut to {length} bytes";
                break;
            }

            case 5:
            {
                int start = random.Next(data.Length);
                int length = Math.Min(random.Next(1, 33), data.Length - start);
                random.NextBytes(data.AsSpan(start, length));
                description = $"{length} random bytes at {start}";
                break;
            }

            case 6:
            {
                int start = random.Next(data.Length);
                int length = Math.Min(random.Next(1, 257), data.Length - start);
                data.AsSpan(start, length).Fill(random.Next(2) == 0 ? (byte)0 : (byte)0xFF);
                description = $"{length} bytes of one value at {start}";
                break;
            }

            default:
            {
                int length = random.Next(1, Math.Min(2048, data.Length) + 1);
                int from = random.Next(data.Length - length + 1), to = random.Next(data.Length - length + 1);
                Array.Copy(original, from, data, to, length);
                description = $"{length} bytes copied from {from} to {to}";
                break;
            }
        }

        return data;
    }

    /// <summary>A whole file with one thing done to its bytes: its headers, offsets and compressed streams.</summary>
    internal static byte[] MutateFile(byte[] original, Random random, out string description)
    {
        byte[] data = (byte[])original.Clone();
        switch (random.Next(4))
        {
            case 0:
            {
                // The header: versions, counts, section offsets.
                int offset = random.Next(Math.Min(data.Length - 4, 256));
                uint value = random.Next(3) switch { 0 => 0xFFFFFFFF, 1 => (uint)random.Next(data.Length * 2), _ => (uint)random.Next(16) };
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
                description = $"u32 at {offset} = {value}";
                break;
            }

            case 1:
            {
                int length = random.Next(data.Length);
                data = data[..length];
                description = $"file cut to {length}";
                break;
            }

            case 2:
            {
                int flips = random.Next(1, 9);
                for (int i = 0; i < flips; i++)
                {
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                }

                description = $"{flips} bit flips in the file";
                break;
            }

            default:
            {
                int length = random.Next(1, Math.Min(1024, data.Length));
                int from = random.Next(data.Length - length + 1), to = random.Next(data.Length - length + 1);
                Array.Copy(original, from, data, to, length);
                description = $"{length} file bytes copied from {from} to {to}";
                break;
            }
        }

        return data;
    }

    /// <summary>Reads possibly corrupt data and returns what is wrong with the outcome, or null.</summary>
    internal static string? Outcome(byte[] data, TimeSpan timeBound, long allocationBound, out bool scene)
    {
        // Reading is deterministic, so a hang is slow every time; a shared CI runner that stalls once is not.
        string? problem = Attempt(data, timeBound, allocationBound, out scene, out bool slow);
        return slow ? Attempt(data, timeBound, allocationBound, out scene, out _) : problem;
    }

    private static string? Attempt(byte[] data, TimeSpan timeBound, long allocationBound, out bool scene, out bool slow)
    {
        scene = false;
        slow = false;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        try
        {
            using Scene read = PrcReader.Read(data, Limits);
            watch.Stop();
            SceneInvariants.Check(read);
            scene = true;
        }
        catch (Scene3DException e) when (e.InnerException is null or InvalidDataException)
        {
            // A deliberate error; a broken compressed stream is reported with the inflater's own.
            watch.Stop();
        }
        catch (Exception e)
        {
            return e.ToString();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (watch.Elapsed > timeBound)
        {
            slow = true;
            return $"took {watch.Elapsed.TotalSeconds:F1} s";
        }

        return allocated > allocationBound ? $"allocated {allocated >> 20} MB" : null;
    }

    public static TheoryData<string> Seeds => new() { "simple", "old", "mixed", "exact", "markup" };

    /// <summary>
    /// Whole small files written by the tests: a plain one, one of an old version, one with every kind of
    /// tessellation (so with parts this reader leaves out), one whose only part has no mesh at all.
    /// </summary>
    internal static byte[] Seed(string name)
    {
        switch (name)
        {
            case "simple":
                return PrcTestFile.Simple().Build();

            case "old":
            {
                var file = new PrcTestFile { AuthoringVersion = 7000, UserDataBits = 21 };
                file.Colors.Add([0.2, 0.4, 0.6]);
                file.Styles.Add((1, 200));
                file.Tessellations.Add(PrcTestFile.Tessellation(7000, [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0], [0, 3, 6, 3, 9, 6]));
                file.Parts.Add(new PrcTestFile.Part("p", "sheet", 1, BiasedStyle: 1));
                file.Occurrences.Add(new PrcTestFile.Occurrence("definition", BiasedPart: 1));
                file.Occurrences.Add(new PrcTestFile.Occurrence("instance", BiasedPrototype: 1, Translation: [0, 0, 5]));
                file.Occurrences.Add(new PrcTestFile.Occurrence("root", Children: [1, 1]));
                return file.Build();
            }

            case "markup":
            {
                // Every kind of entity the reader draws or leaves out, and a markup that names a tessellation that is not there.
                PrcTestFile file = PrcTestFile.Simple();
                file.Tessellations.Add(new PrcTestFile.MarkupTessellation()
                    .Color(0).Polyline(0, 0, 0, 1, 0, 0, 1, 1, 0).Color(3).Triangles(0, 0, 1, 1, 0, 1, 0, 1, 1).Quads(0, 0, 2, 1, 0, 2, 1, 1, 2, 0, 1, 2)
                    .Other(14, [4, 2], 0).Block(6, [1, 2, 3], inside => inside.Polyline(9, 9, 9, 8, 8, 8)).Color(300).Polyline(2, 2, 2, 3, 3, 3)
                    .Write());
                file.Occurrences[^1] = file.Occurrences[^1] with { Markups = [new PrcTestFile.Markup("Note", 2), new PrcTestFile.Markup("Lost", 7)] };
                return file.Build();
            }

            case "exact":
            {
                var file = new PrcTestFile();
                file.Parts.Add(new PrcTestFile.Part("solid part", "solid", 0));
                file.Occurrences.Add(new PrcTestFile.Occurrence("root", BiasedPart: 1));
                return file.Build();
            }

            default:
            {
                PrcTestFile file = PrcTestFile.Simple();
                file.Tessellations.Add(PrcWalkTests.Compressed(8137, hasFaces: true, recalculateNormals: false, texture: true));
                file.Tessellations.Add(PrcWalkTests.Compressed(8137, hasFaces: false, recalculateNormals: true, texture: false));
                file.Tessellations.Add(PrcWalkTests.Wire(perSegment: false, secondEdgeCloses: true, colours: 6));
                file.Parts.Add(new PrcTestFile.Part("compressed part", "compressed", 2));
                file.Parts.Add(new PrcTestFile.Part("wire part", "wire", 4, BiasedStyle: 2));
                file.Occurrences.Insert(0, new PrcTestFile.Occurrence("missing", BiasedPart: 2));
                file.Occurrences.Insert(1, new PrcTestFile.Occurrence("lines", BiasedPart: 3));
                file.Occurrences[2] = file.Occurrences[2] with { BiasedPart = 1 };
                file.Occurrences[4] = new PrcTestFile.Occurrence("assembly", Children: [0, 1, 2, 3]);
                return file.Build();
            }
        }
    }

    /// <summary>
    /// A damaged deflate stream is a deliberate error on every runtime: where the framework reports it as a
    /// ZLibException (net8) and not an InvalidDataException, it used to come out as an unexpected failure.
    /// This is the 17,760th mutation of the "mixed" seed, found by a deep run (SCENE3D_FUZZ_MUTATIONS=3000; on net8
    /// it copies 308 file bytes from 298 to 154).
    /// </summary>
    [Fact]
    public void ADamagedDeflateStreamIsADeliberateError()
    {
        byte[] original = Seed("mixed");
        Outcome(original, TimeSpan.FromSeconds(30), long.MaxValue, out _);

        var random = new Random(SeedOf("mixed", 17759));
        Assert.Equal(0, random.Next(5));
        byte[] mutated = MutateFile(original, random, out _);

        Scene3DException e = Assert.Throws<Scene3DException>(() => PrcReader.Read(mutated, Limits, TestContext.Current.CancellationToken));
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
        Assert.True(e.InnerException is null or InvalidDataException, $"unexpected inner exception: {e.InnerException}");
        Assert.Null(Outcome(mutated, TimeSpan.FromSeconds(2), 64L << 20, out _));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheSeedFilesThemselvesAreRead(string name)
    {
        using Scene scene = PrcReader.Read(Seed(name), cancellationToken: TestContext.Current.CancellationToken);
        SceneInvariants.Check(scene);
        Assert.Equal(name != "exact", scene.Meshes.Length > 0);
        Assert.Equal(name != "mixed", PrcReader.IsComplete(scene));
    }

    /// <summary>
    /// Writes the seed files into the folder SCENE3D_PRC_SEED_FOLDER names, as prc_&lt;name&gt;.prc, for
    /// projects that test with PRC data and cannot write it themselves. Does nothing otherwise.
    /// </summary>
    [Fact]
    public void TheSeedFilesAreWrittenOutOnRequest()
    {
        string? folder = Environment.GetEnvironmentVariable("SCENE3D_PRC_SEED_FOLDER");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        foreach (string name in (string[])["simple", "mixed", "exact"])
        {
            File.WriteAllBytes(Path.Combine(folder, $"prc_{name}.prc"), Seed(name));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AMutatedFileGivesASceneOrADeliberateError(string name)
    {
        byte[] original = Seed(name);
        Unpacked unpacked = Unpacked.Of(original);
        Outcome(original, TimeSpan.FromSeconds(30), long.MaxValue, out _);   // warm the pools and the JIT

        var failures = new List<string>();
        int scenes = 0;
        int count = Mutations * 8;                                            // these files are read in well under a millisecond
        for (int mutation = 0; mutation < count; mutation++)
        {
            int seed = SeedOf(name, mutation);
            var random = new Random(seed);
            byte[] mutated;
            string description;
            if (random.Next(5) == 0)
            {
                mutated = MutateFile(original, random, out description);
            }
            else
            {
                int section = random.Next(unpacked.SectionCount);
                mutated = unpacked.Pack(section, MutateSection(unpacked.Section(section), random, out description));
                description = $"section {section}: {description}";
            }

            if (Outcome(mutated, TimeSpan.FromSeconds(2), 64L << 20, out bool scene) is string problem)
            {
                failures.Add($"mutation {mutation} (seed {seed}, {description}): {problem}");
            }

            scenes += scene ? 1 : 0;
        }

        output.WriteLine($"{name}: {count} mutations, {scenes} still gave a scene, {failures.Count} misbehaved");
        Assert.True(failures.Count == 0, $"{name}: {failures.Count} of {count} mutations misbehaved.\n{string.Join("\n", failures.Take(5))}");

        // The mutations are not all fatal, and not all harmless: both ways are exercised.
        Assert.InRange(scenes, 1, count - 1);
    }

    /// <summary>
    /// The same on real payloads (SCENE3D_PRC_CORPUS; nothing to do without it): the smaller ones, so
    /// that the run stays short. Bounds are set from what reading the unchanged payload takes.
    /// </summary>
    [Fact]
    public void AMutatedPayloadOfTheCorpusGivesASceneOrADeliberateError()
    {
        var failures = new List<string>();
        int total = 0, scenes = 0;
        var watch = Stopwatch.StartNew();
        foreach (string path in PrcContainerTests.CorpusFiles().Where(p => new FileInfo(p).Length < 2_500_000))
        {
            string name = Path.GetFileName(path);
            byte[] original = File.ReadAllBytes(path);
            Unpacked unpacked = Unpacked.Of(original);
            Outcome(original, TimeSpan.FromMinutes(1), long.MaxValue, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();
            var clean = Stopwatch.StartNew();
            Outcome(original, TimeSpan.FromMinutes(1), long.MaxValue, out _);
            TimeSpan timeBound = TimeSpan.FromSeconds(2) + clean.Elapsed * 10;
            long allocationBound = (64L << 20) + (GC.GetAllocatedBytesForCurrentThread() - before) * 4;

            int count = Math.Max(Mutations / 5, 1);
            for (int mutation = 0; mutation < count; mutation++)
            {
                int seed = SeedOf(name, mutation);
                var random = new Random(seed);
                int section = random.Next(unpacked.SectionCount);
                byte[] mutated = unpacked.Pack(section, MutateSection(unpacked.Section(section), random, out string description));
                if (Outcome(mutated, timeBound, allocationBound, out bool scene) is string problem)
                {
                    failures.Add($"{name}, mutation {mutation} (seed {seed}, section {section}: {description}): {problem}");
                }

                total++;
                scenes += scene ? 1 : 0;
            }
        }

        output.WriteLine($"{total} mutations of corpus payloads in {watch.Elapsed.TotalSeconds:F1} s, {scenes} still gave a scene, {failures.Count} misbehaved");
        Assert.True(failures.Count == 0, $"{failures.Count} of {total} mutations misbehaved.\n{string.Join("\n", failures.Take(5))}");
    }
}
