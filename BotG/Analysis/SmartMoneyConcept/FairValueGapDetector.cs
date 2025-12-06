using System;
using System.Collections.Generic;
using Config;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    public class FairValueGapDetector : ISmartMoneyDetector
    {
        private readonly BotConfig _cfg;
        public FairValueGapDetector(BotConfig config) { _cfg = config; IsEnabled = _cfg.ShowFairValueGaps; }
        public SmartMoneyType Type => SmartMoneyType.FairValueGap;
        public bool IsEnabled { get; set; }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 2) return false;
            var prev = bars[bars.Count - 2]; var last = bars[bars.Count - 1];
            double gap = last.Low - prev.High;
            if (_cfg.FairValueGapsAutoThreshold ? gap >= Math.Abs(prev.Close - prev.Open)
                                                : gap >= _cfg.FairValueGapsExtend)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            gap = prev.Low - last.High;
            if (_cfg.FairValueGapsAutoThreshold ? gap >= Math.Abs(prev.Close - prev.Open)
                                                : gap >= _cfg.FairValueGapsExtend)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            return false;
        }
        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}