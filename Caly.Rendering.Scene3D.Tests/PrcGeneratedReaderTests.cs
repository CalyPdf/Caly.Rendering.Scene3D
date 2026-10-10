using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Schema;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The generated readers on entities written bit by bit: the five structures the generator was proven
/// on (a name, the base of every entity, a colour, a transformation, a tessellation) and what the
/// context adds around them.
/// </summary>
public class PrcGeneratedReaderTests
{
    private const uint SchemaInteger = 4, SchemaString = 5, SchemaBlockStart = 19, SchemaBlockVersion = 20, SchemaBlockEnd = 21;

    private static PrcContext Context(uint authoringVersion = 8137, SceneLimits? limits = null) => new(authoringVersion, 1, limits);

    private sealed class Recorder : PrcTrace
    {
        public List<string> Lines { get; } = [];

        public override void Entity(string name, long bitPosition, int depth) => Lines.Add($"{new string(' ', depth)}{name} {bitPosition}");
    }

    [Fact]
    public void ANameLeftOutIsTheNameBefore()
    {
        var data = new PrcBitWriter().Bit(false).String("wheel").Bit(true).Bit(false).String("axle").Bit(true).Bit(false).String(null);
        var r = new PrcBitReader(data.ToArray());
        PrcContext ctx = Context();

        Assert.Equal("wheel", PrcReaders.ReadName(ref r, ctx).Name);
        Assert.Equal("wheel", PrcReaders.ReadName(ref r, ctx).Name);
        Assert.Equal("axle", PrcReaders.ReadName(ref r, ctx).Name);
        Assert.Equal("axle", PrcReaders.ReadName(ref r, ctx).Name);
        Assert.Equal("", PrcReaders.ReadName(ref r, ctx).Name);
        Assert.Equal(data.BitLength, r.BitPosition);

        // Every section starts without a current name.
        r = new PrcBitReader(new PrcBitWriter().Bit(true).ToArray());
        ctx.BeginSection();
        Assert.Equal("", PrcReaders.ReadName(ref r, ctx).Name);
    }

    [Fact]
    public void TheBaseOfAnEntityIsItsAttributesAndItsName()
    {
        // One attribute titled by text with two values (an integer and a text), then the name.
        var data = new PrcBitWriter()
            .Unsigned(1)
                .Unsigned(PrcType.PRC_TYPE_MISC_Attribute)
                .Bit(false).String("material")
                .Unsigned(2)
                    .Bit(true).Unsigned(7).Unsigned(1).Integer(-12)
                    .Bit(false).String("finish").Unsigned(4).String("brushed")
            .Bit(false).String("bracket");
        var r = new PrcBitReader(data.ToArray());
        var trace = new Recorder();
        PrcContext ctx = Context();
        ctx.Trace = trace;

        PrcContentPRCBase entity = PrcReaders.ReadContentPRCBase(ref r, ctx);

        Assert.Equal("bracket", entity.EntityName.Name);
        Assert.Equal(data.BitLength, r.BitPosition);

        // Every structure is reported as it starts, indented by how many enclose it.
        Assert.Equal("ContentPRCBase 0", trace.Lines[0]);
        Assert.Equal(" AttributeData 0", trace.Lines[1]);
        Assert.Equal("  PRC_TYPE_MISC_Attribute 10", trace.Lines[2]);
        Assert.Equal(
            ["ContentPRCBase", " AttributeData", "  PRC_TYPE_MISC_Attribute", "   AttributeEntry", "   AttributeKeyValue", "    AttributeEntry", "   AttributeKeyValue", "    AttributeEntry", " Name"],
            trace.Lines.Select(l => l[..l.LastIndexOf(' ')]));
    }

    [Fact]
    public void AColourIsThreeDoubles()
    {
        var data = new PrcBitWriter().Double(1).Double(0.5).Double(0.25);
        var r = new PrcBitReader(data.ToArray());

        PrcRgbColor color = PrcReaders.ReadRgbColor(ref r, Context());

        Assert.Equal((1, 0.5, 0.25), (color.Red, color.Green, color.Blue));
        Assert.Equal(data.BitLength, r.BitPosition);
    }

    [Fact]
    public void ATransformationHasThePartsItsFlagsName()
    {
        // Translation and rotation.
        var data = new PrcBitWriter().Bits(0x01 | 0x02, 8).Double(1).Double(2).Double(3).Double(0).Double(1).Double(0).Double(-1).Double(0).Double(0);
        var r = new PrcBitReader(data.ToArray());
        PrcTransformation3D t = PrcReaders.ReadTransformation3D(ref r, Context());
        Assert.Equal(data.BitLength, r.BitPosition);
        Assert.Equal(3, t.Behavior);
        Assert.Equal((1, 2, 3), (t.Translation.X, t.Translation.Y, t.Translation.Z));
        Assert.Equal((0, 1, 0), (t.Rotation[0].X, t.Rotation[0].Y, t.Rotation[0].Z));
        Assert.Equal((-1, 0, 0), (t.Rotation[1].X, t.Rotation[1].Y, t.Rotation[1].Z));

        // A general matrix replaces the rotation even when both flags are set; then the scales and the fourth row.
        data = new PrcBitWriter().Bits(0x02 | 0x20 | 0x10 | 0x08 | 0x40, 8);
        for (int i = 1; i <= 9 + 3 + 1 + 4; i++)
        {
            data.Double(i);
        }

        r = new PrcBitReader(data.ToArray());
        t = PrcReaders.ReadTransformation3D(ref r, Context());
        Assert.Equal(data.BitLength, r.BitPosition);
        Assert.Equal((1, 5, 9), (t.NonOrthoMatrix[0].X, t.NonOrthoMatrix[1].Y, t.NonOrthoMatrix[2].Z));
        Assert.Equal(0, t.Rotation[0].Y);
        Assert.Equal((10, 11, 12), (t.NonUniformScale.X, t.NonUniformScale.Y, t.NonUniformScale.Z));
        Assert.Equal(13, t.Scale);
        Assert.Equal([14, 15, 16, 17], t.Homogeneous);

        // The identity is its flags and nothing else.
        r = new PrcBitReader([0, 0xFF]);
        PrcReaders.ReadTransformation3D(ref r, Context());
        Assert.Equal(8, r.BitPosition);
    }

    /// <summary>A tessellation of one triangle, as a file of the given version writes it.</summary>
    private static PrcBitWriter Triangle(uint version, bool vertexColors = false, uint lineAttributes = 0, uint id = PrcType.PRC_TYPE_TESS_3D)
    {
        var w = new PrcBitWriter().Unsigned(id);
        w.Bit(false).Unsigned(9);
        for (int i = 0; i < 9; i++)
        {
            w.Double(i);
        }

        w.Bit(true);                                   // has faces
        if (version >= 7039)
        {
            w.Bit(false);                              // has loops
        }

        if (version >= 7047)
        {
            w.Bit(true).Bits(0, 8).Double(0.5);        // normals to be calculated, with their flags and crease angle
        }

        w.Unsigned(0);                                 // normals
        w.Unsigned(0);                                 // wire indices
        w.Unsigned(3).Unsigned(0).Unsigned(3).Unsigned(6);

        w.Unsigned(1);                                 // faces
        w.Unsigned(PrcType.PRC_TYPE_TESS_Face);
        w.Unsigned(lineAttributes);
        for (uint i = 0; i < lineAttributes; i++)
        {
            w.Unsigned(i + 1);
        }

        w.Unsigned(0).Unsigned(0);                     // wire start, no wire sizes
        w.Unsigned(0x0002).Unsigned(0).Unsigned(1).Unsigned(1);   // triangles, from index 0: one of them
        w.Unsigned(0);                                 // texture coordinate indices
        w.Bit(vertexColors);
        if (vertexColors)
        {
            // Not RGBA, not optimised; a colour for the first point, then per point: the same, or a colour.
            w.Bit(false).Bit(false).Bits(255, 8).Bits(0, 8).Bits(0, 8).Bit(true).Bit(false).Bits(0, 8).Bits(0, 8).Bits(255, 8);
        }

        if (lineAttributes > 0)
        {
            w.Unsigned(1);                             // behaviour
        }

        return w.Unsigned(0);                          // texture coordinates
    }

    /// <summary>Fields the format gained are read from files written since (PDF Association issue #705).</summary>
    [Theory]
    [InlineData(7000u, false, 0.0)]
    [InlineData(7039u, false, 0.0)]
    [InlineData(7047u, true, 0.5)]
    [InlineData(15083u, true, 0.5)]
    public void ATessellationIsReadAsItsVersionWroteIt(uint version, bool calculateNormals, double creaseAngle)
    {
        PrcBitWriter data = Triangle(version);
        var r = new PrcBitReader([.. data.ToArray(), 0xFF, 0xFF]);

        PrcTess3D tess = PrcReaders.ReadTess3D(ref r, Context(version));

        Assert.Equal(data.BitLength, r.BitPosition);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], tess.TessellationCoordinates.Coordinates);
        Assert.True(tess.HasFaces);
        Assert.Equal(calculateNormals, tess.MustCalculateNormals);
        Assert.Equal(creaseAngle, tess.CreaseAngle);
        Assert.Empty(tess.NormalCoordinates);
        Assert.Equal(new uint[] { 0, 3, 6 }, tess.TriangulatedIndexArray);
        PrcTessFace face = Assert.Single(tess.FaceTessellationData);
        Assert.Equal(2u, face.UsedEntitiesFlag);
        Assert.Equal(new uint[] { 1 }, face.Triangulateddata);
        Assert.Null(face.VertexColorData);
    }

    /// <summary>The number of vertex colours is not in the file: one per point of the face (issue #820).</summary>
    [Fact]
    public void VertexColoursAreOnePerPointOfTheFace()
    {
        PrcBitWriter data = Triangle(8137, vertexColors: true, lineAttributes: 1);
        var r = new PrcBitReader([.. data.ToArray(), 0xFF, 0xFF]);

        PrcTess3D tess = PrcReaders.ReadTess3D(ref r, Context());

        Assert.Equal(data.BitLength, r.BitPosition);

        // Red, then "the same", then blue: RGBA with red in the low byte, opaque.
        Assert.Equal(new uint[] { 0xFF0000FF, 0xFF0000FF, 0xFFFF0000 }, tess.FaceTessellationData[0].VertexColorData!.Colors);
        Assert.Equal(new uint[] { 1 }, tess.FaceTessellationData[0].LineAttributes);
        Assert.Equal(1u, tess.FaceTessellationData[0].Behavior);
    }

    [Fact]
    public void OneKindOfTessellationAmongSeveral()
    {
        PrcBitWriter data = Triangle(8137);
        var r = new PrcBitReader([.. data.ToArray(), 0xFF, 0xFF]);
        PrcTess tess = PrcReaders.ReadTess(ref r, Context());
        Assert.Equal(PrcType.PRC_TYPE_TESS_3D, tess.IdType);
        Assert.IsType<PrcTess3D>(tess.Id);
        Assert.Equal(data.BitLength, r.BitPosition);

        // A markup tessellation keeps its coordinates and codes; its text strings need a font and are read past.
        data = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_Markup).Bit(false).Unsigned(3).Double(1).Double(2).Double(3)
            .Unsigned(2).Unsigned(10).Unsigned(20).Unsigned(1).String("a text").String("label").Bits(0, 8);
        r = new PrcBitReader([.. data.ToArray(), 0xFF, 0xFF]);
        tess = PrcReaders.ReadTess(ref r, Context());
        Assert.Equal(PrcType.PRC_TYPE_TESS_Markup, tess.IdType);
        var markup = Assert.IsType<PrcTessMarkup>(tess.Id);
        Assert.Equal([10u, 20u], markup.CodeNumbers);
        Assert.Equal([1.0, 2.0, 3.0], markup.TessellationCoordinates.Coordinates);
        Assert.Equal(data.BitLength, r.BitPosition);
    }

    [Fact]
    public void AnEntityOfAnotherKindThanExpectedIsRefused()
    {
        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(Triangle(8137, id: PrcType.PRC_TYPE_TESS_Markup).ToArray());
            PrcReaders.ReadTess3D(ref r, Context());
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
        Assert.Contains("PRC_TYPE_TESS_Markup", e.Message);
        Assert.Contains("PRC_TYPE_TESS_3D", e.Message);

        // Where several kinds may stand, a kind that is none of them.
        e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_RI_Set).Bits(0, 32).ToArray());
            PrcReaders.ReadTess(ref r, Context());
        });
        Assert.Equal(Scene3DError.Unsupported, e.Error);
        Assert.Contains("PRC_TYPE_RI_Set", e.Message);
    }

    /// <summary>
    /// What a later version added to an entity is read past with the file's schema: at the end of the
    /// entity, but before its user data.
    /// </summary>
    [Fact]
    public void TheSchemaOfAnEntityRunsAtItsEndBeforeTheUserData()
    {
        var schemas = new PrcSchemaEvaluator();
        schemas.Add(PrcType.PRC_TYPE_ASM_FileStructureTessellation, [SchemaBlockStart, SchemaBlockVersion, 9000, SchemaString, SchemaInteger, SchemaBlockEnd, SchemaBlockEnd]);
        schemas.Add(PrcType.PRC_TYPE_TESS_3D, [SchemaBlockStart, SchemaBlockVersion, 8200, SchemaInteger, SchemaBlockEnd, SchemaBlockEnd]);

        var data = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_ASM_FileStructureTessellation)
            .Unsigned(0).Bit(false).String("tessellations")   // base: no attributes, a name
            .Unsigned(1);

        // The tessellation does not end on a byte: it is copied bit by bit.
        PrcBitWriter tess = Triangle(8137);
        byte[] tessBytes = tess.ToArray();
        for (long i = 0; i < tess.BitLength; i++)
        {
            data.Bit((tessBytes[i / 8] & (0x80 >> (int)(i % 8))) != 0);
        }

        data.Integer(-77);                                    // added to the tessellation by version 8200
        data.String("added by version 9000").Integer(5);      // added to the section's entity
        data.Unsigned(11).Bits(0b10110011101, 11);            // user data

        var r = new PrcBitReader([.. data.ToArray(), 0xFF, 0xFF]);
        PrcContext ctx = Context(9100);
        ctx.Schema = schemas;
        PrcAsmFileStructureTessellation section = PrcReaders.ReadAsmFileStructureTessellation(ref r, ctx);

        Assert.Equal(data.BitLength, r.BitPosition);
        Assert.IsType<PrcTess3D>(Assert.Single(section.Tess).Id);
    }

    /// <summary>Review focus: a count is checked against the data before anything is allocated or looped.</summary>
    [Fact]
    public void ACountTheDataCannotHoldIsRefusedBeforeAllocating()
    {
        // Four thousand million coordinates in a few bytes.
        var data = new PrcBitWriter().Unsigned(PrcType.PRC_TYPE_TESS_3D).Bit(false).Unsigned(4_000_000_000).Double(1).Double(2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(data.ToArray());
            PrcReaders.ReadTess3D(ref r, Context());
        });
        Assert.Equal(Scene3DError.Truncated, e.Error);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);

        // The same for an array of entities, kept (faces) and not kept (attributes).
        e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(new PrcBitWriter().Unsigned(3_000_000_000).Bits(0, 32).ToArray());
            PrcReaders.ReadContentPRCBase(ref r, Context());
        });
        Assert.Equal(Scene3DError.Truncated, e.Error);
    }

    [Fact]
    public void WhatIsKeptCountsAgainstTheLimit()
    {
        PrcBitWriter data = Triangle(8137);
        var limits = new SceneLimits { MaxTotalBytes = 50 };

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(data.ToArray());
            PrcReaders.ReadTess3D(ref r, Context(limits: limits));
        });
        Assert.Equal(Scene3DError.LimitExceeded, e.Error);

        var ok = new PrcBitReader([.. data.ToArray(), 0xFF]);
        PrcContext ctx = Context(limits: new SceneLimits { MaxTotalBytes = 1000 });
        PrcReaders.ReadTess3D(ref ok, ctx);
        Assert.InRange(ctx.ChargedBytes, 9 * 8 + 3 * 4, 1000);
    }

    [Fact]
    public void DataThatEndsInsideAnEntityIsTruncation()
    {
        PrcBitWriter whole = Triangle(8137, vertexColors: true, lineAttributes: 2);
        byte[] bytes = whole.ToArray();
        for (int length = 0; length < bytes.Length - 1; length++)
        {
            byte[] cut = bytes[..length];
            Scene3DException e = Assert.Throws<Scene3DException>(() =>
            {
                var r = new PrcBitReader(cut);
                PrcReaders.ReadTess3D(ref r, Context());
            });
            Assert.Equal(Scene3DError.Truncated, e.Error);
        }
    }

    /// <summary>Review focus: entities inside entities without end.</summary>
    [Fact]
    public void NestingDeeperThanTheLimitIsRefusedNotOverflowed()
    {
        // A set of representation items holding a set holding a set...
        var data = new PrcBitWriter();
        for (int i = 0; i < 2000; i++)
        {
            data.Unsigned(PrcType.PRC_TYPE_RI_Set).Unsigned(0);   // no attributes
            if (i == 0)
            {
                data.Bit(false).String("set");
            }
            else
            {
                data.Bit(true);                                // the name as before
            }

            data.Unsigned(0).Unsigned(0).Unsigned(0)           // identifiers
                .Bit(true)                                     // graphics as before
                .Unsigned(0).Unsigned(0)                       // local coordinate system, tessellation: none
                .Unsigned(1);                                  // one item: the next set
        }

        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(data.ToArray());
            PrcReaders.ReadRiRepresentationItem(ref r, Context());
        });
        Assert.Equal(Scene3DError.LimitExceeded, e.Error);

        // Three sets deep is nothing unusual.
        var shallow = new PrcBitWriter();
        for (int i = 0; i < 3; i++)
        {
            shallow.Unsigned(PrcType.PRC_TYPE_RI_Set).Unsigned(0).Bit(true).Unsigned(0).Unsigned(0).Unsigned(0).Bit(true).Unsigned(0).Unsigned(0).Unsigned(i < 2 ? 1u : 0u);
        }

        shallow.Unsigned(0).Unsigned(0).Unsigned(0);           // the user data of each, innermost first
        var ok = new PrcBitReader([.. shallow.ToArray(), 0xFF]);
        PrcReaders.ReadRiRepresentationItem(ref ok, Context());
        Assert.Equal(shallow.BitLength, ok.BitPosition);
    }
}
