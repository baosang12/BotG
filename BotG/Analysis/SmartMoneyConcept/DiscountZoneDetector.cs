using System;
using System.Collections.Generic;
using System.Linq;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Detects Discount Zone (Demand) where price reverses from lower zone.
    /// </summary>
    public class DiscountZoneDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.DiscountZone;
        public bool IsEnabled { get; set; } = true;
        // configurable lookback and zone percent
        public int Lookback { get; set; } = 20;
        public double ZonePercent { get; set; } = 0.2;  // e.g. 0.2 = 20%
        private readonly Action<string> _logger;
        public DiscountZoneDetector(Action<string> logger = null)
        {
            _logger = logger ?? (msg => Console.WriteLine(msg));
        }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < Lookback)
                return false;
            var recent = bars.Skip(bars.Count - Lookback).Take(Lookback).ToList();
            double high = recent.Max(b => b.High);
            double low = recent.Min(b => b.Low);
            double range = high - low;
            double zoneTop = low + range * ZonePercent;
            var last = bars[bars.Count - 1];
            // detect bullish rejection in discount zone
            if (last.Low <= zoneTop && last.Close > last.Open)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = true };
                // Log khi phát hiện Discount Zone
                _logger($"[DiscountZoneDetector] Discount zone detected at {last.OpenTime:yyyy-MM-dd HH:mm}, price={last.Close}");
                return true;
            }
            return false;
        }
        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
