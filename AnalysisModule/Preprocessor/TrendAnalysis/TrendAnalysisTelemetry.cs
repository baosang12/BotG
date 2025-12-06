using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using AnalysisModule.Telemetry;
using Microsoft.Extensions.Logging;

namespace AnalysisModule.Preprocessor.TrendAnalysis
{
    /// <summary>
    /// Ghi nhận metric cơ bản và điều phối telemetry nâng cao cho MarketTrendAnalyzer.
    /// </summary>
    public sealed class TrendAnalysisTelemetry : IDisposable
    {
        private readonly ILogger<TrendAnalysisTelemetry> _logger;
        private readonly Dictionary<string, object> _metrics = new();
        private readonly object _metricsLock = new();
        private readonly object _patternLock = new();
        private readonly Stopwatch _patternStopwatch = new();
        private IPatternLayerTelemetryLogger? _patternLogger;
        private PatternLayerTelemetryConfig? _patternConfig;
        private bool _patternDebugEnabled;
        private bool _disposed;

        public TrendAnalysisTelemetry(ILogger<TrendAnalysisTelemetry> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void ConfigurePatternTelemetry(TrendAnalyzerConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (_patternLock)
            {
                DisposePatternLogger_NoLock();

                _patternConfig = config.PatternTelemetry;
                if (_patternConfig == null)
                {
                    _patternDebugEnabled = false;
                    PatternLayerDebugger.Initialize(false);
                    return;
                }

                _patternConfig.Normalize();

                if (_patternConfig.EnablePatternLogging)
                {
                    _patternLogger = CreateTelemetryLogger(_patternConfig);
                }

                _patternDebugEnabled = _patternConfig.EnableDebugMode;
                try
                {
                    PatternLayerDebugger.Initialize(
                        _patternConfig.EnableDebugMode,
                        _patternConfig.DebugSampleRate,
                        _patternConfig.DebugMinScoreThreshold,
                        _patternConfig.DebugIncludeDetectorDetails,
                        _patternConfig.DebugIncludeRawMetrics);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể khởi tạo PatternLayerDebugger");
                    _patternDebugEnabled = false;
                }
            }
        }

        private static IPatternLayerTelemetryLogger CreateTelemetryLogger(PatternLayerTelemetryConfig config)
        {
            try
            {
                return new cTraderTelemetryLogger(
                    config.LogDirectory,
                    config.SampleRate,
                    config.MinScoreThreshold,
                    config.MaxScoreThreshold,
                    config.EnableConsoleOutput,
                    config.LogProcessingTime);
            }
            catch
            {
                return new SimplecTraderTelemetryLogger(
                    config.LogDirectory,
                    config.SampleRate,
                    config.EnableConsoleOutput,
                    config.LogProcessingTime);
            }
        }

        public void StartPatternAnalysis(string symbol, string timeframe)
        {
            lock (_patternLock)
            {
                if (_patternConfig?.LogProcessingTime == true)
                {
                    _patternStopwatch.Restart();
                }
                else
                {
                    _patternStopwatch.Reset();
                }

                if (_patternDebugEnabled)
                {
                    PatternLayerDebugger.LogAnalysisStart(
                        SanitizeSymbol(symbol),
                        SanitizeTimeframe(timeframe),
                        DateTime.UtcNow);
                }
            }
        }

        public void LogPatternLayerResults(
            string symbol,
            string timeframe,
            PatternLayerTelemetrySnapshot? snapshot,
            double overallConfidence)
        {
            if (snapshot == null)
            {
                return;
            }

            lock (_patternLock)
            {
                var config = _patternConfig;
                if (config == null)
                {
                    return;
                }

                var sanitizedSymbol = SanitizeSymbol(symbol);
                var sanitizedTimeframe = SanitizeTimeframe(timeframe);
                var elapsedTicks = config.LogProcessingTime ? _patternStopwatch.ElapsedTicks : 0L;
                var elapsedMs = config.LogProcessingTime ? _patternStopwatch.Elapsed.TotalMilliseconds : 0.0;

                try
                {
                    if (_patternDebugEnabled)
                    {
                        foreach (var entry in snapshot.DetectorDiagnostics)
                        {
                            var detectorName = entry.Key;
                            var diagnostics = entry.Value;
                            var scores = ExtractScoreBreakdown(detectorName, diagnostics, snapshot);
                            var metrics = ExtractNumericMetrics(diagnostics);
                            var flags = snapshot.GetDetectorFlagsOrDefault(detectorName);
                            PatternLayerDebugger.LogDetectorAnalysis(
                                detectorName,
                                metrics,
                                scores,
                                flags.Count > 0 ? new List<string>(flags) : null,
                                snapshot.GetDetectorScoreOrDefault(detectorName));
                        }

                        PatternLayerDebugger.LogPatternLayerResult(
                            snapshot.FinalScore,
                            snapshot.GetDetectorScoreOrDefault("Liquidity"),
                            snapshot.GetDetectorScoreOrDefault("BreakoutQuality"),
                            snapshot.PatternFlags?.ToList(),
                            elapsedTicks,
                            overallConfidence);
                    }

                    if (_patternLogger != null)
                    {
                        var accumulationScore = snapshot.AccumulationDistributionScore
                            ?? snapshot.GetDetectorScoreOrDefault("AccumulationDistribution");
                        var accumulationFlags = snapshot.AccumulationDistributionFlags;
                        var accumulationFlagsText = accumulationFlags != null && accumulationFlags.Count > 0
                            ? string.Join('|', accumulationFlags)
                            : string.Empty;
                        var accumulationConfidence = snapshot.AccumulationDistributionConfidence ?? 0;
                        var phase = snapshot.MarketPhase ?? string.Empty;
                        var marketStructureScore = snapshot.MarketStructureScore
                            ?? snapshot.GetDetectorScoreOrDefault("MarketStructure");
                        var marketStructureState = snapshot.MarketStructureState ?? string.Empty;
                        var marketStructureTrend = snapshot.MarketStructureTrendDirection ?? 0;
                        var marketStructureBreak = snapshot.MarketStructureBreakDetected ?? false;
                        var marketStructureSwingPoints = snapshot.MarketStructureSwingPoints ?? 0;
                        var lastSwingHigh = snapshot.LastSwingHigh ?? 0;
                        var lastSwingLow = snapshot.LastSwingLow ?? 0;
                        var volumeProfileScore = snapshot.VolumeProfileScore
                            ?? snapshot.GetDetectorScoreOrDefault("VolumeProfile");
                        var volumeProfilePoc = snapshot.VolumeProfilePOC ?? 0;
                        var volumeProfileVaHigh = snapshot.VolumeProfileVAHigh ?? 0;
                        var volumeProfileVaLow = snapshot.VolumeProfileVALow ?? 0;
                        var volumeProfileFlags = snapshot.VolumeProfileFlags ?? string.Empty;
                        var hvnCount = snapshot.HVNCount ?? 0;
                        var lvnCount = snapshot.LVNCount ?? 0;
                        var volumeConcentration = snapshot.VolumeConcentration ?? 0;
                        var telemetryVersion = snapshot.TelemetryVersion;
                        _patternLogger.LogPatternAnalysis(
                            sanitizedSymbol,
                            sanitizedTimeframe,
                            snapshot.FinalScore,
                            snapshot.GetDetectorScoreOrDefault("Liquidity"),
                            snapshot.GetDetectorScoreOrDefault("BreakoutQuality"),
                            ContainsFlag(snapshot.PatternFlags, "LiquidityGrab"),
                            ContainsFlag(snapshot.PatternFlags, "CleanBreakout"),
                            ContainsFlag(snapshot.PatternFlags, "FailedBreakout"),
                            elapsedMs,
                            accumulationScore: accumulationScore,
                            accumulationConfidence: accumulationConfidence,
                            accumulationFlags: accumulationFlagsText,
                            phaseDetected: phase,
                            marketStructureScore: marketStructureScore,
                            marketStructureState: marketStructureState,
                            marketStructureTrendDirection: marketStructureTrend,
                            marketStructureBreakDetected: marketStructureBreak,
                            marketStructureSwingPoints: marketStructureSwingPoints,
                                lastSwingHigh: lastSwingHigh,
                                lastSwingLow: lastSwingLow,
                                volumeProfileScore: volumeProfileScore,
                                volumeProfilePoc: volumeProfilePoc,
                                volumeProfileVaHigh: volumeProfileVaHigh,
                                volumeProfileVaLow: volumeProfileVaLow,
                                volumeProfileFlags: volumeProfileFlags,
                                hvnCount: hvnCount,
                                lvnCount: lvnCount,
                                volumeConcentration: volumeConcentration,
                                telemetryVersion: telemetryVersion);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PatternLayer telemetry ghi log thất bại");
                }
            }
        }

        public void LogPatternWarning(string detector, string message)
        {
            if (!_patternDebugEnabled)
            {
                return;
            }

            PatternLayerDebugger.LogWarning(detector, message);
        }

        public void LogPatternError(string detector, string message, Exception? ex = null)
        {
            if (_patternDebugEnabled)
            {
                PatternLayerDebugger.LogError(detector, message, ex);
            }

            if (ex != null)
            {
                _logger.LogWarning(ex, "PatternLayer lỗi: {Message}", message);
            }
            else
            {
                _logger.LogWarning("PatternLayer cảnh báo: {Message}", message);
            }
        }

        public (bool LoggerActive, bool DebugEnabled) GetTelemetryStatus()
        {
            lock (_patternLock)
            {
                return (_patternLogger != null, _patternDebugEnabled);
            }
        }

        public object GetTelemetryStatistics()
        {
            lock (_patternLock)
            {
                if (_patternLogger == null)
                {
                    return new { Message = "PatternLayer telemetry not active" };
                }

                var stats = _patternLogger.GetStatistics();
                return new
                {
                    LoggerType = _patternLogger.GetType().Name,
                    stats.TotalEntries,
                    stats.FilteredEntries,
                    stats.QueueLength
                };
            }
        }

        public void RecordAnalysis(TrendSignal? signal, TimeSpan duration, IReadOnlyDictionary<string, double> layerScores)
        {
            lock (_metricsLock)
            {
                _metrics["LastAnalysisUtc"] = DateTime.UtcNow;
                _metrics["DurationMs"] = duration.TotalMilliseconds;
                _metrics["TrendScore"] = signal?.Score ?? 0;
                _metrics["TrendConfidence"] = signal?.Confidence ?? 0;

                if (layerScores != null)
                {
                    foreach (var (key, value) in layerScores)
                    {
                        _metrics[$"Layer_{key}"] = value;
                    }
                }
            }

            _logger.LogDebug(
                "Trend analysis hoàn tất: Direction={Direction}, Score={Score:F2}, Confidence={Confidence:F2}, Duration={Duration}ms",
                signal?.Direction,
                signal?.Score ?? 0,
                signal?.Confidence ?? 0,
                duration.TotalMilliseconds);
        }

        public void RecordError(Exception ex, string context)
        {
            _logger.LogError(ex, "TrendAnalyzer lỗi tại {Context}", context);
            lock (_metricsLock)
            {
                _metrics["LastError"] = ex.Message;
                _metrics["LastErrorUtc"] = DateTime.UtcNow;
                _metrics["ErrorCount"] = _metrics.TryGetValue("ErrorCount", out var current) && current is int i ? i + 1 : 1;
            }
        }

        public IReadOnlyDictionary<string, object> Snapshot()
        {
            lock (_metricsLock)
            {
                return new Dictionary<string, object>(_metrics);
            }
        }

        public void Reset()
        {
            lock (_metricsLock)
            {
                _metrics.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_patternLock)
            {
                DisposePatternLogger_NoLock();
                PatternLayerDebugger.Initialize(false);
            }

            _disposed = true;
        }

        private void DisposePatternLogger_NoLock()
        {
            if (_patternLogger != null)
            {
                try
                {
                    _patternLogger.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể dispose PatternLayer logger");
                }
                finally
                {
                    _patternLogger = null;
                }
            }
        }

        private static bool ContainsFlag(IReadOnlyList<string>? flags, string flag)
        {
            if (flags == null || flags.Count == 0)
            {
                return false;
            }

            foreach (var candidate in flags)
            {
                if (candidate.Equals(flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string SanitizeSymbol(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value;
        }

        private static string SanitizeTimeframe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "COMPOSITE" : value;
        }

        private static Dictionary<string, double>? ExtractScoreBreakdown(
            string detectorName,
            IReadOnlyDictionary<string, object> diagnostics,
            PatternLayerTelemetrySnapshot snapshot)
        {
            if (diagnostics == null)
            {
                return null;
            }

            if (diagnostics.TryGetValue("ScoreBreakdown", out var breakdownObj))
            {
                if (TryCoerceDictionary(breakdownObj, out var breakdown))
                {
                    return breakdown;
                }
            }

            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Score"] = snapshot.GetDetectorScoreOrDefault(detectorName)
            };
        }

        private static Dictionary<string, double>? ExtractNumericMetrics(IReadOnlyDictionary<string, object> diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0)
            {
                return null;
            }

            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in diagnostics)
            {
                if (pair.Key.Equals("ScoreBreakdown", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryConvertToDouble(pair.Value, out var value))
                {
                    result[pair.Key] = value;
                }
            }

            return result.Count > 0 ? result : null;
        }

        private static bool TryConvertToDouble(object? value, out double result)
        {
            switch (value)
            {
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = l;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        private static bool TryCoerceDictionary(object? source, out Dictionary<string, double> result)
        {
            result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (source is IReadOnlyDictionary<string, double> readonlyDict)
            {
                foreach (var pair in readonlyDict)
                {
                    result[pair.Key] = pair.Value;
                }

                return result.Count > 0;
            }

            if (source is IDictionary<string, double> dict)
            {
                foreach (var pair in dict)
                {
                    result[pair.Key] = pair.Value;
                }

                return result.Count > 0;
            }

            if (source is IDictionary<string, object> genericDict)
            {
                foreach (var pair in genericDict)
                {
                    if (TryConvertToDouble(pair.Value, out var value))
                    {
                        result[pair.Key] = value;
                    }
                }

                return result.Count > 0;
            }

            return false;
        }
    }
}
