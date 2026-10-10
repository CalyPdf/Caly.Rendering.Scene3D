using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Schema;

namespace Caly.Rendering.Scene3D.Prc;

/// <summary>Receives the name and position of every entity as it is read: the way a reading is compared with another reader's.</summary>
internal abstract class PrcTrace
{
    /// <summary>An entity or traced value starts.</summary>
    /// <param name="name">The format's name of the structure.</param>
    /// <param name="bitPosition">Where in the section it starts.</param>
    /// <param name="depth">How many structures enclose it.</param>
    public abstract void Entity(string name, long bitPosition, int depth);

    /// <summary>The reading of a section starts.</summary>
    public virtual void SectionStart(string name, long totalBits)
    {
    }

    /// <summary>The reading of a section is complete.</summary>
    public virtual void SectionEnd(PrcSectionRead section)
    {
    }
}

/// <summary>
/// What the reading of one section knows beyond the bits: the version the file was written in, the
/// schemas the file carries, the "current" values the format leaves out when they repeat, the limits.
/// The generated readers call it; it is used by one thread at a time.
/// </summary>
internal sealed class PrcContext
{
    /// <summary>Most elements of an array whose elements may take no bits at all, where the data cannot bound the count.</summary>
    private const int MaxFreeElements = 1 << 20;

    private readonly CancellationToken _cancellation;
    private int _depth;
    private uint _entered;
    private long _charged;
    private string _currentName = "";
    private PrcGraphicsContent _currentGraphics = NoGraphics;

    private long _faceVertexColors;
    private bool _insideWire;
    private bool _wireSegmentColor;
    private uint[] _wireIndices = [];
    private long _wireCoordinates;

    public PrcContext(uint authoringVersion, int fileStructureCount, SceneLimits? limits = null, CancellationToken cancellation = default)
    {
        AuthoringVersion = authoringVersion;
        FileStructureCount = fileStructureCount;
        Limits = limits ?? SceneLimits.Default;
        _cancellation = cancellation;
    }

    /// <summary>The version of the format the file was written in: later versions added fields.</summary>
    public uint AuthoringVersion { get; }

    /// <summary>The number of file structures of the file.</summary>
    public int FileStructureCount { get; }

    public SceneLimits Limits { get; }

    /// <summary>The schemas of the file structure being read.</summary>
    public PrcSchemaEvaluator Schema { get; set; } = PrcSchemaEvaluator.None;

    public PrcTrace? Trace { get; set; }

    /// <summary>Bytes of arrays and entities kept so far, counted against <see cref="SceneLimits.MaxTotalBytes"/>.</summary>
    public long ChargedBytes => _charged;

    /// <summary>
    /// Starts a section: the values the format calls current (the name, the graphics) start afresh at
    /// every compressed section (ISO 14739-1, 5.4 and 10.1).
    /// </summary>
    public void BeginSection()
    {
        _depth = 0;
        _currentName = "";
        _currentGraphics = NoGraphics;
        _faceVertexColors = 0;
        _insideWire = false;
    }

    public void Enter(string name, long bitPosition)
    {
        if (++_depth > Limits.MaxNestingDepth)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"PRC entities are nested more than {Limits.MaxNestingDepth} deep.");
        }

        if ((++_entered & 0x3FF) == 0)
        {
            _cancellation.ThrowIfCancellationRequested();
        }

        Trace?.Entity(name, bitPosition, _depth - 1);
    }

    /// <summary>
    /// Goes one level deeper without tracing an entity: for the structures the reference reads with code of its own, which
    /// nests what it traces by one level without a line (the loops of an ana face). Ended by <see cref="Leave"/>.
    /// </summary>
    public void Descend()
    {
        if (++_depth > Limits.MaxNestingDepth)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"PRC entities are nested more than {Limits.MaxNestingDepth} deep.");
        }
    }

    public void Leave() => _depth--;

    /// <summary>Reports a value the reference reader traces although it is not a structure.</summary>
    public void Mark(string name, long bitPosition) => Trace?.Entity(name, bitPosition, _depth);

    /// <summary>
    /// Checks a number of elements the file gives against what is left of the data, each element taking
    /// at least <paramref name="minimumBitsEach"/> bits: nothing is ever allocated or looped on a count alone.
    /// </summary>
    public int Count(ref PrcBitReader r, long count, int minimumBitsEach) => CheckCount(ref r, count, minimumBitsEach);

    public static int CheckCount(ref PrcBitReader r, long count, int minimumBitsEach)
    {
        if (count <= 0)
        {
            return 0;
        }

        if (minimumBitsEach == 0 ? count > MaxFreeElements : count > r.BitsLeft / minimumBitsEach)
        {
            throw new Scene3DException(Scene3DError.Truncated, $"A PRC array of {count} entries does not fit in the {r.BitsLeft} bits left.");
        }

        return (int)Math.Min(count, int.MaxValue);
    }

    /// <summary>Counts memory about to be allocated for what is kept.</summary>
    public void Charge(long bytes)
    {
        _charged += bytes;
        if (_charged > Limits.MaxTotalBytes)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"The PRC file holds more than the {Limits.MaxTotalBytes} bytes of data allowed.");
        }
    }

    public void Expect(uint actual, uint expected, string name)
    {
        if (actual != expected)
        {
            throw new Scene3DException(Scene3DError.InvalidValue, $"A PRC entity of type {PrcType.NameOf(actual)} stands where {name} is expected.");
        }
    }

    /// <summary>The unsigned integer at the position, which stays where it is: the type of the entity that follows.</summary>
    public static uint PeekUnsignedInteger(ref PrcBitReader r)
    {
        long position = r.BitPosition;
        uint value = r.ReadUnsignedInteger();
        r.Seek(position);
        return value;
    }

    /// <summary>An entity of a kind the place does not allow, or of a kind added after the described version.</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public void UnknownType(ref PrcBitReader r, string where, uint type)
    {
        throw new Scene3DException(Scene3DError.Unsupported, $"A PRC entity of type {PrcType.NameOf(type)}, at bit {r.BitPosition}, is not one this reader knows for {where}.");
    }

    /// <summary>
    /// The name of an entity: the format leaves it out when it is the name of the entity read before
    /// (ISO 14739-1, the current name).
    /// </summary>
    public string ResolveName(bool same, string name)
    {
        if (same)
        {
            return _currentName;
        }

        _currentName = name;
        return name;
    }

    /// <summary>
    /// The graphics before any entity gave some: no layer, no style, shown. (The format's text gives
    /// the behaviour "-1" as the start; taken as its bits it would say removed, which no entity that
    /// merely repeats the start can mean.)
    /// </summary>
    public static PrcGraphicsContent NoGraphics => new() { BiasedLayerIndex = 0, BiasedIndexOfLineStyle = 0, BehaviorBitField = 0x0001 };

    /// <summary>
    /// The graphics of an entity: the format leaves them out when they are those of the entity read
    /// before (ISO 14739-1, the current graphics).
    /// </summary>
    public PrcGraphicsContent ResolveGraphics(bool same, PrcGraphicsContent graphics)
    {
        if (same)
        {
            return _currentGraphics;
        }

        _currentGraphics = graphics;
        return graphics;
    }

    /// <summary>Whether the curve being read lives in 3D (otherwise in the (u, v) of a surface): its points have two or three coordinates.</summary>
    public bool CurveIs3D { get; set; }

    /// <summary>Whether the body being read has a bounding box after its topology.</summary>
    public bool BodyHasBoundingBox { get; set; }

    /// <summary>Whether the NURBS curve being read has weights.</summary>
    public bool NurbsCurveIsRational { get; set; }

    /// <summary>Whether a wire tessellation is being read: its vertex colours have one field more.</summary>
    public bool InsideWire => _insideWire;

    /// <summary>
    /// The number of colours of the vertex colours being read. The file does not say it: it follows from
    /// the points of the face or of the wire the colours belong to (PDF Association issue #820).
    /// </summary>
    public long VertexColorCount => _insideWire ? WireColorCount() : _faceVertexColors;

    /// <summary>A face's primitives have been read: its vertex colours, if any, are one per point index.</summary>
    public void SetFace(uint usedEntitiesFlag, uint[] triangulatedData)
    {
        _faceVertexColors = Tessellation.PrcFaceLayout.PointCount(usedEntitiesFlag, triangulatedData);
    }

    /// <summary>The coordinates of a tessellation have been read; a wire without indices is one edge through them all.</summary>
    public void SetCoordinateCount(uint coordinateCount) => _wireCoordinates = coordinateCount;

    public void EnterWire()
    {
        _insideWire = true;
        _wireSegmentColor = false;
        _wireIndices = [];
        _wireCoordinates = 0;
    }

    public void LeaveWire()
    {
        _insideWire = false;
        _wireIndices = [];
    }

    public void SetWireIndices(uint[] wireIndices) => _wireIndices = wireIndices;

    public void SetWireSegmentColor(bool isSegmentColor)
    {
        if (_insideWire)
        {
            _wireSegmentColor = isSegmentColor;
        }
    }

    /// <summary>
    /// One colour per point of each wire edge, or per segment. An edge is a count, with flags in its
    /// four highest bits, followed by that many indices; a closing edge has one point more than it
    /// stores. Without indices the coordinates are one edge (ISO 14739-1, 3D wire tessellation).
    /// </summary>
    private long WireColorCount()
    {
        const uint IsClosing = 0x10000000;
        long colors = 0;
        if (_wireIndices.Length == 0)
        {
            long points = _wireCoordinates / 3;
            return _wireSegmentColor ? Math.Max(0, points - 1) : points;
        }

        long at = 0;
        while (at < _wireIndices.Length)
        {
            uint head = _wireIndices[at];
            long points = head & 0x0FFFFFFF;
            at += 1 + points;
            if (at > _wireIndices.Length)
            {
                throw new Scene3DException(Scene3DError.InvalidValue, "A wire edge of a PRC tessellation has more points than there are indices.");
            }

            if ((head & IsClosing) != 0)
            {
                points++;
            }

            colors += _wireSegmentColor ? Math.Max(0, points - 1) : points;
        }

        return colors;
    }
}
