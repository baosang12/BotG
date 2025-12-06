using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using Microsoft.Extensions.Logging;

namespace AnalysisModule.Preprocessor.TrendAnalysis
{
    /// <summary>
    /// Skeleton MarketTrendAnalyzer: gom điểm từ nhiều layer và publish TrendSignal qua bridge.
    /// </summary>
    public sealed class MarketTrendAnalyzer : IMarketTrendAnalyzer
    {
        private readonly ILogger<MarketTrendAnalyzer> _logger;
        private readonly ITrendAnalysisBridge _bridge;
        private readonly TrendAnalysisTelemetry _telemetry;
        private readonly Dictionary<string, ILayerCalculator> _layers = new(StringComparer.OrdinalIgnoreCase);
        private PatternLayer? _patternLayer;
        private readonly object _configLock = new();
        private readonly Stopwatch _analysisStopwatch = new();
        private TrendAnalyzerConfig _config = new();
        private bool _initialized;

        public MarketTrendAnalyzer(
            ILogger<MarketTrendAnalyzer> logger,
            ITrendAnalysisBridge bridge,
            TrendAnalysisTelemetry telemetry)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        public bool IsEnabled
        {
            get
            {
                lock (_configLock)
                {
                    return _initialized && (_config?.Enabled ?? false);
                }
            }
        }

        public TimeSpan LastAnalysisDuration { get; private set; } = TimeSpan.Zero;

        public event EventHandler<TrendAnalysisCompletedEventArgs>? AnalysisCompleted;

        public void Initialize(TrendAnalyzerConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (_configLock)
            {
                _config = config;
                InitializeLayers_NoLock();
                _telemetry.ConfigurePatternTelemetry(_config);
                _initialized = true;
                _logger.LogInformation("MarketTrendAnalyzer đã khởi tạo {LayerCount} layer", _layers.Count);
            }
        }

        public void UpdateConfig(TrendAnalyzerConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (_configLock)
            {
                _config = config;
                InitializeLayers_NoLock();
                _telemetry.ConfigurePatternTelemetry(_config);
                _logger.LogInformation("MarketTrendAnalyzer cập nhật config, version {Version}", config.Version);
            }
        }

        public TrendSignal? Analyze(PreprocessorSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            TrendAnalyzerConfig? config;
            lock (_configLock)
            {
                config = _config;
            }

            if (config == null || !config.Enabled)
            {
                return null;
            }

            var layerScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var confirmations = new List<string>();
            var warnings = new List<string>();
            TrendSignal? signal = null;
            Exception? failure = null;
            var symbolLabel = ResolveSymbolLabel(snapshot);
            var timeframeLabel = ResolveTimeframeLabel(snapshot);
            PatternLayerTelemetrySnapshot? latestPatternSnapshot = null;

            _analysisStopwatch.Restart();
            var history = SnapshotHistoryRegistry.TryGet(snapshot);
            var accessor = new SnapshotDataAccessor(snapshot, history);
            var patternFlags = new List<string>();
            try
            {
                double weightedScore = 0.0;
                foreach (var layer in _layers.Values)
                {
                    if (!layer.IsEnabled)
                    {
                        continue;
                    }

                    var isPatternLayer = _patternLayer != null && ReferenceEquals(layer, _patternLayer);
                    if (isPatternLayer)
                    {
                        StartPatternTelemetrySafe(symbolLabel, timeframeLabel);
                    }

                    double score = SafeCalculateLayerScore(layer, snapshot, accessor, warnings);
                    layerScores[layer.LayerName] = score;

                    var weight = GetLayerWeight(config, layer.LayerName);
                    weightedScore += score * weight;

                    var diagnostics = layer.GetDiagnostics();
                    MergeDiagnostics(diagnostics, confirmations, warnings);

                    if (layer.LayerName.Equals("Patterns", StringComparison.OrdinalIgnoreCase))
                    {
                        var flags = ExtractPatternFlags(diagnostics);
                        if (flags.Count > 0)
                        {
                            patternFlags.AddRange(flags);
                        }
                    }

                    if (isPatternLayer)
                    {
                        latestPatternSnapshot = _patternLayer?.LastTelemetrySnapshot;
                    }
                }

                var confluenceBonus = CalculateTimeframeConfluenceBonus(snapshot, config);
                var totalScore = Math.Clamp(weightedScore + confluenceBonus, 0, 100);

                var distinctPatternFlags = patternFlags
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                signal = CreateTrendSignal(
                    totalScore,
                    layerScores,
                    confirmations,
                    warnings,
                    snapshot,
                    config,
                    distinctPatternFlags);

                if (latestPatternSnapshot != null)
                {
                    try
                    {
                        _telemetry.LogPatternLayerResults(
                            symbolLabel,
                            timeframeLabel,
                            latestPatternSnapshot,
                            signal?.Confidence ?? 0);
                    }
                    catch (Exception telemetryEx)
                    {
                        _logger.LogDebug(telemetryEx, "PatternLayer telemetry log thất bại");
                    }
                }

                if (signal != null)
                {
                    _bridge.PublishTrendSignal(signal);
                }

                if (config.FeatureFlags.EnableTelemetry)
                {
                    _telemetry.RecordAnalysis(signal, _analysisStopwatch.Elapsed, layerScores);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                _logger.LogError(ex, "Lỗi khi MarketTrendAnalyzer xử lý snapshot");
                _telemetry.RecordError(ex, "Analyze");
            }
            finally
            {
                _analysisStopwatch.Stop();
                LastAnalysisDuration = _analysisStopwatch.Elapsed;
                AnalysisCompleted?.Invoke(this, new TrendAnalysisCompletedEventArgs
                {
                    Signal = signal,
                    AnalysisDuration = LastAnalysisDuration,
                    LayerScores = layerScores,
                    Error = failure
                });
            }

            return signal;
        }

        public void Dispose()
        {
            foreach (var layer in _layers.Values)
            {
                if (layer is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _layers.Clear();
        }

        private void InitializeLayers_NoLock()
        {
            _layers.Clear();
            _patternLayer = null;

            if (_config.FeatureFlags.UseStructureLayer)
            {
                var structureLayer = new StructureLayer(_logger);
                structureLayer.UpdateConfig(_config);
                _layers[structureLayer.LayerName] = structureLayer;
            }

            if (_config.FeatureFlags.UseMALayer)
            {
                var maLayer = new MovingAverageLayer(_logger);
                maLayer.UpdateConfig(_config);
                _layers[maLayer.LayerName] = maLayer;
            }

            if (_config.FeatureFlags.UseMomentumLayer)
            {
                var momentumLayer = new MomentumLayer(_logger);
                momentumLayer.UpdateConfig(_config);
                _layers[momentumLayer.LayerName] = momentumLayer;
            }

            if (_config.FeatureFlags.UsePatternLayer)
            {
                var patternLayer = new PatternLayer(_logger);
                patternLayer.UpdateConfig(_config);
                _layers[patternLayer.LayerName] = patternLayer;
                _patternLayer = patternLayer;
            }
        }

        private static double GetLayerWeight(TrendAnalyzerConfig config, string layerName)
        {
            var weights = config.LayerWeights ?? new LayerWeightsConfig();
            return layerName switch
            {
                "Structure" => weights.Structure,
                "MovingAverages" => weights.MovingAverages,
                "Momentum" => weights.Momentum,
                "Patterns" => weights.Patterns,
                _ => 0.0
            };
        }

        private double SafeCalculateLayerScore(ILayerCalculator layer, PreprocessorSnapshot snapshot, SnapshotDataAccessor accessor, List<string> warnings)
        {
            try
            {
                var score = layer.CalculateScore(snapshot, accessor);
                return Math.Clamp(score, 0, 100);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Layer {Layer} gặp lỗi, trả về điểm trung tính", layer.LayerName);
                warnings.Add($"[{layer.LayerName}] lỗi tính toán: {ex.Message}");
                return 50.0;
            }
        }

        private static void MergeDiagnostics(
            IReadOnlyDictionary<string, object> diagnostics,
            List<string> confirmations,
            List<string> warnings)
        {
            if (diagnostics == null)
            {
                return;
            }

            if (diagnostics.TryGetValue("Confirmations", out var confirmObj) && confirmObj is IEnumerable<string> confirmList)
            {
                confirmations.AddRange(confirmList);
            }

            if (diagnostics.TryGetValue("Warnings", out var warningObj) && warningObj is IEnumerable<string> warningList)
            {
                warnings.AddRange(warningList);
            }
        }

        private double CalculateTimeframeConfluenceBonus(PreprocessorSnapshot snapshot, TrendAnalyzerConfig config)
        {
            if (snapshot.Indicators == null || snapshot.Indicators.Count == 0)
            {
                return 0;
            }

            var weights = config.TimeframeWeights ?? new TimeframeWeightsConfig();
            double score = 0;
            double totalWeight = 0;

            AddBiasContribution(snapshot, "trend.bias.d1", weights.Daily, ref score, ref totalWeight);
            AddBiasContribution(snapshot, "trend.bias.h4", weights.H4, ref score, ref totalWeight);
            AddBiasContribution(snapshot, "trend.bias.h1", weights.H1, ref score, ref totalWeight);
            AddBiasContribution(snapshot, "trend.bias.m15", weights.M15, ref score, ref totalWeight);

            if (totalWeight <= 0)
            {
                return 0;
            }

            var normalized = score / totalWeight; // kỳ vọng -1..1
            return Math.Clamp(normalized * 10.0, -10.0, 10.0);
        }

        private static void AddBiasContribution(
            PreprocessorSnapshot snapshot,
            string indicator,
            double weight,
            ref double score,
            ref double totalWeight)
        {
            if (weight <= 0 || snapshot.Indicators == null)
            {
                return;
            }

            if (snapshot.Indicators.TryGetValue(indicator, out var value))
            {
                score += value * weight;
                totalWeight += weight;
            }
        }

        private TrendSignal CreateTrendSignal(
            double totalScore,
            IReadOnlyDictionary<string, double> layerScores,
            IReadOnlyList<string> confirmations,
            IReadOnlyList<string> warnings,
            PreprocessorSnapshot snapshot,
            TrendAnalyzerConfig config,
            IReadOnlyList<string> patternFlags)
        {
            var timeFrameScores = BuildTimeframeScores(snapshot, config);
            var normalizedPatternFlags = patternFlags ?? Array.Empty<string>();
            var confidence = CalculateConfidence(layerScores, confirmations.Count, warnings.Count, config);
            confidence = ApplyPatternConfidenceMultiplier(confidence, normalizedPatternFlags);
            return new TrendSignal
            {
                Score = totalScore,
                Direction = DetermineTrendDirection(totalScore, config),
                Strength = DetermineTrendStrength(totalScore),
                Confidence = confidence,
                StructureScore = layerScores.GetValueOrDefault("Structure", 0),
                MovingAverageScore = layerScores.GetValueOrDefault("MovingAverages", 0),
                MomentumScore = layerScores.GetValueOrDefault("Momentum", 0),
                PatternScore = layerScores.GetValueOrDefault("Patterns", 0),
                Confirmations = confirmations.ToArray(),
                Warnings = warnings.ToArray(),
                PatternFlags = normalizedPatternFlags,
                GeneratedAtUtc = DateTime.UtcNow,
                PrimaryTimeFrame = DeterminePrimaryTimeframe(timeFrameScores),
                TimeFrameScores = timeFrameScores,
                Version = config.Version
            };
        }

        private static IReadOnlyList<string> ExtractPatternFlags(IReadOnlyDictionary<string, object> diagnostics)
        {
            if (diagnostics == null)
            {
                return Array.Empty<string>();
            }

            if (!diagnostics.TryGetValue("PatternFlags", out var flagsObj) || flagsObj == null)
            {
                return Array.Empty<string>();
            }

            return flagsObj switch
            {
                string single => single.Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                IEnumerable<string> enumerable => enumerable.ToArray(),
                _ => Array.Empty<string>()
            };
        }

        private static double ApplyPatternConfidenceMultiplier(double baseConfidence, IReadOnlyList<string> patternFlags)
        {
            if (patternFlags == null || patternFlags.Count == 0)
            {
                return Math.Clamp(baseConfidence, 0.0, 1.0);
            }

            double multiplier = 1.0;
            foreach (var flag in patternFlags)
            {
                if (flag.Equals("LiquidityGrab", StringComparison.OrdinalIgnoreCase))
                {
                    multiplier *= 0.7;
                }
                else if (flag.Equals("CleanBreakout", StringComparison.OrdinalIgnoreCase))
                {
                    multiplier *= 1.2;
                }
            }

            var adjusted = baseConfidence * multiplier;
            return Math.Clamp(adjusted, 0.0, 1.0);
        }

        private static TrendDirection DetermineTrendDirection(double score, TrendAnalyzerConfig config)
        {
            var thresholds = config.Thresholds ?? new ThresholdsConfig();
            if (score >= thresholds.StrongBullish) return TrendDirection.StrongBullish;
            if (score >= thresholds.Bullish) return TrendDirection.Bullish;
            if (score >= thresholds.NeutralBullish) return TrendDirection.NeutralBullish;
            if (score >= thresholds.Range) return TrendDirection.Range;
            if (score >= thresholds.NeutralBearish) return TrendDirection.NeutralBearish;
            if (score >= thresholds.Bearish) return TrendDirection.Bearish;
            return TrendDirection.StrongBearish;
        }

        private static TrendStrength DetermineTrendStrength(double score)
        {
            if (score >= 85) return TrendStrength.VeryStrong;
            if (score >= 70) return TrendStrength.Strong;
            if (score >= 55) return TrendStrength.Moderate;
            if (score >= 40) return TrendStrength.Weak;
            return TrendStrength.VeryWeak;
        }

        private double CalculateConfidence(
            IReadOnlyDictionary<string, double> layerScores,
            int confirmationCount,
            int warningCount,
            TrendAnalyzerConfig config)
        {
            var avgLayerScore = layerScores.Count > 0 ? layerScores.Values.Average() : 50.0;
            var confirmationFactor = confirmationCount / Math.Max(config.Hysteresis.RequiredConfirmations, 1);
            var warningPenalty = warningCount * 0.05;
            var confidence = (avgLayerScore / 100.0) * 0.6 + confirmationFactor * 0.4 - warningPenalty;
            return Math.Clamp(confidence, 0.0, 1.0);
        }

        private static IReadOnlyDictionary<TimeFrame, double> BuildTimeframeScores(
            PreprocessorSnapshot snapshot,
            TrendAnalyzerConfig config)
        {
            var map = new Dictionary<TimeFrame, double>
            {
                [TimeFrame.D1] = ExtractIndicator(snapshot, "trend.bias.d1"),
                [TimeFrame.H4] = ExtractIndicator(snapshot, "trend.bias.h4"),
                [TimeFrame.H1] = ExtractIndicator(snapshot, "trend.bias.h1"),
                [TimeFrame.M15] = ExtractIndicator(snapshot, "trend.bias.m15")
            };

            return map;
        }

        private static double ExtractIndicator(PreprocessorSnapshot snapshot, string key)
        {
            if (snapshot?.Indicators != null && snapshot.Indicators.TryGetValue(key, out var value))
            {
                return value;
            }

            return 0.0;
        }

        private static TimeFrame DeterminePrimaryTimeframe(IReadOnlyDictionary<TimeFrame, double> scores)
        {
            if (scores == null || scores.Count == 0)
            {
                return TimeFrame.H1;
            }

            var max = scores.MaxBy(pair => Math.Abs(pair.Value));
            return max.Equals(default(KeyValuePair<TimeFrame, double>)) ? TimeFrame.H1 : max.Key;
        }

        private static readonly TimeFrame[] PreferredTelemetryTimeframes =
        {
            TimeFrame.H1,
            TimeFrame.M15,
            TimeFrame.H4,
            TimeFrame.D1,
            TimeFrame.M30,
            TimeFrame.M5,
            TimeFrame.M1,
            TimeFrame.W1,
            TimeFrame.MN1
        };

        private static readonly char[] SymbolSplitDelimiters = { '.', '_', '-', ':' };

        private static string ResolveSymbolLabel(PreprocessorSnapshot snapshot)
        {
            if (snapshot?.Indicators == null || snapshot.Indicators.Count == 0)
            {
                return string.Empty;
            }

            foreach (var key in snapshot.Indicators.Keys)
            {
                var candidate = ExtractSymbolFromIndicatorKey(key);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static string ExtractSymbolFromIndicatorKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var tokens = key.Split(SymbolSplitDelimiters, StringSplitOptions.RemoveEmptyEntries);
            for (var i = tokens.Length - 1; i >= 0; i--)
            {
                var token = tokens[i];
                if (IsLikelySymbolToken(token))
                {
                    return token.ToUpperInvariant();
                }
            }

            return string.Empty;
        }

        private static bool IsLikelySymbolToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length < 5 || token.Length > 8)
            {
                return false;
            }

            for (var i = 0; i < token.Length; i++)
            {
                if (!char.IsLetter(token[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string ResolveTimeframeLabel(PreprocessorSnapshot snapshot)
        {
            if (snapshot?.LatestBars != null && snapshot.LatestBars.Count > 0)
            {
                foreach (var timeframe in PreferredTelemetryTimeframes)
                {
                    if (snapshot.LatestBars.ContainsKey(timeframe))
                    {
                        return timeframe.ToString();
                    }
                }

                var first = snapshot.LatestBars.Keys.FirstOrDefault();
                return first.ToString();
            }

            var fromIndicators = ExtractTimeframeFromIndicators(snapshot?.Indicators);
            return string.IsNullOrWhiteSpace(fromIndicators) ? string.Empty : fromIndicators;
        }

        private static string ExtractTimeframeFromIndicators(IReadOnlyDictionary<string, double>? indicators)
        {
            if (indicators == null || indicators.Count == 0)
            {
                return string.Empty;
            }

            foreach (var key in indicators.Keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                foreach (var timeframe in PreferredTelemetryTimeframes)
                {
                    if (key.IndexOf(timeframe.ToString(), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return timeframe.ToString();
                    }
                }
            }

            return string.Empty;
        }

        private void StartPatternTelemetrySafe(string symbol, string timeframe)
        {
            try
            {
                _telemetry.StartPatternAnalysis(symbol, timeframe);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Không thể khởi động PatternLayer telemetry");
            }
        }
    }
}
