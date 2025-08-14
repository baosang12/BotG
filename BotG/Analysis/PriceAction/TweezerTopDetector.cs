using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
public class TweezerTopDetector : ICandlePatternDetector
    {
        public CandlePattern Pattern => CandlePattern.TweezerTop;
        public bool IsEnabled { get; set; } = true;
        public bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 2) return false;
            var prev = bars[bars.Count - 2];
            var last = bars[bars.Count - 1];
            if (prev.High == last.High && prev.Close > prev.Open && last.Close < last.Open)
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
