using System.Collections.Generic;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.Caching;

public sealed class TickCache
{
    private readonly CircularBuffer<Tick> _buffer;

    public TickCache(int capacity)
    {
        _buffer = new CircularBuffer<Tick>(capacity);
    }

    public void Add(Tick tick)
    {
        _buffer.Add(tick);
    }

    public IReadOnlyList<Tick> Snapshot() => _buffer.Snapshot();

    public Tick? GetLastTick()
    {
        return _buffer.TryPeekLatest(out var tick) ? tick : null;
    }
}
