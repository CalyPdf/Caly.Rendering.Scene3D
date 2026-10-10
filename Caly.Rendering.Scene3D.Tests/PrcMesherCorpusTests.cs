using System.Numerics;
using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Every plain tessellation of the corpus (SCENE3D_PRC_CORPUS; nothing to do without it) as a mesh. The
/// layout of the index array is not checked by any other reader here, so it is checked by what only a
/// right layout gives: indices that all land on elements of their arrays, faces that use the index
/// array exactly once, and triangles that face the way their stored normals point.
/// </summary>
public class PrcMesherCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryPlainTessellationOfTheCorpusBecomesAMesh()
    {
        foreach (string path in PrcContainerTests.CorpusFiles())
        {
            byte[] file = File.ReadAllBytes(path);
            using PrcContainer container = PrcContainer.Open(file);
            PrcFile read = PrcFileReader.Read(container, cancellation: TestContext.Current.CancellationToken);

            using var builder = new SceneBuilder();
            int tessellations = 0, stored = 0, calculated = 0, textured = 0, colored = 0, styled = 0, loose = 0;
            long triangles = 0;
            var kinds = new SortedDictionary<uint, int>();
            var creases = new SortedSet<double>();
            bool contiguous = true;
            foreach (PrcFileStructureData structure in read.FileStructures)
            {
                foreach (PrcTess tess in structure.Tessellation.Tess)
                {
                    if (tess.Id is not PrcTess3D plain)
                    {
                        continue;
                    }

                    tessellations++;
                    PrcMesh mesh;
                    try
                    {
                        mesh = PrcMesher.AddMesh(builder, plain, builder.AddName("t"), TestContext.Current.CancellationToken);
                    }
                    catch (Scene3DException)
                    {
                        // What the tessellation looks like, for whoever has to find out why.
                        output.WriteLine($"{Path.GetFileName(path)}: tessellation {tessellations} fails: {plain.TessellationCoordinates.Coordinates.Length} coordinates, {plain.NormalCoordinates.Length} normals, {plain.TextureCoordinates.Length} texture coordinates, {plain.TriangulatedIndexArray.Length} indices, calculate normals {plain.MustCalculateNormals}, {plain.FaceTessellationData.Length} faces");
                        foreach (PrcTessFace face in plain.FaceTessellationData.Take(6))
                        {
                            output.WriteLine($"  face: kinds 0x{face.UsedEntitiesFlag:X}, start {face.StartTriangulated}, sizes [{string.Join(", ", face.Triangulateddata.Take(12))}], texture indices {face.NumberOfTexturedCoordinateIndices}, styles [{string.Join(", ", face.LineAttributes.Take(6))}], colours {face.VertexColorData?.Colors.Length}");
                        }

                        output.WriteLine($"  indices: {string.Join(" ", plain.TriangulatedIndexArray.Take(40))}");
                        throw;
                    }

                    triangles += mesh.TriangleCount;
                    styled += mesh.Slots.Length > 0 ? 1 : 0;
                    if (plain.MustCalculateNormals)
                    {
                        calculated++;
                        creases.Add(Math.Round(plain.CreaseAngle, 4));
                    }
                    else
                    {
                        stored++;
                    }

                    // The faces together use the whole index array, once.
                    long used = 0;
                    long[] starts = PrcMesher.FaceStarts(plain);
                    for (int f = 0; f < plain.FaceTessellationData.Length; f++)
                    {
                        PrcTessFace face = plain.FaceTessellationData[f];
                        contiguous &= starts[f] == used;
                        kinds[face.UsedEntitiesFlag] = kinds.GetValueOrDefault(face.UsedEntitiesFlag) + 1;
                        colored += face.VertexColorData is { Colors.Length: > 0 } ? 1 : 0;
                        foreach (PrcPrimitive primitive in PrcFaceLayout.Primitives(face.UsedEntitiesFlag, face.Triangulateddata, starts[f], !plain.MustCalculateNormals, (int)face.NumberOfTexturedCoordinateIndices))
                        {
                            used += primitive.IndexCount;
                            textured += primitive.TextureIndices > 0 ? 1 : 0;
                        }
                    }

                    loose += used == plain.TriangulatedIndexArray.Length ? 0 : 1;
                }
            }

            if (tessellations == 0)
            {
                continue;
            }

            using Scene scene = builder.Build();
            long agree = 0, disagree = 0, flat = 0;
            foreach (SceneMesh mesh in scene.Meshes)
            {
                ReadOnlySpan<float> positions = scene.Positions.Slice(mesh.VertexStart * 3, mesh.VertexCount * 3);
                ReadOnlySpan<float> normals = scene.Normals.Slice(mesh.NormalStart * 3, mesh.VertexCount * 3);
                for (int p = mesh.FirstPart; p < mesh.FirstPart + mesh.PartCount; p++)
                {
                    ReadOnlySpan<uint> indices = scene.Indices.Slice(scene.Parts[p].IndexStart, scene.Parts[p].IndexCount);
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        int a = (int)indices[i] * 3, b = (int)indices[i + 1] * 3, c = (int)indices[i + 2] * 3;
                        var pa = new Vector3(positions.Slice(a, 3));
                        Vector3 geometric = Vector3.Cross(new Vector3(positions.Slice(b, 3)) - pa, new Vector3(positions.Slice(c, 3)) - pa);
                        Vector3 shading = new Vector3(normals.Slice(a, 3)) + new Vector3(normals.Slice(b, 3)) + new Vector3(normals.Slice(c, 3));
                        float dot = Vector3.Dot(geometric, shading);
                        if (dot > 0)
                        {
                            agree++;
                        }
                        else if (dot < 0)
                        {
                            disagree++;
                        }
                        else
                        {
                            flat++;
                        }
                    }
                }
            }

            Assert.All(scene.Positions.ToArray(), value => Assert.True(float.IsFinite(value)));
            output.WriteLine($"{Path.GetFileName(path)}: {tessellations} tessellations ({stored} with normals, {calculated} to calculate, crease {string.Join("/", creases)}), {triangles} triangles, {scene.Positions.Length / 3} vertices; kinds {string.Join(" ", kinds.Select(k => $"0x{k.Key:X}:{k.Value}"))}; textured primitives {textured}, faces with colours {colored}, tessellations with styles {styled}; not using their index array exactly: {loose}; faces one after the other in file order: {contiguous}; triangles facing their normals {agree}, against {disagree}, without area {flat}; diagnostics {scene.Diagnostics.Count}");

            // A wrong layout or a wrong turn shows as many triangles facing away from their normals.
            Assert.True(disagree * 50 <= agree + disagree, $"{Path.GetFileName(path)}: {disagree} of {agree + disagree} triangles face away from their normals.");
        }
    }
}
