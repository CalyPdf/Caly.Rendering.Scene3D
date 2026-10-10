using Caly.Rendering.Scene3D.Prc.Generator;

// Usage: scene3d-prc-generator [repository root] [--check]
//   Writes Caly.Rendering.Scene3D.Prc/Generated/PrcReaders.g.cs and PrcEntities.g.cs; with --check, writes
//   nothing and fails if they are not what the inputs give.
bool check = args.Contains("--check");
string? root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
if (root is null)
{
    root = Directory.GetCurrentDirectory();
    while (!File.Exists(Path.Combine(root, "Caly.Rendering.Scene3D.slnx")))
    {
        root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Run this from inside the Caly.Rendering.Scene3D repository, or name its root.");
    }
}

string library = Path.Combine(root, "Caly.Rendering.Scene3D.Prc");
GeneratedCode code;
try
{
    code = PrcReaderGenerator.Generate(
        File.ReadAllText(Path.Combine(library, "Schema", "prc.json")),
        File.ReadAllText(Path.Combine(library, "Schema", "prc-display.json")));
}
catch (GeneratorException e)
{
    Console.Error.WriteLine("Not generated: " + e.Message);
    return 1;
}

Console.Write(code.Report);
int stale = 0;
foreach ((string name, string text) in new[] { ("PrcReaders.g.cs", code.Readers), ("PrcEntities.g.cs", code.Entities) })
{
    string path = Path.Combine(library, "Generated", name);
    string existing = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
    if (existing == text)
    {
        Console.WriteLine($"{name}: up to date.");
    }
    else if (check)
    {
        Console.Error.WriteLine($"{name}: not what prc.json and prc-display.json give. Run the generator.");
        stale++;
    }
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        Console.WriteLine($"{name}: written, {text.Length} characters.");
    }
}

return stale == 0 ? 0 : 1;
