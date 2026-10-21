using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The compressed bodies of real files (SCENE3D_PRC_CORPUS and SCENE3D_PRC_CORPUS_EXTRA; nothing to do without them). There is no
/// other decoder to compare the geometry with; what is checked is what only a right reading gives.
/// </summary>
public class PrcBrepCorpusTests(ITestOutputHelper output)
{
    private static IEnumerable<string> Payloads() => [.. PrcContainerTests.CorpusFiles(), .. PrcCompressedCorpusTests.ExtraCorpusFiles()];

    /// <summary>Every compressed body of every payload, read with the whole geometry section of its file structure.</summary>
    internal static IEnumerable<(string File, PrcTopoBrepDataCompress Body)> Bodies()
    {
        foreach (string file in Payloads())
        {
            PrcFile read;
            try
            {
                using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(file));
                read = PrcFileReader.Read(container, geometry: PrcGeometryReading.Always);
            }
            catch (Scene3DException)
            {
                // A file that cannot be followed to the end of its geometry section: reported by the test that counts them.
                continue;
            }

            foreach (PrcFileStructureData structure in read.FileStructures)
            {
                foreach (PrcTopoContext context in structure.ExactGeometry?.TopoContexts ?? [])
                {
                    foreach (PrcTopoBody body in context.Bodies)
                    {
                        if (body.Id is PrcTopoBrepDataCompress compressed)
                        {
                            yield return (Path.GetFileName(file), compressed);
                        }
                    }
                }
            }
        }
    }

    /// <summary>A body cut into triangles.</summary>
    internal static PrcBrepTessellation Tessellate(PrcTopoBrepDataCompress body, out int triangles)
    {
        using var builder = new SceneBuilder(SceneLimits.Default with { MaxIndices = int.MaxValue / 2, MaxTotalBytes = 2L << 30 });
        PrcBrepTessellator.AddMesh(builder, body, builder.AddName("body"), out PrcBrepTessellation result);
        triangles = result.Triangles;
        return result;
    }

    [Fact]
    public void MostFacesOfTheCorpusAreCutIntoTriangles()
    {
        long faces = 0, dropped = 0, unsupported = 0;
        foreach ((_, PrcTopoBrepDataCompress body) in Bodies())
        {
            PrcBrepTessellation r = Tessellate(body, out _);
            faces += r.Faces;
            dropped += r.FacesDropped;
            unsupported += r.FacesUnsupported;
        }

        if (faces == 0)
        {
            return;
        }

        output.WriteLine($"{faces} faces, {dropped} dropped, {unsupported} unsupported ({100.0 * dropped / faces:F1}% dropped)");
        // A floor, not a goal (0.2% when last measured).
        Assert.True(dropped < faces * 0.005, $"{dropped} of {faces} faces dropped");
    }

    /// <summary>
    /// The LandingGear (the sample that has nothing but compressed bodies): its NURBS grids were first cut with every grid line on a
    /// loop's corner counted outside, a third of their points missing, and slowly (a regular grid is all points on one circle).
    /// </summary>
    [Fact]
    public void TheLandingGearIsCutFinelyAndQuickly()
    {
        string? path = PrcContainerTests.CorpusFiles().FirstOrDefault(f => f.Contains("LandingGear-V03042024_136", StringComparison.Ordinal));
        if (path is null)
        {
            return;
        }

        byte[] file = File.ReadAllBytes(path);
        using (PrcReader.Read(file, cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using Scene scene = PrcReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);
        watch.Stop();
        output.WriteLine($"{scene.Indices.Length / 3} triangles in {watch.ElapsedMilliseconds} ms");
        Assert.InRange(scene.Indices.Length / 3, 50_000, 80_000);
        Assert.True(watch.ElapsedMilliseconds < 600, $"{watch.ElapsedMilliseconds} ms (250 ms in Release on the machine this was written on)");
    }

    /// <summary>
    /// The LandingGear's colours (pink bushings, light blue arms, green lugs, yellow block on a lavender body, as the page's own
    /// picture shows them) are given to single faces, in the extra-geometry section: its one compressed body was drawn all lavender
    /// before that section was read.
    /// </summary>
    [Fact]
    public void TheLandingGearHasTheColoursOfItsFaces()
    {
        string? path = PrcContainerTests.CorpusFiles().FirstOrDefault(f => f.Contains("LandingGear-V03042024_136", StringComparison.Ordinal));
        if (path is null)
        {
            return;
        }

        using Scene scene = PrcReader.Read(File.ReadAllBytes(path), cancellationToken: TestContext.Current.CancellationToken);

        SceneNode node = scene.Nodes.ToArray().Single(n => n.Mesh >= 0);
        var colours = new HashSet<(int, int, int)>();
        for (int b = 0; b < node.BindingCount; b++)
        {
            System.Numerics.Vector3 d = scene.Materials[scene.Bindings[node.FirstBinding + b]].Diffuse;
            colours.Add(((int)Math.Round(d.X * 255), (int)Math.Round(d.Y * 255), (int)Math.Round(d.Z * 255)));
        }

        output.WriteLine($"{node.BindingCount} material slots, {scene.Meshes[node.Mesh].PartCount} parts");
        Assert.Contains((210, 210, 255), colours);                  // the body: lavender
        Assert.Contains((255, 128, 128), colours);                  // the bushings: pink
        Assert.Contains((0, 128, 255), colours);                    // the arms: light blue
        Assert.Contains((0, 255, 0), colours);                      // the lugs: green
        Assert.Contains((255, 255, 0), colours);                    // the block: yellow
    }

    /// <summary>
    /// The Turbine's parts are all grey: its colours (copper casing and gears, red flanges, dark frame, as the page's own picture
    /// shows them) and its cutaway (half the cover, 806 of the housing's 808 bodies) are what its assembly says of the items of its
    /// parts (entity references of its occurrences). Before they were read the engine was drawn whole, grey under a pale green cover.
    /// </summary>
    [Fact]
    public void TheTurbineHasTheColoursAndTheCutawayItsAssemblyGives()
    {
        string? path = PrcContainerTests.CorpusFiles().FirstOrDefault(f => f.Contains("Aero-Turbine-V03042024_105", StringComparison.Ordinal));
        if (path is null)
        {
            return;
        }

        using Scene scene = PrcReader.Read(File.ReadAllBytes(path), cancellationToken: TestContext.Current.CancellationToken);

        SceneNode[] meshes = [.. scene.Nodes.ToArray().Where(n => n.Mesh >= 0)];
        var colours = new HashSet<(int, int, int)>();
        foreach (SceneNode node in meshes)
        {
            for (int b = 0; b < node.BindingCount; b++)
            {
                System.Numerics.Vector3 d = scene.Materials[scene.Bindings[node.FirstBinding + b]].Diffuse;
                colours.Add(((int)Math.Round(d.X * 255), (int)Math.Round(d.Y * 255), (int)Math.Round(d.Z * 255)));
            }
        }

        Assert.Equal(914 - 806 - 1, meshes.Length);                 // the housing's removed bodies and the cover's removed half
        Assert.Contains((255, 173, 91), colours);                   // copper
        Assert.Contains((255, 83, 83), colours);                    // red
        Assert.Contains((106, 106, 106), colours);                  // dark grey
        Assert.DoesNotContain((243, 254, 177), colours);            // the cover's own pale green, which the assembly replaces
    }

    /// <summary>
    /// The ABM8 crystal's parts are grey: the gold of the four pads under each of its three packages is what its assembly says of
    /// single faces of their bodies (topology references), by the face's number in the body's tessellation. Read by the order the
    /// body stores its faces in, the colour lands on faces scattered over the package instead.
    /// </summary>
    [Fact]
    public void TheCrystalHasThePadsItsAssemblyColours()
    {
        string? path = PrcCompressedCorpusTests.ExtraCorpusFiles().FirstOrDefault(f => f.Contains("ABM8-3D", StringComparison.Ordinal));
        if (path is null)
        {
            return;
        }

        using Scene scene = PrcReader.Read(File.ReadAllBytes(path), cancellationToken: TestContext.Current.CancellationToken);

        var triangles = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (SceneNode node in scene.Nodes)
        {
            if (node.Mesh < 0 || !node.Visible)
            {
                continue;
            }

            SceneMesh mesh = scene.Meshes[node.Mesh];
            foreach (SceneMeshPart part in scene.Parts.Slice(mesh.FirstPart, mesh.PartCount))
            {
                System.Numerics.Vector3 d = scene.Materials[scene.Bindings[node.FirstBinding + part.ShadingId]].Diffuse;
                string colour = $"{(int)Math.Round(d.X * 255)},{(int)Math.Round(d.Y * 255)},{(int)Math.Round(d.Z * 255)}";
                triangles[colour] = triangles.GetValueOrDefault(colour) + (part.IndexCount / 3);
            }
        }

        // The pads are yellow in this file; the rest stays the parts' greys.
        Assert.Equal("192,192,192:856 255,255,0:39 64,64,64:2809", string.Join(" ", triangles.Select(t => $"{t.Key}:{t.Value}")));
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code is "prc-face-reference-not-applied" or "prc-topology-reference-not-applied");
    }

    /// <summary>
    /// PROSTEP's Camera has the icons of its display placed by its assembly: the "Icons" occurrence's references name coordinate
    /// systems of its own file structure (counted from 1) that place each item of the icons' part. So placed, the 118 icon meshes
    /// lie on the face of the display (the screen of the "Frame" part: x 59.6 to 104.4, y 16.1 to 55.9, its face at z -41.2).
    /// </summary>
    [Fact]
    public void TheCameraHasTheIconsOnItsDisplay()
    {
        string? path = PrcCompressedCorpusTests.ExtraCorpusFiles().FirstOrDefault(f => f.Contains("PROSTEP-Camera_Features", StringComparison.Ordinal));
        if (path is null)
        {
            return;
        }

        using Scene scene = PrcReader.Read(File.ReadAllBytes(path), cancellationToken: TestContext.Current.CancellationToken);
        int icons = 0;
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            SceneNode node = scene.Nodes[i];
            if (node.Mesh < 0 || !node.Visible || node.Parent < 0 || PrcSceneBuilderTests.Plain(scene.GetName(scene.Nodes[node.Parent].Name)) != "Icons")
            {
                continue;
            }

            SceneBounds local = scene.Meshes[node.Mesh].Bounds;
            System.Numerics.Matrix4x4 world = scene.GetWorldMatrix(i);
            SceneBounds box = SceneBounds.Empty;
            for (int c = 0; c < 8; c++)
            {
                box.Include(System.Numerics.Vector3.Transform(new System.Numerics.Vector3((c & 1) == 0 ? local.Min.X : local.Max.X, (c & 2) == 0 ? local.Min.Y : local.Max.Y, (c & 4) == 0 ? local.Min.Z : local.Max.Z), world));
            }

            Assert.InRange(box.Min.X, 59.6f, 104.4f);
            Assert.InRange(box.Max.X, 59.6f, 104.4f);
            Assert.InRange(box.Min.Y, 16.1f, 55.9f);
            Assert.InRange(box.Max.Y, 16.1f, 55.9f);
            Assert.InRange(box.Min.Z, -41.25f, -41.05f);
            Assert.InRange(box.Max.Z, -41.25f, -41.05f);
            icons++;
        }

        Assert.Equal(118, icons);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "prc-reference-placement-not-applied");
    }

    /// <summary>
    /// A spline surface that closes in u has its last row of control points on its first. The first column is stored as steps and
    /// closes by itself; the rest are corrections to a prediction, and close only if they are read right: types 1 and 2 in the local
    /// frame of the two steps before, type 3 in plain coordinates. Read so, every closed net of the corpus closes to within two
    /// quanta (the NURBS tolerance); read all plain, the median miss was 2,640 quanta.
    /// </summary>
    [Fact]
    public void EveryClosedSplineOfTheCorpusClosesToWithinTwoQuanta()
    {
        int nets = 0;
        var open = new List<string>();
        foreach ((string file, PrcTopoBrepDataCompress body) in Bodies())
        {
            double quantum = body.Tolerance / 5;
            foreach (PrcNurbs n in body.Nurbs)
            {
                if (!n.ClosedU || n.CountV < 2 || (n.ControlPoints[(n.CountU - 1) * n.CountV] - n.ControlPoints[0]).LengthSquared() > 4 * quantum * quantum)
                {
                    continue;
                }

                nets++;
                for (int j = 1; j < n.CountV; j++)
                {
                    double miss = Math.Sqrt((n.ControlPoints[((n.CountU - 1) * n.CountV) + j] - n.ControlPoints[j]).LengthSquared()) / quantum;
                    if (miss > 2)
                    {
                        open.Add($"{file}: {n.CountU} x {n.CountV}, column {j} misses by {miss:F1} quanta");
                        break;
                    }
                }
            }
        }

        output.WriteLine($"{nets} closed nets, {open.Count} that do not close");
        Assert.True(open.Count == 0, string.Join(Environment.NewLine, open.Take(20)));
    }

    [Fact]
    public void EveryPayloadOfTheCorpusIsFollowedToTheEndOfItsGeometrySection()
    {
        string[] payloads = [.. Payloads()];
        if (payloads.Length == 0)
        {
            return;
        }

        var failures = new List<string>();
        foreach (string file in payloads)
        {
            try
            {
                using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(file));
                PrcFile read = PrcFileReader.Read(container, geometry: PrcGeometryReading.Always, cancellation: TestContext.Current.CancellationToken);
                int structure = 0;
                foreach (PrcSectionRead section in read.Sections.Where(s => s.Name == "Geometry"))
                {
                    // Some writers pad an empty geometry section to a fixed size (the reference reader ends in the same place); a section
                    // that holds bodies ends in its last byte (or in a padding byte: the reference reader ends in the same place).
                    bool holdsBodies = read.FileStructures[section.FileStructure].ExactGeometry?.TopoContexts.Any(c => c.Bodies.Length != 0) == true;
                    if (holdsBodies && section.RemainingBits is < 0 or > 8)
                    {
                        failures.Add($"{Path.GetFileName(file)}: structure {structure}: {section.RemainingBits} bits left");
                    }

                    structure++;
                }
            }
            catch (Scene3DException e)
            {
                failures.Add($"{Path.GetFileName(file)}: {e.Error}: {e.Message}");
            }
        }

        output.WriteLine($"{payloads.Length} payloads, {failures.Count} not followed to the end");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TheCompressedBodiesOfTheCorpusHoldWhatTheyHeldWhenTheyWereFirstRead()
    {
        int bodies = 0;
        long faces = 0, curves = 0, vertices = 0, nurbs = 0, generic = 0;
        var kinds = new SortedDictionary<PrcFaceKind, long>();
        foreach ((_, PrcTopoBrepDataCompress body) in Bodies())
        {
            bodies++;
            faces += body.Faces.Length;
            curves += body.Curves.Length;
            vertices += body.Vertices.Length;
            nurbs += body.Nurbs.Length;
            generic += body.GenericSurfaces.Length;
            foreach (PrcBrepFace face in body.Faces)
            {
                kinds[face.Kind] = kinds.GetValueOrDefault(face.Kind) + 1;
            }
        }

        if (bodies == 0)
        {
            return;
        }

        output.WriteLine($"{bodies} bodies, {faces} faces, {curves} curves, {vertices} vertices, {nurbs} NURBS surfaces, {generic} generic surfaces");
        foreach ((PrcFaceKind kind, long count) in kinds)
        {
            output.WriteLine($"  {kind}: {count}");
        }

        // Pins of the first complete reading, in step with prc-rs on every payload of both corpora (348): a change shows up here.
        Assert.Equal(26338, bodies);
        Assert.Equal(160869, faces);
        Assert.Equal(324284, curves);
        Assert.Equal(206990, vertices);
        Assert.Equal(46946, nurbs);
        Assert.Equal(42, generic);
        Assert.Equal(
            [
                (PrcFaceKind.IsoPlane, 23244L), (PrcFaceKind.IsoCylinder, 15129L), (PrcFaceKind.IsoTorus, 3211L), (PrcFaceKind.IsoNurbs, 2785L),
                (PrcFaceKind.AnaPlane, 26452L), (PrcFaceKind.AnaCylinder, 26856L), (PrcFaceKind.AnaTorus, 7369L), (PrcFaceKind.AnaSphere, 3299L),
                (PrcFaceKind.AnaCone, 8321L), (PrcFaceKind.AnaNurbs, 44161L), (PrcFaceKind.AnaGenericFace, 42L),
            ],
            kinds.Select(k => (k.Key, k.Value)).ToArray());
    }
}
