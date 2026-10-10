using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Container;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Schema;

namespace Caly.Rendering.Scene3D.Prc;

/// <summary>How far the reading of one section went: a section read correctly ends in its last byte.</summary>
/// <param name="Name">The reference reader's name of the section: Global, Tree, Tessellation or ModelFile.</param>
/// <param name="FileStructure">The file structure the section belongs to; -1 for the model file.</param>
/// <param name="TotalBits">The size of the inflated section.</param>
/// <param name="ConsumedBits">The bits read.</param>
internal readonly record struct PrcSectionRead(string Name, int FileStructure, long TotalBits, long ConsumedBits)
{
    public long RemainingBits => TotalBits - ConsumedBits;
}

/// <summary>
/// What is kept of one file structure: a part, a subassembly or the assembly, as one file gave it to
/// the writer of the PRC file. Its indices (styles, colours, parts, occurrences, tessellations) are its
/// own; other file structures refer to it by its identifier.
/// </summary>
internal sealed class PrcFileStructureData
{
    /// <summary>The identifier other file structures and the model file name it by.</summary>
    public required PrcUniqueId Id { get; init; }

    /// <summary>The files embedded in its header: the pictures of its textures.</summary>
    public required ReadOnlyMemory<byte>[] Files { get; init; }

    public required PrcFileStructureInternalGlobalData Globals { get; init; }

    public required PrcAsmFileStructureTree Tree { get; init; }

    public required PrcAsmFileStructureTessellation Tessellation { get; init; }

    /// <summary>The topological contexts of the exact-geometry section; null when it was not read (see <see cref="PrcGeometryReading"/>).</summary>
    public PrcFileStructureExactGeometry? ExactGeometry { get; init; }

    /// <summary>Whether the section was needed and could not be read to its end: <see cref="ExactGeometry"/> is then null.</summary>
    public bool ExactGeometryFailed { get; init; }
}

/// <summary>When the exact-geometry section of a file structure is read.</summary>
internal enum PrcGeometryReading
{
    /// <summary>Never: the section stays packed.</summary>
    Never,

    /// <summary>When a solid that has no tessellation is among the items of a part: that is the only use of it.</summary>
    WhenNeeded,

    /// <summary>Always (to trace a file, or to read every body of a corpus).</summary>
    Always,
}

/// <summary>What is kept of a PRC file.</summary>
internal sealed class PrcFile
{
    public required PrcFileStructureData[] FileStructures { get; init; }

    public required PrcAsmModelFile ModelFile { get; init; }

    public required PrcSectionRead[] Sections { get; init; }
}

/// <summary>
/// Reads the sections of a PRC file that lead to what is displayed: per file structure the globals
/// (which start with the file's schemas), the tree and the tessellation, and when asked the exact geometry;
/// then the model file. The extra-geometry sections are never inflated.
/// </summary>
internal static class PrcFileReader
{
    public static PrcFile Read(PrcContainer container, SceneLimits? limits = null, PrcTrace? trace = null, CancellationToken cancellation = default, PrcGeometryReading geometry = PrcGeometryReading.Never)
    {
        var sections = new List<PrcSectionRead>(container.FileStructureCount * 3 + 1);
        var structures = new PrcFileStructureData[container.FileStructureCount];

        // The version that decides which fields entities have is the file's, as for the reference reader.
        var ctx = new PrcContext(container.AuthoringVersion, container.FileStructureCount, limits, cancellation) { Trace = trace };
        for (int i = 0; i < structures.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();

            var r = new PrcBitReader(container.Section(i, PrcSection.Globals), ctx.Limits.MaxStringLength);
            Begin(ctx, "Global", ref r);
            ctx.Schema = ReadSchemas(ref r, ctx);
            PrcAsmFileStructureGlobals globals = PrcReaders.ReadAsmFileStructureGlobals(ref r, ctx);
            End(ctx, sections, "Global", i, ref r);
            container.Release(i, PrcSection.Globals);

            r = new PrcBitReader(container.Section(i, PrcSection.Tree), ctx.Limits.MaxStringLength);
            Begin(ctx, "Tree", ref r);
            PrcAsmFileStructureTree tree = PrcReaders.ReadAsmFileStructureTree(ref r, ctx);
            End(ctx, sections, "Tree", i, ref r);
            container.Release(i, PrcSection.Tree);

            r = new PrcBitReader(container.Section(i, PrcSection.Tessellation), ctx.Limits.MaxStringLength);
            Begin(ctx, "Tessellation", ref r);
            PrcAsmFileStructureTessellation tessellation = PrcReaders.ReadAsmFileStructureTessellation(ref r, ctx);
            End(ctx, sections, "Tessellation", i, ref r);
            container.Release(i, PrcSection.Tessellation);

            PrcFileStructureExactGeometry? exactGeometry = null;
            bool exactGeometryFailed = false;
            if (geometry == PrcGeometryReading.Always || (geometry == PrcGeometryReading.WhenNeeded && PartsNeedExactGeometry(tree.Parts)))
            {
                r = new PrcBitReader(container.Section(i, PrcSection.ExactGeometry), ctx.Limits.MaxStringLength);
                Begin(ctx, "Geometry", ref r);
                try
                {
                    exactGeometry = PrcReaders.ReadAsmFileStructureGeometry(ref r, ctx).ExactGeometry;
                    End(ctx, sections, "Geometry", i, ref r);
                }
                catch (Scene3DException e) when (geometry == PrcGeometryReading.WhenNeeded && e.Error != Scene3DError.LimitExceeded)
                {
                    // The bodies of a section follow one another with no lengths: a body that does not read leaves the rest of the
                    // section out of reach. The solids that need it are reported by the scene; the tessellated ones are not affected.
                    exactGeometryFailed = true;
                }
                finally
                {
                    container.Release(i, PrcSection.ExactGeometry);
                }
            }

            ref readonly PrcFileStructureInfo info = ref container[i];
            structures[i] = new PrcFileStructureData
            {
                Id = new PrcUniqueId { UniqueId0 = info.Id0, UniqueId1 = info.Id1, UniqueId2 = info.Id2, UniqueId3 = info.Id3 },
                Files = info.Files,
                Globals = globals.GlobalData,
                Tree = tree,
                Tessellation = tessellation,
                ExactGeometry = exactGeometry,
                ExactGeometryFailed = exactGeometryFailed,
            };
        }

        var model = new PrcBitReader(container.ModelFile, ctx.Limits.MaxStringLength);
        Begin(ctx, "ModelFile", ref model);
        ctx.Schema = ReadSchemas(ref model, ctx);
        PrcAsmModelFile modelFile = PrcReaders.ReadAsmModelFile(ref model, ctx);
        End(ctx, sections, "ModelFile", -1, ref model);

        return new PrcFile { FileStructures = structures, ModelFile = modelFile, Sections = [.. sections] };
    }

    /// <summary>
    /// Whether a part has a solid that is drawn from its exact geometry: one that has some and no tessellation. A solid that has a
    /// tessellation is drawn from that, whatever else it has (the writer's mesh is what the document's own picture shows).
    /// </summary>
    internal static bool PartsNeedExactGeometry(ReadOnlySpan<PrcAsmPartDefinition> parts)
    {
        foreach (PrcAsmPartDefinition part in parts)
        {
            if (NeedsExactGeometry(part.RepresentationItems))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any of the items, or of those inside a set among them, is a solid drawn from its exact geometry.</summary>
    internal static bool NeedsExactGeometry(ReadOnlySpan<PrcRiRepresentationItem> items)
    {
        foreach (PrcRiRepresentationItem item in items)
        {
            switch (item.Data)
            {
                case PrcRiBrepModel { ExactGeometry: true, ItemContent.BiasedIndexTessellation: 0 }:
                    return true;
                case PrcRiSet set when NeedsExactGeometry(set.RepresentationItems):
                    return true;
            }
        }

        return false;
    }

    /// <summary>The schemas at the head of a globals section or of the model file.</summary>
    private static PrcSchemaEvaluator ReadSchemas(ref PrcBitReader r, PrcContext ctx)
    {
        ctx.Schema = PrcSchemaEvaluator.None;
        PrcSchema schema = PrcReaders.ReadSchema(ref r, ctx);
        if (schema.Schemas.Length == 0)
        {
            return PrcSchemaEvaluator.None;
        }

        var evaluator = new PrcSchemaEvaluator();
        foreach (PrcEntitySchemaDefinition definition in schema.Schemas)
        {
            evaluator.Add(definition.EntityType, definition.SchemaTokens);
        }

        return evaluator;
    }

    private static void Begin(PrcContext ctx, string name, ref PrcBitReader r)
    {
        ctx.BeginSection();
        ctx.Trace?.SectionStart(name, r.BitLength);
    }

    private static void End(PrcContext ctx, List<PrcSectionRead> sections, string name, int fileStructure, ref PrcBitReader r)
    {
        var read = new PrcSectionRead(name, fileStructure, r.BitLength, r.BitPosition);
        sections.Add(read);
        ctx.Trace?.SectionEnd(read);
    }
}
