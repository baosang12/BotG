using System.Collections.Generic;

namespace BotG.RiskManager
{
    /// <summary>
    /// Cấu hình cho module RiskManager.
    /// </summary>
    public class RiskSettings
    {
        // Trade risk parameters
        public double MaxRiskPerTradePercent { get; set; } = 1.0;
        public double StopLossAtrMultiplier { get; set; } = 1.8;
        public double TakeProfitAtrMultiplier { get; set; } = 3.0;
        public double VolatilitySizeAdjustment { get; set; } = 1.0;
        // Point value per 1 unit volume in account currency
        public double PointValuePerUnit { get; set; } = 1.0; // TODO: replace with broker-specific point value for XAUUSD (ICMarkets RAW)
                                                             // Optional: value per 1.0 price unit for 1 lot (if provided, used for lot-based sizing)
        public double PointValuePerLot { get; set; } = 0.0;
        // Clamp for per-lot sizing (safer default)
        public double MaxLotsPerTrade { get; set; } = 0.1;
        // Default lot size in units if symbol LotSize is unavailable
        public int LotSizeDefault { get; set; } = 100;
        // Fallback default stop distance (price units) when no SL/ATR is available
        public double DefaultStopDistance { get; set; } = 0.05;

        // Portfolio risk
        public int MaxConcurrentPositions { get; set; } = 5;
        // TODO: Removed portfolio-level drawdown gating (no longer used)
        // public double MaxDrawdownPercent { get; set; } = 10.0;
        public double VarConfidenceLevel { get; set; } = 95.0;

        // Margin monitoring
        public double MaxMarginUsagePercent { get; set; } = 80.0;

        // Slippage
        public double MaxSlippagePips { get; set; } = 2.0;

        // Alerts
        public Dictionary<string, double> AlertThresholds { get; set; } = new Dictionary<string, double>();

        // Conservative scaling for Ops-controlled testing phases
        public double PositionSizeMultiplier { get; set; } = 1.0;
    }
}
