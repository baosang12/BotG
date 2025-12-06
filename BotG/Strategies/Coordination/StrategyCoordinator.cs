using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BotG.Runtime.Logging;
using cAlgo.API;
using Strategies;

namespace BotG.Strategies.Coordination
{
    /// <summary>
    /// Scores, filters, and reconciles strategy signals before dispatching them to the trade manager.
    /// </summary>
    public sealed class StrategyCoordinator : IStrategyCoordinator
    {
        private StrategyCoordinationConfig _config;
        private SignalConfidenceCalculator _confidenceCalculator;
        private ConflictResolver _conflictResolver;
        private readonly Dictionary<string, DateTime> _lastTradeByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new();
        private readonly object _configSync = new();
        private long _lastCoordinatedTradeTicks = 0;
        private int _timeFallbackWarningEmitted = 0;

        public StrategyCoordinator(StrategyCoordinationConfig? config = null)
        {
            _config = config ?? new StrategyCoordinationConfig();
            _confidenceCalculator = new SignalConfidenceCalculator(_config);
            _conflictResolver = new ConflictResolver(_config);
        }

        public void UpdateConfiguration(StrategyCoordinationConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (_configSync)
            {
                _config = config;
                _confidenceCalculator.UpdateConfig(config);
                _conflictResolver.UpdateConfig(config);
            }
        }

        public Task<IReadOnlyList<StrategyCoordinatorDecision>> CoordinateAsync(
            MarketContext context,
            IReadOnlyList<StrategyEvaluation> evaluations,
            CancellationToken cancellationToken)
        {
            SignalConfidenceCalculator calculator;
            ConflictResolver resolver;
            lock (_configSync)
            {
                calculator = _confidenceCalculator;
                resolver = _conflictResolver;
            }

            if (context == null) throw new ArgumentNullException(nameof(context));
            if (evaluations == null) throw new ArgumentNullException(nameof(evaluations));

            cancellationToken.ThrowIfCancellationRequested();

            if (evaluations.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<StrategyCoordinatorDecision>>(Array.Empty<StrategyCoordinatorDecision>());
            }

            var now = GetCurrentBacktestTime(context);
            var candidates = new List<StrategyCoordinatorDecision>();
            var rejections = new List<object>();
            var totalSignalCount = evaluations.Count(e => e.Signal != null);
            double maxCooldownConfidence = 0.0;

            foreach (var evaluation in evaluations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (evaluation.Signal == null)
                {
                    continue;
                }

                var timeSinceLast = LookupTimeSinceLastTrade(context.LatestTick.Symbol, evaluation.Signal.Action, now, _config);
                var minimumConfidence = ResolveMinimumConfidence(context, now);
                var tradingSignal = calculator.CreateTradingSignal(evaluation, context, timeSinceLast, minimumConfidence);
                var metrics = ExtractMetricsForLog(tradingSignal.SignalMetrics);

                if (!tradingSignal.IsConfirmed)
                {
                    var rejectionReason = evaluation.Risk.IsAcceptable ? "confidence" : "risk";
                    rejections.Add(new
                    {
                        strategy = tradingSignal.StrategyName,
                        direction = tradingSignal.Direction.ToString(),
                        reason = rejectionReason,
                        confidence = tradingSignal.ConfidenceScore,
                        minimum_confidence = minimumConfidence,
                        risk_ok = evaluation.Risk.IsAcceptable,
                        risk_level = evaluation.Risk.Level.ToString(),
                        cooldown_blocked = metrics.TryGetValue("cooldown_blocked", out var cooldown) ? cooldown : 0.0,
                        weighted_confidence = metrics.TryGetValue("weighted_confidence", out var weighted) ? weighted : tradingSignal.ConfidenceScore,
                        risk_adjustment = metrics.TryGetValue("risk_adjustment", out var riskAdj) ? riskAdj : 0.0,
                        regime_adjustment = metrics.TryGetValue("regime_adjustment", out var regimeAdj) ? regimeAdj : 0.0,
                        metrics
                    });
                    LogDetailedRejection(tradingSignal, evaluation, _config, rejectionReason, metrics, minimumConfidence, now);
                    if (metrics.TryGetValue("cooldown_blocked", out var cooldownValue) && cooldownValue > 0)
                    {
                        maxCooldownConfidence = Math.Max(maxCooldownConfidence, tradingSignal.ConfidenceScore);
                    }
                    continue;
                }

                candidates.Add(new StrategyCoordinatorDecision(tradingSignal, evaluation));
            }

            if (candidates.Count == 0)
            {
                if (rejections.Count > 0)
                {
                    var sample = rejections.Count <= 5 ? rejections : rejections.Take(5).ToList();
                    PipelineLogger.Log(
                        "COORD",
                        "Rejected",
                        "Coordinator rejected all signals",
                        new
                        {
                            originalCount = totalSignalCount,
                            rejectedCount = rejections.Count,
                            minimumConfidence = _config.MinimumConfidence,
                            sample,
                            cooldownMaxConfidence = maxCooldownConfidence > 0.0 ? maxCooldownConfidence : (double?)null
                        },
                        null);
                }
                return Task.FromResult<IReadOnlyList<StrategyCoordinatorDecision>>(Array.Empty<StrategyCoordinatorDecision>());
            }

            var resolved = resolver.Resolve(candidates);

            // Calculate coordination metrics for logging
            var consideredCount = candidates.Count;
            var rejectedLowConfidence = totalSignalCount - consideredCount;
            var rejectedConflicts = consideredCount - resolved.Count;

            if (resolved.Count == 0)
            {
                PipelineLogger.Log(
                    "COORD",
                    "Info",
                    "Coordinator processed signals but none passed after conflict resolution",
                    new
                    {
                        originalCount = totalSignalCount,
                        consideredCount,
                        finalCount = 0,
                        rejectedLowConfidence,
                        rejectedConflicts
                    },
                    null);

                return Task.FromResult<IReadOnlyList<StrategyCoordinatorDecision>>(Array.Empty<StrategyCoordinatorDecision>());
            }

            lock (_sync)
            {
                foreach (var decision in resolved)
                {
                    var key = BuildTradeKey(decision.Signal.Symbol, decision.Signal.Direction);
                    _lastTradeByKey[key] = now;
                    calculator.RecordTradeExecuted();
                }
                Interlocked.Exchange(ref _lastCoordinatedTradeTicks, now.Ticks);
            }

            LogSelection(context, resolved);

            return Task.FromResult<IReadOnlyList<StrategyCoordinatorDecision>>(resolved);
        }

        private TimeSpan LookupTimeSinceLastTrade(string symbol, TradeAction action, DateTime now, StrategyCoordinationConfig config)
        {
            var key = BuildTradeKey(symbol, SignalConfidenceCalculator.MapDirection(action));
            lock (_sync)
            {
                if (_lastTradeByKey.TryGetValue(key, out var lastTrade))
                {
                    var elapsed = now - lastTrade;
                    if (config.EnableTimeBasedFiltering)
                    {
                        PipelineLogger.Log(
                            "TIME",
                            "CooldownCheck",
                            "Cooldown interval evaluated",
                            new
                            {
                                symbol,
                                direction = action.ToString(),
                                current = now.ToString("HH:mm:ss"),
                                lastTrade = lastTrade.ToString("HH:mm:ss"),
                                elapsed = elapsed,
                                required = config.MinimumTimeBetweenTrades
                            },
                            null);
                    }
                    return elapsed;
                }
            }

            return TimeSpan.MaxValue;
        }

        private static string BuildTradeKey(string symbol, TradeType direction)
        {
            return symbol + "|" + direction;
        }

        private static Dictionary<string, double> ExtractMetricsForLog(Dictionary<string, double>? metrics)
        {
            if (metrics == null || metrics.Count == 0)
            {
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            // Retain only the metrics that help explain coordinator decisions.
            var keys = new[]
            {
                "base_confidence",
                "weighted_confidence",
                "risk_adjustment",
                "regime_adjustment",
                "cooldown_penalty",
                "cooldown_blocked"
            };

            var filtered = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (metrics.TryGetValue(key, out var value))
                {
                    filtered[key] = value;
                }
            }

            return filtered;
        }

        private static void LogDetailedRejection(
            TradingSignal signal,
            StrategyEvaluation evaluation,
            StrategyCoordinationConfig config,
            string reason,
            Dictionary<string, double> metrics,
            double minimumConfidenceUsed,
            DateTime currentTime)
        {
            if (signal == null)
            {
                return;
            }

            var baseConfidence = metrics.TryGetValue("base_confidence", out var baseConf)
                ? baseConf
                : evaluation.Signal?.Confidence ?? signal.ConfidenceScore;
            var weightedConfidence = metrics.TryGetValue("weighted_confidence", out var weightedConf)
                ? weightedConf
                : signal.ConfidenceScore;
            var payload = new
            {
                strategy = signal.StrategyName,
                symbol = signal.Symbol,
                action = evaluation.Signal?.Action.ToString() ?? "Unknown",
                regime = signal.RegimeContext.ToString(),
                base_confidence = baseConfidence,
                weighted_confidence = weightedConfidence,
                final_confidence = signal.ConfidenceScore,
                threshold = minimumConfidenceUsed,
                reason,
                cooldown_blocked = metrics.TryGetValue("cooldown_blocked", out var blocked) && blocked > 0.0,
                cooldown_penalty = metrics.TryGetValue("cooldown_penalty", out var cooldownPenalty) ? cooldownPenalty : 0.0,
                risk_adjustment = metrics.TryGetValue("risk_adjustment", out var riskAdj) ? riskAdj : 0.0,
                regime_adjustment = metrics.TryGetValue("regime_adjustment", out var regimeAdj) ? regimeAdj : 0.0,
                risk_level = evaluation.Risk.Level.ToString(),
                risk_acceptable = evaluation.Risk.IsAcceptable,
                evaluation_latency_ms = evaluation.EvaluationLatency.TotalMilliseconds,
                time_since_last_trade_seconds = signal.TimeSinceLastTrade.TotalSeconds,
                timestamp_utc = currentTime
            };

            PipelineLogger.Log(
                "COORD",
                "DetailedRejection",
                $"Signal rejected ({reason})",
                payload,
                null);
        }

        private double ResolveMinimumConfidence(MarketContext context, DateTime currentTime)
        {
            var threshold = _config.MinimumConfidence;

            if (_config.AdaptiveConfidence != null &&
                _config.AdaptiveConfidence.TryGetThreshold(context.CurrentRegime, out var adaptiveThreshold))
            {
                threshold = adaptiveThreshold;
            }

            var metadataFloor = TryGetMetadataDouble(context.Metadata, "regime_confidence_floor");
            if (metadataFloor.HasValue)
            {
                threshold = Math.Max(threshold, metadataFloor.Value);
            }

            if (_config.DroughtAdaptive != null && _config.DroughtAdaptive.Enabled)
            {
                var lastTicks = Interlocked.Read(ref _lastCoordinatedTradeTicks);
                if (lastTicks > 0)
                {
                    var lastTrade = new DateTime(lastTicks, DateTimeKind.Utc);
                    var droughtDuration = currentTime - lastTrade;
                    var reduction = _config.DroughtAdaptive.GetReduction(droughtDuration);
                    if (reduction > 0.0)
                    {
                        threshold = Math.Max(threshold - reduction, _config.DroughtAdaptive.MinimumFloor);
                    }
                }
            }

            return Math.Clamp(threshold, 0.05, 1.0);
        }

        private DateTime GetCurrentBacktestTime(MarketContext context)
        {
            if (context != null)
            {
                if (context.CurrentTime != default)
                {
                    return SpecifyUtc(context.CurrentTime);
                }

                var tickTime = context.LatestTick.TimestampUtc;
                if (tickTime != default)
                {
                    return SpecifyUtc(tickTime);
                }

                if (TryExtractMetadataTime(context.Metadata, "server_time", out var serverTime))
                {
                    return SpecifyUtc(serverTime);
                }

                if (TryExtractMetadataTime(context.Metadata, "bar_time", out var barTime))
                {
                    return SpecifyUtc(barTime);
                }
            }

            if (Interlocked.Exchange(ref _timeFallbackWarningEmitted, 1) == 0)
            {
                PipelineLogger.Log("TIME", "UsingRealtimeFallback", "Backtest time not available; defaulting to DateTime.UtcNow", null, null);
            }

            return DateTime.UtcNow;
        }

        private static bool TryExtractMetadataTime(IReadOnlyDictionary<string, object?>? metadata, string key, out DateTime value)
        {
            value = default;
            if (metadata == null || string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (!metadata.TryGetValue(key, out var raw) || raw == null)
            {
                return false;
            }

            switch (raw)
            {
                case DateTime dt:
                    value = dt;
                    return true;
                case string s when DateTime.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed):
                    value = parsed;
                    return true;
                case long ticks when ticks > 0:
                    value = new DateTime(ticks, DateTimeKind.Utc);
                    return true;
                case double oaDate when oaDate > 0:
                    value = DateTime.FromOADate(oaDate);
                    return true;
            }

            return false;
        }

        private static DateTime SpecifyUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                _ => value.ToUniversalTime()
            };
        }

        private static double? TryGetMetadataDouble(IReadOnlyDictionary<string, object?>? metadata, string key)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (!metadata.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            switch (value)
            {
                case double d:
                    return d;
                case float f:
                    return f;
                case decimal m:
                    return (double)m;
                case int i:
                    return i;
                case long l:
                    return l;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                case IConvertible convertible:
                    try
                    {
                        return convertible.ToDouble(CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        return null;
                    }
            }

            return null;
        }

        private static void LogSelection(MarketContext context, IReadOnlyList<StrategyCoordinatorDecision> selections)
        {
            var payload = selections.Select(s => new
            {
                strategy = s.Signal.StrategyName,
                symbol = s.Signal.Symbol,
                confidence = s.Signal.ConfidenceScore,
                direction = s.Signal.Direction.ToString(),
                conflicts = s.Signal.ConflictingSignals
            }).ToList();

            PipelineLogger.Log(
                "COORD",
                "Selected",
                $"Coordinator selected {payload.Count} signal(s)",
                new
                {
                    regime = context.CurrentRegime.ToString(),
                    regime_confidence = context.RegimeAnalysis?.Confidence,
                    selections = payload
                },
                null);
        }
    }
}
