using System;
using System.Collections.Generic;
using BotG.MarketRegime;

namespace Strategies
{
    public enum TradeAction
    {
        None,
        Buy,
        Sell,
        Exit
    }

    /// <summary>
    /// Represents an actionable trading signal emitted by a strategy.
    /// </summary>
    public class Signal
    {
        public string StrategyName { get; set; } = string.Empty;
        public TradeAction Action { get; set; } = TradeAction.None;
        public double Price { get; set; }
        public double? StopLoss { get; set; }
        public double? TakeProfit { get; set; }
        public double Confidence { get; set; } = 0.0;
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public IReadOnlyDictionary<string, double>? Indicators { get; set; }
        public IReadOnlyDictionary<string, string>? Notes { get; set; }
    }

    /// <summary>
    /// Backwards compatible alias for legacy code paths still referencing TradeSignal.
    /// </summary>
    public class TradeSignal : Signal
    {
    }

    /// <summary>
    /// Strategy-specific extension used by Wyckoff analysis components.
    /// </summary>
    public class WyckoffTradeSignal : Signal
    {
        public bool HasStrongOrderBlock { get; set; }
        public bool HasFairValueGap { get; set; }
        public bool IsInDiscountZone { get; set; }
        public bool HasVolumeSpike { get; set; }
        public int ConfirmationCount { get; set; }
    }

    public enum RiskLevel
    {
        Blocked = 0,
        Elevated = 1,
        Normal = 2,
        Preferred = 3
    }

    /// <summary>
    /// Encapsulates risk scoring results used to gate execution.
    /// </summary>
    public sealed record RiskScore(
        double Score,
        RiskLevel Level,
        bool IsAcceptable,
        string? Reason = null,
        IReadOnlyDictionary<string, double>? Factors = null);

    /// <summary>
    /// Lightweight tick snapshot delivered to strategies.
    /// </summary>
    public sealed record MarketData(
        string Symbol,
        double Bid,
        double Ask,
        DateTime TimestampUtc,
        double? Volume = null,
        IReadOnlyDictionary<string, double>? Indicators = null)
    {
        public double Mid => (Bid + Ask) / 2.0;
        public double Spread => Math.Abs(Ask - Bid);
    }

    /// <summary>
    /// Aggregated market + account context used for risk evaluation.
    /// </summary>
    public sealed record MarketContext(
        MarketData LatestTick,
        double AccountEquity,
        double OpenPositionExposure,
        double DailyDrawdown,
        RegimeType CurrentRegime = RegimeType.Uncertain,
        RegimeAnalysisResult? RegimeAnalysis = null,
        IReadOnlyDictionary<string, double>? RiskMetrics = null,
        IReadOnlyDictionary<string, object>? Metadata = null)
    {
        public DateTime CurrentTime { get; set; } = DateTime.UtcNow;
    }
}
