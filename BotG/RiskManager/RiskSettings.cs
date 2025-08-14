using System.Collections.Generic;

namespace RiskManager
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
    }
}
