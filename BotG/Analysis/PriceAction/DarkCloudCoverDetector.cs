using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
public class DarkCloudCoverDetector : ICandlePatternDetector
    {
        public CandlePattern Pattern => CandlePattern.DarkCloudCover;
        public bool IsEnabled { get; set; } = true;
        public bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 2) return false;
            var prev = bars[bars.Count - 2];
            var last = bars[bars.Count - 1];
            if (prev.Close > prev.Open && last.Close < last.Open && last.Open > prev.Close && last.Close < (prev.Open + prev.Close) / 2)
            {
                signal = new CandlePatternSignal { Pattern = Pattern, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            return false;
        }

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
