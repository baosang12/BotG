using System;

namespace Strategies.Config
{
    public class BreakoutStrategyConfig
    {
        public double MinimumStrength { get; set; } = 0.35;
        public double VolumeMultiplier { get; set; } = 1.8;
        public int RetestWindowBars { get; set; } = 3;
        public int MaxBreakoutBars { get; set; } = 2;
        public double TouchTolerancePercent { get; set; } = 0.2;
        public double WeeklyVolumeThreshold { get; set; } = 0.2;
        public int OrderBlockDensityMin { get; set; } = 1;
        public int TrendEmaFast { get; set; } = 50;
        public int TrendEmaSlow { get; set; } = 200;
        public double MaximumRetestPercent { get; set; } = 0.5;
        public double AtrConfirmationMultiplier { get; set; } = 0.25;
        public int AtrPeriod { get; set; } = 14;
        public int VolumeSmaPeriod { get; set; } = 20;
        public int TouchLookbackBars { get; set; } = 120;
        public int MinimumTouches { get; set; } = 3;
        public int MinimumH1Bars { get; set; } = 20;
        public int MinimumM15Bars { get; set; } = 48;
        public bool EnableMultiTimeframeConfirmation { get; set; } = true;
        public double MinimumConfirmationThreshold { get; set; } = 0.7;

        public void Validate()
        {
            if (MinimumStrength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumStrength));
            }

            if (VolumeMultiplier <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(VolumeMultiplier));
            }

            if (RetestWindowBars <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(RetestWindowBars));
            }

            if (MaxBreakoutBars <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxBreakoutBars));
            }

            if (TouchTolerancePercent <= 0 || TouchTolerancePercent > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(TouchTolerancePercent));
            }

            if (WeeklyVolumeThreshold <= 0 || WeeklyVolumeThreshold > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(WeeklyVolumeThreshold));
            }

            if (OrderBlockDensityMin < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(OrderBlockDensityMin));
            }

            if (TrendEmaFast <= 0 || TrendEmaSlow <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(TrendEmaFast));
            }

            if (TrendEmaFast >= TrendEmaSlow)
            {
                throw new ArgumentException("Fast EMA must be shorter than slow EMA.");
            }

            if (MaximumRetestPercent <= 0 || MaximumRetestPercent > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumRetestPercent));
            }

            if (AtrConfirmationMultiplier <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(AtrConfirmationMultiplier));
            }

            if (AtrPeriod <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(AtrPeriod));
            }

            if (VolumeSmaPeriod <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(VolumeSmaPeriod));
            }

            if (TouchLookbackBars <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(TouchLookbackBars));
            }

            if (MinimumTouches <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumTouches));
            }

            if (MinimumH1Bars <= 0 || MinimumM15Bars <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumH1Bars));
            }

            if (MinimumConfirmationThreshold < 0 || MinimumConfirmationThreshold > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumConfirmationThreshold));
            }
        }
    }
}
