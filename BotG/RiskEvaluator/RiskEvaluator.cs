
using Strategies;
namespace RiskEvaluator
{
    public class RiskEvaluator : IRiskEvaluator
    {
        // Checklist scoring: mỗi tiêu chí đạt được cộng điểm
        public double Evaluate(TradeSignal signal)
        {
            double score = 0;
            var wyckoff = signal as WyckoffTradeSignal;
            if (wyckoff != null)
            {
                if (wyckoff.HasStrongOrderBlock) score += 3;
                if (wyckoff.HasFairValueGap) score += 2;
                if (wyckoff.IsInDiscountZone) score += 1.5;
                if (wyckoff.HasVolumeSpike) score += 1.5;
                if (wyckoff.ConfirmationCount >= 2) score += 2;
            }
            // ... thêm tiêu chí khác nếu cần
            return score;
        }

        public bool IsAcceptable(TradeSignal signal)
        {
            return Evaluate(signal) >= 7.5; // Ngưỡng có thể chỉnh
        }
    }
}
