using System;
using System.Collections.Generic;

namespace BotG.Performance.Monitoring
{
    /// <summary>
    /// Maintains rolling performance statistics derived from tick, benchmark, and trade events.
    /// </summary>
    public sealed class PerformanceMetrics
    {
        private const double TickEwmaAlpha = 0.2;

        private readonly double _tickBaselineHz;
        private readonly double _latencyTargetMs;
        private readonly SlidingWindow _latencyWindow = new(64);
        private readonly SlidingWindow _spreadWindow = new(64);
        private readonly Dictionary<string, WarmupProgress> _warmup = new(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastTickUtc = DateTime.MinValue;
        private double _tickRateHz;
        private double _latestLatencyMs;
        private double _maxLatencyMs;
        private double _latestSpreadPips;
        private bool _isAligned;
        private bool _antiRepaintSafe;
        private long _fillCount;
        private long _rejectCount;

        public PerformanceMetrics(double tickBaselineHz, double latencyTargetMs)
        {
            _tickBaselineHz = tickBaselineHz <= 0 ? 1.0 : tickBaselineHz;
            _latencyTargetMs = latencyTargetMs <= 0 ? 50.0 : latencyTargetMs;
            _tickRateHz = _tickBaselineHz;
        }

        public void TrackTick(DateTime timestampUtc, double spreadPips, bool isAligned, bool antiRepaintSafe)
        {
            if (_lastTickUtc != DateTime.MinValue)
            {
                var deltaSeconds = (timestampUtc - _lastTickUtc).TotalSeconds;
                if (deltaSeconds > 0)
                {
                    var hz = 1.0 / deltaSeconds;
                    _tickRateHz = Blend(_tickRateHz, hz);
                }
            }
            else
            {
                _tickRateHz = _tickBaselineHz;
            }

            _lastTickUtc = timestampUtc;
            _latestSpreadPips = spreadPips;
            _spreadWindow.Push(spreadPips);
            _isAligned = isAligned;
            _antiRepaintSafe = antiRepaintSafe;
        }

        public void TrackLatency(double latencyMs)
        {
            if (latencyMs < 0)
            {
                return;
            }

            _latestLatencyMs = latencyMs;
            _latencyWindow.Push(latencyMs);
            _maxLatencyMs = Math.Max(_maxLatencyMs, latencyMs);
        }

        public void TrackTradeFill()
        {
            _fillCount++;
        }

        public void TrackTradeReject()
        {
            _rejectCount++;
        }

        public void TrackWarmup(string timeframe, int completedBars, int requiredBars)
        {
            if (string.IsNullOrWhiteSpace(timeframe))
            {
                return;
            }

            var requirement = requiredBars < 0 ? 0 : requiredBars;
            var completed = completedBars < 0 ? 0 : completedBars;
            _warmup[timeframe] = new WarmupProgress(timeframe, completed, requirement);
        }

        public PerformanceMetricsSnapshot GetSnapshot(DateTime timestampUtc)
        {
            var totalEvents = _fillCount + _rejectCount;
            var rejectRate = totalEvents == 0 ? 0.0 : (double)_rejectCount / totalEvents;

            return new PerformanceMetricsSnapshot(
                timestampUtc,
                _tickRateHz,
                _tickBaselineHz,
                _latestSpreadPips,
                _spreadWindow.Average,
                _latestLatencyMs,
                _latencyWindow.Average,
                _maxLatencyMs,
                _latencyTargetMs,
                rejectRate,
                _fillCount,
                _rejectCount,
                _isAligned,
                _antiRepaintSafe,
                new Dictionary<string, WarmupProgress>(_warmup, StringComparer.OrdinalIgnoreCase));
        }

        private static double Blend(double current, double incoming)
        {
            return (TickEwmaAlpha * incoming) + ((1 - TickEwmaAlpha) * current);
        }

        private sealed class SlidingWindow
        {
            private readonly int _capacity;
            private readonly Queue<double> _samples;
            private double _sum;

            public SlidingWindow(int capacity)
            {
                _capacity = Math.Max(1, capacity);
                _samples = new Queue<double>(_capacity);
            }

            public void Push(double value)
            {
                if (_samples.Count == _capacity)
                {
                    _sum -= _samples.Dequeue();
                }

                _samples.Enqueue(value);
                _sum += value;
            }

            public double Average => _samples.Count == 0 ? 0.0 : _sum / _samples.Count;
        }
    }

    public sealed class PerformanceMetricsSnapshot
    {
        public PerformanceMetricsSnapshot(
            DateTime timestampUtc,
            double tickRateHz,
            double tickBaselineHz,
            double lastSpreadPips,
            double avgSpreadPips,
            double lastLatencyMs,
            double avgLatencyMs,
            double maxLatencyMs,
            double latencyTargetMs,
            double rejectRate,
            long fills,
            long rejects,
            bool isAligned,
            bool antiRepaintSafe,
            IReadOnlyDictionary<string, WarmupProgress> warmup)
        {
            TimestampUtc = timestampUtc;
            TickRateHz = tickRateHz;
            TickBaselineHz = tickBaselineHz;
            LastSpreadPips = lastSpreadPips;
            AverageSpreadPips = avgSpreadPips;
            LastLatencyMs = lastLatencyMs;
            AverageLatencyMs = avgLatencyMs;
            MaxLatencyMs = maxLatencyMs;
            LatencyTargetMs = latencyTargetMs;
            RejectRate = rejectRate;
            FillCount = fills;
            RejectCount = rejects;
            IsAligned = isAligned;
            AntiRepaintSafe = antiRepaintSafe;
            Warmup = warmup;
        }

        public DateTime TimestampUtc { get; }
        public double TickRateHz { get; }
        public double TickBaselineHz { get; }
        public double LastSpreadPips { get; }
        public double AverageSpreadPips { get; }
        public double LastLatencyMs { get; }
        public double AverageLatencyMs { get; }
        public double MaxLatencyMs { get; }
        public double LatencyTargetMs { get; }
        public double RejectRate { get; }
        public long FillCount { get; }
        public long RejectCount { get; }
        public bool IsAligned { get; }
        public bool AntiRepaintSafe { get; }
        public IReadOnlyDictionary<string, WarmupProgress> Warmup { get; }
    }

    public sealed class WarmupProgress
    {
        public WarmupProgress(string timeframe, int completedBars, int requiredBars)
        {
            Timeframe = timeframe;
            CompletedBars = completedBars;
            RequiredBars = requiredBars;
        }

        public string Timeframe { get; }
        public int CompletedBars { get; }
        public int RequiredBars { get; }

        public double CompletionRatio
        {
            get
            {
                if (RequiredBars <= 0)
                {
                    return CompletedBars > 0 ? 1.0 : 0.0;
                }

                return Math.Min(1.0, (double)CompletedBars / RequiredBars);
            }
        }
    }
}
