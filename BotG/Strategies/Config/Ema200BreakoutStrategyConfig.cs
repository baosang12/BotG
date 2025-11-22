using System;

namespace Strategies.Config
{
    /// <summary>
    /// Tham số điều khiển chiến lược breakout theo EMA200 cho swing timeframe.
    /// </summary>
    public sealed class Ema200BreakoutStrategyConfig
    {
        public string TriggerTimeframe { get; set; } = "H1";
        public int EmaPeriod { get; set; } = 200;
        public int AtrPeriod { get; set; } = 14;
        public int AdxPeriod { get; set; } = 14;
        public double MinimumAtr { get; set; } = 0.0008;
        public double MinimumAdx { get; set; } = 22.0;
        public double BreakoutBuffer { get; set; } = 0.0003;
        public double MinimumDistanceAtrMultiple { get; set; } = 0.25;
        public double AtrStopMultiplier { get; set; } = 1.6;
        public double AtrTakeProfitMultiplier { get; set; } = 3.2;
        public double CooldownMinutes { get; set; } = 90.0;
        public int MinimumBars { get; set; } = 240;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(TriggerTimeframe))
            {
                throw new ArgumentException("TriggerTimeframe is required", nameof(TriggerTimeframe));
            }

            if (EmaPeriod <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
            }

            if (AtrPeriod <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(AtrPeriod));
            }

            if (AdxPeriod <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(AdxPeriod));
            }

            if (MinimumAtr <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumAtr));
            }

            if (MinimumAdx <= 0 || MinimumAdx > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumAdx));
            }

            if (BreakoutBuffer < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(BreakoutBuffer));
            }

            if (MinimumDistanceAtrMultiple <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumDistanceAtrMultiple));
            }

            if (AtrStopMultiplier <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(AtrStopMultiplier));
            }

            if (AtrTakeProfitMultiplier <= AtrStopMultiplier)
            {
                throw new ArgumentOutOfRangeException(nameof(AtrTakeProfitMultiplier));
            }

            if (CooldownMinutes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(CooldownMinutes));
            }

            if (MinimumBars < EmaPeriod + 5)
            {
                MinimumBars = EmaPeriod + 5;
            }
        }
    }
}
