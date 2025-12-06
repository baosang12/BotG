using System;
using System.Collections.Generic;
using BotG.MarketRegime;
using cAlgo.API;
using Strategies;

namespace BotG.Strategies.Coordination
{
    /// <summary>
    /// Represents a scored trading signal used by the strategy coordinator.
    /// </summary>
    public class TradingSignal
    {
        public string StrategyName { get; set; } = string.Empty;
        public TradeType Direction { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public double ConfidenceScore { get; set; }
        public DateTime GeneratedTime { get; set; }
        public RegimeType RegimeContext { get; set; } = RegimeType.Uncertain;
        public Dictionary<string, double> SignalMetrics { get; set; } = new Dictionary<string, double>();
        public bool IsConfirmed { get; set; }
        public List<string> ConflictingSignals { get; set; } = new List<string>();
        public TimeSpan TimeSinceLastTrade { get; set; }
        public Signal? SourceSignal { get; set; }
    }
}
