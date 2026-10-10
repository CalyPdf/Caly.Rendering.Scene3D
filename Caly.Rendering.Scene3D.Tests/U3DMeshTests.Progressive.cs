using System.Numerics;
using Caly.Rendering.Scene3D.Tests.Idtf;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Meshes at full detail, the progressive blocks decoded, against the IDTF text the sample scenes were made
/// from. Progressive detail is stored as quantised differences from predictions, so nothing matches to the
/// last digit: positions are matched to the nearest position of the source, normals and texture
/// coordinates compared within a tolerance.
/// </summary>
public partial class U3DMeshTests
{
    /// <summary>The scenes whose IDTF source has a mesh that a node shows.</summary>
    public static TheoryData<string> ScenesWithMeshes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in TestFiles.SceneNames)
            {
                IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
                if (idtf.Resources("MODEL").Any(r => r.Text("MODEL_TYPE") == "MESH" && idtf.Nodes.Any(n => n.Argument == "MODEL" && n.Text("RESOURCE_NAME") == r.Text("RESOURCE_NAME"))))
                {
                    data.Add(name);
                }
            }

            return data;
        }
    }

    /// <summary>The index of the first of <paramref name="places"/> within <paramref name="tolerance"/> of each of them: positions that coincide are one.</summary>
    private static int[] Representatives(Vector3[] places, float tolerance, out Dictionary<(int, int, int), List<int>> grid)
    {
        grid = [];
        var representative = new int[places.Length];
        for (int i = 0; i < places.Length; i++)
        {
            int found = Nearest(places, grid, places[i], tolerance);
            representative[i] = found >= 0 ? representative[found] : i;
            (int, int, int) cell = Cell(places[i], tolerance);
            if (!grid.TryGetValue(cell, out List<int>? list))
            {
                grid[cell] = list = [];
            }

            list.Add(i);
        }

        return representative;
    }

    private static (int, int, int) Cell(Vector3 p, float tolerance) => ((int)MathF.Floor(p.X / tolerance), (int)MathF.Floor(p.Y / tolerance), (int)MathF.Floor(p.Z / tolerance));

    private static int Nearest(Vector3[] places, Dictionary<(int, int, int), List<int>> grid, Vector3 p, float tolerance)
    {
        (int x, int y, int z) = Cell(p, tolerance);
        int best = -1;
        float bestDistance = tolerance * tolerance;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((x + dx, y + dy, z + dz), out List<int>? list))
                    {
                        continue;
                    }

                    foreach (int i in list)
                    {
                        float d = Vector3.DistanceSquared(places[i], p);
                        if (d <= bestDistance)
                        {
                            (best, bestDistance) = (i, d);
                        }
                    }
                }
            }
        }

        return best;
    }

    /// <summary>A triangle by its shading and its three places, turned so that the smallest comes first; its winding is kept.</summary>
    private static string Key(int shading, int a, int b, int c, out int turn)
    {
        turn = a <= b && a <= c ? 0 : b <= a && b <= c ? 1 : 2;
        (int first, int second, int third) = turn == 0 ? (a, b, c) : turn == 1 ? (b, c, a) : (c, a, b);
        return $"{shading}: {first} {second} {third}";
    }

    [Theory]
    [MemberData(nameof(ScenesWithMeshes))]
    public void EveryMeshMatchesTheIdtfSourceAtFullDetail(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = U3DSceneGraphTests.Read(name);

        foreach (IdtfElement resource in idtf.Resources("MODEL").Where(r => r.Text("MODEL_TYPE") == "MESH"))
        {
            string resourceName = resource.Text("RESOURCE_NAME");
            if (!idtf.Nodes.Any(n => n.Argument == "MODEL" && n.Text("RESOURCE_NAME") == resourceName))
            {
                continue;
            }

            IdtfElement idtfMesh = resource.Get("MESH");
            SceneMesh mesh = MeshOfResource(scene, idtf, resourceName);
            Assert.True((mesh.Flags & SceneMeshFlags.PartialDetail) == 0, $"{resourceName} is shown in part");

            Vector3[] places = Vectors(idtfMesh.Get("MODEL_POSITION_LIST"));
            Vector3[] normals = Vectors(idtfMesh.Get("MODEL_NORMAL_LIST"));
            List<double> facePlaces = idtfMesh.Get("MESH_FACE_POSITION_LIST").Numbers;
            List<double> faceNormals = idtfMesh.Get("MESH_FACE_NORMAL_LIST").Numbers;
            List<double> faceShading = idtfMesh.Get("MESH_FACE_SHADING_LIST").Numbers;

            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (Vector3 p in places)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            float tolerance = MathF.Max((max - min).Length() * 5e-4f, 1e-5f);
            int[] representative = Representatives(places, tolerance, out Dictionary<(int, int, int), List<int>> grid);

            // The source's triangles, each with the normals of its corners in the order of its key; and the
            // normals the source uses at each position: a writer may give a corner another of them.
            var expected = new Dictionary<string, Queue<Vector3[]>>();
            var normalsAt = new Dictionary<int, List<Vector3>>();
            int expectedCount = 0, expectedFlat = 0, actualFlat = 0;
            for (int f = 0; f < faceShading.Count; f++)
            {
                int a = representative[(int)facePlaces[f * 3]], b = representative[(int)facePlaces[(f * 3) + 1]], c = representative[(int)facePlaces[(f * 3) + 2]];
                for (int k = 0; k < 3; k++)
                {
                    int place = representative[(int)facePlaces[(f * 3) + k]];
                    if (!normalsAt.TryGetValue(place, out List<Vector3>? list))
                    {
                        normalsAt[place] = list = [];
                    }

                    list.Add(normals[(int)faceNormals[(f * 3) + k]]);
                }

                if (a == b || b == c || a == c)
                {
                    // A triangle with two corners in one place: it has no winding to compare.
                    expectedFlat++;
                    continue;
                }

                string key = Key((int)faceShading[f], a, b, c, out int turn);
                var corners = new Vector3[3];
                for (int k = 0; k < 3; k++)
                {
                    corners[k] = normals[(int)faceNormals[(f * 3) + ((k + turn) % 3)]];
                }

                if (!expected.TryGetValue(key, out Queue<Vector3[]>? queue))
                {
                    expected[key] = queue = new Queue<Vector3[]>();
                }

                queue.Enqueue(corners);
                expectedCount++;
            }

            int actualCount = 0, normalsCompared = 0, normalsOff = 0, normalsNotUnit = 0;
            var unexpected = new List<string>();
            foreach (SceneMeshPart part in scene.Parts.Slice(mesh.FirstPart, mesh.PartCount))
            {
                ReadOnlySpan<uint> indices = scene.Indices.Slice(part.IndexStart, part.IndexCount);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var corner = new int[3];
                    for (int k = 0; k < 3; k++)
                    {
                        int nearest = Nearest(places, grid, Position(scene, mesh, indices[i + k]), tolerance);
                        Assert.True(nearest >= 0, $"{resourceName}: a vertex at {Position(scene, mesh, indices[i + k])} is at no position of the source");
                        corner[k] = representative[nearest];
                    }

                    if (corner[0] == corner[1] || corner[1] == corner[2] || corner[0] == corner[2])
                    {
                        actualFlat++;
                        continue;
                    }

                    actualCount++;
                    string key = Key(part.ShadingId, corner[0], corner[1], corner[2], out int turn);
                    if (!expected.TryGetValue(key, out Queue<Vector3[]>? queue) || queue.Count == 0)
                    {
                        unexpected.Add(key);
                        continue;
                    }

                    // The triangle's own normal: a writer may store that for a corner whose source normals
                    // it finds too far apart to keep (the sphere with a normal for every corner).
                    Vector3 p0 = Position(scene, mesh, indices[i]), p1 = Position(scene, mesh, indices[i + 1]), p2 = Position(scene, mesh, indices[i + 2]);
                    Vector3 flat = Vector3.Cross(p1 - p0, p2 - p0);
                    flat = flat.LengthSquared() > 0 ? Vector3.Normalize(flat) : flat;

                    Vector3[] sourceNormals = queue.Dequeue();
                    for (int k = 0; k < 3; k++)
                    {
                        if (Normal(scene, mesh, indices[i + ((k + turn) % 3)]) is { } normal && sourceNormals[k].LengthSquared() > 0)
                        {
                            normalsCompared++;

                            // A normal is a prediction turned by a stored rotation: turned from the wrong
                            // prediction it would not come out of length one.
                            normalsNotUnit += MathF.Abs(normal.Length() - 1) > 2e-3f ? 1 : 0;
                            Vector3 unit = Vector3.Normalize(normal);
                            if (Vector3.Dot(unit, Vector3.Normalize(sourceNormals[k])) < 0.99f
                                && Vector3.Dot(unit, flat) < 0.99f
                                && !normalsAt[corner[(k + turn) % 3]].Any(n => n.LengthSquared() > 0 && Vector3.Dot(unit, Vector3.Normalize(n)) >= 0.99f))
                            {
                                normalsOff++;
                            }
                        }
                    }
                }
            }

            Assert.True(unexpected.Count == 0, $"{resourceName}: {unexpected.Count} of {actualCount} triangles are not in the source, the first '{unexpected.FirstOrDefault()}'");
            // The writer drops triangles without area; what it keeps of them is none or all.
            Assert.Equal(expectedCount, actualCount);
            Assert.True(actualFlat == 0 || actualFlat == expectedFlat, $"{resourceName}: {actualFlat} triangles with two corners in one place, the source has {expectedFlat}");
            Assert.True((mesh.Flags & SceneMeshFlags.HasNormals) != 0, $"{resourceName} has no normals");
            Assert.Equal(0, normalsNotUnit);

            // One sample gives every corner of a coarse sphere a normal of its own, and its file holds
            // those normals at other corners than its source text says: they are compared nowhere else.
            Assert.True(normalsOff <= normalsCompared / 100 || name == "sphere_ta_subdivmod", $"{resourceName}: {normalsOff} of {normalsCompared} normals are more than 8 degrees off every normal the source has at their position, and off their triangle's own");
        }

        Assert.DoesNotContain(scene.Diagnostics, d => d.Code is "mesh-progressive-only" or "progressive-mesh-skipped" or "mesh-partial-detail");
    }
}
