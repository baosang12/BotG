using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.TrendAnalysis;
using Microsoft.Extensions.Logging;
using BotG.Runtime.Logging;

namespace BotG.Runtime.TrendAnalysis
{
    /// <summary>
    /// Điều phối MarketTrendAnalyzer với PreprocessorSnapshot để phát TrendSignal và PatternLayer telemetry.
    /// </summary>
    public sealed class TrendAnalysisService : IDisposable
    {
        private const string Module = "TREND";
        private static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan DefaultMaxSnapshotAge = TimeSpan.FromSeconds(5);

        private readonly ITrendAnalysisBridge _bridge;
        private readonly Action<string>? _print;
        private readonly string _configPath;
        private readonly TimeSpan _minAnalysisInterval;
        private readonly TimeSpan _maxSnapshotAge;
        private readonly bool _enableDebugLogs;
        private readonly object _lifecycleLock = new object();
        private readonly object _analysisLock = new object();
        private readonly ILogger _configLogger;
        private readonly PipelineTrendLogger<TrendAnalysisTelemetry> _telemetryLogger;
        private readonly PipelineTrendLogger<MarketTrendAnalyzer> _analyzerLogger;

        private TrendAnalysisTelemetry? _telemetry;
        private MarketTrendAnalyzer? _analyzer;
        private TrendAnalyzerConfig? _currentConfig;
        private DateTime _lastAnalysisUtc = DateTime.MinValue;
        private TrendDirection? _lastLoggedDirection;
        private DateTime _lastSignalLogUtc = DateTime.MinValue;
        private long _throttleSkipCount;
        private long _staleSkipCount;
        private bool _started;
        private bool _disposed;

        public TrendAnalysisService(
            ITrendAnalysisBridge bridge,
            string? configPath = null,
            Action<string>? print = null,
            TimeSpan? minAnalysisInterval = null,
            TimeSpan? maxSnapshotAge = null)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _print = print;
            _configPath = ResolveConfigPath(configPath);
            _enableDebugLogs = IsDebugEnabled();
            _telemetryLogger = new PipelineTrendLogger<TrendAnalysisTelemetry>(Module, print, _enableDebugLogs);
            _analyzerLogger = new PipelineTrendLogger<MarketTrendAnalyzer>(Module, print, _enableDebugLogs);
            _configLogger = new PipelineTrendLogger(nameof(TrendAnalyzerConfigLoader), Module, print, _enableDebugLogs);
            _minAnalysisInterval = NormalizeInterval(minAnalysisInterval ?? ReadDurationFromEnv("BOTG_TREND_MIN_INTERVAL_MS", DefaultMinInterval));
            _maxSnapshotAge = NormalizeSnapshotAge(maxSnapshotAge ?? ReadDurationFromEnv("BOTG_TREND_MAX_SNAPSHOT_AGE_MS", DefaultMaxSnapshotAge));
        }

        public bool Start()
        {
            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                if (_started)
                {
                    return true;
                }

                try
                {
                    var config = LoadConfig();
                    _telemetry = new TrendAnalysisTelemetry(_telemetryLogger);
                    _analyzer = new MarketTrendAnalyzer(_analyzerLogger, _bridge, _telemetry);
                    _analyzer.Initialize(config);
                    _currentConfig = config;
                    _started = true;

                    PipelineLogger.Log(
                        Module,
                        "ServiceStart",
                        "TrendAnalysisService đã khởi động",
                        new Dictionary<string, object?>
                        {
                            ["config_path"] = _configPath,
                            ["pattern_log"] = config.PatternTelemetry?.LogDirectory,
                            ["min_interval_ms"] = _minAnalysisInterval.TotalMilliseconds,
                            ["max_snapshot_age_ms"] = _maxSnapshotAge.TotalMilliseconds
                        },
                        _print);

                    return true;
                }
                catch (Exception ex)
                {
                    PipelineLogger.Log(
                        Module,
                        "ServiceStartFailed",
                        "Không thể khởi động TrendAnalysisService",
                        new { error = ex.Message, _configPath },
                        _print);
                    DisposeInternal();
                    return false;
                }
            }
        }

        public bool ReloadConfig()
        {
            lock (_lifecycleLock)
            {
                if (!_started || _analyzer == null)
                {
                    return false;
                }

                try
                {
                    var config = LoadConfig();
                    _analyzer.UpdateConfig(config);
                    _currentConfig = config;
                    PipelineLogger.Log(
                        Module,
                        "ConfigReloaded",
                        "TrendAnalyzer config đã được reload",
                        new
                        {
                            path = _configPath,
                            version = config.Version,
                            enabled = config.Enabled,
                            pattern_logging = config.PatternTelemetry?.EnablePatternLogging
                        },
                        _print);
                    return true;
                }
                catch (Exception ex)
                {
                    PipelineLogger.Log(Module, "ConfigReloadFailed", "Reload TrendAnalyzer config thất bại", new { error = ex.Message }, _print);
                    return false;
                }
            }
        }

        public void ProcessSnapshot(PreprocessorSnapshot? snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            TrendSignal? signal = null;

            lock (_analysisLock)
            {
                if (!_started || _analyzer == null || _telemetry == null)
                {
                    return;
                }

                if (!_bridge.IsTrendAnalysisEnabled)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                if ((now - _lastAnalysisUtc) < _minAnalysisInterval)
                {
                    TrackThrottleSkip(now);
                    return;
                }

                if (_maxSnapshotAge > TimeSpan.Zero && snapshot.TimestampUtc != default && (now - snapshot.TimestampUtc) > _maxSnapshotAge)
                {
                    TrackStaleSkip(now, snapshot);
                    return;
                }

                try
                {
                    _lastAnalysisUtc = now;
                    signal = _analyzer.Analyze(snapshot);
                }
                catch (Exception ex)
                {
                    PipelineLogger.Log(Module, "AnalyzeException", "Trend analyzer xử lý snapshot thất bại", new { error = ex.Message }, _print);
                    _telemetry.RecordError(ex, "ProcessSnapshot");
                }
            }

            if (signal != null)
            {
                MaybeLogSignal(signal);
            }
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                DisposeInternal();
                _disposed = true;
            }
        }

        private void DisposeInternal()
        {
            lock (_analysisLock)
            {
                _analyzer?.Dispose();
                _telemetry?.Dispose();
                _analyzer = null;
                _telemetry = null;
                _started = false;
            }

            PipelineLogger.Log(Module, "ServiceStop", "TrendAnalysisService đã dừng", null, _print);
        }

        private TrendAnalyzerConfig LoadConfig()
        {
            var loader = new TrendAnalyzerConfigLoader(_configPath, _configLogger);
            var config = loader.Load();
            EnsurePatternDirectory(config);
            return config;
        }

        private static void EnsurePatternDirectory(TrendAnalyzerConfig config)
        {
            var directory = config?.PatternTelemetry?.LogDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch
            {
                // logging layer sẽ báo lỗi riêng; không throw để tránh dừng bot.
            }
        }

        private void MaybeLogSignal(TrendSignal signal)
        {
            var now = DateTime.UtcNow;
            var directionChanged = !_lastLoggedDirection.HasValue || _lastLoggedDirection != signal.Direction;
            var timeElapsed = (now - _lastSignalLogUtc) >= TimeSpan.FromMinutes(5);

            if (!directionChanged && !timeElapsed)
            {
                return;
            }

            _lastLoggedDirection = signal.Direction;
            _lastSignalLogUtc = now;

            PipelineLogger.Log(
                Module,
                "SignalUpdated",
                "TrendSignal mới đã được publish",
                new Dictionary<string, object?>
                {
                    ["direction"] = signal.Direction.ToString(),
                    ["strength"] = signal.Strength.ToString(),
                    ["score"] = Math.Round(signal.Score, 2),
                    ["confidence"] = Math.Round(signal.Confidence, 3),
                    ["flags"] = signal.PatternFlags ?? Array.Empty<string>(),
                    ["primary_tf"] = signal.PrimaryTimeFrame.ToString()
                },
                _print);
        }

        private void TrackThrottleSkip(DateTime now)
        {
            var count = Interlocked.Increment(ref _throttleSkipCount);
            if (count == 1 || count % 200 == 0)
            {
                PipelineLogger.Log(
                    Module,
                    "Throttle",
                    "TrendAnalysisService bỏ qua snapshot do throttling",
                    new { interval_ms = _minAnalysisInterval.TotalMilliseconds, total = count },
                    _print);
            }
        }

        private void TrackStaleSkip(DateTime now, PreprocessorSnapshot snapshot)
        {
            var count = Interlocked.Increment(ref _staleSkipCount);
            if (count == 1 || count % 50 == 0)
            {
                PipelineLogger.Log(
                    Module,
                    "SnapshotStale",
                    "Snapshot bị bỏ qua vì quá cũ",
                    new
                    {
                        age_ms = (now - snapshot.TimestampUtc).TotalMilliseconds,
                        limit_ms = _maxSnapshotAge.TotalMilliseconds,
                        total = count
                    },
                    _print);
            }
        }

        private static TimeSpan NormalizeInterval(TimeSpan value)
        {
            if (value <= TimeSpan.Zero)
            {
                return DefaultMinInterval;
            }

            return value;
        }

        private static TimeSpan NormalizeSnapshotAge(TimeSpan value)
        {
            if (value <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return value;
        }

        private static TimeSpan ReadDurationFromEnv(string key, TimeSpan fallback)
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return fallback;
                }

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0)
                {
                    return TimeSpan.FromMilliseconds(ms);
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static bool IsDebugEnabled()
        {
            var raw = Environment.GetEnvironmentVariable("BOTG_TREND_DEBUG");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            return !string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TrendAnalysisService));
            }
        }

        public static string ResolveConfigPath(string? overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }

            var envPath = Environment.GetEnvironmentVariable("BOTG_TREND_CONFIG_PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                return Path.GetFullPath(envPath);
            }

            var baseDir = AppContext.BaseDirectory ?? string.Empty;
            var currentDir = GetSafeCurrentDirectory();
            var candidates = new List<string>
            {
                Path.Combine(baseDir, "TrendAnalyzerConfig.json"),
                Path.Combine(baseDir, "Config", "TrendAnalyzerConfig.json"),
                @"D:\\botg\\config\\TrendAnalyzerConfig.json",
                @"D:\\botg\\TrendAnalyzerConfig.json"
            };

            if (!string.IsNullOrWhiteSpace(currentDir))
            {
                candidates.Add(Path.Combine(currentDir, "TrendAnalyzerConfig.json"));
                candidates.Add(Path.Combine(currentDir, "Config", "TrendAnalyzerConfig.json"));
            }

            foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    // ignored, thử path khác
                }
            }

            return Path.GetFullPath(Path.Combine(baseDir, "TrendAnalyzerConfig.json"));
        }

        private static string GetSafeCurrentDirectory()
        {
            try
            {
                return Environment.CurrentDirectory;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
