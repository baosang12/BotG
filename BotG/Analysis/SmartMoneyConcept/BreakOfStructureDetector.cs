using System;
using System.Collections.Generic;
using System.Linq;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Phát hiện Break of Structure (BOS): phá vỡ mức swing high/low trước đó.
    /// </summary>
    public class BreakOfStructureDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.InternalBreakOfStructure;
        public bool IsEnabled { get; set; } = true;
        private const int Lookback = 5;

        public void Initialize() { }
        public void Start() { }
        public void Stop() { }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < Lookback)
                return false;

            var last = bars[bars.Count - 1];
            // Xác định swing bằng max/min trên chuỗi trước đó
            var window = bars.Take(bars.Count - 1).Reverse().Take(Lookback - 1).ToList();
            double priorMaxHigh = window.Max(b => b.High);
            double priorMinLow = window.Min(b => b.Low);

            // Bullish BOS: giá hiện tại phá lên trên priorMaxHigh
            if (last.High > priorMaxHigh)
            {
                signal = new SmartMoneySignal
                {
                    Type = Type,
                    Time = last.OpenTime,
                    IsBullish = true
                };
                return true;
            }
            // Bearish BOS: giá hiện tại phá xuống dưới priorMinLow
            if (last.Low < priorMinLow)
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
