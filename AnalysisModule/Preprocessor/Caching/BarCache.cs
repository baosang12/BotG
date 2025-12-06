using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.Caching;

public sealed class BarCache
{
    private readonly Dictionary<TimeFrame, CircularBuffer<Bar>> _cache = new();
    private readonly int _bufferSize;

    public BarCache(int bufferSize)
    {
        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        _bufferSize = bufferSize;
    }

    public void AddBar(TimeFrame timeframe, Bar bar)
    {
        if (!_cache.TryGetValue(timeframe, out var buffer))
        {
            buffer = new CircularBuffer<Bar>(_bufferSize);
            _cache[timeframe] = buffer;
        }

        buffer.Add(bar);
    }

    public IReadOnlyList<Bar> GetBars(TimeFrame timeframe)
    {
        return _cache.TryGetValue(timeframe, out var buffer)
            ? buffer.Snapshot()
            : Array.Empty<Bar>();
    }

    public IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> SnapshotAll()
    {
        var result = new Dictionary<TimeFrame, IReadOnlyList<Bar>>();
        foreach (var (key, buffer) in _cache)
        {
            result[key] = buffer.Snapshot();
        }

        return result;
    }

    public Bar? GetLastBar(TimeFrame timeframe)
    {
        if (_cache.TryGetValue(timeframe, out var buffer) && buffer.TryPeekLatest(out var bar))
        {
            return bar;
        }

        return null;
    }
}
