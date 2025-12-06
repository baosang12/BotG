using System;
using System.Collections.Generic;
using System.Linq;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using BotG.MultiTimeframe;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;
using PreprocessorBar = AnalysisModule.Preprocessor.DataModels.Bar;
using PreprocessorTimeFrame = AnalysisModule.Preprocessor.DataModels.TimeFrame;

namespace BotG.Runtime.Preprocessor;

/// <summary>
/// Converts preprocessor bar snapshots into TimeframeManager updates when migrating away from cTrader data series.
/// </summary>
public sealed class PreprocessorTimeframeAdapter : IDisposable
{
    private readonly TimeframeManager _timeframeManager;
    private readonly ModelTimeFrame[] _timeframes;
    private readonly HashSet<ModelTimeFrame> _timeframeSet;
    private readonly Dictionary<ModelTimeFrame, Queue<ModelBar>> _pendingByTimeframe = new();
    private readonly Dictionary<ModelTimeFrame, DateTime> _lastBarOpenTimes = new();
    private readonly Dictionary<ModelTimeFrame, ModelBar> _latestBars = new();
    private readonly object _sync = new();
    private bool _disposed;

    public PreprocessorTimeframeAdapter(TimeframeManager timeframeManager, IEnumerable<ModelTimeFrame> supportedTimeframes)
    {
        _timeframeManager = timeframeManager ?? throw new ArgumentNullException(nameof(timeframeManager));
        if (supportedTimeframes == null)
        {
            throw new ArgumentNullException(nameof(supportedTimeframes));
        }

        _timeframes = supportedTimeframes.Distinct().ToArray();
        if (_timeframes.Length == 0)
        {
            throw new ArgumentException("At least one timeframe must be provided", nameof(supportedTimeframes));
        }

        _timeframeSet = new HashSet<ModelTimeFrame>(_timeframes);
        foreach (var tf in _timeframes)
        {
            _pendingByTimeframe[tf] = new Queue<ModelBar>();
            _lastBarOpenTimes[tf] = DateTime.MinValue;
        }
    }

    public DateTime? LatestSnapshotTimeUtc { get; private set; }

    public void HandleSnapshot(PreprocessorSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            foreach (var pair in snapshot.LatestBars)
            {
                if (!TryMap(pair.Key, out var modelTf) || !_timeframeSet.Contains(modelTf))
                {
                    continue;
                }

                EnqueueIfNew(modelTf, pair.Value);
            }

            LatestSnapshotTimeUtc = snapshot.TimestampUtc;
        }
    }

    public void SeedHistory(IReadOnlyDictionary<PreprocessorTimeFrame, IReadOnlyList<PreprocessorBar>> history)
    {
        if (history == null || history.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            foreach (var pair in history)
            {
                if (!TryMap(pair.Key, out var modelTf) || !_timeframeSet.Contains(modelTf))
                {
                    continue;
                }

                var orderedBars = pair.Value?.OrderBy(b => b.OpenTimeUtc) ?? Enumerable.Empty<PreprocessorBar>();
                foreach (var bar in orderedBars)
                {
                    EnqueueIfNew(modelTf, bar);
                }
            }
        }
    }

    public bool FlushPending(string symbol, DateTime serverTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            var ingested = false;
            foreach (var timeframe in _timeframes)
            {
                if (!_pendingByTimeframe.TryGetValue(timeframe, out var queue) || queue.Count == 0)
                {
                    continue;
                }

                while (queue.Count > 0)
                {
                    var bar = queue.Peek();
                    if (_timeframeManager.TryAddBar(symbol, bar, serverTimeUtc, isClosedBar: true))
                    {
                        queue.Dequeue();
                        _lastBarOpenTimes[timeframe] = bar.OpenTime;
                        _latestBars[timeframe] = bar;
                        ingested = true;
                    }
                    else
                    {
                        // TimeframeManager rejected the bar (likely guard); keep it for next tick
                        break;
                    }
                }
            }

            return ingested;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var queue in _pendingByTimeframe.Values)
            {
                queue.Clear();
            }
            _disposed = true;
        }
    }

    public ModelBar? GetLatestBar(ModelTimeFrame timeframe)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (_latestBars.TryGetValue(timeframe, out var ingested))
            {
                return ingested;
            }

            if (_pendingByTimeframe.TryGetValue(timeframe, out var queue) && queue.Count > 0)
            {
                return queue.Peek();
            }

            return null;
        }
    }

    private void EnqueueIfNew(ModelTimeFrame timeframe, PreprocessorBar source)
    {
        var converted = ConvertBar(source, timeframe);
        var lastOpen = _lastBarOpenTimes.TryGetValue(timeframe, out var last)
            ? last
            : DateTime.MinValue;

        if (converted.OpenTime <= lastOpen)
        {
            return;
        }

        if (!_pendingByTimeframe.TryGetValue(timeframe, out var queue))
        {
            queue = new Queue<ModelBar>();
            _pendingByTimeframe[timeframe] = queue;
        }

        queue.Enqueue(converted);
    }

    private static ModelBar ConvertBar(PreprocessorBar source, ModelTimeFrame timeframe)
    {
        return new ModelBar
        {
            OpenTime = DateTime.SpecifyKind(source.OpenTimeUtc, DateTimeKind.Utc),
            Open = source.Open,
            High = source.High,
            Low = source.Low,
            Close = source.Close,
            Volume = source.Volume,
            Tf = timeframe
        };
    }

    private static bool TryMap(PreprocessorTimeFrame source, out ModelTimeFrame target)
    {
        var name = Enum.GetName(typeof(PreprocessorTimeFrame), source);
        if (name != null && Enum.TryParse(name, ignoreCase: true, out ModelTimeFrame parsed))
        {
            target = parsed;
            return true;
        }

        target = default;
        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PreprocessorTimeframeAdapter));
        }
    }
}
