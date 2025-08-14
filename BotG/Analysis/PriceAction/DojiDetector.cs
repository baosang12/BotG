using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
public class DojiDetector : ICandlePatternDetector
    {
        public CandlePattern Pattern => CandlePattern.Doji;
        public bool IsEnabled { get; set; } = true;
        public bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 1) return false;
            var last = bars[bars.Count - 1];
            double body = Math.Abs(last.Close - last.Open);
            double range = last.High - last.Low;
            if (body < range * 0.1)
            {
                signal = new CandlePatternSignal { Pattern = Pattern, Time = last.OpenTime, IsBullish = last.Close > last.Open };
                return true;
            }
            return false;
        }

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
