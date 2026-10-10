using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Caly.Rendering.Scene3D.Prc.Container;

/// <summary>The sections of a file structure that are read.</summary>
internal enum PrcSection
{
    /// <summary>Colours, materials, pictures, styles, coordinate systems; preceded by the embedded schemas.</summary>
    Globals = 0,

    /// <summary>Product occurrences, part definitions, representation items, markup.</summary>
    Tree = 1,

    /// <summary>Tessellations.</summary>
    Tessellation = 2,

    /// <summary>The exact geometry: topological contexts and their bodies (surfaces and solids as the equations and boundaries they are).</summary>
    ExactGeometry = 3
}

/// <summary>What the uncompressed header of one file structure says.</summary>
internal readonly struct PrcFileStructureInfo
{
    public required uint Id0 { get; init; }

    public required uint Id1 { get; init; }

    public required uint Id2 { get; init; }

    public required uint Id3 { get; init; }

    public required uint VersionForRead { get; init; }

    public required uint AuthoringVersion { get; init; }

    /// <summary>The files embedded in the structure's header (pictures), as slices of the payload.</summary>
    public required ReadOnlyMemory<byte>[] Files { get; init; }

    public bool HasId(uint id0, uint id1, uint id2, uint id3) => Id0 == id0 && Id1 == id1 && Id2 == id2 && Id3 == id3;
}

/// <summary>
/// The outer layout of a PRC file: an uncompressed header naming a number of file structures, each with
/// an uncompressed header of its own and compressed sections at offsets the header gives, and one
/// compressed "model file" that ties the structures together.
/// <para>
/// Sections are inflated when first asked for, into pooled buffers, and every byte inflated counts against
/// <see cref="SceneLimits.MaxInflatedBytes"/>. The exact-geometry section is inflated only when asked for; the extra-geometry
/// section has no accessor: it is never inflated.
/// </para>
/// </summary>
internal sealed class PrcContainer : IDisposable
{
    private const int UniqueIdBytes = 16;
    private const int DescriptionBytes = UniqueIdBytes + 8 * 4;
    private const int SectionsPerStructure = 4;

    private readonly ReadOnlyMemory<byte> _payload;
    private readonly SceneLimits _limits;
    private readonly (int Start, int End)[] _ranges;      // per structure: globals, tree, tessellation, exact geometry
    private readonly byte[]?[] _inflated;
    private readonly int[] _inflatedLengths;
    private readonly PrcFileStructureInfo[] _structures;
    private readonly (int Start, int End) _modelFileRange;
    private byte[]? _modelFile;
    private int _modelFileLength;
    private long _inflatedTotal;

    private PrcContainer(ReadOnlyMemory<byte> payload, SceneLimits limits, uint versionForRead, uint authoringVersion,
        PrcFileStructureInfo[] structures, (int, int)[] ranges, (int, int) modelFileRange)
    {
        _payload = payload;
        _limits = limits;
        VersionForRead = versionForRead;
        AuthoringVersion = authoringVersion;
        _structures = structures;
        _ranges = ranges;
        _modelFileRange = modelFileRange;
        _inflated = new byte[ranges.Length][];
        _inflatedLengths = new int[ranges.Length];
    }

    /// <summary>The lowest version of the format a reader needs to understand to read the file.</summary>
    public uint VersionForRead { get; }

    /// <summary>The version of the format the file was written in.</summary>
    public uint AuthoringVersion { get; }

    public int FileStructureCount => _structures.Length;

    public ref readonly PrcFileStructureInfo this[int fileStructure] => ref _structures[fileStructure];

    /// <summary>Bytes inflated so far.</summary>
    public long InflatedBytes => _inflatedTotal;

    /// <summary>Whether the data starts as a PRC file does.</summary>
    public static bool IsPrc(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == (byte)'P' && data[1] == (byte)'R' && data[2] == (byte)'C';

    /// <summary>Reads the headers. Nothing is inflated yet.</summary>
    /// <exception cref="Scene3DException">The data is not a PRC file, or its headers contradict its length.</exception>
    public static PrcContainer Open(ReadOnlyMemory<byte> payload, SceneLimits? limits = null)
    {
        limits ??= SceneLimits.Default;
        ReadOnlySpan<byte> data = payload.Span;
        var cursor = new Cursor(data, 0);

        cursor.Magic();
        uint versionForRead = cursor.UInt32();
        uint authoringVersion = cursor.UInt32();
        cursor.Skip(2 * UniqueIdBytes);

        uint count = cursor.UInt32();
        if ((long)count * DescriptionBytes > cursor.Left)
        {
            throw Truncated("The PRC header names more file structures than the file could hold.");
        }

        var structures = new PrcFileStructureInfo[count];
        var ranges = new (int, int)[count * SectionsPerStructure];
        for (int i = 0; i < count; i++)
        {
            uint id0 = cursor.UInt32(), id1 = cursor.UInt32(), id2 = cursor.UInt32(), id3 = cursor.UInt32();
            cursor.UInt32();   // reserved
            if (cursor.UInt32() != 6)
            {
                throw Invalid("A PRC file structure does not have six sections.");
            }

            uint header = cursor.UInt32(), globals = cursor.UInt32(), tree = cursor.UInt32(), tessellation = cursor.UInt32();
            uint geometry = cursor.UInt32();
            uint extraGeometry = cursor.UInt32();   // where the exact geometry ends; the section itself is never read

            // The sections follow each other; the three that are always read must each hold something.
            if (!(header <= globals && globals < tree && tree < tessellation && tessellation < geometry && geometry <= extraGeometry && extraGeometry <= (uint)data.Length))
            {
                throw Invalid($"The section offsets of PRC file structure {i} are out of order or outside the file.");
            }

            ranges[i * SectionsPerStructure + (int)PrcSection.Globals] = ((int)globals, (int)tree);
            ranges[i * SectionsPerStructure + (int)PrcSection.Tree] = ((int)tree, (int)tessellation);
            ranges[i * SectionsPerStructure + (int)PrcSection.Tessellation] = ((int)tessellation, (int)geometry);
            ranges[i * SectionsPerStructure + (int)PrcSection.ExactGeometry] = ((int)geometry, (int)extraGeometry);
            structures[i] = ReadStructureHeader(payload, (int)header, (int)globals, id0, id1, id2, id3, i);
        }

        uint modelStart = cursor.UInt32(), modelEnd = cursor.UInt32();
        if (!(modelStart < modelEnd && modelEnd <= (uint)data.Length))
        {
            throw Invalid("The model file of the PRC file lies outside the file.");
        }

        // The uncompressed files of the file header itself are not used; the header ends before them.
        return new PrcContainer(payload, limits, versionForRead, authoringVersion, structures, ranges, ((int)modelStart, (int)modelEnd));
    }

    private static PrcFileStructureInfo ReadStructureHeader(ReadOnlyMemory<byte> payload, int start, int end, uint id0, uint id1, uint id2, uint id3, int index)
    {
        var cursor = new Cursor(payload.Span[..end], start);
        cursor.Magic();
        uint versionForRead = cursor.UInt32();
        uint authoringVersion = cursor.UInt32();
        cursor.Skip(2 * UniqueIdBytes);

        uint fileCount = cursor.UInt32();
        if ((long)fileCount * 4 > cursor.Left)
        {
            throw Truncated($"The header of PRC file structure {index} names more embedded files than it could hold.");
        }

        var files = new ReadOnlyMemory<byte>[fileCount];
        for (int f = 0; f < files.Length; f++)
        {
            uint size = cursor.UInt32();
            if (size > cursor.Left)
            {
                throw Truncated($"An embedded file of PRC file structure {index} runs past the structure's header.");
            }

            files[f] = payload.Slice(cursor.Position, (int)size);
            cursor.Skip((int)size);
        }

        return new PrcFileStructureInfo
        {
            Id0 = id0, Id1 = id1, Id2 = id2, Id3 = id3, VersionForRead = versionForRead, AuthoringVersion = authoringVersion, Files = files
        };
    }

    /// <summary>The model file, inflated: the root product occurrences and the units.</summary>
    public ReadOnlySpan<byte> ModelFile
    {
        get
        {
            _modelFile ??= Inflate(_modelFileRange, out _modelFileLength);
            return _modelFile.AsSpan(0, _modelFileLength);
        }
    }

    /// <summary>A section of a file structure, inflated on first use.</summary>
    public ReadOnlySpan<byte> Section(int fileStructure, PrcSection section)
    {
        int slot = fileStructure * SectionsPerStructure + (int)section;
        _inflated[slot] ??= Inflate(_ranges[slot], out _inflatedLengths[slot]);
        return _inflated[slot].AsSpan(0, _inflatedLengths[slot]);
    }

    /// <summary>Gives a section's buffer back once it has been read; asking for the section again inflates it again.</summary>
    public void Release(int fileStructure, PrcSection section)
    {
        int slot = fileStructure * SectionsPerStructure + (int)section;
        if (_inflated[slot] is { } buffer)
        {
            _inflated[slot] = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private byte[] Inflate((int Start, int End) range, out int length)
    {
        ReadOnlyMemory<byte> compressed = _payload[range.Start..range.End];
        long budget = Math.Max(0, _limits.MaxInflatedBytes - _inflatedTotal);

        // A first guess of four times the compressed size, doubled as often as the data asks for, and
        // never filled beyond what the limit still allows.
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(Math.Min((long)compressed.Length * 4, budget), 256, 1 << 30));
        length = 0;
        try
        {
            using var source = new MemoryStreamOverMemory(compressed);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            while (true)
            {
                int capacity = (int)Math.Min(buffer.Length, budget);
                if (length == capacity)
                {
                    if (capacity == budget)
                    {
                        // At the limit. It is exceeded only if there is more to come.
                        if (zlib.ReadByte() < 0)
                        {
                            break;
                        }

                        throw new Scene3DException(Scene3DError.LimitExceeded,
                            $"The PRC file inflates to more than the {_limits.MaxInflatedBytes} bytes allowed.");
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, Array.MaxLength));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                    continue;
                }

                int read = zlib.Read(buffer.AsSpan(length, capacity - length));
                if (read == 0)
                {
                    break;
                }

                length += read;
            }
        }
        catch (InvalidDataException e)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw new Scene3DException(Scene3DError.InvalidValue, "A compressed section of the PRC file is damaged.", e);
        }
        catch (IOException e)
        {
            // Some damage to a deflate stream is reported by the framework as a ZLibException, an IOException that is not
            // public, rather than as invalid data (net8); the source is memory, so nothing else raises it. It is the same
            // error, and says so with the same inner exception.
            ArrayPool<byte>.Shared.Return(buffer);
            throw new Scene3DException(Scene3DError.InvalidValue, "A compressed section of the PRC file is damaged.", new InvalidDataException(e.Message, e));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        _inflatedTotal += length;
        return buffer;
    }

    public void Dispose()
    {
        for (int i = 0; i < _inflated.Length; i++)
        {
            if (_inflated[i] is { } buffer)
            {
                _inflated[i] = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (_modelFile is { } modelFile)
        {
            _modelFile = null;
            ArrayPool<byte>.Shared.Return(modelFile);
        }
    }

    private static Scene3DException Invalid(string message) => new(Scene3DError.InvalidValue, message);

    private static Scene3DException Truncated(string message) => new(Scene3DError.Truncated, message);

    /// <summary>Reads the byte-aligned, little-endian values of the headers.</summary>
    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _data;

        public Cursor(ReadOnlySpan<byte> data, int position)
        {
            _data = data;
            Position = position;
            if ((uint)position > (uint)data.Length)
            {
                throw Truncated("A PRC header starts outside the file.");
            }
        }

        public int Position { get; private set; }

        public readonly int Left => _data.Length - Position;

        public void Magic()
        {
            if (Left < 3)
            {
                throw Truncated("The PRC data ends inside a header.");
            }

            if (!IsPrc(_data[Position..]))
            {
                throw Invalid("The data does not start with the PRC signature.");
            }

            Position += 3;
        }

        public uint UInt32()
        {
            if (Left < 4)
            {
                throw Truncated("The PRC data ends inside a header.");
            }

            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
            Position += 4;
            return value;
        }

        public void Skip(int count)
        {
            if (count < 0 || Left < count)
            {
                throw Truncated("The PRC data ends inside a header.");
            }

            Position += count;
        }
    }

    /// <summary>A read-only stream over a piece of the payload, so that inflating copies nothing first.</summary>
    private sealed class MemoryStreamOverMemory(ReadOnlyMemory<byte> data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, data.Length - _position);
            data.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
