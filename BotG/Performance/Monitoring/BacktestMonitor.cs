using System;
using System.Collections.Generic;
using System.Linq;

namespace BotG.Performance.Monitoring
{
    public sealed class BacktestMonitor : IDisposable
    {
        private readonly string _symbol;
        private readonly double _pipSize;
        private readonly BacktestMonitorConfig _config;
        private readonly PerformanceMetrics _metrics;
        private readonly PerformanceAlertEvaluator _alertEvaluator;
        private readonly PerformanceReportWriter _reportWriter;
        private readonly object _gate = new object();
        private readonly Action<string, string, string, object?>? _log;

        private DateTime _nextFlushUtc = DateTime.MinValue;
        private PerformanceMetricsSnapshot? _latestSnapshot;
        private IReadOnlyList<PerformanceAlert> _latestAlerts = Array.Empty<PerformanceAlert>();

        public BacktestMonitor(
            string symbol,
            double pipSize,
            BacktestMonitorConfig config,
            Action<string, string, string, object?>? logHandler = null)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _pipSize = pipSize <= 0 ? 0.00001 : pipSize;
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _metrics = new PerformanceMetrics(_config.TickBaselineHz, _config.LatencyTargetMs);
            _alertEvaluator = new PerformanceAlertEvaluator(_config);
            _reportWriter = new PerformanceReportWriter(_symbol, _config.OutputDirectory);
            _log = logHandler;
        }

        public PerformanceMetricsSnapshot? LatestSnapshot
        {
            get
            {
                lock (_gate)
                {
                    return _latestSnapshot;
                }
            }
        }

        public IReadOnlyList<PerformanceAlert> LatestAlerts
        {
            get
            {
                lock (_gate)
                {
                    return _latestAlerts;
                }
            }
        }

        public void OnTick(DateTime timestampUtc, double bid, double ask, double? latencyMs, bool isAligned, bool antiRepaintSafe)
        {
            var spread = Math.Max(0.0, ask - bid);
            var spreadPips = _pipSize > 0 ? spread / _pipSize : spread;

            lock (_gate)
            {
                _metrics.TrackTick(timestampUtc, spreadPips, isAligned, antiRepaintSafe);
                if (latencyMs.HasValue)
                {
                    _metrics.TrackLatency(latencyMs.Value);
                }

                MaybeFlush(timestampUtc);
            }
        }

        public void OnTradeFill()
        {
            lock (_gate)
            {
                _metrics.TrackTradeFill();
            }
        }

        public void OnTradeReject()
        {
            lock (_gate)
            {
                _metrics.TrackTradeReject();
            }
        }

        public void TrackWarmup(string timeframe, int completedBars, int requiredBars)
        {
            lock (_gate)
            {
                _metrics.TrackWarmup(timeframe, completedBars, requiredBars);
            }
        }

        private void MaybeFlush(DateTime timestampUtc)
        {
            if (_nextFlushUtc != DateTime.MinValue && timestampUtc < _nextFlushUtc)
            {
                return;
            }

            var snapshot = _metrics.GetSnapshot(timestampUtc);
            var alerts = _alertEvaluator.Evaluate(snapshot);

            _reportWriter.Write(snapshot, alerts);
            _latestSnapshot = snapshot;
            _latestAlerts = alerts;

            if (_config.EchoReports)
            {
                var summary = new
                {
                    tickHz = snapshot.TickRateHz,
                    spread = snapshot.LastSpreadPips,
                    latency = snapshot.LastLatencyMs,
                    alerts = alerts.Select(a => new { a.Metric, severity = a.Severity.ToString(), a.Message })
                };
                _log?.Invoke("MONITOR", "Report", "Backtest monitor snapshot", summary);
            }
            else if (alerts.Count > 0)
            {
                foreach (var alert in alerts)
                {
                    _log?.Invoke(
                        "MONITOR",
                        "Alert",
                        alert.Message,
                        new { alert.Metric, severity = alert.Severity.ToString(), alert.Recommendation });
                }
            }

            _nextFlushUtc = timestampUtc + _config.ReportInterval;
        }

        public void Dispose()
        {
            // Nothing disposable yet; placeholder for future resources.
        }
    }

    public sealed class BacktestMonitorConfig
    {
        public double TickBaselineHz { get; set; } = 5.0;
        public double TickRateFloorRatio { get; set; } = 0.5;
        public double LatencyTargetMs { get; set; } = 50.0;
        public double LatencyAlertMultiplier { get; set; } = 1.5;
        public double SpreadAlertMultiplier { get; set; } = 5.0;
        public double RejectRateThreshold { get; set; } = 0.1;
        public TimeSpan AlertSuppressionWindow { get; set; } = TimeSpan.FromMinutes(2);
        public TimeSpan ReportInterval { get; set; } = TimeSpan.FromSeconds(30);
        public string OutputDirectory { get; set; } = "D:\\botg\\logs\\monitoring";
        public bool EchoReports { get; set; } = true;

        public double TickRateHzFloor()
        {
            if (TickBaselineHz <= 0)
            {
                return 0.5;
            }

            var ratio = TickRateFloorRatio <= 0 || TickRateFloorRatio > 1 ? 0.5 : TickRateFloorRatio;
            return TickBaselineHz * ratio;
        }

        public static BacktestMonitorConfig FromEnvironment(string? logRoot)
        {
            var cfg = new BacktestMonitorConfig();
            cfg.TickBaselineHz = ReadDouble("BOTG_MONITOR_TICK_HZ_BASELINE", cfg.TickBaselineHz);
            cfg.TickRateFloorRatio = ReadDouble("BOTG_MONITOR_TICK_FLOOR_RATIO", cfg.TickRateFloorRatio);
            cfg.LatencyTargetMs = ReadDouble("BOTG_MONITOR_LATENCY_TARGET_MS", cfg.LatencyTargetMs);
            cfg.LatencyAlertMultiplier = ReadDouble("BOTG_MONITOR_LATENCY_MULTIPLIER", cfg.LatencyAlertMultiplier);
            cfg.SpreadAlertMultiplier = ReadDouble("BOTG_MONITOR_SPREAD_MULTIPLIER", cfg.SpreadAlertMultiplier);
            cfg.RejectRateThreshold = ReadDouble("BOTG_MONITOR_REJECT_RATE", cfg.RejectRateThreshold);
            cfg.ReportInterval = TimeSpan.FromSeconds(ReadDouble("BOTG_MONITOR_ALERT_SAMPLING_SEC", cfg.ReportInterval.TotalSeconds));
            cfg.AlertSuppressionWindow = TimeSpan.FromSeconds(ReadDouble("BOTG_MONITOR_ALERT_SUPPRESS_SEC", cfg.AlertSuppressionWindow.TotalSeconds));
            cfg.OutputDirectory = GetPath("BOTG_MONITOR_OUTPUT_DIR", logRoot, cfg.OutputDirectory);
            cfg.EchoReports = ReadBool("BOTG_MONITOR_ECHO", cfg.EchoReports);
            return cfg;
        }

        private static double ReadDouble(string key, double fallback)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            return double.TryParse(raw, out var value) && value > 0 ? value : fallback;
        }

        private static bool ReadBool(string key, bool fallback)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            return raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPath(string key, string? logRoot, string fallback)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }

            if (!string.IsNullOrWhiteSpace(logRoot))
            {
                return System.IO.Path.Combine(logRoot, "monitoring");
            }

            return fallback;
        }
    }
}
