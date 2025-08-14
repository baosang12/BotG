using System;
using System.Collections.Generic;
using Config;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    public class InternalOrderBlockDetector : ISmartMoneyDetector
    {
        private readonly BotConfig _cfg;
        public InternalOrderBlockDetector(BotConfig config) { _cfg = config; IsEnabled = _cfg.ShowInternalOrderBlocks; }
        public SmartMoneyType Type => SmartMoneyType.InternalOrderBlock;
        public bool IsEnabled { get; set; }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 2) return false;
            var prev = bars[bars.Count - 2];
            var last = bars[bars.Count - 1];
            int size = Math.Min(_cfg.InternalOrderBlocksSize, bars.Count - 1);
            double atr = Math.Abs(prev.High - prev.Low);
            bool passFilter = _cfg.OrderBlockFilter == "Atr"
                ? atr >= _cfg.InternalOrderBlocksSize
                : true; // cumulative not implemented
            if (!passFilter) return false;
            if (prev.Close < prev.Open && last.Close > last.Open
                && last.Close < prev.Open && last.Open > prev.Close)
            {
                signal = new SmartMoneySignal { Type=Type, Time= last.OpenTime, IsBullish=true };
                return true;
            }
            if (prev.Close > prev.Open && last.Close < last.Open
                && last.High < prev.Close && last.Open < prev.Open)
            {
                signal = new SmartMoneySignal { Type=Type, Time= last.OpenTime, IsBullish=false };
                return true;
            }
            return false;
        }
        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
