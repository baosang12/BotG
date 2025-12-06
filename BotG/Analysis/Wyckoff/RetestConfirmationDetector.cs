using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.Wyckoff
{
    public class RetestConfirmationEvent
    {
        public int Index { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public double Volume { get; set; }
        public bool IsValid { get; set; }
    }

    public class RetestConfirmationDetector
    {
        public double VolumeRatioThreshold { get; set; } = 0.7; // volume retest < 70% volume breakout
        public double PriceProximity { get; set; } = 0.01; // retest gần breakout (1%)

        private readonly Action<string> _logger;
        public RetestConfirmationDetector(double volumeRatioThreshold = 0.7, double priceProximity = 0.01, Action<string> logger = null)
        {
            VolumeRatioThreshold = volumeRatioThreshold;
            PriceProximity = priceProximity;
            _logger = logger;
        }

        /// <summary>
        /// Phát hiện LPS/LPSY sau breakout (SOS/UT).
        /// </summary>
        /// <param name="bars">Danh sách bar</param>
        /// <param name="breakout">StrengthSignalEvent (SOS/UT)</param>
        /// <returns>RetestConfirmationEvent hoặc null nếu không tìm thấy</returns>
        public RetestConfirmationEvent DetectRetest(IList<Bar> bars, StrengthSignalEvent breakout)
        {
            if (bars == null || breakout == null)
            {
                _logger?.Invoke($"[RetestConfirmationDetector] Input bars or breakout is null. bars: {(bars == null ? "null" : bars.Count.ToString())}, breakout: {(breakout == null ? "null" : breakout.Index.ToString())}");
                return null;
            }
            int start = breakout.Index + 1;
            int end = Math.Min(bars.Count, start + 20);
            double breakoutLevel = breakout.Price;
            double breakoutVolume = breakout.Volume;
            for (int i = start; i < end; i++)
            {
                var bar = bars[i];
                double priceDist = Math.Abs(bar.Close - breakoutLevel) / breakoutLevel;
                bool priceNear = priceDist <= PriceProximity;
                bool volumeLow = bar.Volume < breakoutVolume * VolumeRatioThreshold;
                bool noBreakStructure = bar.Low >= breakoutLevel; // bullish: không phá vỡ cấu trúc đã thiết lập
                _logger?.Invoke($"[RetestConfirmationDetector] Checking bar {i} ({bar.OpenTime}): priceDist={priceDist:F5}, priceNear={priceNear}, volumeLow={volumeLow}, noBreakStructure={noBreakStructure}");
                if (priceNear && volumeLow && noBreakStructure)
                {
                    _logger?.Invoke($"[RetestConfirmationDetector] Retest confirmed at bar {i} ({bar.OpenTime}), price={bar.Close}, volume={bar.Volume}");
                    return new RetestConfirmationEvent
                    {
                        Index = i,
                        Time = bar.OpenTime,
                        Price = bar.Close,
                        Volume = bar.Volume,
                        IsValid = true
                    };
                }
            }
            _logger?.Invoke($"[RetestConfirmationDetector] No retest confirmation found after breakout at {breakout.Index} ({breakout.Time})");
            return null;
        }
    }
}
