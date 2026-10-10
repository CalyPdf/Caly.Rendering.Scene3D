using System.Numerics;
using Caly.Rendering.Scene3D.Tests.Idtf;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Line sets and point sets against the IDTF text the sample scenes were made from. Their positions are
/// stored as quantised differences from one another, so they are compared to a hundredth.
/// </summary>
public partial class U3DMeshTests
{
    private static string Place(Vector3 p) => $"{F(p.X, 2)},{F(p.Y, 2)},{F(p.Z, 2)}";

    [Theory]
    [InlineData("lineset")]
    [InlineData("lineset_grid")]
    [InlineData("lineset_anim")]
    public void LineSetsMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = U3DSceneGraphTests.Read(name);

        int checkedSets = 0;
        foreach (IdtfElement resource in idtf.Resources("MODEL").Where(r => r.Text("MODEL_TYPE") == "LINE_SET"))
        {
            IdtfElement set = resource.Get("LINE_SET");
            Vector3[] positions = Vectors(set.Get("MODEL_POSITION_LIST"));
            List<double> ends = set.Get("LINE_POSITION_LIST").Numbers;
            var expected = new List<string>();
            for (int i = 0; i < ends.Count; i += 2)
            {
                expected.Add(Segment(positions[(int)ends[i]], positions[(int)ends[i + 1]]));
            }

            SceneMesh mesh = MeshOfResource(scene, idtf, resource.Text("RESOURCE_NAME"));
            Assert.Equal(SceneTopology.Lines, mesh.Topology);
            Assert.True((mesh.Flags & SceneMeshFlags.HasNormals) == 0);

            var actual = new List<string>();
            foreach (SceneMeshPart part in scene.Parts.Slice(mesh.FirstPart, mesh.PartCount))
            {
                Assert.Equal(0, part.IndexCount % 2);
                ReadOnlySpan<uint> indices = scene.Indices.Slice(part.IndexStart, part.IndexCount);
                for (int i = 0; i < indices.Length; i += 2)
                {
                    Assert.True(indices[i] < (uint)mesh.VertexCount && indices[i + 1] < (uint)mesh.VertexCount, "index out of range");
                    actual.Add(Segment(Position(scene, mesh, indices[i]), Position(scene, mesh, indices[i + 1])));
                }
            }

            expected.Sort(StringComparer.Ordinal);
            actual.Sort(StringComparer.Ordinal);
            Assert.Equal(expected, actual);
            checkedSets++;
        }

        Assert.True(checkedSets > 0);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "line-set-skipped");

        static string Segment(Vector3 a, Vector3 b)
        {
            string first = Place(a), second = Place(b);
            return string.CompareOrdinal(first, second) <= 0 ? first + " - " + second : second + " - " + first;
        }
    }

    /// <summary>
    /// A shading modifier says which kinds of geometry it is for (1 meshes, 2 lines, 4 points): a line set
    /// takes the shaders of one for lines and not of one for points. The sample's own modifier is for
    /// everything; its attributes are rewritten here.
    /// </summary>
    [Theory]
    [InlineData(2u, "Box010")]
    [InlineData(4u, "")]
    public void ALineSetTakesTheShaderOfAShadingModifierForLines(uint attributes, string material)
    {
        byte[] file = TestFiles.Scene("lineset.u3d");
        int block = -1;
        for (int at = 0; at + 12 <= file.Length && block < 0; at += 4)
        {
            block = BitConverter.ToUInt32(file, at) == 0xFFFFFF45 ? at : -1;
        }

        Assert.True(block > 0, "the sample has a shading modifier");
        int nameLength = BitConverter.ToUInt16(file, block + 12);
        BitConverter.TryWriteBytes(file.AsSpan(block + 12 + 2 + nameLength + 4), attributes);

        using Scene scene = Caly.Rendering.Scene3D.U3D.U3DReader.Read(file, cancellationToken: TestContext.Current.CancellationToken);

        SceneNode node = scene.Nodes.ToArray().Single(n => n.Mesh >= 0);
        int bound = node.BindingCount > 0 ? scene.Bindings[node.FirstBinding] : 0;
        Assert.Equal(material, scene.GetName(scene.Materials[bound].Name));
    }

    [Theory]
    [InlineData("pointset")]
    [InlineData("pointset_anim")]
    public void PointSetsMatchTheIdtfSource(string name)
    {
        IdtfDocument idtf = IdtfDocument.Load(TestFiles.ScenePath(name + ".idtf"));
        using Scene scene = U3DSceneGraphTests.Read(name);

        int checkedSets = 0;
        foreach (IdtfElement resource in idtf.Resources("MODEL").Where(r => r.Text("MODEL_TYPE") == "POINT_SET"))
        {
            IdtfElement set = resource.Get("POINT_SET");
            Vector3[] positions = Vectors(set.Get("MODEL_POSITION_LIST"));
            List<string> expected = set.Get("POINT_POSITION_LIST").Numbers.Select(n => Place(positions[(int)n])).Order(StringComparer.Ordinal).ToList();

            SceneMesh mesh = MeshOfResource(scene, idtf, resource.Text("RESOURCE_NAME"));
            Assert.Equal(SceneTopology.Points, mesh.Topology);

            var actual = new List<string>();
            foreach (SceneMeshPart part in scene.Parts.Slice(mesh.FirstPart, mesh.PartCount))
            {
                foreach (uint index in scene.Indices.Slice(part.IndexStart, part.IndexCount))
                {
                    Assert.True(index < (uint)mesh.VertexCount, "index out of range");
                    actual.Add(Place(Position(scene, mesh, index)));
                }
            }

            actual.Sort(StringComparer.Ordinal);
            Assert.Equal(expected, actual);
            checkedSets++;
        }

        Assert.True(checkedSets > 0);
        Assert.DoesNotContain(scene.Diagnostics, d => d.Code == "point-set-skipped");
    }
}
