using System;
using System.Collections.Generic;
using Config;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    public class TrendlineLiquidityDetector : ISmartMoneyDetector
    {
        private readonly BotConfig _cfg;
        public TrendlineLiquidityDetector(BotConfig config) { _cfg = config; IsEnabled = _cfg.ShowTrendlineLiquidity; }
        public SmartMoneyType Type => SmartMoneyType.TrendlineLiquidity;
        public bool IsEnabled { get; set; }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < _cfg.TrendlineLiquidityLookback) return false;
            int look = Math.Min(_cfg.TrendlineLiquidityLookback, bars.Count - 1);
            double high = double.MinValue;
            double low = double.MaxValue;
            for (int i = bars.Count - look - 1; i < bars.Count - 1; i++)
            {
                high = Math.Max(high, bars[i].High);
                low = Math.Min(low, bars[i].Low);
            }
            var last = bars[bars.Count - 1];
            if (last.High > high)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            if (last.Low < low)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            return false;
        }
    }
}
