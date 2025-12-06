using System;

namespace Strategies.Config
{
    /// <summary>
    /// Cấu hình cho chiến lược Trend Pullback RSI: bám xu hướng H1, kích hoạt bằng RSI trên M15.
    /// </summary>
    public sealed class TrendPullbackRsiStrategyConfig
    {
        public int TrendEmaFast { get; set; } = 50;
        public int TrendEmaSlow { get; set; } = 200;
        public double MinimumTrendSeparationRatio { get; set; } = 0.0008;
        public double TargetTrendSeparationRatio { get; set; } = 0.0016;
        public int AtrPeriod { get; set; } = 14;
        public double AtrStopMultiplier { get; set; } = 1.6;
        public double AtrTakeProfitMultiplier { get; set; } = 3.0;
        public double MinimumAtr { get; set; } = 0.0004;
        public int EntryRsiPeriod { get; set; } = 14;
        public double Oversold { get; set; } = 30.0;
        public double Overbought { get; set; } = 70.0;
        public double TriggerReleaseRange { get; set; } = 20.0;
        public double MinimumAlignmentRatio { get; set; } = 0.55;
        public double MinimumConfidence { get; set; } = 0.5;
        public double ReentryCooldownMinutes { get; set; } = 45.0;
        public double ExitCooldownMinutes { get; set; } = 20.0;

        public void Validate()
        {
            TrendEmaFast = Math.Max(5, TrendEmaFast);
            TrendEmaSlow = Math.Max(TrendEmaFast + 1, TrendEmaSlow);
            MinimumTrendSeparationRatio = ClampRatio(MinimumTrendSeparationRatio, 1e-5, 0.01);
            TargetTrendSeparationRatio = ClampRatio(TargetTrendSeparationRatio, MinimumTrendSeparationRatio, 0.02);
            AtrPeriod = Math.Max(5, AtrPeriod);
            AtrStopMultiplier = Math.Clamp(AtrStopMultiplier, 0.5, 5.0);
            AtrTakeProfitMultiplier = Math.Clamp(AtrTakeProfitMultiplier, AtrStopMultiplier * 1.1, 6.0);
            MinimumAtr = Math.Clamp(MinimumAtr, 1e-5, 0.01);
            EntryRsiPeriod = Math.Max(5, EntryRsiPeriod);
            Oversold = Math.Clamp(Oversold, 5.0, 45.0);
            Overbought = Math.Clamp(Overbought, 55.0, 95.0);
            if (Oversold >= Overbought)
            {
                Overbought = Oversold + 10.0;
            }
            TriggerReleaseRange = Math.Clamp(TriggerReleaseRange, 5.0, 40.0);
            MinimumAlignmentRatio = Math.Clamp(MinimumAlignmentRatio, 0.3, 1.0);
            MinimumConfidence = Math.Clamp(MinimumConfidence, 0.35, 0.85);
            ReentryCooldownMinutes = Math.Clamp(ReentryCooldownMinutes, 5.0, 180.0);
            ExitCooldownMinutes = Math.Clamp(ExitCooldownMinutes, 2.0, 120.0);
        }

        private static double ClampRatio(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return min;
            }

            return Math.Clamp(value, min, max);
        }
    }
}
