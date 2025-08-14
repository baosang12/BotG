using System;
using System.Collections.Generic;
using System.Linq;
using Analysis.SmartMoneyConcept;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Detects Premium Zone (Supply) where price reverses from upper zone.
    /// </summary>
    public class PremiumZoneDetector : ISmartMoneyDetector
    {
        public SmartMoneyType Type => SmartMoneyType.PremiumZone;
        public bool IsEnabled { get; set; } = true;
        private readonly Action<string> _logger;
        public PremiumZoneDetector(Action<string> logger = null)
        {
            _logger = logger ?? (msg => Console.WriteLine(msg));
        }
        // configurable lookback and zone percent
        public int Lookback { get; set; } = 20;
        public double ZonePercent { get; set; } = 0.2;  // e.g. 0.2 = 20%

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < Lookback)
                return false;
            var recent = bars.Skip(bars.Count - Lookback).Take(Lookback).ToList();
            double high = recent.Max(b => b.High);
            double low = recent.Min(b => b.Low);
            double range = high - low;
            double zoneBottom = high - range * ZonePercent;
            var last = bars[bars.Count - 1];
            // detect bearish rejection in premium zone
            if (last.High >= zoneBottom && last.Close < last.Open)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = false };
                // Log khi phát hiện Premium Zone
                _logger($"[PremiumZoneDetector] Premium zone detected at {last.OpenTime:yyyy-MM-dd HH:mm}, price={last.Close}");
                return true;
            }
            return false;
        }
        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
    }
}
