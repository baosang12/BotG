using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
    public class PinBarDetector : ICandlePatternDetector
    {
        public CandlePattern Pattern => CandlePattern.PinBarBullish;
        public bool IsEnabled { get; set; } = true;
        public bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 1) return false;
            var last = bars[bars.Count - 1];
            double body = Math.Abs(last.Close - last.Open);
            double upperWick = last.High - Math.Max(last.Close, last.Open);
            double lowerWick = Math.Min(last.Close, last.Open) - last.Low;
            if (lowerWick > body * 2 && body < atr * 0.5)
            {
                signal = new CandlePatternSignal { Pattern = Pattern, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            return false;
        }

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
