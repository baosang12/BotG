using System;
using System.Collections.Generic;

namespace BotG.Performance.Monitoring
{
    public enum PerformanceAlertSeverity
    {
        Info,
        Warning,
        Critical
    }

    public sealed class PerformanceAlert
    {
        public PerformanceAlert(
            DateTime timestampUtc,
            string metric,
            PerformanceAlertSeverity severity,
            string message,
            string? recommendation = null)
        {
            TimestampUtc = timestampUtc;
            Metric = metric;
            Severity = severity;
            Message = message;
            Recommendation = recommendation;
        }

        public DateTime TimestampUtc { get; }
        public string Metric { get; }
        public PerformanceAlertSeverity Severity { get; }
        public string Message { get; }
        public string? Recommendation { get; }
    }

    public sealed class PerformanceAlertEvaluator
    {
        private readonly BacktestMonitorConfig _config;
        private readonly Dictionary<string, DateTime> _suppression = new(StringComparer.OrdinalIgnoreCase);

        public PerformanceAlertEvaluator(BacktestMonitorConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public IReadOnlyList<PerformanceAlert> Evaluate(PerformanceMetricsSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var alerts = new List<PerformanceAlert>();
            EvaluateTickRate(snapshot, alerts);
            EvaluateLatency(snapshot, alerts);
            EvaluateSpread(snapshot, alerts);
            EvaluateRejectRate(snapshot, alerts);
            EvaluateWarmup(snapshot, alerts);
            return alerts;
        }

        private void EvaluateTickRate(PerformanceMetricsSnapshot snapshot, List<PerformanceAlert> alerts)
        {
            if (snapshot.TickRateHz >= _config.TickRateHzFloor())
            {
                return;
            }

            var deficit = _config.TickRateHzFloor() - snapshot.TickRateHz;
            var message = $"Tick rate degraded by {deficit:F2} Hz (current={snapshot.TickRateHz:F2} baseline={snapshot.TickBaselineHz:F2})";
            MaybeAddAlert(alerts, snapshot.TimestampUtc, "tickRate", PerformanceAlertSeverity.Warning, message, "Verify cTrader backtest speed or reduce visual rendering");
        }

        private void EvaluateLatency(PerformanceMetricsSnapshot snapshot, List<PerformanceAlert> alerts)
        {
            if (snapshot.LastLatencyMs <= _config.LatencyTargetMs * _config.LatencyAlertMultiplier)
            {
                return;
            }

            var message = $"MTF latency {snapshot.LastLatencyMs:F1} ms exceeds target {_config.LatencyTargetMs:F1} ms";
            var recommendation = "Lower timeframe load or inspect TimeframeSynchronizer logs";
            var severity = snapshot.LastLatencyMs > (_config.LatencyTargetMs * 2)
                ? PerformanceAlertSeverity.Critical
                : PerformanceAlertSeverity.Warning;
            MaybeAddAlert(alerts, snapshot.TimestampUtc, "mtfLatency", severity, message, recommendation);
        }

        private void EvaluateSpread(PerformanceMetricsSnapshot snapshot, List<PerformanceAlert> alerts)
        {
            if (snapshot.AverageSpreadPips <= 0)
            {
                return;
            }

            var spreadRatio = snapshot.LastSpreadPips / snapshot.AverageSpreadPips;
            if (spreadRatio < _config.SpreadAlertMultiplier)
            {
                return;
            }

            var message = $"Spread spike detected ({snapshot.LastSpreadPips:F2} pips vs avg {snapshot.AverageSpreadPips:F2} pips)";
            MaybeAddAlert(alerts, snapshot.TimestampUtc, "spread", PerformanceAlertSeverity.Warning, message, "Check liquidity provider or pause backtest");
        }

        private void EvaluateRejectRate(PerformanceMetricsSnapshot snapshot, List<PerformanceAlert> alerts)
        {
            if (snapshot.RejectRate <= _config.RejectRateThreshold)
            {
                return;
            }

            var message = $"Order reject rate {snapshot.RejectRate:P1} exceeds {_config.RejectRateThreshold:P1}";
            MaybeAddAlert(alerts, snapshot.TimestampUtc, "rejectRate", PerformanceAlertSeverity.Critical, message, "Validate trade sizing and bridge connectivity");
        }

        private void EvaluateWarmup(PerformanceMetricsSnapshot snapshot, List<PerformanceAlert> alerts)
        {
            foreach (var kvp in snapshot.Warmup)
            {
                var progress = kvp.Value;
                if (progress.CompletionRatio >= 1.0)
                {
                    continue;
                }

                var message = $"Warmup incomplete for {progress.Timeframe}: {progress.CompletedBars}/{progress.RequiredBars}";
                MaybeAddAlert(alerts, snapshot.TimestampUtc, $"warmup:{progress.Timeframe}", PerformanceAlertSeverity.Info, message, "Allow more history to load or reduce warmup requirements");
            }
        }

        private void MaybeAddAlert(
            List<PerformanceAlert> alerts,
            DateTime timestampUtc,
            string metric,
            PerformanceAlertSeverity severity,
            string message,
            string? recommendation)
        {
            var key = metric + ":" + severity;
            if (_suppression.TryGetValue(key, out var lastEmitted))
            {
                if (timestampUtc - lastEmitted < _config.AlertSuppressionWindow)
                {
                    return;
                }
            }

            _suppression[key] = timestampUtc;
            alerts.Add(new PerformanceAlert(timestampUtc, metric, severity, message, recommendation));
        }
    }
}
