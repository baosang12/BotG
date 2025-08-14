using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
public class HaramiDetector : ICandlePatternDetector
    {
        public CandlePattern Pattern => CandlePattern.HaramiBullish;
        public bool IsEnabled { get; set; } = true;
        public bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 2) return false;
            var prev = bars[bars.Count - 2];
            var last = bars[bars.Count - 1];
            if (last.Open > last.Close && prev.Open < prev.Close && last.Open < prev.Close && last.Close > prev.Open)
            {
                signal = new CandlePatternSignal { Pattern = CandlePattern.HaramiBearish, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            if (last.Open < last.Close && prev.Open > prev.Close && last.Close < prev.Open && last.Open > prev.Close)
            {
                signal = new CandlePatternSignal { Pattern = CandlePattern.HaramiBullish, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            return false;
        }

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
