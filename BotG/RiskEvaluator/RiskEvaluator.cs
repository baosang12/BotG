using System;
using System.Collections.Generic;
using Strategies;

namespace RiskEvaluator
{
    public class RiskEvaluator : IRiskEvaluator
    {
        private const double AcceptableScore = 5.0;

        public RiskScore Evaluate(Signal signal, MarketContext? context = null)
        {
            double score = 0;
            var factors = new Dictionary<string, double>();

            if (signal is WyckoffTradeSignal wyckoff)
            {
                if (wyckoff.HasStrongOrderBlock) { score += 3; factors["order_block"] = 1; }
                if (wyckoff.HasFairValueGap) { score += 2; factors["fair_value_gap"] = 1; }
                if (wyckoff.IsInDiscountZone) { score += 1.5; factors["discount_zone"] = 1; }
                if (wyckoff.HasVolumeSpike) { score += 1.5; factors["volume_spike"] = 1; }
                if (wyckoff.ConfirmationCount >= 2)
                {
                    score += Math.Min(3, wyckoff.ConfirmationCount);
                    factors["confirmation"] = wyckoff.ConfirmationCount;
                }
            }

            var level = RiskLevel.Normal;
            string? reason = null;

            if (context != null)
            {
                // Hard stop: daily drawdown beyond 3% of equity
                if (context.DailyDrawdown <= -Math.Abs(context.AccountEquity) * 0.03)
                {
                    level = RiskLevel.Blocked;
                    reason = "DailyDrawdownLimit";
                }
                else if (context.OpenPositionExposure >= Math.Abs(context.AccountEquity) * 0.5)
                {
                    level = RiskLevel.Elevated;
                    reason ??= "ExposureHigh";
                }
            }

            bool acceptable = score >= AcceptableScore && level != RiskLevel.Blocked;
            if (!acceptable && reason == null)
            {
                reason = score < AcceptableScore ? "ScoreBelowThreshold" : "RiskLevelBlocked";
            }

            return new RiskScore(score, level, acceptable, reason, factors);
        }
    }
}
