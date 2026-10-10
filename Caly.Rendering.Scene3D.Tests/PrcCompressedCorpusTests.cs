using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The compressed tessellations of real files (SCENE3D_PRC_CORPUS, and the larger second set of
/// SCENE3D_PRC_CORPUS_EXTRA; nothing to do without them). There is no other decoder to compare with, so
/// what is checked is what only a right reading gives: arrays whose sizes follow from each other, and
/// geometry that is where the file says it is.
/// </summary>
public class PrcCompressedCorpusTests(ITestOutputHelper output)
{
    /// <summary>A second, larger set of payloads gathered from the web (the prc-db collection); not pinned.</summary>
    internal static IReadOnlyList<string> ExtraCorpusFiles()
    {
        var files = new List<string>();
        foreach (string folder in (Environment.GetEnvironmentVariable("SCENE3D_PRC_CORPUS_EXTRA") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(folder))
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*.prc", SearchOption.AllDirectories));
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IEnumerable<PrcTess3DCompressed> Compressed(PrcFile file)
    {
        foreach (PrcFileStructureData structure in file.FileStructures)
        {
            foreach (PrcTess tess in structure.Tessellation.Tess)
            {
                if (tess.Id is PrcTess3DCompressed compressed)
                {
                    yield return compressed;
                }
            }
        }
    }

    /// <summary>What must hold of the arrays of any compressed tessellation, whatever it describes.</summary>
    private static void AssertKeptWhole(PrcTess3DCompressed tess, string where, bool textureAddsUp = true)
    {
        int triangles = tess.TriangleCount;
        // One value a triangle. Most writers store three times as many and leave the rest at zero.
        Assert.True(tess.EdgeStatus.Length == triangles || tess.EdgeStatus.Length == 3 * triangles, $"{where}: {tess.EdgeStatus.Length} neighbour flags for {triangles} triangles");
        Assert.True(tess.EdgeStatus.AsSpan(triangles).IndexOfAnyExcept((byte)0) < 0, $"{where}: a neighbour flag past the triangles' own");
        Assert.True(Array.TrueForAll(tess.EdgeStatus, s => s <= 3), $"{where}: a neighbour flag above 3");
        Assert.True(tess.Points.Length % 3 == 0, $"{where}: {tess.Points.Length} point integers");
        if (tess.Texture is { } texture && textureAddsUp)
        {
            // One bit per corner that has texture coordinates: set for a reference, clear for a pair stored.
            Assert.True(texture.Bits.CountSet() == texture.References.Length, $"{where}: {texture.Bits.CountSet()} texture bits set for {texture.References.Length} references");
            Assert.True(texture.Bits.Count - texture.References.Length == texture.Parameters.Length / 2 && texture.Parameters.Length % 2 == 0, $"{where}: {texture.Bits.Count} texture bits, {texture.References.Length} references, {texture.Parameters.Length} parameters");
        }
        Assert.True(tess.PointReferences.Length == tess.PointIsReference.CountSet(), $"{where}: {tess.PointReferences.Length} references for {tess.PointIsReference.CountSet()} flags set");

        // The points stored are the points met that are not references; the flags number T + 2 C.
        Assert.True(tess.Points.Length / 3 == tess.PointIsReference.Count - tess.PointReferences.Length, $"{where}: {tess.Points.Length / 3} points, {tess.PointIsReference.Count} flags, {tess.PointReferences.Length} references");
        int surplus = tess.PointIsReference.Count - triangles;
        Assert.True(triangles == 0 ? surplus == 0 : surplus > 0 && surplus % 2 == 0, $"{where}: {tess.PointIsReference.Count} flags for {triangles} triangles");

        if (tess.MustRecalculateNormals)
        {
            Assert.True(tess.NormalIsReversed.Count == triangles, $"{where}: reversed bits");
        }
        else
        {
            Assert.True(tess.NormalAngleBits is > 0 and <= 16, $"{where}: {tess.NormalAngleBits} bits an angle");
            Assert.True(tess.IsFacePlanar.Count == tess.FaceCount, $"{where}: planar bits");
            Assert.True(tess.NormalAngles.Length % 2 == 0, $"{where}: {tess.NormalAngles.Length} angles");
            int limit = 1 << tess.NormalAngleBits;
            Assert.True(Array.TrueForAll(tess.NormalAngles, a => a < limit), $"{where}: an angle of more than {tess.NormalAngleBits} bits");
        }
    }

    [Fact]
    public void EveryCompressedTessellationIsKeptWhole()
    {
        IReadOnlyList<string> files = PrcContainerTests.CorpusFiles();
        if (files.Count == 0)
        {
            return;
        }

        int tessellations = 0, payloads = 0;
        long triangles = 0;
        var stylesPerFace = new SortedDictionary<string, int>();
        foreach (string path in files)
        {
            using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(path));
            PrcFile file = PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken);
            int before = tessellations;
            foreach (PrcTess3DCompressed tess in Compressed(file))
            {
                tessellations++;
                triangles += tess.TriangleCount;
                AssertKeptWhole(tess, $"{Path.GetFileName(path)} #{tessellations - before}");

                // How the per-face arrays are sized, for the decoding note.
                string key = $"styles {Relation(tess.LineAttributes.Length, tess)}, behaviours {Relation(tess.Behaviors.Length, tess)}, faces {(tess.HasFaces ? "yes" : "no")}";
                stylesPerFace[key] = stylesPerFace.GetValueOrDefault(key) + 1;
            }

            payloads += tessellations > before ? 1 : 0;
        }

        foreach ((string key, int count) in stylesPerFace)
        {
            output.WriteLine($"{count,5}  {key}");
        }

        output.WriteLine($"{tessellations} compressed tessellations in {payloads} payloads, {triangles} triangles");

        // The census of the first corpus (plans/phase4-logs/census.txt).
        Assert.Equal((3780, 27, 6423315L), (tessellations, payloads, triangles));

        static string Relation(int length, PrcTess3DCompressed tess) =>
            length == 0 ? "none" : length == tess.FaceCount ? "= faces" : length == 1 ? "= 1" : length == tess.TriangleCount ? "= triangles" : length < tess.FaceCount ? "< faces" : "> faces";
    }

    /// <summary>
    /// The first set, decoded: how many tessellations are used up exactly by their own triangles, and how
    /// many of those sit where their origin says (this writer puts it at the middle of the points' box).
    /// The numbers are what the decoder reaches today, pinned so that a change of arithmetic shows.
    /// </summary>
    [Fact]
    public void TheCorpusDecodes()
    {
        IReadOnlyList<string> files = PrcContainerTests.CorpusFiles();
        if (files.Count == 0)
        {
            return;
        }

        int calculated = 0, calculatedDecoded = 0, calculatedOff = 0, stored = 0, storedDecoded = 0, storedOff = 0, whole = 0, payloads = 0, made = 0;
        int storedOutward = 0, storedInward = 0, calculatedOutward = 0, calculatedInward = 0;
        foreach (string path in files)
        {
            using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(path));
            int bad = 0, all = 0;
            foreach (PrcTess3DCompressed tess in Compressed(PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken)))
            {
                all++;
                bool isStored = !tess.MustRecalculateNormals;
                _ = isStored ? stored++ : calculated++;
                try
                {
                    using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);
                    _ = isStored ? storedDecoded++ : calculatedDecoded++;

                    // Which way the triangles face: the volume they enclose, counted with their winding.
                    double volume = 0;
                    for (int c = 0; c < geometry.TriangleCount * 3; c += 3)
                    {
                        ReadOnlySpan<double> p0 = geometry.Points.AsSpan(geometry.Corners[c] * 3, 3), p1 = geometry.Points.AsSpan(geometry.Corners[c + 1] * 3, 3), p2 = geometry.Points.AsSpan(geometry.Corners[c + 2] * 3, 3);
                        double ox = tess.OriginX, oy = tess.OriginY, oz = tess.OriginZ;
                        double ax = p0[0] - ox, ay = p0[1] - oy, az = p0[2] - oz, bx = p1[0] - ox, by = p1[1] - oy, bz = p1[2] - oz, cx = p2[0] - ox, cy = p2[1] - oy, cz = p2[2] - oz;
                        volume += (ax * ((by * cz) - (bz * cy))) + (ay * ((bz * cx) - (bx * cz))) + (az * ((bx * cy) - (by * cx)));
                    }

                    if (isStored)
                    {
                        _ = volume > 0 ? storedOutward++ : storedInward++;
                    }
                    else
                    {
                        _ = volume > 0 ? calculatedOutward++ : calculatedInward++;
                    }

                    made += geometry.MadeFrames;
                    if (geometry.OffOrigin)
                    {
                        _ = isStored ? storedOff++ : calculatedOff++;
                        bad++;
                    }

                    // Every stored normal is of length one, within what ten-bit angles and four terms give.
                    for (int i = 0; i < geometry.NormalCount * 3; i += 3)
                    {
                        double length = Math.Sqrt((geometry.Normals[i] * geometry.Normals[i]) + (geometry.Normals[i + 1] * geometry.Normals[i + 1]) + (geometry.Normals[i + 2] * geometry.Normals[i + 2]));
                        Assert.InRange(length, 0.999, 1.001);
                    }
                }
                catch (Scene3DException)
                {
                    bad++;
                }
            }

            payloads += all > 0 ? 1 : 0;
            whole += all > 0 && bad == 0 ? 1 : 0;
        }

        output.WriteLine($"calculated {calculatedDecoded} of {calculated} ({calculatedOff} off their origin); stored {storedDecoded} of {stored} ({storedOff} off); {whole} of {payloads} payloads whole; {made} made frames");
        output.WriteLine($"facing: stored {storedOutward} enclose a volume, {storedInward} a negative one; calculated {calculatedOutward} and {calculatedInward}");
        Assert.Equal((1049, 1049, 0), (calculated, calculatedDecoded, calculatedOff));
        Assert.Equal((2731, 2731, 0), (stored, storedDecoded, storedOff));
        Assert.Equal((27, 27), (payloads, whole));
    }

    /// <summary>
    /// The texture coordinates of the first set: every tessellation that has them and decodes gives each
    /// corner of its textured faces a pair, using up the bits, the references and the pairs exactly.
    /// (A reference counts from the first pair stored for its point: read the other way, the 38,663
    /// triangles that differ span 2.7 times as much of the texture.)
    /// </summary>
    [Fact]
    public void TextureCoordinatesAreUsedUpExactly()
    {
        IReadOnlyList<string> files = PrcContainerTests.CorpusFiles();
        if (files.Count == 0)
        {
            return;
        }

        int decoded = 0, dropped = 0;
        foreach (string path in files)
        {
            using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(path));
            foreach (PrcTess3DCompressed tess in Compressed(PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken)).Where(t => t.Texture is not null))
            {
                PrcCompressedGeometry geometry;
                try
                {
                    geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);
                }
                catch (Scene3DException)
                {
                    continue;
                }

                using (geometry)
                {
                    _ = geometry.TextureDropped ? dropped++ : decoded++;
                    for (int c = 0; c < geometry.CornerTextures.AsSpan(0, geometry.TextureDropped ? 0 : geometry.TriangleCount * 3).Length; c++)
                    {
                        Assert.InRange(geometry.CornerTextures[c], -1, geometry.TextureCount - 1);
                    }
                }
            }
        }

        output.WriteLine($"{decoded} tessellations with texture coordinates decoded, {dropped} dropped");
        Assert.Equal((1353, 0), (decoded, dropped));
    }

    /// <summary>
    /// Real tessellations with their arrays damaged at random: each is decoded or refused, and nothing
    /// else ever comes out (no other exception, no read outside an array, no endless walk).
    /// </summary>
    [Fact]
    public void DamagedTessellationsAreDecodedOrRefused()
    {
        IReadOnlyList<string> files = PrcContainerTests.CorpusFiles();
        if (files.Count == 0)
        {
            return;
        }

        int rounds = int.TryParse(Environment.GetEnvironmentVariable("SCENE3D_FUZZ_MUTATIONS"), out int n) && n > 0 ? n : 40;
        var random = new Random(20261011);
        int tried = 0, decoded = 0, refused = 0, taken = 0;
        foreach (string path in files)
        {
            using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(path));
            foreach (PrcTess3DCompressed tess in Compressed(PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken)).Where(t => t.TriangleCount is > 0 and < 3000).Take(6))
            {
                taken++;
                for (int round = 0; round < rounds; round++)
                {
                    PrcTess3DCompressed damaged = Damage(tess, random);
                    tried++;
                    try
                    {
                        using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(damaged, TestContext.Current.CancellationToken);
                        decoded++;
                        Assert.Equal(damaged.TriangleCount, geometry.TriangleCount);
                        for (int c = 0; c < geometry.TriangleCount * 3; c++)
                        {
                            Assert.InRange(geometry.Corners[c], 0, geometry.PointCount - 1);
                            Assert.InRange(geometry.CornerNormals[c], -1, geometry.NormalCount - 1);
                        }
                    }
                    catch (Scene3DException)
                    {
                        refused++;
                    }
                }
            }
        }

        output.WriteLine($"{taken} tessellations, {tried} damaged copies: {decoded} decoded, {refused} refused");
        Assert.True(refused > 0 && tried > 0);

        static PrcTess3DCompressed Damage(PrcTess3DCompressed t, Random random)
        {
            int[] points = (int[])t.Points.Clone();
            byte[] status = (byte[])t.EdgeStatus.Clone();
            int[] faces = (int[])t.TriangleFaces.Clone();
            int[] references = (int[])t.PointReferences.Clone();
            ushort[] angles = (ushort[])t.NormalAngles.Clone();
            PrcBits flags = t.PointIsReference, reversed = t.NormalIsReversed, normalBits = t.NormalBits, planar = t.IsFacePlanar;
            for (int change = random.Next(1, 4); change > 0; change--)
            {
                switch (random.Next(9))
                {
                    case 0 when points.Length > 0: points[random.Next(points.Length)] = random.Next(-50000, 50000); break;
                    case 1 when status.Length > 0: status[random.Next(Math.Min(status.Length, t.TriangleCount))] = (byte)random.Next(4); break;
                    case 2 when faces.Length > 0: faces[random.Next(faces.Length)] = random.Next(-2, t.FaceCount + 3); break;
                    case 3 when references.Length > 0: references[random.Next(references.Length)] = random.Next(-3, t.Points.Length / 3 + 3); break;
                    case 4 when angles.Length > 0: angles[random.Next(angles.Length)] = (ushort)random.Next(1 << 16); break;
                    case 5: flags = Flip(flags, random); break;
                    case 6: reversed = Flip(reversed, random); break;
                    case 7: normalBits = Flip(normalBits, random); break;
                    default: planar = Flip(planar, random); break;
                }
            }

            return new PrcTess3DCompressed
            {
                IsCalculated = t.IsCalculated,
                HasFaces = t.HasFaces,
                Tolerance = random.Next(40) == 0 ? double.NaN : t.Tolerance,
                HasOrigin = t.HasOrigin,
                OriginX = random.Next(40) == 0 ? float.PositiveInfinity : t.OriginX,
                OriginY = t.OriginY,
                OriginZ = t.OriginZ,
                Points = random.Next(30) == 0 ? points[..(points.Length / 2)] : points,
                EdgeStatus = status,
                TriangleFaces = faces,
                FaceCount = t.FaceCount,
                PointIsReference = flags,
                PointReferences = references,
                MustRecalculateNormals = t.MustRecalculateNormals,
                NormalIsReversed = reversed,
                CreaseAngle = t.CreaseAngle,
                NormalAngleBits = random.Next(40) == 0 ? random.Next(0, 40) : t.NormalAngleBits,
                NormalBits = normalBits,
                NormalAngles = angles,
                IsFacePlanar = planar,
                IsPointColorOnFace = t.IsPointColorOnFace,
                PointColors = t.PointColors,
                IsMultipleLineAttributeOnFace = t.IsMultipleLineAttributeOnFace,
                LineAttributes = t.LineAttributes,
                Texture = t.Texture,
                FaceHasTexture = t.FaceHasTexture,
                Behaviors = t.Behaviors,
            };
        }

        static PrcBits Flip(PrcBits bits, Random random)
        {
            if (bits.Count == 0)
            {
                return bits;
            }

            var words = new uint[(bits.Count + 31) / 32];
            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    words[i >> 5] |= 0x80000000u >> (i & 31);
                }
            }

            int at = random.Next(bits.Count);
            words[at >> 5] ^= 0x80000000u >> (at & 31);
            return new PrcBits(words, random.Next(20) == 0 ? bits.Count / 2 : bits.Count);
        }
    }

    /// <summary>
    /// Colours on points (the second set has them, the first none): five bytes a corner come out even, and
    /// the order of the corners is the walk's: a point then has one colour in nearly all the triangles
    /// that meet at it, as the colours of a result painted on a mesh do.
    /// </summary>
    [Fact]
    public void ColoursOnPointsAreUsedUpExactlyAndAgreeAroundAPoint()
    {
        int coloured = 0, dropped = 0;
        long corners = 0, agreeing = 0;
        foreach (string path in ExtraCorpusFiles())
        {
            using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(path));
            foreach (PrcTess3DCompressed tess in Compressed(PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken)).Where(t => t.PointColors.Length > 0))
            {
                PrcCompressedGeometry geometry;
                try
                {
                    geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);
                }
                catch (Scene3DException)
                {
                    continue;
                }

                using (geometry)
                {
                    coloured++;
                    if (geometry.ColorsDropped)
                    {
                        dropped++;
                        continue;
                    }

                    // The first colour met at each point, against the others met there.
                    var first = new Dictionary<int, int>();
                    for (int c = 0; c < geometry.TriangleCount * 3; c++)
                    {
                        int color = geometry.CornerColors[c];
                        if (color == -1)
                        {
                            continue;
                        }

                        corners++;
                        if (first.TryGetValue(geometry.Corners[c], out int seen))
                        {
                            agreeing += seen == color ? 1 : 0;
                        }
                        else
                        {
                            first.Add(geometry.Corners[c], color);
                            agreeing++;
                        }
                    }
                }
            }
        }

        output.WriteLine($"{coloured} tessellations with colours on points, {dropped} dropped; {agreeing} of {corners} corners have the colour first met at their point");
        if (coloured == 27)
        {
            Assert.Equal(0, dropped);
            Assert.True(agreeing > corners * 0.9, $"{agreeing} of {corners}");
        }
    }

    /// <summary>The second set, decoded: nothing but a refusal ever comes out of a tessellation that does not decode.</summary>
    [Fact]
    public void TheExtraCorpusDecodesOrIsRefused()
    {
        int decoded = 0, refused = 0, offOrigin = 0, doubtful = 0, withMade = 0;
        foreach (string path in ExtraCorpusFiles())
        {
            PrcFile file;
            try
            {
                using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(path));
                file = PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken);
            }
            catch (Scene3DException)
            {
                continue;
            }

            foreach (PrcTess3DCompressed tess in Compressed(file))
            {
                try
                {
                    using PrcCompressedGeometry geometry = PrcCompressedDecoder.Decode(tess, TestContext.Current.CancellationToken);
                    decoded++;
                    offOrigin += geometry.OffOrigin ? 1 : 0;
                    doubtful += geometry.OffOrigin && geometry.MadeFrames > 0 ? 1 : 0;
                    withMade += geometry.MadeFrames > 0 ? 1 : 0;
                }
                catch (Scene3DException)
                {
                    refused++;
                }
            }
        }

        output.WriteLine($"second set: {decoded} decoded, {refused} refused; {offOrigin} away from their origin, {withMade} with points placed after flat triangles, {doubtful} both");
        if (decoded + refused == 34464)
        {
            // The set the numbers were taken on: all of it decodes. Those away from their origin are whole
            // files of writers that put the origin elsewhere, with or without flat triangles.
            Assert.Equal((34464, 0), (decoded, refused));
        }
    }

    /// <summary>
    /// No payload of either set comes out as meshes that are all fully transparent: that is a model drawn
    /// as nothing (17 payloads of the second set did, their writer leaving every alpha of a material at 0).
    /// </summary>
    [Fact]
    public void NoPayloadIsDrawnAsNothing()
    {
        int scenes = 0, clearMaterials = 0;
        foreach (string path in PrcContainerTests.CorpusFiles().Concat(ExtraCorpusFiles()))
        {
            Scene scene;
            try
            {
                scene = PrcReader.Read(File.ReadAllBytes(path), cancellationToken: TestContext.Current.CancellationToken);
            }
            catch (Scene3DException)
            {
                continue;
            }

            using (scene)
            {
                if (scene.Meshes.Length == 0 || scene.Materials.Length == 0)
                {
                    continue;
                }

                scenes++;
                int clear = 0;
                foreach (SceneMaterial material in scene.Materials)
                {
                    clear += material.Opacity <= 0 ? 1 : 0;
                }

                clearMaterials += clear;
                Assert.True(clear < scene.Materials.Length, $"{Path.GetFileName(path)}: all {scene.Materials.Length} materials are fully transparent");
            }
        }

        output.WriteLine($"{scenes} scenes with meshes; {clearMaterials} materials fully transparent among them");
    }

    /// <summary>The second set: every payload either reads or is refused as a file can be, and what reads holds together.</summary>
    [Fact]
    public void TheExtraCorpusIsReadOrRefused()
    {
        IReadOnlyList<string> files = ExtraCorpusFiles();
        if (files.Count == 0)
        {
            return;
        }

        int read = 0, refused = 0, tessellations = 0;
        long triangles = 0;
        var reasons = new SortedDictionary<string, int>();
        foreach (string path in files)
        {
            PrcFile file;
            try
            {
                using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(path));
                file = PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken);
            }
            catch (Scene3DException e)
            {
                refused++;
                string reason = e.Message.Length > 70 ? e.Message[..70] : e.Message;
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                continue;
            }

            read++;
            int index = 0;
            foreach (PrcTess3DCompressed tess in Compressed(file))
            {
                tessellations++;
                triangles += tess.TriangleCount;
                // Another writer's texture data does not add up the same way (PDF3D): not asserted here.
                AssertKeptWhole(tess, $"{Path.GetFileName(path)} #{++index}", textureAddsUp: false);
            }
        }

        output.WriteLine($"{files.Count} payloads: {read} read, {refused} refused; {tessellations} compressed tessellations, {triangles} triangles");
        foreach ((string reason, int count) in reasons)
        {
            output.WriteLine($"{count,5}  {reason}");
        }
    }
}
