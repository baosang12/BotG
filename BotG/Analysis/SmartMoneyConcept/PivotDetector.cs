using System;
using System.Collections.Generic;
using System.Linq;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Phát hiện các điểm swing high/low (pivot) dựa trên window lookback.
    /// </summary>
    public class PivotDetector
    {
        /// <param name="bars">Danh sách bars (sắp xếp theo thời gian).</param>
        /// <param name="lookback">Số bars trước và sau dùng cho swing detection.</param>
        /// <returns>Danh sách Pivot (High hoặc Low).</returns>
        public List<Pivot> DetectSwings(IList<Bar> bars, int lookback)
        {
            // Validate input
            if (bars == null)
                throw new ArgumentNullException(nameof(bars));
            int window = lookback * 2 + 1;
            if (bars.Count < window)
                return new List<Pivot>();
            var pivots = new List<Pivot>();
            for (int i = lookback; i < bars.Count - lookback; i++)
            {
                var segment = bars.Skip(i - lookback).Take(window).ToList();
                var current = bars[i];
                bool isHigh = current.High == segment.Max(b => b.High);
                bool isLow = current.Low == segment.Min(b => b.Low);
                if (isHigh)
                {
                    pivots.Add(new Pivot
                    {
                        Type = PivotType.High,
                        Time = current.OpenTime,
                        Price = current.High,
                        Index = i,
                        IsMajor = false, // will be set later
                        IsMinor = false
                    });
                }
                if (isLow)
                {
                    pivots.Add(new Pivot
                    {
                        Type = PivotType.Low,
                        Time = current.OpenTime,
                        Price = current.Low,
                        Index = i,
                        IsMajor = false, // will be set later
                        IsMinor = false
                    });
                }
            }
            // Classify major pivots: those that cause BOS/CHoCH
            var detector = new StructureDetector();
            var events = detector.DetectStructure(pivots);
            var majorIndices = events.Select(e => e.Pivot.Index).ToHashSet();
            foreach (var p in pivots)
            {
                if (majorIndices.Contains(p.Index))
                {
                    p.IsMajor = true;
                    p.IsMinor = false;
                }
                else
                {
                    p.IsMajor = false;
                    p.IsMinor = true;
                }
            }
            return pivots;
        }
    }
}
