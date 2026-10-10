using System.Buffers.Binary;
using System.Diagnostics;
using Caly.Rendering.Scene3D.U3D;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Deterministic mutation fuzzing: whatever is done to a file, reading it gives a consistent scene or a
/// <see cref="Scene3DException"/> raised on purpose — never another exception (the reader wraps those, and
/// the wrapped one shows as <see cref="Exception.InnerException"/>), a hang or a runaway allocation.
/// </summary>
public class U3DFuzzTests
{
    /// <summary>Mutations tried per scene; SCENE3D_FUZZ_MUTATIONS raises it for a deeper run.</summary>
    private static readonly int MutationsPerScene = int.TryParse(Environment.GetEnvironmentVariable("SCENE3D_FUZZ_MUTATIONS"), out int n) && n > 0 ? n : 150;

    // Generous for files of at most a few hundred kilobytes; a mutation that needs more is a finding.
    private static readonly TimeSpan TimeBound = TimeSpan.FromSeconds(2);
    private const long AllocationBound = 64L << 20;

    private static readonly SceneLimits Limits = SceneLimits.Default with { MaxTotalBytes = 256L << 20, MaxTextureBytes = 64L << 20 };

    private static readonly uint[] InterestingValues = [0, 1, 2, 3, 0x7F, 0xFF, 0x3FF, 0x400, 0x401, 0x3FFE, 0x3FFF, 0x4000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFF0, 0xFFFFFFFF];

    /// <summary>A seed that does not depend on the process (string hashes are randomised).</summary>
    private static int SeedOf(string name, int mutation)
    {
        int hash = 17;
        foreach (char c in name)
        {
            hash = unchecked(hash * 31 + c);
        }

        return unchecked(hash * 1009 + mutation);
    }

    internal static byte[] Mutate(byte[] original, int seed, out string description)
    {
        var random = new Random(seed);
        byte[] data = (byte[])original.Clone();

        switch (random.Next(7))
        {
            case 0:
            {
                int flips = random.Next(1, 9);
                for (int i = 0; i < flips; i++)
                {
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                }

                description = $"{flips} bit flips";
                break;
            }

            case 1:
            {
                // Fields sit on 32-bit boundaries often enough for this to hit counts and sizes.
                int offset = random.Next(data.Length / 4) * 4;
                uint value = random.Next(3) == 0 ? (uint)random.NextInt64(0, 1L << 32) : InterestingValues[random.Next(InterestingValues.Length)];
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
                description = $"u32 at {offset} = 0x{value:X}";
                break;
            }

            case 2:
            {
                int length = random.Next(data.Length);
                data = data[..length];
                description = $"truncated to {length}";
                break;
            }

            case 3:
            case 4:
            {
                // A block's data size (case 3) or metadata size (case 4).
                List<RawBlocks.Block> blocks = RawBlocks.Enumerate(original);
                RawBlocks.Block block = blocks[random.Next(blocks.Count)];
                int field = block.DataStart - (random.Next(2) == 0 ? 8 : 4);
                uint old = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(field));
                uint value = random.Next(4) switch
                {
                    0 => InterestingValues[random.Next(InterestingValues.Length)],
                    1 => old + (uint)random.Next(1, 64),
                    2 => old - (uint)random.Next(1, 64),
                    _ => (uint)random.Next(data.Length * 2)
                };
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(field), value);
                description = $"block size field at {field}: {old} -> {value}";
                break;
            }

            case 5:
            {
                int start = random.Next(data.Length);
                int length = Math.Min(random.Next(1, 65), data.Length - start);
                random.NextBytes(data.AsSpan(start, length));
                description = $"{length} random bytes at {start}";
                break;
            }

            default:
            {
                // Part of the file copied over another part: valid-looking content in the wrong place.
                int length = random.Next(1, Math.Min(4096, data.Length));
                int from = random.Next(data.Length - length + 1), to = random.Next(data.Length - length + 1);
                Array.Copy(original, from, data, to, length);
                description = $"{length} bytes copied from {from} to {to}";
                break;
            }
        }

        return data;
    }

    /// <summary>Reads possibly corrupt data and returns what is wrong with the outcome, or null.</summary>
    internal static string? Outcome(byte[] data) => Outcome(data, TimeBound, AllocationBound);

    internal static string? Outcome(byte[] data, TimeSpan timeBound, long allocationBound)
    {
        // Reading is deterministic, so a hang is slow every time; a shared CI runner that stalls once is not.
        string? problem = Attempt(data, timeBound, allocationBound, out bool slow);
        return slow ? Attempt(data, timeBound, allocationBound, out _) : problem;
    }

    private static string? Attempt(byte[] data, TimeSpan timeBound, long allocationBound, out bool slow)
    {
        slow = false;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        try
        {
            using Scene scene = U3DReader.Read(data, Limits);
            watch.Stop();
            SceneInvariants.Check(scene);
        }
        catch (Scene3DException e) when (e.InnerException is null)
        {
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

    [Theory]
    [MemberData(nameof(TestFiles.AllScenes), MemberType = typeof(TestFiles))]
    public void AMutatedSceneGivesASceneOrADeliberateError(string name)
    {
        byte[] original = TestFiles.Scene(name + ".u3d");
        Outcome(original); // warm the pools so that allocation figures are those of a steady state

        var failures = new List<string>();
        for (int mutation = 0; mutation < MutationsPerScene; mutation++)
        {
            int seed = SeedOf(name, mutation);
            byte[] mutated = Mutate(original, seed, out string description);
            if (Outcome(mutated) is string problem)
            {
                failures.Add($"mutation {mutation} (seed {seed}, {description}): {problem}");
            }
        }

        Assert.True(failures.Count == 0, $"{name}: {failures.Count} of {MutationsPerScene} mutations misbehaved.\n{string.Join("\n", failures.Take(5))}");
    }

    /// <summary>
    /// Review focus: a file cut off anywhere reads as a scene or a deliberate error. Every prefix of the
    /// first bytes, then every 97th.
    /// </summary>
    [Theory]
    [InlineData("box_base")]
    [InlineData("shading_alpha")]
    [InlineData("plane64x64_base")]
    public void ATruncatedFileGivesASceneOrADeliberateError(string name)
    {
        byte[] original = TestFiles.Scene(name + ".u3d");
        Outcome(original);

        int truncatedErrors = 0, scenes = 0;
        for (int length = 0; length < original.Length; length += length < 256 ? 1 : 97)
        {
            byte[] prefix = original[..length];
            string? problem = Outcome(prefix);
            Assert.True(problem is null, $"{name} cut to {length} bytes: {problem}");

            try
            {
                U3DReader.Read(prefix, Limits, TestContext.Current.CancellationToken).Dispose();
                scenes++;
            }
            catch (Scene3DException e)
            {
                truncatedErrors += e.Error == Scene3DError.Truncated ? 1 : 0;
            }
        }

        // Most cuts fall inside a block and are reported as truncation; cuts between blocks leave a
        // shorter valid file.
        Assert.True(truncatedErrors > 0);
        Assert.True(truncatedErrors + scenes > 0);
    }
}
