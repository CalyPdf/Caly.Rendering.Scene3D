using Caly.Rendering.Scene3D.Prc.Generator;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The tool that writes the PRC readers from the description of the format: what it translates, what it
/// refuses, and that the readers in the repository are what it writes today.
/// </summary>
public class PrcGeneratorTests
{
    private static readonly Dictionary<string, string> ContextCalls = new()
    {
        ["get_authoring_version"] = "ctx.AuthoringVersion",
        ["VertexColors_get_number_of_colors"] = "ctx.VertexColorCount",
    };

    private static readonly Dictionary<string, long> Constants = new()
    {
        ["PRC_TRANSFORMATION_Rotate"] = 2,
        ["PRC_TRANSFORMATION_NonOrtho"] = 32,
        ["TextureMappingType::Operator"] = 3,
    };

    private static string Translate(string rust, bool asCount = false) =>
        RustExpression.Translate(rust, name => name is "unknown" or "coordinates" ? null : name == "base" ? "@base" : name, ContextCalls, Constants, asCount);

    /// <summary>Every form of condition the display structures of prc.json use.</summary>
    [Theory]
    [InlineData("!self.same_name", "!same_name")]
    [InlineData("!!self.exact_geometry", "exact_geometry")]
    [InlineData("!self.b_optimized && !!self.is_rgba", "!b_optimized && is_rgba")]
    [InlineData("self.type_.value == 5", "type_ == 5")]
    [InlineData("self.size_of_line_attributes.value > 0", "size_of_line_attributes > 0")]
    [InlineData("_ctx.get_authoring_version() >= 7039", "ctx.AuthoringVersion >= 7039")]
    [InlineData("!!self.must_calculate_normals.value && _ctx.get_authoring_version() >= 7047", "must_calculate_normals && ctx.AuthoringVersion >= 7047")]
    [InlineData("((self.behavior.value as u8 & PRC_TRANSFORMATION_Rotate as u8) != 0) && ((self.behavior.value as u8& PRC_TRANSFORMATION_NonOrtho as u8) == 0)", "((behavior & 2) != 0) && ((behavior & 32) == 0)")]
    [InlineData("self.texture_mapping_type.value as i32 == TextureMappingType::Operator as i32", "texture_mapping_type == 3")]
    [InlineData("self.texture_mapping_type.value == TextureMappingType::Operator as i32 && !!self.has_transformation.value", "texture_mapping_type == 3 && has_transformation")]
    [InlineData("self.blend_src_rgb.value != 0 as i32", "blend_src_rgb != 0")]
    [InlineData("!!self.base", "@base")]
    public void ConditionsAreTranslated(string rust, string expected)
    {
        Assert.Equal(expected, Translate(rust));
    }

    /// <summary>A count is computed in 64 bits: nothing a file says can make it overflow.</summary>
    [Theory]
    [InlineData("number_of_coordinates.value", "(long)number_of_coordinates")]
    [InlineData("2*number_default_styles.value", "2 * (long)number_default_styles")]
    [InlineData("texture_binary_data_size.value as usize * 4usize", "(long)texture_binary_data_size * 4")]
    [InlineData("_ctx.VertexColors_get_number_of_colors() - 1", "(long)ctx.VertexColorCount - 1")]
    public void SizesAreTranslatedAsWideCounts(string rust, string expected)
    {
        Assert.Equal(expected, Translate(rust, asCount: true));
    }

    /// <summary>What the translator does not know stops the generation: a guess would be a reader that misreads files.</summary>
    [Theory]
    [InlineData("self.unknown.value > 0")]                               // not a field read earlier
    [InlineData("coordinates.len() > 0")]                                // a method
    [InlineData("self.a.b.value == 1")]                                  // a field of a field
    [InlineData("_ctx.t3dc.number_of_faces(&a.b)")]                      // context state the reader does not have
    [InlineData("_ctx.something_else()")]
    [InlineData("SomeEnum::Value as i32 == self.a.value")]
    [InlineData("if !has_faces.value { 1 } else { 2 }")]
    [InlineData("self.a.value as f32 > 0")]
    [InlineData("self.a.value self.b.value")]
    [InlineData("self.a.value ==")]
    [InlineData("(self.a.value == 1")]
    [InlineData("self.a.value % 2 == 0")]
    [InlineData("")]
    public void AnythingElseStopsTheGeneration(string rust)
    {
        Assert.Throws<GeneratorException>(() => Translate(rust));
    }

    [Theory]
    [InlineData("PRC_TYPE_ASM_ProductOccurrence", "AsmProductOccurrence")]
    [InlineData("PRC_TYPE_TESS_3D_Compressed", "Tess3DCompressed")]
    [InlineData("PRC_TYPE_ROOT_PRCBaseWithGraphics", "RootPRCBaseWithGraphics")]
    [InlineData("ContentPRCBase", "ContentPRCBase")]
    [InlineData("Entity_schema_definition", "EntitySchemaDefinition")]
    [InlineData("number_of_coordinates", "NumberOfCoordinates")]
    [InlineData("type_", "Type")]
    public void NamesBecomeCSharpNames(string name, string expected)
    {
        Assert.Equal(expected, PrcReaderGenerator.Pascal(name));
    }

    private const string SmallFormat = """
        { "version": 8137, "structures": [ {
            "PRC_TYPE_Thing": { "fields": [
                { "name": "id", "data_type": "UnsignedInteger", "expected_value": ["PrcType", "PRC_TYPE_Thing"] },
                { "name": "flag", "data_type": "Boolean" },
                { "name": "label", "data_type": "String", "condition_rust": "!!self.flag", "on_set": "_ctx.remember(label.value.clone())" },
                { "name": "count", "data_type": "UnsignedInteger" },
                { "name": "parts", "data_type": "Part", "is_vec": true, "size": "count.value" },
                { "name": "other", "data_type": "UnsignedInteger", "eligible_type_kind": "PrcType", "eligible_type_ids": [["PRC_TYPE_Thing", "thing"], ["PRC_TYPE_Odd", "odd"]] },
                { "name": "user_data", "data_type": "UserData" } ] },
            "Part": { "fields": [
                { "name": "x", "data_type": "Double", "on_set": "debug!(\"{}\", x)" },
                { "name": "weights", "data_type": "Double", "multiplicity": 2 } ] },
            "PRC_TYPE_Odd": { "fields": [
                { "name": "bits", "data_type": "CharacterArray", "vbr": "8" } ] },
            "Unreached": { "fields": [ { "name": "n", "data_type": "Integer" } ] }
        } ] }
        """;

    private static string Overlay(string keep = """ "PRC_TYPE_Thing": ["label", "parts", "other"], "Part": ["*"] """, string hooks = """ "PRC_TYPE_Thing.label": "ctx.Remember(label);" """, string manual = """ "PRC_TYPE_Odd": "walked" """, string roots = """ "PRC_TYPE_Thing" """) => $$"""
        {
            "roots": [ {{roots}} ],
            "manual": { {{manual}} },
            "keep": { {{keep}} },
            "valueTypes": [],
            "hooks": { {{hooks}} },
            "ignoredHooks": [],
            "contextCalls": {},
            "constants": {}
        }
        """;

    [Fact]
    public void AStructureBecomesAReaderOfEveryFieldInOrder()
    {
        GeneratedCode code = PrcReaderGenerator.Generate(SmallFormat, Overlay());

        // The structure the roots do not lead to is not generated; the hand-written one is called, not written.
        Assert.DoesNotContain("Unreached", code.Readers);
        Assert.DoesNotContain("static void ReadOdd", code.Readers);
        Assert.Contains("internal static PrcThing ReadThing(ref PrcBitReader r, PrcContext ctx)", code.Readers);
        Assert.Contains("internal static PrcPart ReadPart(ref PrcBitReader r, PrcContext ctx)", code.Readers);

        string thing = code.Readers[code.Readers.IndexOf("// PRC_TYPE_Thing", StringComparison.Ordinal)..];
        string[] inOrder =
        [
            "ctx.Enter(\"PRC_TYPE_Thing\", r.BitPosition);",
            "uint id = r.ReadUnsignedInteger();",
            "ctx.Expect(id, PrcType.PRC_TYPE_Thing, \"PRC_TYPE_Thing\");",
            "bool flag = r.ReadBoolean();",
            "if (flag)",
            "label = r.ReadString();",
            "ctx.Remember(label);",
            "uint count = r.ReadUnsignedInteger();",
            "ctx.Count(ref r, (long)count, 6);",               // a part is three doubles of two bits at least
            "parts[i] = ReadPart(ref r, ctx);",
            "other__type = PrcContext.PeekUnsignedInteger(ref r);",
            "case PrcType.PRC_TYPE_Thing:",
            "other = ReadThing(ref r, ctx);",
            "case PrcType.PRC_TYPE_Odd:",
            "ReadOdd(ref r, ctx);",
            "ctx.UnknownType(ref r, \"PRC_TYPE_Thing.other\", other__type);",
            "ctx.Schema.Evaluate(ref r, PrcType.PRC_TYPE_Thing);",   // before the user data, as the reference does
            "ctx.Mark(\"UserData\", r.BitPosition);",
            "r.SkipUserData();",
            "ctx.Leave();",
            "Label = label,",
            "Parts = parts,",
            "Other = other,",
            "OtherType = other__type,",
        ];
        int at = 0;
        foreach (string line in inOrder)
        {
            int found = thing.IndexOf(line, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"'{line}' is missing, or not after what should precede it, in:\n{thing}");
            at = found;
        }

        Assert.Contains("internal sealed class PrcThing", code.Entities);
        Assert.Contains("public required string Label;", code.Entities);
        Assert.Contains("public required PrcPart[] Parts;", code.Entities);
        Assert.Contains("public required object? Other;", code.Entities);
        Assert.Contains("public required double[] Weights;", code.Entities);
    }

    [Fact]
    public void WhatIsNotKeptIsReadAndDropped()
    {
        GeneratedCode code = PrcReaderGenerator.Generate(SmallFormat, Overlay(keep: "", hooks: """ "PRC_TYPE_Thing.label": "" """));

        Assert.Contains("internal static void ReadThing(ref PrcBitReader r, PrcContext ctx)", code.Readers);
        Assert.Contains("internal static void ReadPart(ref PrcBitReader r, PrcContext ctx)", code.Readers);
        Assert.Contains("r.SkipString();", code.Readers);          // a text nobody needs is not built
        Assert.Contains("for (int i = 0, n = ctx.Count(ref r, (long)count, 6); i < n; i++)", code.Readers);
        Assert.DoesNotContain("class", code.Entities);
    }

    [Fact]
    public void TheSameInputsGiveTheSameText()
    {
        Assert.Equal(PrcReaderGenerator.Generate(SmallFormat, Overlay()), PrcReaderGenerator.Generate(SmallFormat, Overlay()));
    }

    [Theory]
    [InlineData("hook the overlay does not translate", null, "", null, null)]
    [InlineData("kept field of a structure nothing is kept of", """ "PRC_TYPE_Thing": ["parts"] """, null, null, null)]
    [InlineData("kept field that does not exist", """ "PRC_TYPE_Thing": ["nothing"] """, null, null, null)]
    [InlineData("kept structure that is not generated", """ "Unreached": ["*"] """, null, null, null)]
    [InlineData("hook on nothing", null, """ "PRC_TYPE_Thing.label": "", "PRC_TYPE_Thing.nothing": "" """, null, null)]
    [InlineData("field of variable bits in a generated structure", null, null, "", null)]
    [InlineData("hand-written structure nothing leads to", null, null, """ "PRC_TYPE_Odd": "walked", "Unreached": "walked" """, null)]
    [InlineData("root that does not exist", null, null, null, """ "PRC_TYPE_Nothing" """)]
    public void ADescriptionTheGeneratorCannotHonourStopsIt(string what, string? keep, string? hooks, string? manual, string? roots)
    {
        string overlay = Overlay(
            keep ?? """ "PRC_TYPE_Thing": ["label", "parts", "other"], "Part": ["*"] """,
            hooks ?? """ "PRC_TYPE_Thing.label": "ctx.Remember(label);" """,
            manual ?? """ "PRC_TYPE_Odd": "walked" """,
            roots ?? """ "PRC_TYPE_Thing" """);

        var e = Assert.Throws<GeneratorException>(() => PrcReaderGenerator.Generate(SmallFormat, overlay));
        Assert.False(string.IsNullOrWhiteSpace(e.Message), what);
    }

    /// <summary>
    /// The readers in the repository are the generator's output for the description in the repository:
    /// nobody edited them by hand, and nobody changed the description without running the generator.
    /// </summary>
    [Fact]
    public void TheCheckedInReadersAreWhatTheGeneratorWrites()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Caly.Rendering.Scene3D.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("The tests do not run from inside the repository.");
        }

        string library = Path.Combine(root, "Caly.Rendering.Scene3D.Prc");
        GeneratedCode code = PrcReaderGenerator.Generate(
            File.ReadAllText(Path.Combine(library, "Schema", "prc.json")),
            File.ReadAllText(Path.Combine(library, "Schema", "prc-display.json")));

        Assert.Equal(code.Readers, File.ReadAllText(Path.Combine(library, "Generated", "PrcReaders.g.cs")).ReplaceLineEndings("\n"));
        Assert.Equal(code.Entities, File.ReadAllText(Path.Combine(library, "Generated", "PrcEntities.g.cs")).ReplaceLineEndings("\n"));
    }
}
