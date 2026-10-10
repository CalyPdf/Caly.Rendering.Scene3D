using System.Numerics;
using Caly.Rendering.Scene3D.Prc;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Units on real models (SCENE3D_PRC_CORPUS; nothing to do without it). A PRC model has one unit, and the unit an
/// occurrence carries is not a scale: where a part in inches sits in an assembly in millimetres, the writer
/// puts the factor (25.4 or 1/25.4) into the part's placement. If that were wrong, a part would come out 25
/// times too large or too small, and so stand apart from the rest of its assembly. So every part of a model is
/// next to another one: its box, grown by a few percent of the model's size, meets the box of a second part.
/// The sample files that mix units (the Tetra4D TDP of PDF-in-Manufacturing, the engine of PROSTEP, the flashlight)
/// are held to the same measure as the others.
/// </summary>
public class PrcUnitsCorpusTests(ITestOutputHelper output)
{
    /// <summary>How far, as a share of the model's diagonal, a part may be from the next one and still be "next to" it.</summary>
    private const float Reach = 0.02f;

    /// <summary>The share of parts allowed to stand alone: scenery, labels and the odd detached piece.</summary>
    private const double MaxAlone = 0.06;

    private static readonly string[] MixedUnits = ["Tetra4D_-_TDP_Creo", "ENGINE_TYPE", "Flashlight"];

    internal static (int Parts, int Alone) Measure(Scene scene)
    {
        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            SceneNode node = scene.Nodes[i];
            if (node.Mesh < 0 || !node.Visible)
            {
                continue;
            }

            SceneBounds local = scene.Meshes[node.Mesh].Bounds;
            if (local.IsEmpty)
            {
                continue;
            }

            Matrix4x4 world = scene.GetWorldMatrix(i);
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                var p = Vector3.Transform(new Vector3((corner & 1) == 0 ? local.Min.X : local.Max.X, (corner & 2) == 0 ? local.Min.Y : local.Max.Y, (corner & 4) == 0 ? local.Min.Z : local.Max.Z), world);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            boxes.Add((min, max));
        }

        if (boxes.Count < 2)
        {
            return (boxes.Count, 0);
        }

        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach ((Vector3 min, Vector3 max) in boxes)
        {
            lo = Vector3.Min(lo, min);
            hi = Vector3.Max(hi, max);
        }

        float reach = Vector3.Distance(lo, hi) * Reach;
        int alone = 0;
        for (int a = 0; a < boxes.Count; a++)
        {
            bool met = false;
            for (int b = 0; b < boxes.Count && !met; b++)
            {
                met = b != a
                    && boxes[a].Min.X - reach <= boxes[b].Max.X && boxes[b].Min.X - reach <= boxes[a].Max.X
                    && boxes[a].Min.Y - reach <= boxes[b].Max.Y && boxes[b].Min.Y - reach <= boxes[a].Max.Y
                    && boxes[a].Min.Z - reach <= boxes[b].Max.Z && boxes[b].Min.Z - reach <= boxes[a].Max.Z;
            }

            alone += met ? 0 : 1;
        }

        return (boxes.Count, alone);
    }

    /// <summary>
    /// The measure can tell a part that is far from the rest: a row of boxes that touch, and one more at the end of the
    /// row, whose whole placement (size and position) is 25.4 times too large, as when a part in inches is taken for
    /// millimetres. What it cannot see is the opposite error that leaves a part inside another's box (a bolt a
    /// twenty-fifth of its size): that is looked for in the pictures, not here.
    /// </summary>
    [Theory]
    [InlineData(1f, 0)]
    [InlineData(25.4f, 1)]
    public void APartPlacedOutOfScaleStandsAlone(float factor, int alone)
    {
        using var builder = new SceneBuilder();
        builder.BeginMesh(builder.AddName("cube"), SceneMeshFlags.None);
        SceneVertexSpans vertices = builder.AllocateVertices(2);
        new float[] { 0, 0, 0, 10, 10, 10 }.CopyTo(vertices.Positions);
        Span<uint> indices = builder.AllocateIndices(3);
        new uint[] { 0, 1, 1 }.CopyTo(indices);
        builder.AddPart(3, shadingId: 0);
        int mesh = builder.EndMesh();

        for (int i = 0; i < 10; i++)
        {
            builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.CreateTranslation(i * 10, 0, 0), Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, Name = builder.AddName("part" + i), Visible = true });
        }

        // The eleventh part belongs at x = 100, at the end of the row.
        builder.AddNode(new SceneNode { Parent = -1, Local = Matrix4x4.CreateScale(factor) * Matrix4x4.CreateTranslation(100 * factor, 0, 0), Kind = SceneNodeKind.Model, Mesh = mesh, Light = -1, Camera = -1, Name = builder.AddName("eleventh"), Visible = true });
        using Scene scene = builder.Build();

        Assert.Equal((11, alone), Measure(scene));
    }
    [Fact]
    public void PartsOfRealModelsAreNextToOtherParts()
    {
        int mixed = 0;
        foreach (string path in PrcContainerTests.CorpusFiles())
        {
            string name = Path.GetFileName(path);
            using Scene scene = PrcReader.Read(File.ReadAllBytes(path), cancellationToken: TestContext.Current.CancellationToken);
            (int parts, int alone) = Measure(scene);
            bool mixesUnits = MixedUnits.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
            mixed += mixesUnits ? 1 : 0;
            output.WriteLine($"{(mixesUnits ? "MIXED " : "      ")}{name}: {parts} parts, {alone} alone ({(parts == 0 ? 0 : 100.0 * alone / parts):F1}%), unit {scene.UnitScale}");
            if (parts >= 10)
            {
                Assert.True(alone <= parts * MaxAlone, $"{name}: {alone} of {parts} parts stand away from every other one");
            }
        }

        if (PrcContainerTests.CorpusFiles().Count > 0)
        {
            Assert.True(mixed > 0, "None of the sample files that mix units is in the corpus.");
        }
    }
}
