using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Caly.Rendering.Scene3D.Prc.Generator;

/// <summary>What the generator writes.</summary>
/// <param name="Readers">The file of reading methods.</param>
/// <param name="Entities">The file of types for what is kept.</param>
/// <param name="Report">What was generated, for a person to read.</param>
public sealed record GeneratedCode(string Readers, string Entities, string Report);

/// <summary>
/// Writes the C# that reads PRC entities, from two descriptions: prc.json (every structure of the format
/// and its fields, in file order, from prc-rs) and prc-display.json (which structures this library reads,
/// what it keeps of them, and the C# for the few things prc.json says in Rust).
/// <para>
/// A PRC section is a stream of bits without lengths: the only way past an entity is to read every one of
/// its fields, so every field of every structure on the way to the display data is read, and most are
/// thrown away. A structure nothing is kept of becomes a method that returns nothing.
/// </para>
/// </summary>
public static partial class PrcReaderGenerator
{
    private sealed record Builtin(string CSharpType, string Read, string? Skip, int MinimumBits, string Default, string? Array, bool Traced = false);

    // The simple values of the format. Arrays that carry their own compression are read by hand-written
    // code only, so they are not listed: meeting one in a generated structure stops the generation.
    private static readonly Dictionary<string, Builtin> Builtins = new()
    {
        ["Boolean"] = new("bool", "r.ReadBoolean()", null, 1, "false", null),
        ["UnsignedInteger"] = new("uint", "r.ReadUnsignedInteger()", null, 1, "0", "UnsignedIntegers"),
        ["Integer"] = new("int", "r.ReadInteger()", null, 1, "0", "Integers"),
        ["Double"] = new("double", "r.ReadDouble()", null, 2, "0", "Doubles"),
        ["String"] = new("string", "r.ReadString()", "r.SkipString()", 1, "\"\"", "Strings"),
        ["Character"] = new("sbyte", "r.ReadCharacter()", null, 8, "0", "Characters"),
        ["UnsignedCharacter"] = new("byte", "r.ReadUnsignedCharacter()", null, 8, "0", "UnsignedCharacters"),
        ["UnsignedShort"] = new("ushort", "r.ReadUnsignedShort()", null, 16, "0", null),
        ["FloatAsBytes"] = new("float", "r.ReadFloatAsBytes()", null, 32, "0", "Floats"),
        ["NumberOfBitsThenUnsignedInteger"] = new("uint", "r.ReadNumberOfBitsThenUnsignedInteger()", null, 5, "0", "BitCountedUnsignedIntegers"),
        ["UserData"] = new("", "", "r.SkipUserData()", 1, "", null, Traced: true),
    };

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
        "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    ];

    private static readonly HashSet<string> ReservedLocals = ["r", "ctx", "i"];

    private sealed class Field
    {
        public required string Name { get; init; }

        public required string DataType { get; init; }

        public string? Condition { get; set; }

        public string? Size { get; set; }

        public bool IsVector { get; init; }

        public int Multiplicity { get; init; }

        public List<(string Type, string Variant)>? Eligible { get; init; }

        public string? ExpectedValue { get; init; }

        public List<string> OnSet { get; } = [];

        public bool VariableBits { get; init; }

        public List<string> Notes { get; } = [];
    }

    private sealed class Structure
    {
        public required string Name { get; init; }

        public List<Field> Fields { get; } = [];

        public string? OnEnter { get; init; }

        public string? OnLeave { get; init; }

        public bool ByteAligned { get; init; }

        public int Order { get; init; }
    }

    private sealed class Overlay
    {
        public List<string> Roots { get; } = [];

        // Structures read by hand-written code; true when that code returns the entity.
        public Dictionary<string, bool> Manual { get; } = [];

        public Dictionary<string, HashSet<string>> Keep { get; } = [];

        public HashSet<string> ValueTypes { get; } = [];

        public Dictionary<string, string> Hooks { get; } = [];

        public HashSet<string> IgnoredHooks { get; } = [];

        public Dictionary<string, string> ContextCalls { get; } = [];

        public Dictionary<string, long> Constants { get; } = [];

        // Text of conditions and sizes that the translator cannot read as it stands (it names a value inside a structure read
        // earlier), replaced wherever it occurs by an equivalent that it can.
        public Dictionary<string, string> Rewrites { get; } = [];
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z_0-9]*")]
    private static partial Regex Identifier();

    /// <summary>Generates the readers.</summary>
    /// <param name="prcJson">The text of prc.json.</param>
    /// <param name="overlayJson">The text of prc-display.json.</param>
    public static GeneratedCode Generate(string prcJson, string overlayJson)
    {
        Dictionary<string, Structure> structures = ParseStructures(prcJson, out int version);
        Overlay overlay = ParseOverlay(overlayJson);
        foreach ((string from, string to) in overlay.Rewrites)
        {
            int used = 0;
            foreach (Field field in structures.Values.SelectMany(s => s.Fields))
            {
                if (field.Condition?.Contains(from, StringComparison.Ordinal) == true)
                {
                    field.Condition = field.Condition.Replace(from, to, StringComparison.Ordinal);
                    used++;
                }

                if (field.Size?.Contains(from, StringComparison.Ordinal) == true)
                {
                    field.Size = field.Size.Replace(from, to, StringComparison.Ordinal);
                    used++;
                }
            }

            if (used == 0)
            {
                throw new GeneratorException($"rewrites: '{from}' occurs in no condition or size of prc.json.");
            }
        }

        return new Emitter(structures, overlay, version).Run();
    }

    /// <summary>The C# name for a structure or a field of prc.json, without prefix.</summary>
    public static string Pascal(string name)
    {
        if (name.StartsWith("PRC_TYPE_", StringComparison.Ordinal))
        {
            name = name["PRC_TYPE_".Length..];
        }

        var result = new StringBuilder();
        foreach (string part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            bool shout = part.Length > 1 && part.All(char.IsUpper);
            result.Append(char.ToUpperInvariant(part[0]));
            result.Append(shout ? part[1..].ToLowerInvariant() : part[1..]);
        }

        return result.ToString();
    }

    private static Dictionary<string, Structure> ParseStructures(string prcJson, out int version)
    {
        using JsonDocument document = JsonDocument.Parse(prcJson);
        JsonElement root = document.RootElement;
        version = root.GetProperty("version").GetInt32();
        var result = new Dictionary<string, Structure>();
        int order = 0;
        foreach (JsonProperty entry in root.GetProperty("structures")[0].EnumerateObject())
        {
            JsonElement value = entry.Value;
            // prc.json defines two structures twice; the later definition is the one its own
            // generator sees (a Python dictionary), in the place of the first.
            var structure = new Structure
            {
                Name = entry.Name,
                OnEnter = Text(value, "on_enter"),
                OnLeave = Text(value, "on_leave"),
                ByteAligned = value.TryGetProperty("byte_aligned", out _),
                Order = result.TryGetValue(entry.Name, out Structure? earlier) ? earlier.Order : order++,
            };

            foreach (JsonElement f in value.GetProperty("fields").EnumerateArray())
            {
                List<(string, string)>? eligible = null;
                if (f.TryGetProperty("eligible_type_ids", out JsonElement ids))
                {
                    eligible = [];
                    foreach (JsonElement id in ids.EnumerateArray())
                    {
                        eligible.Add((id[0].GetString()!, id[1].GetString()!));
                    }
                }

                int multiplicity = 0;
                if (f.TryGetProperty("multiplicity", out JsonElement m))
                {
                    multiplicity = m.ValueKind == JsonValueKind.Number ? m.GetInt32() : int.Parse(m.GetString()!, CultureInfo.InvariantCulture);
                }

                var field = new Field
                {
                    Name = f.GetProperty("name").GetString()!,
                    DataType = f.GetProperty("data_type").GetString()!,
                    Condition = Text(f, "condition_rust"),
                    Size = Text(f, "size"),

                    // As the reference generator has it: a vector is a field with the key, whatever its value.
                    IsVector = f.TryGetProperty("is_vec", out _),
                    Multiplicity = multiplicity,
                    Eligible = eligible,
                    ExpectedValue = f.TryGetProperty("expected_value", out JsonElement expected) ? expected[1].GetString() : null,
                    VariableBits = f.TryGetProperty("vbr", out _) || f.TryGetProperty("tol", out _) || f.TryGetProperty("size1", out _),
                };

                foreach (string key in (string[])["on_set", "on_set1"])
                {
                    if (Text(f, key) is { } hook)
                    {
                        field.OnSet.Add(hook);
                    }
                }

                if (Text(f, "bug") is { } bug)
                {
                    field.Notes.Add(bug);
                }

                structure.Fields.Add(field);
            }

            result[structure.Name] = structure;
        }

        return result;

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static Overlay ParseOverlay(string overlayJson)
    {
        using JsonDocument document = JsonDocument.Parse(overlayJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        JsonElement root = document.RootElement;
        var overlay = new Overlay();
        foreach (JsonElement e in root.GetProperty("roots").EnumerateArray())
        {
            overlay.Roots.Add(e.GetString()!);
        }

        foreach (JsonProperty p in root.GetProperty("manual").EnumerateObject())
        {
            overlay.Manual.Add(p.Name, p.Value.GetString() switch
            {
                "kept" => true,
                "walked" => false,
                _ => throw new GeneratorException($"manual.{p.Name} must be \"kept\" or \"walked\"."),
            });
        }

        foreach (JsonProperty p in root.GetProperty("keep").EnumerateObject())
        {
            overlay.Keep.Add(p.Name, [.. p.Value.EnumerateArray().Select(e => e.GetString()!)]);
        }

        foreach (JsonElement e in root.GetProperty("valueTypes").EnumerateArray())
        {
            overlay.ValueTypes.Add(e.GetString()!);
        }

        foreach (JsonProperty p in root.GetProperty("hooks").EnumerateObject())
        {
            overlay.Hooks.Add(p.Name, p.Value.GetString()!);
        }

        foreach (JsonElement e in root.GetProperty("ignoredHooks").EnumerateArray())
        {
            overlay.IgnoredHooks.Add(e.GetString()!);
        }

        foreach (JsonProperty p in root.GetProperty("contextCalls").EnumerateObject())
        {
            overlay.ContextCalls.Add(p.Name, p.Value.GetString()!);
        }

        foreach (JsonProperty p in root.GetProperty("constants").EnumerateObject())
        {
            overlay.Constants.Add(p.Name, p.Value.GetInt64());
        }

        if (root.TryGetProperty("rewrites", out JsonElement rewrites))
        {
            foreach (JsonProperty p in rewrites.EnumerateObject())
            {
                overlay.Rewrites.Add(p.Name, p.Value.GetString()!);
            }
        }

        return overlay;
    }

    private sealed class Emitter(Dictionary<string, Structure> structures, Overlay overlay, int version)
    {
        private readonly StringBuilder _readers = new();
        private readonly StringBuilder _entities = new();
        private readonly StringBuilder _report = new();
        private readonly Dictionary<string, int> _minimumBits = [];
        private int _indent;

        public GeneratedCode Run()
        {
            List<Structure> generated = Reach();
            Validate(generated);

            // A raw string takes the line endings of this file's checkout (CRLF under .gitattributes); the output is LF.
            string header = """
                // <auto-generated>
                // Written by tools/Caly.Rendering.Scene3D.Prc.Generator from Schema/prc.json (the description of the PRC
                // format by prc-rs, MIT, Copyright Kristof Ralovich 2025-2026; see THIRD-PARTY-NOTICES.md)
                // and Schema/prc-display.json. Do not edit: change the inputs and run the generator.
                // </auto-generated>

                #nullable enable


                """.ReplaceLineEndings("\n");

            _readers.Append(header);
            _readers.Append("using Caly.Rendering.Scene3D.Prc.Bits;\n\nnamespace Caly.Rendering.Scene3D.Prc.Generated;\n\n");
            _readers.Append("/// <summary>One method per structure of the format, each reading every field in file order.</summary>\n");
            _readers.Append("internal static partial class PrcReaders\n{\n");
            _readers.Append(CultureInfo.InvariantCulture, $"    /// <summary>The version of the format prc.json describes.</summary>\n    public const uint DescribedVersion = {version};\n");
            _indent = 1;
            foreach (Structure structure in generated)
            {
                _readers.Append('\n');
                EmitReader(structure);
            }

            _readers.Append("}\n");

            _entities.Append(header);
            _entities.Append("namespace Caly.Rendering.Scene3D.Prc.Generated;\n");
            foreach (Structure structure in generated.Where(s => overlay.Keep.ContainsKey(s.Name)))
            {
                _entities.Append('\n');
                EmitEntity(structure);
            }

            int kept = generated.Count(s => overlay.Keep.ContainsKey(s.Name));
            _report.Insert(0, string.Create(CultureInfo.InvariantCulture, $"prc.json version {version}: {structures.Count} structures; {generated.Count} generated ({kept} kept, {generated.Count - kept} walked), {overlay.Manual.Count} hand-written, {generated.Sum(s => s.Fields.Count)} fields.\n"));
            return new GeneratedCode(_readers.ToString(), _entities.ToString(), _report.ToString());
        }

        private bool IsKept(string structure) => overlay.Keep.ContainsKey(structure) || (overlay.Manual.TryGetValue(structure, out bool returns) && returns);

        private bool IsKept(Structure structure, Field field) =>
            overlay.Keep.TryGetValue(structure.Name, out HashSet<string>? fields) && (fields.Contains("*") || fields.Contains(field.Name));

        private static string TypeName(string structure) => "Prc" + Pascal(structure);

        private static string Local(string field) => Keywords.Contains(field) ? "@" + field : field;

        /// <summary>The structures to generate: everything the roots lead to, in the order of prc.json.</summary>
        private List<Structure> Reach()
        {
            var seen = new HashSet<string>();
            var pending = new Stack<string>(overlay.Roots);
            while (pending.Count != 0)
            {
                string name = pending.Pop();
                if (!seen.Add(name))
                {
                    continue;
                }

                if (!structures.TryGetValue(name, out Structure? structure))
                {
                    throw new GeneratorException($"prc.json has no structure '{name}'.");
                }

                if (overlay.Manual.ContainsKey(name))
                {
                    continue;
                }

                foreach (Field field in structure.Fields)
                {
                    if (field.Eligible is not null)
                    {
                        foreach ((string type, _) in field.Eligible)
                        {
                            pending.Push(type);
                        }
                    }
                    else if (!Builtins.ContainsKey(field.DataType))
                    {
                        pending.Push(field.DataType);
                    }
                }
            }

            foreach (string name in overlay.Manual.Keys.Where(n => !seen.Contains(n)))
            {
                throw new GeneratorException($"'{name}' is listed as hand-written but nothing leads to it.");
            }

            return [.. seen.Where(n => !overlay.Manual.ContainsKey(n)).Select(n => structures[n]).OrderBy(s => s.Order)];
        }

        private void Validate(List<Structure> generated)
        {
            var names = new Dictionary<string, string>();
            foreach (string name in generated.Select(s => s.Name).Concat(overlay.Manual.Keys))
            {
                if (!names.TryAdd(Pascal(name), name))
                {
                    throw new GeneratorException($"'{name}' and '{names[Pascal(name)]}' get the same C# name.");
                }
            }

            var generatedNames = generated.Select(s => s.Name).ToHashSet();
            foreach ((string name, HashSet<string> fields) in overlay.Keep)
            {
                if (!generatedNames.Contains(name))
                {
                    throw new GeneratorException($"keep.{name}: not a generated structure.");
                }

                foreach (string field in fields.Where(f => f != "*" && structures[name].Fields.All(x => x.Name != f)))
                {
                    throw new GeneratorException($"keep.{name}: no field '{field}'.");
                }
            }

            foreach (string name in overlay.ValueTypes.Where(n => !overlay.Keep.ContainsKey(n)))
            {
                throw new GeneratorException($"valueTypes: '{name}' is not kept.");
            }

            foreach (string key in overlay.Hooks.Keys.Concat(overlay.IgnoredHooks))
            {
                string[] parts = key.Split('.', ':');
                if (parts.Length != 2 || !generatedNames.Contains(parts[0]) ||
                    (key.Contains(':') ? parts[1] is not ("enter" or "leave") : structures[parts[0]].Fields.All(f => f.Name != parts[1])))
                {
                    throw new GeneratorException($"The hook '{key}' names nothing that is generated.");
                }
            }

            foreach (Structure structure in generated)
            {
                if (structure.ByteAligned)
                {
                    throw new GeneratorException($"'{structure.Name}' is byte aligned: it belongs to the container, not to a section.");
                }

                var members = new HashSet<string>();
                foreach (Field field in structure.Fields)
                {
                    if (ReservedLocals.Contains(field.Name) || field.Name.Contains("__", StringComparison.Ordinal))
                    {
                        throw new GeneratorException($"{structure.Name}.{field.Name}: the name collides with one the generated code uses.");
                    }

                    if (IsKept(structure, field) && (!members.Add(Pascal(field.Name)) || (field.Eligible is not null && !members.Add(Pascal(field.Name) + "Type"))))
                    {
                        throw new GeneratorException($"{structure.Name}.{field.Name}: two kept fields get the same C# name.");
                    }
                }
            }
        }

        private int MinimumBits(string structureName, HashSet<string>? underway = null)
        {
            if (overlay.Manual.ContainsKey(structureName))
            {
                return 1;
            }

            if (_minimumBits.TryGetValue(structureName, out int known))
            {
                return known;
            }

            underway ??= [];
            if (!underway.Add(structureName))
            {
                return 0;
            }

            long bits = 0;
            foreach (Field field in structures[structureName].Fields)
            {
                if (field.Condition is not null || field.IsVector)
                {
                    continue;
                }

                int each = field.Eligible is not null ? 1
                    : Builtins.TryGetValue(field.DataType, out Builtin? builtin) ? builtin.MinimumBits
                    : MinimumBits(field.DataType, underway);
                bits += (long)each * Math.Max(1, field.Multiplicity);
            }

            underway.Remove(structureName);
            int result = (int)Math.Min(bits, 1 << 20);
            if (underway.Count == 0)
            {
                _minimumBits[structureName] = result;
            }

            return result;
        }

        private void Line(string text = "")
        {
            if (text.Length != 0)
            {
                _readers.Append(' ', _indent * 4).Append(text);
            }

            _readers.Append('\n');
        }

        private void Open(string? text = null)
        {
            if (text is not null)
            {
                Line(text);
            }

            Line("{");
            _indent++;
        }

        private void Close()
        {
            _indent--;
            Line("}");
        }

        private string? HookText(Structure structure, string key, IEnumerable<string> rust)
        {
            string full = structure.Name + key;
            bool work = false;
            foreach (string hook in rust)
            {
                // Logging of the reference implementation is not behaviour.
                work |= !hook.TrimStart().StartsWith("debug!(", StringComparison.Ordinal) || hook.Contains('\n');
            }

            if (overlay.Hooks.TryGetValue(full, out string? text))
            {
                return text;
            }

            if (work && !overlay.IgnoredHooks.Contains(full))
            {
                throw new GeneratorException($"{full}: prc.json does something here ({string.Join(" / ", rust)}) that prc-display.json neither translates (hooks) nor sets aside (ignoredHooks).");
            }

            return null;
        }

        private bool IsUsed(Structure structure, Field field)
        {
            if (field.ExpectedValue is not null)
            {
                return true;
            }

            bool Mentions(string? text) => text is not null && Identifier().Matches(text).Any(m => m.Value == field.Name);
            int index = structure.Fields.IndexOf(field);
            if (structure.Fields.Skip(index + 1).Any(f => Mentions(f.Condition) || Mentions(f.Size)))
            {
                return true;
            }

            return overlay.Hooks.Any(h => (h.Key.StartsWith(structure.Name + ".", StringComparison.Ordinal) || h.Key.StartsWith(structure.Name + ":", StringComparison.Ordinal)) && Mentions(h.Value));
        }

        private string Translate(Structure structure, Field field, string rust, bool asCount)
        {
            int index = structure.Fields.IndexOf(field);
            try
            {
                return RustExpression.Translate(
                    rust,
                    name =>
                    {
                        int at = structure.Fields.FindIndex(f => f.Name == name);
                        if (at < 0 || at >= index)
                        {
                            return null;
                        }

                        Field referenced = structure.Fields[at];
                        bool scalar = referenced.Eligible is null && !referenced.IsVector && referenced.Multiplicity == 0 &&
                            Builtins.TryGetValue(referenced.DataType, out Builtin? builtin) && builtin.CSharpType.Length != 0;
                        return scalar ? Local(name) : null;
                    },
                    overlay.ContextCalls,
                    overlay.Constants,
                    asCount);
            }
            catch (GeneratorException e)
            {
                throw new GeneratorException($"{structure.Name}.{field.Name}: {e.Message}");
            }
        }

        private void EmitReader(Structure structure)
        {
            bool kept = overlay.Keep.ContainsKey(structure.Name);
            string pascal = Pascal(structure.Name);
            Line($"// {structure.Name}");
            Open($"internal static {(kept ? TypeName(structure.Name) : "void")} Read{pascal}(ref PrcBitReader r, PrcContext ctx)");
            Line($"ctx.Enter(\"{structure.Name}\", r.BitPosition);");
            if (HookText(structure, ":enter", structure.OnEnter is null ? [] : [structure.OnEnter]) is { } enter)
            {
                Line(enter);
            }

            // The file's own schema for this entity is run where the reference runs it: after the last
            // field, or before the user data when that is the last field.
            string? schemaAfter = null;
            if (structure.Name.StartsWith("PRC_TYPE_", StringComparison.Ordinal) && structure.Fields.Count != 0)
            {
                bool hasUserData = structure.Fields.Any(f => f.Name == "user_data" && f.DataType == "UserData");
                schemaAfter = structure.Fields.Count >= 2 && hasUserData ? structure.Fields[^2].Name : structure.Fields[^1].Name;
            }

            var keptLocals = new List<(string Member, string Local)>();
            foreach (Field field in structure.Fields)
            {
                foreach (string note in field.Notes)
                {
                    Line($"// {field.Name}: {note}");
                }

                EmitField(structure, field, keptLocals);
                if (HookText(structure, "." + field.Name, field.OnSet) is { } hook)
                {
                    Line(hook);
                }

                if (field.Name == schemaAfter)
                {
                    Line($"ctx.Schema.Evaluate(ref r, PrcType.{structure.Name});");
                    schemaAfter = null;
                }
            }

            if (HookText(structure, ":leave", structure.OnLeave is null ? [] : [structure.OnLeave]) is { } leave)
            {
                Line(leave);
            }

            Line("ctx.Leave();");
            if (kept)
            {
                if (keptLocals.Count == 0)
                {
                    Line($"return new {TypeName(structure.Name)}();");
                }
                else
                {
                    Open($"return new {TypeName(structure.Name)}");
                    foreach ((string member, string local) in keptLocals)
                    {
                        Line($"{member} = {local},");
                    }

                    _indent--;
                    Line("};");
                }
            }

            Close();
        }

        private void EmitField(Structure structure, Field field, List<(string Member, string Local)> keptLocals)
        {
            string where = $"{structure.Name}.{field.Name}";
            bool kept = IsKept(structure, field);
            bool needed = kept || IsUsed(structure, field);
            string local = Local(field.Name);
            string member = Pascal(field.Name);
            string? condition = field.Condition is null ? null : Translate(structure, field, field.Condition, asCount: false);
            if (field.VariableBits)
            {
                throw new GeneratorException($"{where}: fields with a variable number of bits are read by hand-written code only.");
            }

            if (field.IsVector && field.Size is null)
            {
                throw new GeneratorException($"{where}: a vector without a size.");
            }

            if (field.IsVector && field.Multiplicity != 0)
            {
                throw new GeneratorException($"{where}: both a vector and a fixed number of values.");
            }

            void Guarded(Action body)
            {
                if (condition is null)
                {
                    body();
                }
                else
                {
                    Open($"if ({condition})");
                    body();
                    Close();
                }
            }

            // One entity among several kinds: the kind is the number the entity starts with.
            if (field.Eligible is not null)
            {
                if (field.DataType != "UnsignedInteger" || field.IsVector || field.Multiplicity != 0)
                {
                    throw new GeneratorException($"{where}: a choice of entity kinds in a shape the generator does not know.");
                }

                Line($"uint {field.Name}__type = 0;");
                if (kept)
                {
                    Line($"object? {local} = null;");
                    keptLocals.Add((member, local));
                    keptLocals.Add((member + "Type", field.Name + "__type"));
                }

                Guarded(() =>
                {
                    Line($"{field.Name}__type = PrcContext.PeekUnsignedInteger(ref r);");
                    Open($"switch ({field.Name}__type)");
                    foreach ((string type, _) in field.Eligible)
                    {
                        Line($"case PrcType.{type}:");
                        _indent++;
                        Line(kept && IsKept(type) ? $"{local} = Read{Pascal(type)}(ref r, ctx);" : $"Read{Pascal(type)}(ref r, ctx);");
                        Line("break;");
                        _indent--;
                    }

                    Line("default:");
                    _indent++;
                    Line($"ctx.UnknownType(ref r, \"{where}\", {field.Name}__type);");
                    Line("break;");
                    _indent--;
                    Close();
                });
                return;
            }

            if (Builtins.TryGetValue(field.DataType, out Builtin? builtin))
            {
                if (builtin.CSharpType.Length == 0)
                {
                    // User data: read past, never kept.
                    if (kept || field.IsVector || field.Multiplicity != 0)
                    {
                        throw new GeneratorException($"{where}: {field.DataType} can only be read past, once.");
                    }

                    Guarded(() =>
                    {
                        Line($"ctx.Mark(\"{field.DataType}\", r.BitPosition);");
                        Line(builtin.Skip + ";");
                    });
                    return;
                }

                if (field.IsVector)
                {
                    if (builtin.Array is null)
                    {
                        throw new GeneratorException($"{where}: no array reader for {field.DataType}.");
                    }

                    string count = Translate(structure, field, field.Size!, asCount: true);
                    if (needed)
                    {
                        Line(condition is null
                            ? $"{builtin.CSharpType}[] {local} = PrcArrays.Read{builtin.Array}(ref r, ctx, {count});"
                            : $"{builtin.CSharpType}[] {local} = [];");
                        if (condition is not null)
                        {
                            Guarded(() => Line($"{local} = PrcArrays.Read{builtin.Array}(ref r, ctx, {count});"));
                        }
                    }
                    else
                    {
                        Guarded(() => Line($"PrcArrays.Skip{builtin.Array}(ref r, {count});"));
                    }
                }
                else if (field.Multiplicity != 0)
                {
                    if (needed)
                    {
                        Line($"var {local} = new {builtin.CSharpType}[{field.Multiplicity}];");
                    }

                    Guarded(() =>
                    {
                        Open($"for (int i = 0; i < {field.Multiplicity}; i++)");
                        Line(needed ? $"{local}[i] = {builtin.Read};" : (builtin.Skip ?? builtin.Read) + ";");
                        Close();
                    });
                }
                else if (needed)
                {
                    if (condition is null)
                    {
                        Line($"{builtin.CSharpType} {local} = {builtin.Read};");
                    }
                    else
                    {
                        Line($"{builtin.CSharpType} {local} = {builtin.Default};");
                        Guarded(() => Line($"{local} = {builtin.Read};"));
                    }

                    if (field.ExpectedValue is not null)
                    {
                        Guarded(() => Line($"ctx.Expect({local}, PrcType.{field.ExpectedValue}, \"{field.ExpectedValue}\");"));
                    }
                }
                else
                {
                    Guarded(() => Line((builtin.Skip ?? builtin.Read) + ";"));
                }

                if (kept)
                {
                    keptLocals.Add((member, local));
                }

                return;
            }

            // Another structure.
            if (!structures.ContainsKey(field.DataType))
            {
                throw new GeneratorException($"{where}: unknown type '{field.DataType}'.");
            }

            string read = $"Read{Pascal(field.DataType)}(ref r, ctx)";
            if (kept && !IsKept(field.DataType))
            {
                throw new GeneratorException($"{where} is kept, but nothing is kept of {field.DataType}.");
            }

            string type = TypeName(field.DataType);
            bool isValue = overlay.ValueTypes.Contains(field.DataType);
            if (field.IsVector)
            {
                string count = Translate(structure, field, field.Size!, asCount: true);
                int minimumBits = MinimumBits(field.DataType);
                if (minimumBits == 0)
                {
                    _report.Append(CultureInfo.InvariantCulture, $"{where}: an element of {field.DataType} may take no bits; the count is capped by PrcContext.\n");
                }

                if (kept)
                {
                    Line($"{type}[] {local} = [];");
                    Guarded(() =>
                    {
                        Line($"int {field.Name}__count = ctx.Count(ref r, {count}, {minimumBits});");
                        Line($"ctx.Charge({field.Name}__count * {EntityBytes(field.DataType)}L);");

                        // Grown as the entities are read: memory follows the data, not the count.
                        Line($"{local} = new {type}[Math.Min({field.Name}__count, 256)];");
                        Open($"for (int i = 0; i < {field.Name}__count; i++)");
                        Open($"if (i == {local}.Length)");
                        Line($"Array.Resize(ref {local}, (int)Math.Min({local}.Length * 2L, {field.Name}__count));");
                        Close();
                        Line();
                        Line($"{local}[i] = {read};");
                        Close();
                    });
                }
                else
                {
                    Guarded(() =>
                    {
                        Open($"for (int i = 0, n = ctx.Count(ref r, {count}, {minimumBits}); i < n; i++)");
                        Line(read + ";");
                        Close();
                    });
                }
            }
            else if (field.Multiplicity != 0)
            {
                if (kept)
                {
                    Line($"var {local} = new {type}[{field.Multiplicity}];");
                }

                Guarded(() =>
                {
                    Open($"for (int i = 0; i < {field.Multiplicity}; i++)");
                    Line(kept ? $"{local}[i] = {read};" : read + ";");
                    Close();
                });
            }
            else if (kept)
            {
                if (condition is null)
                {
                    Line($"{type} {local} = {read};");
                }
                else
                {
                    Line(isValue ? $"{type} {local} = default;" : $"{type}? {local} = null;");
                    Guarded(() => Line($"{local} = {read};"));
                }
            }
            else
            {
                Guarded(() => Line(read + ";"));
            }

            if (kept)
            {
                keptLocals.Add((member, local));
            }
        }

        /// <summary>A rough size in memory of one kept entity, for the limit on what a file may make the reader allocate.</summary>
        private int EntityBytes(string structureName)
        {
            if (!overlay.Keep.TryGetValue(structureName, out HashSet<string>? fields))
            {
                return 64;
            }

            int count = fields.Contains("*") ? structures[structureName].Fields.Count : fields.Count;
            return (overlay.ValueTypes.Contains(structureName) ? 0 : 32) + 8 * Math.Max(1, count);
        }

        private void EmitEntity(Structure structure)
        {
            bool isValue = overlay.ValueTypes.Contains(structure.Name);
            _entities.Append(CultureInfo.InvariantCulture, $"/// <summary>What is kept of <c>{structure.Name}</c>.</summary>\n");
            _entities.Append(CultureInfo.InvariantCulture, $"internal {(isValue ? "struct" : "sealed class")} {TypeName(structure.Name)}\n{{\n");
            bool first = true;
            foreach (Field field in structure.Fields.Where(f => IsKept(structure, f)))
            {
                string member = Pascal(field.Name);
                string type;
                string optional = field.Condition is null ? "" : " Default when the file does not have it.";
                if (field.Eligible is not null)
                {
                    string kinds = string.Join(", ", field.Eligible.Where(e => IsKept(e.Type)).Select(e => $"<see cref=\"{TypeName(e.Type)}\"/>"));
                    Member("object?", member, $"<c>{field.Name}</c>: one of {(kinds.Length == 0 ? "nothing that is kept" : kinds)}, by <see cref=\"{member}Type\"/>; null for a kind nothing is kept of.{optional}");
                    Member("uint", member + "Type", $"The entity type of <see cref=\"{member}\"/>.");
                    continue;
                }

                if (Builtins.TryGetValue(field.DataType, out Builtin? builtin))
                {
                    type = builtin.CSharpType;
                }
                else
                {
                    type = TypeName(field.DataType);
                    if (field.Condition is not null && !field.IsVector && field.Multiplicity == 0 && !overlay.ValueTypes.Contains(field.DataType))
                    {
                        type += "?";
                    }
                }

                if (field.IsVector || field.Multiplicity != 0)
                {
                    type += "[]";
                }

                Member(type, member, $"<c>{field.Name}</c>.{optional}");
            }

            _entities.Append("}\n");

            void Member(string type, string name, string summary)
            {
                if (!first)
                {
                    _entities.Append('\n');
                }

                first = false;
                _entities.Append(CultureInfo.InvariantCulture, $"    /// <summary>{summary}</summary>\n    public required {type} {name};\n");
            }
        }
    }
}
