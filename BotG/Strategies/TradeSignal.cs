using System;

namespace Strategies
{
    public enum TradeAction
    {
        None,
        Buy,
        Sell,
        Exit
    }

    public class TradeSignal
    {
        public TradeAction Action { get; set; }
        public double Price { get; set; }
        public double? StopLoss { get; set; }
        public double? TakeProfit { get; set; }
    }

    // TradeSignal chuyên biệt cho Wyckoff, bổ sung các property checklist cho RiskEvaluator
    public class WyckoffTradeSignal : TradeSignal
    {
        public bool HasStrongOrderBlock { get; set; }
        public bool HasFairValueGap { get; set; }
        public bool IsInDiscountZone { get; set; }
        public bool HasVolumeSpike { get; set; }
        public int ConfirmationCount { get; set; }
        // Có thể bổ sung thêm các trường khác nếu cần
    }
}
