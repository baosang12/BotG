using System;
using System.Collections.Generic;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Detects Swing Change of Character: bearish to bullish or vice versa at swing points.
    /// </summary>
    public class SwingChangeOfCharacterDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.SwingChangeOfCharacter;
        public bool IsEnabled { get; set; } = true;
        private const int Lookback = 3;

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (bars == null || bars.Count < Lookback)
                return false;

            var p2 = bars[bars.Count - 3];
            var p1 = bars[bars.Count - 2];
            var last = bars[bars.Count - 1];

            // Bullish CHoCH: last closes above p1.High
            if (last.Close > p1.High && p1.High > p2.High)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            // Bearish CHoCH: last closes below p1.Low
            if (last.Close < p1.Low && p1.Low < p2.Low)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            return false;
        }
    }
}
