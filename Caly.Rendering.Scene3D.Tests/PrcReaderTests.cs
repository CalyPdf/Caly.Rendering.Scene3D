using System.Numerics;
using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Model;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// <see cref="PrcReader"/> from bytes to scene: on a small whole file written by the tests, and on the
/// payloads of the corpus (SCENE3D_PRC_CORPUS; those tests have nothing to do without it).
/// </summary>
public class PrcReaderTests(ITestOutputHelper output)
{
    private static string Tree(Scene scene)
    {
        var lines = new List<string>();
        for (int i = 0; i < scene.Nodes.Length; i++)
        {
            SceneNode node = scene.Nodes[i];
            lines.Add($"{node.Parent}>{PrcSceneBuilderTests.Plain(scene.GetName(node.Name))}{(node.Mesh >= 0 ? "#" + node.Mesh : "")}{(node.Visible ? "" : "(hidden)")}");
        }

        return string.Join(" ", lines);
    }

    [Fact]
    public void AWholeFileBecomesItsScene()
    {
        byte[] file = PrcTestFile.Simple().Build();
        Assert.True(PrcReader.IsPrc(file));

        using Scene scene = PrcReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneInvariants.Check(scene);
        Assert.Equal("-1>assembly 0>left 1>plate#0 0>right 3>plate#0", Tree(scene));
        Assert.Equal(0.001, scene.UnitScale);
        Assert.Equal(1, scene.Meshes.Length);
        Assert.Equal(3, scene.Indices.Length);
        Assert.Equal(new Vector3(-2, 0, 0), scene.GetWorldMatrix(2).Translation);
        Assert.Equal(new Vector3(2, 0, 0), scene.GetWorldMatrix(4).Translation);
        Assert.Equal([0, 0, 0, 1, 0, 0, 0, 1, 0], scene.Positions.ToArray());
        Assert.Equal([0, 0, 1, 0, 0, 1, 0, 0, 1], scene.Normals.ToArray());       // calculated: the file stores none

        // The plate is red by its own style under both occurrences; the right one adds its transparency.
        SceneMaterial left = scene.Materials[scene.Bindings[scene.Nodes[2].FirstBinding]];
        SceneMaterial right = scene.Materials[scene.Bindings[scene.Nodes[4].FirstBinding]];
        Assert.Equal((new Vector3(1, 0, 0), 1f), (left.Diffuse, left.Opacity));
        Assert.Equal(new Vector3(1, 0, 0), right.Diffuse);
        Assert.Equal(128 / 255f, right.Opacity, 5);
        Assert.True(PrcReader.IsComplete(scene));
        Assert.Empty(scene.Diagnostics);
    }

    [Fact]
    public void EverySectionOfTheFileIsReadToItsEnd()
    {
        using PrcContainer container = PrcContainer.Open(PrcTestFile.Simple().Build());
        PrcFile file = PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["Global", "Tree", "Tessellation", "ModelFile"], file.Sections.Select(s => s.Name));
        Assert.All(file.Sections, s => Assert.InRange(s.RemainingBits, 0, 7));
    }

    [Theory]
    [InlineData(7000u)]     // before loops and calculated normals were in the format
    [InlineData(7040u)]
    [InlineData(8137u)]
    [InlineData(15083u)]
    public void FilesOfEveryVersionAreRead(uint version)
    {
        var description = new PrcTestFile { AuthoringVersion = version, UserDataBits = 13 };
        description.Tessellations.Add(PrcTestFile.Tessellation(version, [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0], [0, 3, 6, 3, 9, 6]));
        description.Parts.Add(new PrcTestFile.Part("p", "sheet", 1));
        description.Occurrences.Add(new PrcTestFile.Occurrence("definition", BiasedPart: 1));
        description.Occurrences.Add(new PrcTestFile.Occurrence("instance", BiasedPrototype: 1, Translation: [0, 0, 5]));
        description.Occurrences.Add(new PrcTestFile.Occurrence("root", Children: [1]));

        using Scene scene = PrcReader.Read(description.Build(), cancellationToken: TestContext.Current.CancellationToken);

        SceneInvariants.Check(scene);
        Assert.Equal("-1>root 0>instance 1>sheet#0", Tree(scene));
        Assert.Equal(6, scene.Indices.Length);
        Assert.Equal(4, scene.Positions.Length / 3);
        Assert.Equal(new Vector3(0, 0, 5), scene.GetWorldMatrix(2).Translation);
    }

    [Fact]
    public void WhatIsNotAPrcFileIsRefused()
    {
        Assert.False(PrcReader.IsPrc("U3D\0"u8));
        Assert.Equal(Scene3DError.InvalidValue, Assert.Throws<Scene3DException>(() => PrcReader.Read("%PDF-1.7 and then some more bytes to be longer than a header"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.Truncated, Assert.Throws<Scene3DException>(() => PrcReader.Read(Array.Empty<byte>(), cancellationToken: TestContext.Current.CancellationToken)).Error);
    }

    [Fact]
    public void ReadingStopsWhenAsked()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PrcReader.Read(PrcTestFile.Simple().Build(), null, cancelled.Token));
    }

    [Fact]
    public void TheLimitsOfTheSceneApply()
    {
        byte[] file = PrcTestFile.Simple().Build();
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxNodes = 3 }, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxVertices = 2 }, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxInflatedBytes = 40 }, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxTotalBytes = 64 }, TestContext.Current.CancellationToken)).Error);

        // A name longer than allowed, among names that are not.
        using (PrcReader.Read(file, new SceneLimits { MaxStringLength = 13 }, TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxStringLength = 12 }, TestContext.Current.CancellationToken)).Error);

        // Deeper than allowed: the entities of the file nest six deep, its assembly three.
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxNestingDepth = 2 }, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxMeshes = 0 }, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxMaterials = 2 }, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(Scene3DError.LimitExceeded, Assert.Throws<Scene3DException>(() => PrcReader.Read(file, new SceneLimits { MaxIndices = 2 }, TestContext.Current.CancellationToken)).Error);
    }

    /// <summary>Review focus: a file cut anywhere, or with a section cut anywhere, fails as what it is.</summary>
    [Fact]
    public void AFileOrASectionCutAnywhereFailsCleanly()
    {
        PrcTestFile description = PrcTestFile.Simple();
        byte[] whole = description.Build();
        for (int length = 0; length < whole.Length; length += length < 200 ? 1 : 7)
        {
            var e = Assert.Throws<Scene3DException>(() => PrcReader.Read(whole[..length], cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(e.InnerException is null or InvalidDataException, $"Cut at {length}: {e.InnerException}");
        }

        (byte[] globals, byte[] tree, byte[] tessellation, byte[] modelFile) = description.Sections();
        (string Name, byte[] Data)[] sections = [("globals", globals), ("tree", tree), ("tessellation", tessellation), ("model", modelFile)];
        foreach ((string name, byte[] data) in sections)
        {
            for (int length = 0; length < data.Length - 1; length++)
            {
                var container = new PrcTestContainer { ModelFile = name == "model" ? data[..length] : modelFile };
                container.Structures.Add(new PrcTestContainer.Structure
                {
                    Globals = name == "globals" ? data[..length] : globals,
                    Tree = name == "tree" ? data[..length] : tree,
                    Tessellation = name == "tessellation" ? data[..length] : tessellation,
                });

                var e = Assert.Throws<Scene3DException>(() => PrcReader.Read(container.Build(), cancellationToken: TestContext.Current.CancellationToken));
                Assert.True(e.InnerException is null, $"{name} cut at {length}: {e.InnerException}");
                Assert.Contains(e.Error, new[] { Scene3DError.Truncated, Scene3DError.InvalidValue });
            }
        }
    }

    [Fact]
    public void PicturesAreKnownByTheirOwnHeaders()
    {
        Assert.True(PrcPictures.TryDescribe(0, PrcSceneBuilderTests.PngHeader(640, 480, colourType: 2), out SceneImageEncoding encoding, out SceneTextureChannels channels, out int width, out int height));
        Assert.Equal((SceneImageEncoding.Png, SceneTextureChannels.Rgb, 640, 480), (encoding, channels, width, height));
        Assert.True(PrcPictures.TryDescribe(0, PrcSceneBuilderTests.PngHeader(1, 2, colourType: 6), out _, out channels, out _, out _));
        Assert.Equal(SceneTextureChannels.Rgba, channels);

        // A JPEG: start, an application segment, a quantisation table, then the frame header.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 1, 2, 0xFF, 0xDB, 0, 3, 9, 0xFF, 0xC0, 0, 11, 8, 0x01, 0x00, 0x02, 0x80, 3, 1, 0x11, 0];
        Assert.True(PrcPictures.TryDescribe(1, jpeg, out encoding, out channels, out width, out height));
        Assert.Equal((SceneImageEncoding.Jpeg24, SceneTextureChannels.Rgb, 640, 256), (encoding, channels, width, height));
        jpeg[22] = 1;
        Assert.True(PrcPictures.TryDescribe(1, jpeg, out encoding, out channels, out _, out _));
        Assert.Equal((SceneImageEncoding.Jpeg8, SceneTextureChannels.Luminance), (encoding, channels));

        // Not what the entity says it is; cut short; pixels without a file format.
        Assert.False(PrcPictures.TryDescribe(1, PrcSceneBuilderTests.PngHeader(1, 1, 2), out _, out _, out _, out _));
        Assert.False(PrcPictures.TryDescribe(0, jpeg, out _, out _, out _, out _));
        Assert.False(PrcPictures.TryDescribe(0, PrcSceneBuilderTests.PngHeader(0, 5, 2), out _, out _, out _, out _));
        for (int length = 0; length < 23; length++)       // the frame header ends at byte 23
        {
            Assert.False(PrcPictures.TryDescribe(1, jpeg.AsSpan(0, length), out _, out _, out _, out _));
        }

        Assert.False(PrcPictures.TryDescribe(1, [0xFF, 0xD8, 0xFF, 0xDA, 0, 2], out _, out _, out _, out _));
        Assert.False(PrcPictures.TryDescribe(2, new byte[64], out _, out _, out _, out _));
    }

    /// <summary>
    /// The payloads this reader shows whole: number of nodes, meshes, triangles and materials, as first
    /// read and looked at (previews in the workspace). A change here is a change in what is shown.
    /// </summary>
    private static readonly (string File, int Nodes, int Meshes, int Triangles, int Materials)[] Shown =
    [
        ("Anark_-_NAVAIR", 59, 57, 88_947, 890),         // 56 markups, hidden
        ("PROSTEP-Change_Process_201408__3DData", 1698, 337, 208_380, 235),   // 42 markups, hidden
        ("3dpdf-Auto-Engine", 1193, 596, 402_680, 17),
        ("3dpdf-Industrial-Grinder", 1999, 247, 92_680, 451),
        ("3dpdf-Industrial-Saw", 64, 18, 82_660, 14),
        ("Elysium_PDQ_Report", 17, 16, 29_512, 2),       // a style without a colour no longer makes a grey material of its own
        ("NAVAir-native-Creo", 100, 98, 87_616, 198),     // 97 markups, hidden
        // 71 markups: a node and a mesh each (of lines and triangles) and the one material they share, hidden.
        ("Native-NX-PMI", 74, 72, 48_541, 195),
        ("Release-to-Manufacturing", 4, 2, 9_445, 3),
    ];

    /// <summary>The payloads whose solids have exact geometry only: drawn from it (phase 5 compressed, phase B uncompressed), the scene whole.</summary>
    private static readonly string[] ExactOnly = ["3dpdf-Aero-LandingGear", "Service_documentation"];

    [Fact]
    public void EveryPayloadOfTheCorpusBecomesASceneOrSaysWhatIsMissing()
    {
        int whole = 0, exact = 0, decoded = 0;
        long bytes = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (string path in PrcContainerTests.CorpusFiles())
        {
            byte[] file = File.ReadAllBytes(path);
            bytes += file.Length;
            using Scene scene = PrcReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);
            SceneInvariants.Check(scene);

            string name = Path.GetFileName(path);
            int triangles = scene.Indices.Length / 3;
            SceneBounds bounds = scene.ComputeWorldBounds();
            int missing = scene.Diagnostics.Where(d => d.Code == PrcReader.CompressedTessellationDiagnostic).Sum(d => d.Count);
            output.WriteLine($"{name}: {scene.Nodes.Length} nodes, {scene.Meshes.Length} meshes, {triangles} triangles, {scene.Materials.Length} materials, {scene.Textures.Count} textures; parts missing (compressed) {missing}; {string.Join(", ", scene.Diagnostics.Select(d => $"{d.Code} x{d.Count}"))}");

            (string File, int Nodes, int Meshes, int Triangles, int Materials) pinned = Shown.FirstOrDefault(s => name.Contains(s.File, StringComparison.Ordinal));
            if (pinned.File is not null)
            {
                whole++;
                Assert.True(PrcReader.IsComplete(scene), name);
                Assert.Equal((pinned.Nodes, pinned.Meshes, pinned.Triangles, pinned.Materials), (scene.Nodes.Length, scene.Meshes.Length, triangles, scene.Materials.Length));
                Assert.False(bounds.IsEmpty, name);
                Assert.True(float.IsFinite(bounds.Min.Length() + bounds.Max.Length()), name);
            }
            else if (ExactOnly.Any(w => name.Contains(w, StringComparison.Ordinal)))
            {
                exact++;
                Assert.True(PrcReader.IsComplete(scene), name);
                Assert.True(triangles > 0, name);
                Assert.False(bounds.IsEmpty, name);
                Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "prc-exact-geometry-only");
            }

            else
            {
                // Compressed meshes, all decoded.
                decoded++;
                Assert.True(PrcReader.IsComplete(scene), name);
                Assert.True(triangles > 0, name);
                Assert.False(bounds.IsEmpty, name);
                Assert.True(float.IsFinite(bounds.Min.Length() + bounds.Max.Length()), name);
            }
        }

        output.WriteLine($"{whole} plain and whole, {decoded} compressed and whole, {exact} from exact geometry; {bytes / 1e6:F1} MB in {watch.Elapsed.TotalSeconds:F2} s");
        if (whole + decoded + exact == 38)
        {
            Assert.Equal((9, 27, 2), (whole, decoded, exact));
        }
    }
}
