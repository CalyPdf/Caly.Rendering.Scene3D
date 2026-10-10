namespace Caly.Rendering.Scene3D.U3D.Internal;

/// <summary>
/// One U3D file being read. <see cref="Load"/> walks the blocks once and fills lookup tables keyed by the
/// names objects are referred to by; <see cref="BuildScene"/> then resolves those names and emits the scene.
/// Two passes are needed because the format refers to things by name in any order.
/// </summary>
internal sealed partial class U3DDocument
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly SceneLimits _limits;
    private readonly CancellationToken _cancellationToken;
    private readonly U3DContexts _contexts = new();
    private readonly SortedDictionary<uint, int> _skippedBlocks = [];

    private U3DHeader _header;

    public U3DDocument(ReadOnlyMemory<byte> data, SceneLimits limits, CancellationToken cancellationToken)
    {
        _data = data;
        _limits = limits;
        _cancellationToken = cancellationToken;
    }

    public void Load()
    {
        ReadOnlySpan<byte> file = _data.Span;
        _header = U3DHeader.Read(file);

        List<U3DBlock> blocks = U3DBlockWalker.Walk(file, _limits, _cancellationToken);

        // The types of the modifier chains enclosing the current block, outermost first.
        var chainTypes = new List<int>();

        for (int i = 0; i < blocks.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            U3DBlock block = blocks[i];

            if (chainTypes.Count > block.Depth)
            {
                chainTypes.RemoveRange(block.Depth, chainTypes.Count - block.Depth);
            }

            switch (block.Type)
            {
                case U3DConstants.BlockModifierChain:
                    // Its contents follow as blocks of their own, one level deeper.
                    chainTypes.Add(ReadModifierChainType(block));
                    break;

                case U3DConstants.BlockFileHeader:
                case U3DConstants.BlockPriorityUpdate:
                    break;

                case U3DConstants.BlockMaterialResource:
                    ReadMaterialResource(block);
                    break;
                case U3DConstants.BlockLitTextureShader:
                    ReadLitTextureShader(block);
                    break;
                case U3DConstants.BlockShadingModifier:
                    // Outside any chain a modifier can only be a node's.
                    ReadShadingModifier(block, chainTypes.Count > 0 ? chainTypes[^1] : 0);
                    break;
                case U3DConstants.BlockTextureDeclaration:
                    ReadTextureDeclaration(block);
                    break;
                case U3DConstants.BlockTextureContinuation:
                    ReadTextureContinuation(block);
                    break;

                case U3DConstants.BlockGroupNode:
                    ReadNode(block, SceneNodeKind.Group);
                    break;
                case U3DConstants.BlockModelNode:
                    ReadNode(block, SceneNodeKind.Model);
                    break;
                case U3DConstants.BlockLightNode:
                    ReadNode(block, SceneNodeKind.Light);
                    break;
                case U3DConstants.BlockViewNode:
                    ReadNode(block, SceneNodeKind.Camera);
                    break;
                case U3DConstants.BlockLightResource:
                    ReadLightResource(block);
                    break;

                case U3DConstants.BlockMeshDeclaration:
                    ReadMeshDeclaration(block);
                    break;
                case U3DConstants.BlockBaseMeshContinuation:
                    ReadBaseMeshContinuation(block);
                    break;

                case U3DConstants.BlockViewResource:
                    // Fog and render passes only; nothing a scene carries.
                    break;

                case U3DConstants.BlockProgressiveMeshContinuation:
                    NoteProgressiveMesh(block);
                    break;

                case U3DConstants.BlockMotionResource:
                    ReadMotionResource(block);
                    break;
                case U3DConstants.BlockAnimationModifier:
                    ReadAnimationModifier(block, chainTypes.Count > 0 ? chainTypes[^1] : 0);
                    break;
                case U3DConstants.BlockBoneWeightModifier:
                    ReadBoneWeights(block, chainTypes.Count > 0 ? chainTypes[^1] : 0);
                    break;

                case U3DConstants.BlockPointSetDeclaration:
                    ReadSetDeclaration(block, lines: false);
                    break;
                case U3DConstants.BlockLineSetDeclaration:
                    ReadSetDeclaration(block, lines: true);
                    break;
                case U3DConstants.BlockPointSetContinuation:
                case U3DConstants.BlockLineSetContinuation:
                    ReadSetContinuation(block);
                    break;

                case U3DConstants.BlockNewObjectType:
                    ReadNewObjectType(block);
                    break;
                case U3DConstants.BlockRHAdobeMeshResource:
                    ReadRHAdobeMeshResource(block);
                    break;

                default:
                    if (_customRHMeshBlockTypes is not null && _customRHMeshBlockTypes.Contains(block.Type))
                    {
                        ReadRHAdobeMeshResource(block);
                    }
                    else
                    {
                        _skippedBlocks[block.Type] = _skippedBlocks.GetValueOrDefault(block.Type) + 1;
                    }
                    break;
            }
        }
    }

    /// <summary>The type of a modifier chain (ECMA-363 9.4.3.2): 0 node, 1 model resource, 2 texture resource.</summary>
    private int ReadModifierChainType(U3DBlock block)
    {
        // The walker has already checked that the chain's preamble is all there.
        U3DBitReader reader = OpenBlock(block);
        reader.SkipString();
        return (int)Math.Min(reader.ReadU32(), int.MaxValue);
    }

    /// <summary>Returns what the document holds of pooled memory; it reads nothing afterwards.</summary>
    public void Release() => _contexts.Dispose();

    /// <summary>A reader over one block's data, with fresh compression contexts.</summary>
    private U3DBitReader OpenBlock(U3DBlock block)
    {
        _contexts.Reset();
        return new U3DBitReader(_data.Span.Slice(block.DataOffset, block.DataLength), _contexts, _header.NoCompression);
    }

    private string ReadName(ref U3DBitReader reader) => reader.ReadString(_limits.MaxStringLength);

    private void ReportSkippedBlocks(SceneBuilder builder)
    {
        foreach ((uint type, int count) in _skippedBlocks)
        {
            string code = type switch
            {
                U3DConstants.BlockProgressiveMeshContinuation => "progressive-mesh-skipped",
                U3DConstants.BlockPointSetDeclaration or U3DConstants.BlockPointSetContinuation => "point-set-skipped",
                U3DConstants.BlockLineSetDeclaration or U3DConstants.BlockLineSetContinuation => "line-set-skipped",
                U3DConstants.BlockBoneWeightModifier => "bone-weights-skipped",
                U3DConstants.BlockFileReference => "file-reference-skipped",
                U3DConstants.BlockNewObjectType => "extension-skipped",
                U3DConstants.BlockSubdivisionModifier or U3DConstants.BlockClodModifier or U3DConstants.BlockGlyphModifier => "modifier-skipped",
                _ when type is >= 0x00000100 and <= 0x00FFFFFF => "extension-skipped",
                _ => "unknown-block-skipped"
            };

            SceneDiagnosticSeverity severity = code is "modifier-skipped" or "animation-skipped" ? SceneDiagnosticSeverity.Info : SceneDiagnosticSeverity.Warning;
            for (int i = 0; i < count; i++)
            {
                builder.Report(severity, code, $"U3D block type 0x{type:X8}");
            }
        }
    }

    private static Scene3DException Invalid(string message) => new(Scene3DError.InvalidValue, message);

    private static Scene3DException Truncated(string message) => new(Scene3DError.Truncated, message);
}
