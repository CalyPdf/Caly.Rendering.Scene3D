using System.Buffers;

namespace Caly.Rendering.Scene3D.Internal;

/// <summary>
/// A growable array rented from <see cref="ArrayPool{T}.Shared"/>. A mutable struct: keep it in a field
/// and never copy it.
/// </summary>
internal struct PooledBuffer<T>
    where T : unmanaged
{
    private T[]? _array;
    private int _count;

    public readonly int Count => _count;

    public readonly Span<T> Span => _array is null ? default : _array.AsSpan(0, _count);

    /// <summary>Appends <paramref name="count"/> zeroed elements and returns them.</summary>
    public Span<T> Append(int count)
    {
        Span<T> span = AppendUninitialized(count);
        span.Clear();
        return span;
    }

    /// <summary>Appends <paramref name="count"/> elements whose content is whatever the pool left there.</summary>
    public Span<T> AppendUninitialized(int count)
    {
        if (count == 0)
        {
            return default;
        }

        int required = checked(_count + count);
        if (_array is null || required > _array.Length)
        {
            Grow(required);
        }

        Span<T> span = _array.AsSpan(_count, count);
        _count = required;
        return span;
    }

    public int Add(in T item)
    {
        int index = _count;
        AppendUninitialized(1)[0] = item;
        return index;
    }

    /// <summary>Drops elements from the end so that <paramref name="count"/> remain.</summary>
    public void Truncate(int count)
    {
        if ((uint)count > (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _count = count;
    }

    /// <summary>Hands the rented array to the caller, who must return it to the pool.</summary>
    public T[]? Detach(out int count)
    {
        T[]? array = _array;
        count = _count;
        _array = null;
        _count = 0;
        return array;
    }

    public void Return()
    {
        if (_array is not null)
        {
            ArrayPool<T>.Shared.Return(_array);
            _array = null;
        }

        _count = 0;
    }

    private void Grow(int required)
    {
        int capacity = _array is null ? 0 : _array.Length;
        int target = Math.Max(required, capacity < 1_073_741_823 ? capacity * 2 : Array.MaxLength);
        T[] grown = ArrayPool<T>.Shared.Rent(Math.Max(target, 16));
        if (_array is not null)
        {
            _array.AsSpan(0, _count).CopyTo(grown);
            ArrayPool<T>.Shared.Return(_array);
        }

        _array = grown;
    }
}
