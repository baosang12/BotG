using System;
using System.Collections.Generic;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Detects Equal Lows where last two lows are within a small threshold.
    /// </summary>
    public class EqualLowsDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.EqualLows;
        public bool IsEnabled { get; set; } = true;
        private const int Lookback = 2;
        private const double Threshold = 0.1; // as ratio of range

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (bars == null || bars.Count < Lookback)
                return false;

            var prev = bars[bars.Count - 2];
            var last = bars[bars.Count - 1];
            double range = last.High - last.Low;
            if (range <= 0)
                return false;

            if (Math.Abs(last.Low - prev.Low) <= range * Threshold)
            {
                signal = new SmartMoneySignal
                {
                    Type = Type,
                    Time = last.OpenTime,
                    IsBullish = last.Close >= last.Open
                };
                return true;
            }
            return false;
        }
    }
}
