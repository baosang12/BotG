using System;
using System.Collections.Generic;
using System.Linq;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Phát hiện Change of Character (CHoCH): dấu hiệu đảo chiều xu hướng.
    /// </summary>
    public class ChangeOfCharacterDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.InternalChangeOfCharacter;
        public bool IsEnabled { get; set; } = true;
        private const int Lookback = 2;

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < Lookback)
                return false;

            var prev = bars[bars.Count - 2];
            var last = bars[bars.Count - 1];

            // Bullish CHoCH: bullish candle closes above prior high
            if (last.Close > prev.High && last.Close > last.Open)
            {
                signal = new SmartMoneySignal
                {
                    Type = Type,
                    Time = last.OpenTime,
                    IsBullish = true
                };
                return true;
            }
            // Bearish CHoCH: bearish candle closes below prior low
            if (last.Close < prev.Low && last.Close < last.Open)
            {
                signal = new SmartMoneySignal
                {
                    Type = Type,
                    Time = last.OpenTime,
                    IsBullish = false
                };
                return true;
            }

            return false;
        }
    }
}
