using System;
using System.Collections;
using System.Collections.Generic;

namespace AnalysisModule.Preprocessor.Caching;

internal sealed class CircularBuffer<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _buffer = new T[capacity];
    }

    public int Capacity { get; }

    public int Count => _count;

    public bool IsFull => _count >= Capacity;

    public void Add(T item)
    {
        _buffer[_head] = item;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var idx = (_head - _count + index + Capacity) % Capacity;
            return _buffer[idx];
        }
    }

    public bool TryPeekLatest(out T value)
    {
        if (_count == 0)
        {
            value = default!;
            return false;
        }

        var idx = (_head - 1 + Capacity) % Capacity;
        value = _buffer[idx];
        return true;
    }

    public IReadOnlyList<T> Snapshot()
    {
        var result = new List<T>(_count);
        foreach (var item in this)
        {
            result.Add(item);
        }

        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
