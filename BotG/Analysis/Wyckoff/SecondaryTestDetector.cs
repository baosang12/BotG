using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.Wyckoff
{
    public class SecondaryTestEvent
    {
        public int Index { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public double Volume { get; set; }
        public double ClimaxVolume { get; set; }
        public double ClimaxPrice { get; set; }
        public bool IsValid { get; set; }
    }

    public class SecondaryTestDetector
    {
        public double VolumeRatioThreshold { get; set; } = 0.7; // ST volume < 70% Climax volume
        public double PriceProximity { get; set; } = 0.015; // ST gần Climax (1.5%)

        private readonly Action<string> _logger;
        public SecondaryTestDetector(double volumeRatioThreshold = 0.7, double priceProximity = 0.015, Action<string> logger = null)
        {
            VolumeRatioThreshold = volumeRatioThreshold;
            PriceProximity = priceProximity;
            _logger = logger;
        }

        /// <summary>
        /// Phát hiện Secondary Test sau Climax và AR.
        /// </summary>
        /// <param name="bars">Danh sách bar</param>
        /// <param name="climax">ClimaxEvent</param>
        /// <param name="ar">AREvent</param>
        /// <returns>SecondaryTestEvent hoặc null nếu không tìm thấy</returns>
        public SecondaryTestEvent DetectST(IList<Bar> bars, ClimaxEvent climax, AREvent ar)
        {
            if (bars == null || climax == null || ar == null) {
                _logger?.Invoke($"[SecondaryTestDetector] Input bars, climax, or ar is null. bars: {(bars == null ? "null" : bars.Count.ToString())}, climax: {(climax == null ? "null" : climax.Index.ToString())}, ar: {(ar == null ? "null" : ar.Index.ToString())}");
                return null;
            }
            int start = ar.Index + 1;
            int end = Math.Min(bars.Count, start + 20); // kiểm tra 20 bar sau AR
            for (int i = start; i < end; i++)
            {
                var bar = bars[i];
                double priceDist = Math.Abs(bar.Close - climax.Price) / climax.Price;
                bool priceNear = priceDist <= PriceProximity;
                bool volumeLow = bar.Volume < climax.Bar.Volume * VolumeRatioThreshold;
                bool noLowerLow = bar.Low >= climax.Bar.Low; // bullish: không tạo đáy thấp mới
                _logger?.Invoke($"[SecondaryTestDetector] Checking bar {i} ({bar.OpenTime}): priceDist={priceDist:F5}, priceNear={priceNear}, volumeLow={volumeLow}, noLowerLow={noLowerLow}");
                if (priceNear && volumeLow && noLowerLow)
                {
                    _logger?.Invoke($"[SecondaryTestDetector] ST confirmed at bar {i} ({bar.OpenTime}), price={bar.Close}, volume={bar.Volume}");
                    return new SecondaryTestEvent
                    {
                        Index = i,
                        Time = bar.OpenTime,
                        Price = bar.Close,
                        Volume = bar.Volume,
                        ClimaxVolume = climax.Bar.Volume,
                        ClimaxPrice = climax.Price,
                        IsValid = true
                    };
                }
            }
            _logger?.Invoke($"[SecondaryTestDetector] No ST found after AR at {ar.Index} ({ar.Time})");
            return null;
        }
    }
}
