using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Detects Marubozu candles where wicks are minimal.
    /// </summary>
    public class MarubozuDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.Marubozu;
        public bool IsEnabled { get; set; } = true;
        /// <summary>
        /// Maximum wick size as percentage of total candle range (e.g., 0.05 = 5%).
        /// </summary>
        public double WickThreshold { get; set; } = 0.05;

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count == 0)
                return false;

            var last = bars[bars.Count - 1];
            double high = last.High;
            double low = last.Low;
            double open = last.Open;
            double close = last.Close;
            double range = high - low;
            if (range <= 0)
                return false;

            double upperWick = high - Math.Max(open, close);
            double lowerWick = Math.Min(open, close) - low;
            if (upperWick <= range * WickThreshold && lowerWick <= range * WickThreshold)
            {
                signal = new SmartMoneySignal
                {
                    Type = Type,
                    Time = last.OpenTime,
                    IsBullish = close > open,
                    Price = close
                };
                return true;
            }
            return false;
        }
    }
}
