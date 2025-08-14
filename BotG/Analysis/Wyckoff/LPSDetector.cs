using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.Wyckoff
{
    public class LPSEvent
    {
        public int Index { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public double Volume { get; set; }
        public bool IsLPSY { get; set; } // true nếu là LPSY (phân phối), false nếu là LPS (tích lũy)
        public bool IsValid { get; set; }
    }

    public class LPSDetector
    {
        public double VolumeRatioThreshold { get; set; } = 1.0; // volume hồi < 100% volume breakout (looser)
        public double PriceProximity { get; set; } = 0.01; // giá đóng cửa cách breakout <=1%
        public int LookaheadBars { get; set; } = 20; // số bar tiếp theo để tìm LPS/LPSY (extended lookahead)

        private readonly Action<string> _logger;
        public LPSDetector(double volumeRatioThreshold = 1.0, double priceProximity = 0.01, Action<string> logger = null)
        {
            VolumeRatioThreshold = volumeRatioThreshold;
            PriceProximity = priceProximity;
            _logger = logger;
        }

        /// <summary>
        /// Phát hiện LPS (sau SOS) hoặc LPSY (sau UT).
        /// </summary>
        /// <param name="bars">Danh sách bar</param>
        /// <param name="breakout">StrengthSignalEvent (SOS hoặc UT)</param>
        /// <param name="st">SecondaryTestEvent (đáy ST, nếu có)</param>
        /// <returns>LPSEvent hoặc null nếu không tìm thấy</returns>
        public LPSEvent DetectLPS(IList<Bar> bars, StrengthSignalEvent breakout, SecondaryTestEvent st = null)
        {
            if (bars == null || breakout == null) {
                _logger?.Invoke($"[LPSDetector] Input bars or breakout is null. bars: {(bars == null ? "null" : bars.Count.ToString())}, breakout: {(breakout == null ? "null" : breakout.Index.ToString())}");
                return null;
            }
            int start = breakout.Index + 1;
            int end = Math.Min(bars.Count, start + LookaheadBars);
            double breakoutLevel = breakout.Price;
            double breakoutVolume = breakout.Volume;
            for (int i = start; i < end; i++)
            {
                var bar = bars[i];
                double priceDist = Math.Abs(bar.Close - breakoutLevel) / breakoutLevel;
                bool priceNear = priceDist <= PriceProximity;
                bool volumeLow = bar.Volume < breakoutVolume * VolumeRatioThreshold;
                _logger?.Invoke($"[LPSDetector] Checking bar {i} ({bar.OpenTime}): priceDist={priceDist:F5}, priceNear={priceNear}, volumeLow={volumeLow}");
                // Relaxed: chỉ require priceNear & volumeLow
                if (priceNear)
                {
                    _logger?.Invoke($"[LPSDetector] LPS/LPSY detected at bar {i} ({bar.OpenTime}), price={bar.Close}, breakoutIsUT={breakout.IsUT}");
                    return new LPSEvent
                    {
                        Index = i,
                        Time = bar.OpenTime,
                        Price = bar.Close,
                        Volume = bar.Volume,
                        IsLPSY = breakout.IsUT,
                        IsValid = true
                    };
                }
            }
            _logger?.Invoke($"[LPSDetector] No LPS/LPSY event found within {LookaheadBars} bars after breakout at idx={breakout.Index} time={breakout.Time}");
            return null;
        }
    }
}
