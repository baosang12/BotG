using System;
using System.Collections.Generic;
using System.Linq;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Swing Break of Structure: phá vỡ cấu trúc swing high/low.
    /// </summary>
public class SwingBreakOfStructureDetector : ISmartMoneyDetector
    {
        public void Initialize() { }
        public void Start() { }
        public void Stop() { }

        public SmartMoneyType Type => SmartMoneyType.SwingBreakOfStructure;
        public bool IsEnabled { get; set; } = true;
        private const int Lookback = 20;

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (bars == null || bars.Count < Lookback)
                return false;

            var last = bars[bars.Count - 1];
            var prior = bars.Take(bars.Count - 1).ToList();
            double maxHigh = prior.Max(b => b.High);
            double minLow = prior.Min(b => b.Low);

            if (last.High > maxHigh)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            if (last.Low < minLow)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            return false;
        }
    }
}
