using System;
using BotG.MultiTimeframe;

namespace BotG.Performance
{
    public sealed class MultiTimeframeBenchmark
    {
        private readonly string _symbol;
        private readonly double _targetLatencyMs;
        private readonly int _logInterval;
        private long _samples;
        private double _maxLatencyMs;
        private double _sumLatencyMs;
        private long _overBudgetSamples;

        public MultiTimeframeBenchmark(string symbol, double targetLatencyMs = 50.0, int logInterval = 600)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                throw new ArgumentException("Symbol is required.", nameof(symbol));
            }

            if (targetLatencyMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetLatencyMs), "Target latency must be positive.");
            }

            if (logInterval <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(logInterval), "Log interval must be positive.");
            }

            _symbol = symbol;
            _targetLatencyMs = targetLatencyMs;
            _logInterval = logInterval;
        }

        public BenchmarkReport Record(TimeSpan latency, TimeframeAlignmentResult alignment, DateTime tickTimeUtc)
        {
            var latencyMs = latency.TotalMilliseconds;
            if (latencyMs < 0)
            {
                latencyMs = 0;
            }

            _samples++;
            _sumLatencyMs += latencyMs;

            if (latencyMs > _maxLatencyMs)
            {
                _maxLatencyMs = latencyMs;
            }

            if (latencyMs > _targetLatencyMs)
            {
                _overBudgetSamples++;
            }

            var averageLatency = _samples > 0 ? _sumLatencyMs / _samples : 0.0;
            var shouldLog = _samples == 1 || _samples % _logInterval == 0;
            var shouldAlert = latencyMs > _targetLatencyMs * 1.5;

            return new BenchmarkReport(
                _symbol,
                tickTimeUtc,
                latencyMs,
                _maxLatencyMs,
                averageLatency,
                _targetLatencyMs,
                _samples,
                _overBudgetSamples,
                alignment.IsAligned,
                alignment.AntiRepaintSafe,
                shouldLog,
                shouldAlert);
        }
    }

    public sealed record BenchmarkReport(
        string Symbol,
        DateTime TimestampUtc,
        double LastLatencyMs,
        double MaxLatencyMs,
        double AverageLatencyMs,
        double TargetLatencyMs,
        long Samples,
        long OverBudgetSamples,
        bool LastAligned,
        bool LastAntiRepaintSafe,
        bool ShouldLog,
        bool ShouldAlert)
    {
        public double OverBudgetRatio => Samples > 0 ? OverBudgetSamples / (double)Samples : 0.0;
    }
}
