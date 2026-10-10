using System.Diagnostics;
using System.Globalization;
using System.Text;
using Caly.Rendering.Scene3D;
using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Tool;
using Caly.Rendering.Scene3D.U3D;

// scene3d-tool: looks at 3D files through the Caly.Rendering.Scene3D libraries.
//
//   scene3d-tool summary <file>...               what each file decodes to
//   scene3d-tool glb <file> [out.glb]            convert to binary glTF
//   scene3d-tool png <file> [out.png] [size]     draw a preview image (default 800 pixels)
//   scene3d-tool all <output folder> <file>...   summary, .glb and .png for every file, into the folder
//   scene3d-tool trace <file.prc> [out.txt]      every entity of a PRC file and the bit it starts at

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length < 2)
{
    return Usage();
}

try
{
    switch (args[0])
    {
        case "summary":
            foreach (string file in args.AsSpan(1))
            {
                using Scene scene = Read(file, out double milliseconds);
                Console.WriteLine(Summary(file, scene, milliseconds));
            }

            return 0;

        case "glb":
        {
            using Scene scene = Read(args[1], out _);
            string path = args.Length > 2 ? args[2] : Path.ChangeExtension(args[1], ".glb");
            WriteGlb(scene, path);
            Console.WriteLine(path);
            return 0;
        }

        case "png":
        {
            using Scene scene = Read(args[1], out _);
            string path = args.Length > 2 ? args[2] : Path.ChangeExtension(args[1], ".png");
            int size = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 800;
            WritePng(scene, path, size);
            Console.WriteLine(path);
            return 0;
        }

        case "all" when args.Length >= 3:
        {
            string folder = Directory.CreateDirectory(args[1]).FullName;
            var report = new StringBuilder();
            int failed = 0;
            foreach (string file in args.AsSpan(2))
            {
                try
                {
                    using Scene scene = Read(file, out double milliseconds);
                    string name = Path.GetFileNameWithoutExtension(file);
                    WriteGlb(scene, Path.Combine(folder, name + ".glb"));
                    int drawn = WritePng(scene, Path.Combine(folder, name + ".png"), 800);
                    report.AppendLine(Summary(file, scene, milliseconds)).AppendLine($"  preview: {drawn} triangles drawn").AppendLine();
                }
                catch (Scene3DException e)
                {
                    failed++;
                    report.AppendLine($"{file}\n  NOT READ: {e.Error}: {e.Message}").AppendLine();
                }
            }

            File.WriteAllText(Path.Combine(folder, "summary.txt"), report.ToString());
            Console.Write(report);
            return failed == 0 ? 0 : 2;
        }

        case "trace":
        {
            if (args.Length > 2)
            {
                using var file = new StreamWriter(args[2], append: false, new UTF8Encoding(false), 1 << 16);
                return PrcTraceCommand.Run(args[1], file);
            }

            using var console = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16);
            return PrcTraceCommand.Run(args[1], console);
        }

        default:
            return Usage();
    }
}
catch (Scene3DException e)
{
    Console.Error.WriteLine($"{e.Error}: {e.Message}");
    return 2;
}
catch (IOException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

static int Usage()
{
    Console.Error.WriteLine("""
        scene3d-tool summary <file>...
        scene3d-tool glb <file> [out.glb]
        scene3d-tool png <file> [out.png] [size]
        scene3d-tool all <output folder> <file>...
        scene3d-tool trace <file.prc> [out.txt]
        """);
    return 1;
}

static Scene Read(string file, out double milliseconds)
{
    byte[] data = File.ReadAllBytes(file);
    bool prc = PrcReader.IsPrc(data);
    if (!prc && !U3DReader.IsU3D(data))
    {
        throw new Scene3DException(Scene3DError.Unsupported, $"'{file}' is neither a U3D nor a PRC file.");
    }

    var watch = Stopwatch.StartNew();
    Scene scene = prc ? PrcReader.Read(data) : U3DReader.Read(data);
    milliseconds = watch.Elapsed.TotalMilliseconds;
    return scene;
}

static void WriteGlb(Scene scene, string path)
{
    using FileStream output = File.Create(path);
    GlbWriter.Write(scene, output);
}

static int WritePng(Scene scene, string path, int size)
{
    byte[] rgb = PreviewRenderer.Render(scene, size, out int drawn);
    using FileStream output = File.Create(path);
    PngEncoder.Write(output, size, size, rgb);
    return drawn;
}

static string Summary(string file, Scene scene, double milliseconds)
{
    SceneNode[] nodes = scene.Nodes.ToArray();
    int Count(SceneNodeKind kind) => nodes.Count(n => n.Kind == kind);

    var text = new StringBuilder();
    text.AppendLine(CultureInfo.InvariantCulture, $"{file}");
    text.AppendLine(CultureInfo.InvariantCulture, $"  {new FileInfo(file).Length:N0} bytes, read in {milliseconds:F1} ms; unit scale {scene.UnitScale}");
    text.AppendLine(CultureInfo.InvariantCulture, $"  nodes {nodes.Length}: {Count(SceneNodeKind.Group)} groups, {Count(SceneNodeKind.Model)} models ({nodes.Count(n => n.Mesh >= 0)} with a mesh), {Count(SceneNodeKind.Light)} lights, {Count(SceneNodeKind.Camera)} cameras");
    long triangles = 0, segments = 0, points = 0;
    foreach (SceneMesh mesh in scene.Meshes)
    {
        foreach (SceneMeshPart part in scene.Parts.Slice(mesh.FirstPart, mesh.PartCount))
        {
            switch (mesh.Topology)
            {
                case SceneTopology.Lines: segments += part.IndexCount / 2; break;
                case SceneTopology.Points: points += part.IndexCount; break;
                default: triangles += part.IndexCount / 3; break;
            }
        }
    }

    string others = (segments > 0 ? string.Create(CultureInfo.InvariantCulture, $", {segments:N0} line segments") : "") + (points > 0 ? string.Create(CultureInfo.InvariantCulture, $", {points:N0} points") : "");
    text.AppendLine(CultureInfo.InvariantCulture, $"  meshes {scene.Meshes.Length}: {scene.Positions.Length / 3:N0} vertices, {triangles:N0} triangles{others}, {scene.Parts.Length} parts, {scene.GeometryBytes:N0} bytes");
    text.AppendLine(CultureInfo.InvariantCulture, $"  materials {scene.Materials.Length}, texture layers {scene.TextureLayers.Length}, textures {scene.Textures.Count} ({scene.Textures.Sum(t => t.Images.Sum(i => (long)i.Data.Length)):N0} bytes of images)");

    SceneBounds bounds = scene.ComputeWorldBounds();
    text.AppendLine(bounds.IsEmpty ? "  visible bounds: none" : string.Create(CultureInfo.InvariantCulture, $"  visible bounds: {bounds.Min} to {bounds.Max}"));

    // The instances that decide the bounds: where a stray or oversized part shows up.
    var instances = new List<(float Size, string Line)>();
    for (int i = 0; i < nodes.Length; i++)
    {
        if (nodes[i].Mesh < 0 || scene.Meshes[nodes[i].Mesh].Bounds.IsEmpty)
        {
            continue;
        }

        SceneBounds box = SceneBounds.Empty;
        SceneBounds local = scene.Meshes[nodes[i].Mesh].Bounds;
        System.Numerics.Matrix4x4 world = scene.GetWorldMatrix(i);
        for (int corner = 0; corner < 8; corner++)
        {
            box.Include(System.Numerics.Vector3.Transform(new System.Numerics.Vector3((corner & 1) == 0 ? local.Min.X : local.Max.X, (corner & 2) == 0 ? local.Min.Y : local.Max.Y, (corner & 4) == 0 ? local.Min.Z : local.Max.Z), world));
        }

        float size = (box.Max - box.Min).Length();
        instances.Add((size, string.Create(CultureInfo.InvariantCulture, $"    {size:G5} across, centre {(box.Min + box.Max) / 2:F2}, {(nodes[i].Visible ? "visible" : "hidden")}, {scene.Meshes[nodes[i].Mesh].VertexCount} vertices: {scene.GetName(nodes[i].Name)}")));
    }

    text.AppendLine("  largest mesh instances:");
    foreach ((_, string line) in instances.OrderByDescending(x => x.Size).Take(4))
    {
        text.AppendLine(line);
    }

    text.Append("  diagnostics:");
    if (scene.Diagnostics.Count == 0)
    {
        text.Append(" none");
    }

    foreach (SceneDiagnostic diagnostic in scene.Diagnostics)
    {
        text.AppendLine().Append(CultureInfo.InvariantCulture, $"    {diagnostic.Severity} {diagnostic.Code} x{diagnostic.Count}: {diagnostic.Message}");
    }

    return text.ToString();
}
