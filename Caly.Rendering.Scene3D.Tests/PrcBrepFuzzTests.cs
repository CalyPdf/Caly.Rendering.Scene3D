using Caly.Rendering.Scene3D.Prc;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Mutation fuzzing of whole PRC files whose only geometry is a compressed body, with the contract of <see cref="PrcFuzzTests"/>:
/// a consistent scene or a <see cref="Scene3DException"/> raised on purpose, within 2 s and 64 MB, whatever is done to the file
/// (mostly to its exact-geometry section, which the mutations of the other seeds never reach).
/// </summary>
public sealed class PrcBrepFuzzTests(ITestOutputHelper output)
{
    /// <summary>Mutations tried per seed; SCENE3D_FUZZ_MUTATIONS raises it for a deeper run.</summary>
    private static readonly int Mutations = int.TryParse(Environment.GetEnvironmentVariable("SCENE3D_FUZZ_MUTATIONS"), out int n) && n > 0 ? n : 1200;

    public static TheoryData<string> Seeds => new() { "rectangles", "cylinder", "patch", "torus" };

    private static Action<PrcBrepWriter> Line(BrepVertex a, BrepVertex b) => c => c.NewCurve(l => l.Line(a, b));

    private static PrcBitWriter Body(Action<PrcBrepWriter> shell)
    {
        var w = new PrcBrepWriter(0.01, 5);
        w.BodyStart(isoVertices: 8, isoEdges: 8, singleConnex: true);
        shell(w);
        return w.EndBody();
    }

    private static (Action<PrcBrepWriter> Face, bool Iso) Rectangle(double x, int firstVertex) =>
        (f => f.IsoFace(2, PrcBrepWriter.PlaneNormal(0, 0, true), true, true,
            Line(BrepVertex.New(x, 0, 0), BrepVertex.New(x + 2, 0, 0)), Line(BrepVertex.Ref(firstVertex + 1), BrepVertex.New(x + 2, 3, 0)),
            third: null, fourth: null, common: BrepVertex.New(x, 3, 0)), true);

    private static PrcBitWriter SeedBody(string name)
    {
        switch (name)
        {
            case "rectangles":
                return Body(w => w.Shell(Rectangle(0, 0), Rectangle(5, 4)));

            case "cylinder":
            {
                // A cylinder of radius 2 about the z axis, between two circles.
                Action<PrcBrepWriter> Circle(double z) => c => c.NewCurve(l => l.FullCircle(BrepVertex.New(2, 0, z), BrepVertex.New(2, 0, z), new PrcVector(0, 0, z), new PrcVector(0, 0, 1)));
                return Body(w => w.Shell((f => f.AnaFace(14, null, true, [new PrcBrepWriter.AnaLoop(true, Circle(0)), new PrcBrepWriter.AnaLoop(false, Circle(3))], null, PrcBrepWriter.GenericCylinder(2)), false)));
            }

            case "patch":
            {
                // A flat patch of degree 1 by 1, 3 by 2, on its natural boundaries.
                var spec = new PrcBrepWriter.NurbsSpec(
                    1, 1, [2, 2], [2, 2], new PrcVector(0, 0, 0), [new PrcVector(0, 2, 0)], [new PrcVector(3, 0, 0)],
                    [new PrcBrepWriter.InteriorPoint(0)], PrcBrepWriter.KnotSpec.Uniform, PrcBrepWriter.KnotSpec.Uniform);
                PrcBrepWriter.IsoTrim[] trims = [new PrcBrepWriter.IsoTrim.IsoBoundary(), new PrcBrepWriter.IsoTrim.IsoBoundary(), new PrcBrepWriter.IsoTrim.IsoBoundary(), new PrcBrepWriter.IsoTrim.IsoBoundary()];
                return Body(w => w.Shell((f => f.IsoNurbsFace(true, true, (false, false, false), spec, trims, [BrepVertex.New(0, 2, 0), BrepVertex.New(0, 0, 0), BrepVertex.New(3, 0, 0), BrepVertex.New(3, 2, 0)]), true)));
            }

            default:
            {
                // A torus (major radius 3, minor 1, about the z axis) whose loops are single vertices.
                Action<PrcBrepWriter> Vertex(double x) => c => c.NewCurve(l => l.Line(BrepVertex.New(x, 0, 0), BrepVertex.New(x, 0, 0)));
                return Body(w => w.Shell((f => f.AnaFace(12, null, true, [new PrcBrepWriter.AnaLoop(true, Vertex(4))], new PrcVector(4, 0, 0),
                    a => a.Point(new PrcVector(0, 0, 0)).Point(new PrcVector(3, 0, 0)).Bits.Double(0).Double(0).Double(1)), false)));
            }
        }
    }

    internal static byte[] Seed(string name)
    {
        var file = new PrcTestFile();
        file.Colors.Add([0.2, 0.4, 0.6]);
        file.Styles.Add((1, -1));
        file.TopologicalContexts.Add([SeedBody(name)]);
        file.Parts.Add(new PrcTestFile.Part("solid part", "solid", 0, BiasedStyle: 1, Body: (0, 0)));
        file.Occurrences.Add(new PrcTestFile.Occurrence("root", BiasedPart: 1));
        return file.Build();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheSeedFilesThemselvesAreRead(string name)
    {
        using Scene scene = PrcReader.Read(Seed(name), cancellationToken: TestContext.Current.CancellationToken);

        SceneInvariants.Check(scene);
        Assert.True(PrcReader.IsComplete(scene), string.Join("; ", scene.Diagnostics.Select(d => d.Code)));
        if (name == "rectangles")
        {
            Assert.Equal(12, scene.Indices.Length);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AMutatedFileGivesASceneOrADeliberateError(string name)
    {
        byte[] original = Seed(name);
        PrcFuzzTests.Unpacked unpacked = PrcFuzzTests.Unpacked.Of(original);
        PrcFuzzTests.Outcome(original, TimeSpan.FromSeconds(30), long.MaxValue, out _);   // warm the pools and the JIT

        var failures = new List<string>();
        int scenes = 0;
        for (int mutation = 0; mutation < Mutations; mutation++)
        {
            int seed = PrcFuzzTests.SeedOf("brep-" + name, mutation);
            var random = new Random(seed);
            byte[] mutated;
            string description;
            int geometry = 3;                                           // the exact-geometry section of the only file structure
            if (random.Next(10) == 0)
            {
                mutated = PrcFuzzTests.MutateFile(original, random, out description);
            }
            else
            {
                int section = random.Next(5) == 0 ? random.Next(unpacked.SectionCount) : geometry;
                mutated = unpacked.Pack(section, PrcFuzzTests.MutateSection(unpacked.Section(section), random, out description));
                description = $"section {section}: {description}";
            }

            if (PrcFuzzTests.Outcome(mutated, TimeSpan.FromSeconds(2), 64L << 20, out bool scene) is string problem)
            {
                failures.Add($"mutation {mutation} (seed {seed}, {description}): {problem}");
            }

            scenes += scene ? 1 : 0;
        }

        output.WriteLine($"{name}: {Mutations} mutations, {scenes} still gave a scene, {failures.Count} misbehaved");
        Assert.True(failures.Count == 0, $"{name}: {failures.Count} of {Mutations} mutations misbehaved.\n{string.Join("\n", failures.Take(5))}");
        Assert.InRange(scenes, 1, Mutations - 1);
    }
}
