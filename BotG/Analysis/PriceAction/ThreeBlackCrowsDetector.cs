using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
public class ThreeBlackCrowsDetector : ICandlePatternDetector
    {
        public CandlePattern Pattern => CandlePattern.ThreeBlackCrows;
        public bool IsEnabled { get; set; } = true;
        public bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 3) return false;
            var b1 = bars[bars.Count - 3];
            var b2 = bars[bars.Count - 2];
            var b3 = bars[bars.Count - 1];
            if (b1.Close < b1.Open && b2.Close < b2.Open && b3.Close < b3.Open)
            {
                signal = new CandlePatternSignal { Pattern = Pattern, Time = b3.OpenTime, IsBullish = false };
                return true;
            }
            return false;
        }

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
