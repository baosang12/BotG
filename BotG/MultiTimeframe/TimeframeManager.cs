using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using DataFetcher.Caching;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.MultiTimeframe
{
    public sealed class TimeframeManagerConfig
    {
        public IReadOnlyList<ModelTimeFrame> Timeframes { get; set; } = TimeframeMath.DefaultTimeframes.ToArray();
        public int DefaultBufferSize { get; set; } = 256;
        public bool RequireClosedBars { get; set; } = true;
        public TimeSpan AntiRepaintGuard { get; set; } = TimeSpan.FromSeconds(1);
        public IReadOnlyDictionary<ModelTimeFrame, int> CustomBufferSizes { get; set; } = new Dictionary<ModelTimeFrame, int>();

        public TimeframeManagerConfig Clone()
        {
            return new TimeframeManagerConfig
            {
                Timeframes = (Timeframes ?? Array.Empty<ModelTimeFrame>()).ToArray(),
                DefaultBufferSize = DefaultBufferSize,
                RequireClosedBars = RequireClosedBars,
                AntiRepaintGuard = AntiRepaintGuard,
                CustomBufferSizes = CustomBufferSizes != null
                    ? new Dictionary<ModelTimeFrame, int>(CustomBufferSizes)
                    : new Dictionary<ModelTimeFrame, int>()
            };
        }

        public int ResolveCapacity(ModelTimeFrame timeframe)
        {
            if (CustomBufferSizes != null &&
                CustomBufferSizes.TryGetValue(timeframe, out var capacity) &&
                capacity > 0)
            {
                return capacity;
            }

            return DefaultBufferSize;
        }
    }

    /// <summary>
    /// Maintains synchronized bar buffers across multiple timeframes for each symbol.
    /// </summary>
    public sealed class TimeframeManager
    {
        private readonly Dictionary<string, SymbolSeries> _seriesBySymbol = new(StringComparer.OrdinalIgnoreCase);
        private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
        private TimeframeManagerConfig _config;

        public TimeframeManager(TimeframeManagerConfig? config = null)
        {
            _config = Sanitize(config);
        }

        public void UpdateConfiguration(TimeframeManagerConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var sanitized = Sanitize(config);

            _gate.EnterWriteLock();
            try
            {
                _config = sanitized;
            }
            finally
            {
                _gate.ExitWriteLock();
            }
        }

        public bool TryAddBar(string symbol, ModelBar bar, DateTime? serverTimeUtc = null, bool isClosedBar = true)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                throw new ArgumentException("Symbol is required.", nameof(symbol));
            }

            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            var timestamp = serverTimeUtc ?? DateTime.UtcNow;

            _gate.EnterUpgradeableReadLock();
            try
            {
                if (!_seriesBySymbol.TryGetValue(symbol, out var series))
                {
                    _gate.EnterWriteLock();
                    try
                    {
                        if (!_seriesBySymbol.TryGetValue(symbol, out series))
                        {
                            series = new SymbolSeries(_config);
                            _seriesBySymbol[symbol] = series;
                        }
                    }
                    finally
                    {
                        _gate.ExitWriteLock();
                    }
                }

                return series.TryAddBar(bar, timestamp, isClosedBar, _config);
            }
            finally
            {
                _gate.ExitUpgradeableReadLock();
            }
        }

        public TimeframeSnapshot CaptureSnapshot(string symbol, DateTime timestampUtc)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                throw new ArgumentException("Symbol is required.", nameof(symbol));
            }

            _gate.EnterReadLock();
            try
            {
                if (!_seriesBySymbol.TryGetValue(symbol, out var series))
                {
                    return TimeframeSnapshot.Empty(symbol, timestampUtc, _config.Timeframes);
                }

                var barsByTf = series.BuildSnapshot(timestampUtc, _config);
                return new TimeframeSnapshot(symbol, timestampUtc, _config.Timeframes, barsByTf);
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }

        public TimeframeAlignmentResult EvaluateAlignment(
            string symbol,
            DateTime timestampUtc,
            TimeframeSynchronizer synchronizer)
        {
            if (synchronizer == null)
            {
                throw new ArgumentNullException(nameof(synchronizer));
            }

            var snapshot = CaptureSnapshot(symbol, timestampUtc);
            return synchronizer.GetAlignmentResult(snapshot);
        }

        private static TimeframeManagerConfig Sanitize(TimeframeManagerConfig? config)
        {
            var clone = (config ?? new TimeframeManagerConfig()).Clone();
            if (clone.Timeframes == null || clone.Timeframes.Count == 0)
            {
                clone.Timeframes = TimeframeMath.DefaultTimeframes.ToArray();
            }

            if (clone.DefaultBufferSize <= 0)
            {
                clone.DefaultBufferSize = 128;
            }

            if (clone.AntiRepaintGuard <= TimeSpan.Zero)
            {
                clone.AntiRepaintGuard = TimeSpan.FromSeconds(1);
            }

            return clone;
        }

        private sealed class SymbolSeries
        {
            private readonly Dictionary<ModelTimeFrame, CircularBuffer<ModelBar>> _buffers;
            private readonly Dictionary<ModelTimeFrame, DateTime> _lastClosedOpenTimes = new();
            private readonly object _sync = new();

            public SymbolSeries(TimeframeManagerConfig config)
            {
                _buffers = new Dictionary<ModelTimeFrame, CircularBuffer<ModelBar>>(config.Timeframes.Count);
                foreach (var timeframe in config.Timeframes)
                {
                    _buffers[timeframe] = new CircularBuffer<ModelBar>(config.ResolveCapacity(timeframe));
                }
            }

            public bool TryAddBar(ModelBar bar, DateTime serverTimeUtc, bool isClosedBar, TimeframeManagerConfig config)
            {
                var timeframe = bar.Tf;

                lock (_sync)
                {
                    if (!_buffers.TryGetValue(timeframe, out var buffer))
                    {
                        buffer = new CircularBuffer<ModelBar>(config.ResolveCapacity(timeframe));
                        _buffers[timeframe] = buffer;
                    }

                    if (config.RequireClosedBars)
                    {
                        if (!isClosedBar)
                        {
                            return false;
                        }

                        var closeTime = TimeframeMath.GetCloseTime(bar);
                        if (closeTime > serverTimeUtc - config.AntiRepaintGuard)
                        {
                            return false;
                        }
                    }

                    if (_lastClosedOpenTimes.TryGetValue(timeframe, out var lastOpen) &&
                        bar.OpenTime <= lastOpen)
                    {
                        return false;
                    }

                    buffer.Add(bar);
                    _lastClosedOpenTimes[timeframe] = bar.OpenTime;
                    return true;
                }
            }

            public IReadOnlyDictionary<ModelTimeFrame, IReadOnlyList<ModelBar>> BuildSnapshot(
                DateTime timestampUtc,
                TimeframeManagerConfig config)
            {
                var result = new Dictionary<ModelTimeFrame, IReadOnlyList<ModelBar>>(config.Timeframes.Count);

                lock (_sync)
                {
                    foreach (var timeframe in config.Timeframes)
                    {
                        if (!_buffers.TryGetValue(timeframe, out var buffer) || buffer.IsEmpty)
                        {
                            result[timeframe] = Array.Empty<ModelBar>();
                            continue;
                        }

                        var filtered = FilterClosed(buffer, timeframe, timestampUtc, config.AntiRepaintGuard);
                        result[timeframe] = filtered.Count == 0
                            ? Array.Empty<ModelBar>()
                            : new ReadOnlyCollection<ModelBar>(filtered);
                    }
                }

                return new ReadOnlyDictionary<ModelTimeFrame, IReadOnlyList<ModelBar>>(result);
            }

            private static List<ModelBar> FilterClosed(
                CircularBuffer<ModelBar> buffer,
                ModelTimeFrame timeframe,
                DateTime referenceTimeUtc,
                TimeSpan guard)
            {
                var closedBars = new List<ModelBar>(buffer.Count);
                var duration = TimeframeMath.GetDuration(timeframe);
                var cutoff = referenceTimeUtc - guard;

                foreach (var bar in buffer)
                {
                    var closeTime = bar.OpenTime + duration;
                    if (closeTime <= cutoff)
                    {
                        closedBars.Add(bar);
                    }
                }

                return closedBars;
            }
        }
    }
}
