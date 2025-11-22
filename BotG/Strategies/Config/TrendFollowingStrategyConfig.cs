using System;

namespace Strategies.Config
{
    /// <summary>
    /// Configuration container for the multi-timeframe trend-following strategy.
    /// Defaults are tuned for EMA50/EMA200 alignment on H4 with H1 confirmation and M15 triggers.
    /// </summary>
    public sealed class TrendFollowingStrategyConfig
    {
        public int TrendEmaFast { get; set; } = 50;
        public int TrendEmaSlow { get; set; } = 200;
        public int SignalEmaFast { get; set; } = 34;
        public int SignalEmaSlow { get; set; } = 89;
        public int TriggerEmaFast { get; set; } = 13;
        public int TriggerEmaSlow { get; set; } = 34;
        public int TrendSlopeLookback { get; set; } = 6;
        public int MomentumSlopeLookback { get; set; } = 12;
        public double MinimumTrendSeparationRatio { get; set; } = 0.0010; // 0.10%
        public double TargetTrendSeparationRatio { get; set; } = 0.0020;  // 0.20%
        public double MinimumTrendSlopeRatio { get; set; } = 0.00015;
        public double MinimumMomentumSlopeRatio { get; set; } = 0.00008;
        public double ExitSeparationRatio { get; set; } = 0.0006;
        public int AdxPeriod { get; set; } = 14;
        public double MinimumAdx { get; set; } = 22.0;
        public int AtrPeriod { get; set; } = 14;
        public double AtrStopMultiplier { get; set; } = 1.8;
        public double AtrTakeProfitMultiplier { get; set; } = 3.0;
        public double MaxPullbackAtr { get; set; } = 1.2;
        public double TriggerReEntryAtr { get; set; } = 0.4;
        public int PullbackLookbackBars { get; set; } = 12;
        public double MinimumAlignmentRatio { get; set; } = 0.6;
        public double MinimumConfidence { get; set; } = 0.55;
        public double ReentryCooldownMinutes { get; set; } = 45;
        public double ExitCooldownMinutes { get; set; } = 15;

        public void Validate()
        {
            TrendEmaFast = Math.Max(2, TrendEmaFast);
            TrendEmaSlow = Math.Max(TrendEmaFast + 1, TrendEmaSlow);
            SignalEmaFast = Math.Max(2, SignalEmaFast);
            SignalEmaSlow = Math.Max(SignalEmaFast + 1, SignalEmaSlow);
            TriggerEmaFast = Math.Max(2, TriggerEmaFast);
            TriggerEmaSlow = Math.Max(TriggerEmaFast + 1, TriggerEmaSlow);
            TrendSlopeLookback = Math.Max(2, TrendSlopeLookback);
            MomentumSlopeLookback = Math.Max(2, MomentumSlopeLookback);
            MinimumTrendSeparationRatio = ClampRatio(MinimumTrendSeparationRatio, 1e-5, 0.01);
            TargetTrendSeparationRatio = ClampRatio(TargetTrendSeparationRatio, MinimumTrendSeparationRatio, 0.02);
            MinimumTrendSlopeRatio = ClampRatio(MinimumTrendSlopeRatio, 1e-5, 0.01);
            MinimumMomentumSlopeRatio = ClampRatio(MinimumMomentumSlopeRatio, 1e-5, 0.01);
            ExitSeparationRatio = ClampRatio(ExitSeparationRatio, 1e-5, 0.01);
            AdxPeriod = Math.Max(5, AdxPeriod);
            MinimumAdx = Math.Clamp(MinimumAdx, 5.0, 60.0);
            AtrPeriod = Math.Max(5, AtrPeriod);
            AtrStopMultiplier = Math.Clamp(AtrStopMultiplier, 0.5, 5.0);
            AtrTakeProfitMultiplier = Math.Clamp(AtrTakeProfitMultiplier, AtrStopMultiplier * 1.1, 8.0);
            MaxPullbackAtr = Math.Clamp(MaxPullbackAtr, 0.2, 5.0);
            TriggerReEntryAtr = Math.Clamp(TriggerReEntryAtr, 0.1, 2.5);
            PullbackLookbackBars = Math.Max(3, PullbackLookbackBars);
            MinimumAlignmentRatio = Math.Clamp(MinimumAlignmentRatio, 0.3, 1.0);
            MinimumConfidence = Math.Clamp(MinimumConfidence, 0.35, 0.9);
            ReentryCooldownMinutes = Math.Clamp(ReentryCooldownMinutes, 5, 180);
            ExitCooldownMinutes = Math.Clamp(ExitCooldownMinutes, 1, 120);
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
