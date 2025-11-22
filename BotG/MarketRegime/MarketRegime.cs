using System;
using System.Collections.Generic;

namespace BotG.MarketRegime
{
    /// <summary>
    /// Defines the current market condition classification used by the Strategy Router (MoE architecture).
    /// Each regime indicates a distinct market behavior pattern requiring different trading approaches.
    /// </summary>
    public enum RegimeType
    {
        /// <summary>
        /// Strong directional movement (uptrend or downtrend) with ADX > 25.
        /// Trend-following strategies perform best.
        /// </summary>
        Trending,

        /// <summary>
        /// Sideways price action without clear direction, ADX &lt; 20.
        /// Mean-reversion and range-trading strategies preferred.
        /// </summary>
        Ranging,

        /// <summary>
        /// High volatility with ATR exceeding 1.5x average.
        /// Risk management critical; reduce position sizes or avoid trading.
        /// </summary>
        Volatile,

        /// <summary>
        /// Low volatility with ATR below 0.5x average.
        /// Tight spreads; scalping and breakout anticipation strategies applicable.
        /// </summary>
        Calm,

        /// <summary>
        /// Mixed signals; regime cannot be confidently classified.
        /// Conservative approach recommended; wait for clearer signals.
        /// </summary>
        Uncertain
    }

    /// <summary>
    /// Extension helpers for RegimeType to support strategy routing, risk sizing, and UI display.
    /// Delegates to <see cref="RegimeProfileRegistry"/> so that mappings remain configurable.
    /// </summary>
    public static class RegimeTypeExtensions
    {
        public static IReadOnlyList<string> GetRecommendedStrategies(this RegimeType regime)
        {
            return RegimeProfileRegistry.GetRecommendedStrategies(regime);
        }

        public static IReadOnlyList<string> GetStrategyTags(this RegimeType regime)
        {
            return RegimeProfileRegistry.GetStrategyTags(regime);
        }

        public static bool IsStrategyCompatible(this RegimeType regime, string? strategyName)
        {
            return RegimeProfileRegistry.IsStrategyCompatible(regime, strategyName);
        }

        public static double GetRiskMultiplier(this RegimeType regime)
        {
            return RegimeProfileRegistry.GetProfile(regime).RiskMultiplier;
        }

        public static string ToDisplayString(this RegimeType regime)
        {
            return RegimeProfileRegistry.GetProfile(regime).DisplayName;
        }

        public static string GetTradingRecommendation(this RegimeType regime)
        {
            return RegimeProfileRegistry.GetProfile(regime).Recommendation ?? string.Empty;
        }

        public static double GetConfidenceThreshold(this RegimeType regime)
        {
            return RegimeProfileRegistry.GetProfile(regime).MinimumConfidence;
        }
    }

    /// <summary>
    /// Represents a detailed regime classification result including telemetry for downstream consumers.
    /// </summary>
    public sealed class RegimeAnalysisResult
    {
        public RegimeType Regime { get; init; } = RegimeType.Uncertain;

        public double Confidence { get; init; } = 0.0;

        public double Adx { get; init; } = 0.0;

        public double Atr { get; init; } = 0.0;

        public double AverageAtr { get; init; } = 0.0;

        public double BollingerWidth { get; init; } = 0.0;

        public DateTime AnalysisTimeUtc { get; init; } = DateTime.UtcNow;

        public string Symbol { get; init; } = string.Empty;

        public string Timeframe { get; init; } = string.Empty;

        public string Notes { get; init; } = string.Empty;

        public IReadOnlyDictionary<string, double>? Indicators { get; init; } = null;

        public bool IsConfident(double? minimumConfidence = null)
        {
            var threshold = minimumConfidence ?? Regime.GetConfidenceThreshold();
            return Confidence >= threshold;
        }

        public IReadOnlyList<string> GetRecommendedStrategies()
        {
            return Regime.GetRecommendedStrategies();
        }

        public double GetRiskMultiplier()
        {
            return Regime.GetRiskMultiplier();
        }

        public override string ToString()
        {
            return $"{Regime.ToDisplayString()} (Confidence={Confidence:P1}, ADX={Adx:F2}, ATR={Atr:F5})";
        }

        public static RegimeAnalysisResult CreateFallback(RegimeType regime)
        {
            return new RegimeAnalysisResult
            {
                Regime = regime,
                Confidence = 0.0,
                Notes = "Fallback regime analysis",
                AnalysisTimeUtc = DateTime.UtcNow
            };
        }
    }
}
