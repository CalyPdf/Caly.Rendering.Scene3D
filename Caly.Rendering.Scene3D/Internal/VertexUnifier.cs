using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Caly.Rendering.Scene3D.Internal;

/// <summary>
/// Turns corners that index position, normal, colour and texture coordinate separately into vertices that
/// index them together: each distinct combination becomes one vertex, numbered in order of first use.
/// <para>
/// An open-addressing hash table over the combinations, in pooled arrays that grow with the number of
/// distinct vertices rather than with the number of corners. The hash is seeded per process
/// (<see cref="HashCode"/>), so a file cannot be built to make every probe collide.
/// </para>
/// </summary>
internal struct VertexUnifier : IDisposable
{
    /// <summary>Ints stored per vertex: position, normal, colour and texture coordinate index (-1 = none).</summary>
    public const int Stride = 4;

    private readonly long _maxVertices;
    private int[] _table;   // vertex index + 1; 0 marks an empty slot
    private int[] _tuples;  // Stride ints per vertex
    private int _mask;
    private int _count;

    /// <param name="expectedVertices">A guess at the number of distinct vertices; the table grows past it.</param>
    /// <param name="maxVertices">Most vertices allowed before <see cref="Scene3DError.LimitExceeded"/>.</param>
    public VertexUnifier(int expectedVertices, long maxVertices)
    {
        _maxVertices = maxVertices;
        int vertices = Math.Clamp(expectedVertices, 16, 1 << 20);
        int slots = (int)BitOperations.RoundUpToPowerOf2((uint)vertices * 2);

        _table = ArrayPool<int>.Shared.Rent(slots);
        _table.AsSpan(0, slots).Clear();
        _mask = slots - 1;
        _tuples = ArrayPool<int>.Shared.Rent(vertices * Stride);
        _count = 0;
    }

    /// <summary>Number of distinct vertices so far.</summary>
    public readonly int Count => _count;

    /// <summary>The index combination of each vertex, <see cref="Stride"/> ints per vertex.</summary>
    public readonly ReadOnlySpan<int> Tuples => _tuples.AsSpan(0, _count * Stride);

    /// <summary>The vertex for this combination of indices, added if it is new.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOrAdd(int position, int normal, int color, int texCoord)
    {
        int[] table = _table;
        int[] tuples = _tuples;
        int mask = _mask;
        int slot = HashCode.Combine(position, normal, color, texCoord) & mask;

        while (true)
        {
            int entry = table[slot];
            if (entry == 0)
            {
                return Add(slot, position, normal, color, texCoord);
            }

            int at = (entry - 1) * Stride;
            if (tuples[at] == position && tuples[at + 1] == normal && tuples[at + 2] == color && tuples[at + 3] == texCoord)
            {
                return entry - 1;
            }

            slot = (slot + 1) & mask;
        }
    }

    private int Add(int slot, int position, int normal, int color, int texCoord)
    {
        if (_count >= _maxVertices)
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, $"A mesh has more than {_maxVertices} vertices.");
        }

        int index = _count;
        int at = index * Stride;
        if (at + Stride > _tuples.Length)
        {
            int[] grown = ArrayPool<int>.Shared.Rent(Double(_tuples.Length));
            _tuples.AsSpan(0, at).CopyTo(grown);
            ArrayPool<int>.Shared.Return(_tuples);
            _tuples = grown;
        }

        _tuples[at] = position;
        _tuples[at + 1] = normal;
        _tuples[at + 2] = color;
        _tuples[at + 3] = texCoord;
        _table[slot] = index + 1;
        _count = index + 1;

        // Keep the table at most half full.
        if (_count * 2L > _mask + 1L)
        {
            Rehash(Double(_mask + 1));
        }

        return index;
    }

    private static int Double(int length)
    {
        if (length > (1 << 29))
        {
            throw new Scene3DException(Scene3DError.LimitExceeded, "A mesh has more distinct vertices than can be indexed.");
        }

        return length * 2;
    }

    private void Rehash(int slots)
    {
        ArrayPool<int>.Shared.Return(_table);
        int[] table = ArrayPool<int>.Shared.Rent(slots);
        table.AsSpan(0, slots).Clear();
        int mask = slots - 1;

        int[] tuples = _tuples;
        for (int index = 0; index < _count; index++)
        {
            int at = index * Stride;
            int slot = HashCode.Combine(tuples[at], tuples[at + 1], tuples[at + 2], tuples[at + 3]) & mask;
            while (table[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            table[slot] = index + 1;
        }

        _table = table;
        _mask = mask;
    }

    public void Dispose()
    {
        if (_table is not null)
        {
            ArrayPool<int>.Shared.Return(_table);
            ArrayPool<int>.Shared.Return(_tuples);
            _table = null!;
            _tuples = null!;
        }
    }
}
