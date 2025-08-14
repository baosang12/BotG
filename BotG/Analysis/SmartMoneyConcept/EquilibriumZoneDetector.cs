using System;
using System.Collections.Generic;
using System.Linq;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Detects Equilibrium Zone where price gravitates around mid-range.
    /// </summary>
    public class EquilibriumZoneDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.EquilibriumZone;
        public bool IsEnabled { get; set; } = true;
        private const int Lookback = 20;
        private const double ThresholdRatio = 0.1;

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (bars == null || bars.Count < Lookback)
                return false;

            var recent = bars.Skip(bars.Count - Lookback).Take(Lookback).ToList();
            double high = recent.Max(b => b.High);
            double low = recent.Min(b => b.Low);
            double mid = (high + low) / 2;
            double threshold = (high - low) * ThresholdRatio;
            var last = bars.Last();

            if (Math.Abs(last.Close - mid) <= threshold)
            {
                bool bullish = last.Close > mid;
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = bullish };
                return true;
            }
            return false;
        }
        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
