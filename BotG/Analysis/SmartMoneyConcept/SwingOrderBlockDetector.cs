using System;
using System.Collections.Generic;
using System.Linq;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Detects Swing Order Blocks based on last significant swing rejection.
    /// </summary>
public class SwingOrderBlockDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.SwingOrderBlock;
        public bool IsEnabled { get; set; } = true;

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (bars == null || bars.Count < 3)
                return false;
            int look = Math.Min(bars.Count - 1, 10);
            var prior = bars.Skip(bars.Count - 1 - look).Take(look).ToList();
            double swingHigh = prior.Max(b => b.High);
            double swingLow = prior.Min(b => b.Low);
            var last = bars[bars.Count - 1];
            // Bearish swing OB: close below swingHigh after a reject
            if (last.Close < swingHigh && last.Close < last.Open)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            // Bullish swing OB: close above swingLow after a reject
            if (last.Close > swingLow && last.Close > last.Open)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            return false;
        }
    }
}
