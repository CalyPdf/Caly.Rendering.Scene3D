using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Generated;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The uncompressed bodies of real files (SCENE3D_PRC_CORPUS and SCENE3D_PRC_CORPUS_EXTRA; nothing to do without them): every one
/// converts, and most of their faces are cut into triangles.
/// </summary>
public class PrcBrepDataCorpusTests(ITestOutputHelper output)
{
    /// <summary>Every uncompressed body of every payload, with the tables of its context and its tolerance.</summary>
    internal static IEnumerable<(string File, PrcTopoBrepData Body, PrcBrepDataConverter.Tables Tables, double Tolerance)> Bodies()
    {
        foreach (string file in PrcContainerTests.CorpusFiles().Concat(PrcCompressedCorpusTests.ExtraCorpusFiles()))
        {
            PrcFile read;
            try
            {
                using PrcContainer container = PrcContainer.Open(File.ReadAllBytes(file));
                read = PrcFileReader.Read(container, geometry: PrcGeometryReading.Always);
            }
            catch (Scene3DException)
            {
                continue;
            }

            foreach (PrcFileStructureData structure in read.FileStructures)
            {
                foreach (PrcTopoContext context in structure.ExactGeometry?.TopoContexts ?? [])
                {
                    if (!context.Bodies.Any(b => b.Id is PrcTopoBrepData))
                    {
                        continue;
                    }

                    PrcBrepDataConverter.Tables tables = PrcBrepDataConverter.Index(context);
                    foreach (PrcTopoBody body in context.Bodies)
                    {
                        if (body.Id is PrcTopoBrepData data)
                        {
                            yield return (Path.GetFileName(file), data, tables, context.Tolerance);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryUncompressedBodyConvertsAndMostOfItsFacesAreCut()
    {
        long bodies = 0, faces = 0, dropped = 0, unsupported = 0;
        var failures = new List<string>();
        foreach ((string file, PrcTopoBrepData data, PrcBrepDataConverter.Tables tables, double tolerance) in Bodies())
        {
            bodies++;
            if (!PrcBrepDataConverter.TryConvert(data, tables, tolerance, out PrcTopoBrepDataCompress body, out string? error))
            {
                failures.Add($"{file}: {error}");
                continue;
            }

            PrcBrepTessellation r = PrcBrepCorpusTests.Tessellate(body, out _);
            faces += r.Faces;
            dropped += r.FacesDropped;
            unsupported += r.FacesUnsupported;
        }

        if (bodies == 0)
        {
            return;
        }

        output.WriteLine($"{bodies} bodies, {faces} faces, {dropped} dropped, {unsupported} unsupported ({100.0 * dropped / Math.Max(faces, 1):F1}% dropped)");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
        // A floor, not a goal (0.6% when last measured).
        Assert.True(dropped < faces * 0.01, $"{dropped} of {faces} faces dropped");
    }
}
