using System;
using System.Collections.Generic;
using Config;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    public class BreakerBlockDetector : ISmartMoneyDetector
    {
        private readonly BotConfig _cfg;
        public BreakerBlockDetector(BotConfig config) { _cfg = config; IsEnabled = _cfg.ShowBreakerBlocks; }
        public SmartMoneyType Type => SmartMoneyType.BreakerBlock;
        public bool IsEnabled { get; set; }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < _cfg.BreakerBlockLookback) return false;
            int look = Math.Min(_cfg.BreakerBlockLookback, bars.Count - 1);
            double priorHigh = double.MinValue;
            double priorLow = double.MaxValue;
            for (int i = bars.Count - look - 1; i < bars.Count - 1; i++)
            {
                priorHigh = Math.Max(priorHigh, bars[i].High);
                priorLow = Math.Min(priorLow, bars[i].Low);
            }
            var last = bars[bars.Count - 1];
            if (last.Close > priorHigh && last.Open < priorHigh)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            if (last.Close < priorLow && last.Open > priorLow)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            return false;
        }
    }
}
