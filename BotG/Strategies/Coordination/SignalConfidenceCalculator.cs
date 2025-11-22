using System;
using System.Collections.Generic;
using BotG.MarketRegime;
using BotG.Runtime.Logging;
using Strategies;

namespace BotG.Strategies.Coordination
{
    /// <summary>
    /// Combines signal metadata, market context, and risk to produce a normalized confidence score.
    /// </summary>
    public sealed class SignalConfidenceCalculator
    {
        private StrategyCoordinationConfig _config;
        private readonly CooldownRecoverySystem _cooldownRecovery = new();
        private readonly ConfidenceBooster _confidenceBooster = new();

        public SignalConfidenceCalculator(StrategyCoordinationConfig config)
        {
            UpdateConfig(config);
        }

        public void UpdateConfig(StrategyCoordinationConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _config = config;
            _cooldownRecovery.UpdateSettings(config.CooldownRecovery);
        }

        public TradingSignal CreateTradingSignal(
            StrategyEvaluation evaluation,
            MarketContext context,
            TimeSpan timeSinceLastTrade,
            double? dynamicThreshold = null)
        {
            if (evaluation == null) throw new ArgumentNullException(nameof(evaluation));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var signal = evaluation.Signal ?? throw new ArgumentException("Strategy evaluation does not contain a signal.", nameof(evaluation));

            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var baseConfidence = Math.Clamp(signal.Confidence, 0.0, 1.0);
            if (baseConfidence <= 0.0)
            {
                baseConfidence = _config.ConfidenceFloor;
            }

            var boostedConfidence = _confidenceBooster.BoostConfidence(baseConfidence, signal, context);
            metrics["boosted_confidence"] = boostedConfidence;

            var strategyWeight = _config.GetStrategyWeight(signal.StrategyName, 1.0);
            var weightedConfidence = boostedConfidence * strategyWeight;
            if (weightedConfidence <= 0.0)
            {
                weightedConfidence = _config.ConfidenceFloor;
            }

            var riskAdjustment = CalculateRiskAdjustment(evaluation.Risk);
            var regimeAdjustment = CalculateRegimeAdjustment(signal.Action, context.CurrentRegime);
            var exposurePenalty = CalculateExposurePenalty(context);
            var cooldownPenalty = CalculateCooldownPenalty(signal.Action, timeSinceLastTrade);
            var latencyPenalty = CalculateLatencyPenalty(evaluation.EvaluationLatency);

            metrics["base_confidence"] = baseConfidence;
            metrics["strategy_weight"] = strategyWeight;
            metrics["weighted_confidence"] = weightedConfidence;
            metrics["risk_adjustment"] = riskAdjustment;
            metrics["regime_adjustment"] = regimeAdjustment;
            metrics["exposure_penalty"] = exposurePenalty;
            metrics["cooldown_penalty"] = cooldownPenalty;
            metrics["latency_ms"] = evaluation.EvaluationLatency.TotalMilliseconds;

            var score = weightedConfidence
                        + riskAdjustment
                        + regimeAdjustment
                        - exposurePenalty
                        - cooldownPenalty
                        - latencyPenalty;

            score = Math.Clamp(score, 0.0, 1.0);

            var threshold = dynamicThreshold ?? _config.MinimumConfidence;
            var direction = MapDirection(signal.Action);
            var clampedTimeSinceTrade = timeSinceLastTrade < TimeSpan.Zero ? TimeSpan.Zero : timeSinceLastTrade;

                // If time-based filtering is enabled and we're within the cooldown window, block the signal
                if (_config.EnableTimeBasedFiltering && signal.Action != TradeAction.Exit && clampedTimeSinceTrade < _config.MinimumTimeBetweenTrades)
                {
                    metrics["cooldown_blocked"] = 1.0;
                    _cooldownRecovery.RecordCooldownBlock();
                    LogConfidenceBreakdown(
                        signal,
                        evaluation,
                        context,
                        baseConfidence,
                        boostedConfidence,
                        strategyWeight,
                        weightedConfidence,
                        riskAdjustment,
                        regimeAdjustment,
                        exposurePenalty,
                        cooldownPenalty,
                        latencyPenalty,
                        0.0,
                        clampedTimeSinceTrade,
                        cooldownBlocked: true,
                        thresholdOverride: threshold);
                    return new TradingSignal
                    {
                        StrategyName = evaluation.StrategyName,
                        Direction = direction,
                        Symbol = context.LatestTick.Symbol,
                        ConfidenceScore = 0.0,
                        GeneratedTime = signal.TimestampUtc,
                        RegimeContext = context.CurrentRegime,
                        SignalMetrics = metrics,
                        IsConfirmed = false,
                        TimeSinceLastTrade = clampedTimeSinceTrade,
                        SourceSignal = signal
                    };
                }

            var tradingSignal = new TradingSignal
            {
                StrategyName = evaluation.StrategyName,
                Direction = direction,
                Symbol = context.LatestTick.Symbol,
                ConfidenceScore = score,
                GeneratedTime = signal.TimestampUtc,
                RegimeContext = context.CurrentRegime,
                SignalMetrics = metrics,
                IsConfirmed = score >= threshold && evaluation.Risk.IsAcceptable,
                TimeSinceLastTrade = clampedTimeSinceTrade,
                SourceSignal = signal
            };

            LogConfidenceBreakdown(
                signal,
                evaluation,
                context,
                baseConfidence,
                boostedConfidence,
                strategyWeight,
                weightedConfidence,
                riskAdjustment,
                regimeAdjustment,
                exposurePenalty,
                cooldownPenalty,
                latencyPenalty,
                score,
                clampedTimeSinceTrade,
                cooldownBlocked: false,
                thresholdOverride: threshold);

            return tradingSignal;
        }

        private double CalculateRiskAdjustment(RiskScore risk)
        {
            var adjustment = 0.0;
            switch (risk.Level)
            {
                case RiskLevel.Preferred:
                    adjustment += _config.PreferredRiskBonus;
                    break;
                case RiskLevel.Elevated:
                    adjustment -= _config.ElevatedRiskPenalty;
                    break;
                case RiskLevel.Blocked:
                    adjustment -= _config.BlockedRiskPenalty;
                    break;
            }

            if (!risk.IsAcceptable)
            {
                adjustment -= _config.BlockedRiskPenalty;
            }

            return adjustment;
        }

        private static double CalculateRegimeAdjustment(TradeAction action, RegimeType regime)
        {
            if (action == TradeAction.None)
            {
                return 0.0;
            }

            return regime switch
            {
                RegimeType.Trending => 0.08,
                RegimeType.Ranging => action == TradeAction.Exit ? 0.02 : -0.05,
                RegimeType.Volatile => -0.10,
                RegimeType.Calm => 0.03,
                _ => 0.0
            };
        }

        private static double CalculateExposurePenalty(MarketContext context)
        {
            if (context.AccountEquity <= 0.0)
            {
                return 0.0;
            }

            var ratio = context.OpenPositionExposure / context.AccountEquity;
            if (ratio <= 0.5)
            {
                return 0.0;
            }

            return Math.Min((ratio - 0.5) * 0.4, 0.2);
        }

        private double CalculateCooldownPenalty(TradeAction action, TimeSpan timeSinceLastTrade)
        {
            if (action == TradeAction.Exit)
            {
                return 0.0;
            }

            if (timeSinceLastTrade <= TimeSpan.Zero)
            {
                return _config.CooldownPenalty;
            }

            if (_config.MinimumTimeBetweenTrades <= TimeSpan.Zero || timeSinceLastTrade >= _config.MinimumTimeBetweenTrades)
            {
                return 0.0;
            }

            var ratio = 1.0 - (timeSinceLastTrade.TotalSeconds / _config.MinimumTimeBetweenTrades.TotalSeconds);
            var penalty = Math.Clamp(ratio, 0.0, 1.0) * _config.CooldownPenalty;
            var multiplier = _cooldownRecovery.GetPenaltyMultiplier();
            return penalty * multiplier;
        }

        private static double CalculateLatencyPenalty(TimeSpan evaluationLatency)
        {
            if (evaluationLatency <= TimeSpan.Zero)
            {
                return 0.0;
            }

            var ms = evaluationLatency.TotalMilliseconds;
            if (ms <= 500.0)
            {
                return 0.0;
            }

            return Math.Min(ms / 4000.0, 0.1);
        }

        internal static cAlgo.API.TradeType MapDirection(TradeAction action)
        {
            return action switch
            {
                TradeAction.Sell => cAlgo.API.TradeType.Sell,
                TradeAction.Exit => cAlgo.API.TradeType.Sell,
                TradeAction.Buy => cAlgo.API.TradeType.Buy,
                _ => cAlgo.API.TradeType.Buy
            };
        }

        public void RecordTradeExecuted()
        {
            _cooldownRecovery.RecordTradeExecuted();
            _confidenceBooster.RecordTrade();
        }

        private void LogConfidenceBreakdown(
            Signal sourceSignal,
            StrategyEvaluation evaluation,
            MarketContext context,
            double baseConfidence,
            double boostedConfidence,
            double strategyWeight,
            double weightedConfidence,
            double riskAdjustment,
            double regimeAdjustment,
            double exposurePenalty,
            double cooldownPenalty,
            double latencyPenalty,
            double finalScore,
            TimeSpan timeSinceLastTrade,
            bool cooldownBlocked,
            double? thresholdOverride = null)
        {
            try
            {
                PipelineLogger.Log(
                    "COORD",
                    "ConfidenceBreakdown",
                    $"Confidence breakdown for {sourceSignal.StrategyName}",
                    new
                {
                    strategy = sourceSignal.StrategyName,
                    action = sourceSignal.Action.ToString(),
                    symbol = context.LatestTick.Symbol,
                    regime = context.CurrentRegime.ToString(),
                    base_confidence = baseConfidence,
                    boosted_confidence = boostedConfidence,
                    strategy_weight = strategyWeight,
                    weighted_confidence = weightedConfidence,
                        risk_adjustment = riskAdjustment,
                        regime_adjustment = regimeAdjustment,
                        exposure_penalty = exposurePenalty,
                        cooldown_penalty = cooldownPenalty,
                        latency_penalty = latencyPenalty,
                        final_score = finalScore,
                        threshold = thresholdOverride ?? _config.MinimumConfidence,
                        cooldown_blocked = cooldownBlocked,
                        time_since_last_trade_seconds = timeSinceLastTrade.TotalSeconds,
                        risk_level = evaluation.Risk.Level.ToString(),
                        risk_acceptable = evaluation.Risk.IsAcceptable,
                        evaluation_latency_ms = evaluation.EvaluationLatency.TotalMilliseconds
                    },
                    null);
            }
            catch
            {
                // Telemetry logging must never interfere with trading flow.
            }
        }
    }
}
