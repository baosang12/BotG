using System;
using System.Collections.Generic;
using System.Linq;
using DataFetcher.Models;

namespace Analysis.Wyckoff
{
    public class StrengthSignalEvent
    {
        public int Index { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public double Volume { get; set; }
        public bool IsBreakout { get; set; }
        public bool IsUT { get; set; } // Upthrust (bull trap)
        public bool IsValid { get; set; } // true nếu là SOS, false nếu là UT
    }

    public class StrengthSignalDetector
    {
        public double VolumeSpikeRatio { get; set; } = 1.3; // volume tăng ít nhất 30%
        public int Lookahead { get; set; } = 30; // số bar kiểm tra sau AR
        public int VolumeLookback { get; set; } = 10; // số bar lấy trung bình volume

        private readonly Action<string> _logger;
        public StrengthSignalDetector(double volumeSpikeRatio = 1.3, int lookahead = 30, int volumeLookback = 10, Action<string> logger = null)
        {
            VolumeSpikeRatio = volumeSpikeRatio;
            Lookahead = lookahead;
            VolumeLookback = volumeLookback;
            _logger = logger;
        }

        /// <summary>
        /// Phân biệt SOS (Sign of Strength) và UT (Upthrust) sau AR.
        /// </summary>
        /// <param name="bars">Danh sách bar</param>
        /// <param name="ar">AREvent</param>
        /// <returns>StrengthSignalEvent hoặc null nếu không tìm thấy</returns>
        public StrengthSignalEvent DetectStrengthSignal(IList<Bar> bars, AREvent ar)
        {
            if (bars == null || ar == null) {
                _logger?.Invoke($"[StrengthSignalDetector] Input bars or ar is null. bars: {(bars == null ? "null" : bars.Count.ToString())}, ar: {(ar == null ? "null" : ar.Index.ToString())}");
                return null;
            }
            int start = ar.Index + 1;
            int end = Math.Min(bars.Count, start + Lookahead);
            double arLevel = ar.Price;
            // Tính volume trung bình trước AR
            int volStart = Math.Max(0, ar.Index - VolumeLookback);
            double avgVolume = bars.Skip(volStart).Take(ar.Index - volStart).Select(b => b.Volume).DefaultIfEmpty(0).Average();
            for (int i = start; i < end; i++)
            {
                var bar = bars[i];
                bool breakout = bar.High > arLevel;
                bool isUT = breakout && bar.Close < arLevel;
                bool isSOS = breakout && bar.Close > arLevel;
                bool volumeSpike = bar.Volume > avgVolume * VolumeSpikeRatio;
                _logger?.Invoke($"[StrengthSignalDetector] Checking bar {i} ({bar.OpenTime}): breakout={breakout}, isUT={isUT}, isSOS={isSOS}, volume={bar.Volume}, avgVolume={avgVolume:0.##}, volumeSpike={volumeSpike}");
                if (breakout && volumeSpike)
                {
                    _logger?.Invoke($"[StrengthSignalDetector] Strength signal detected at bar {i} ({bar.OpenTime}), price={bar.Close}, isSOS={isSOS}, isUT={isUT}, volume={bar.Volume}");
                    return new StrengthSignalEvent
                    {
                        Index = i,
                        Time = bar.OpenTime,
                        Price = bar.Close,
                        Volume = bar.Volume,
                        IsBreakout = breakout,
                        IsUT = isUT,
                        IsValid = isSOS // chỉ true nếu là SOS
                    };
                }
            }
            _logger?.Invoke($"[StrengthSignalDetector] No strength signal found after AR at {ar.Index} ({ar.Time})");
            return null;
        }
    }
}
