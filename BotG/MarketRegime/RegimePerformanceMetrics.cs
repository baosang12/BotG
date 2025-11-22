using System;

namespace BotG.MarketRegime
{
    /// <summary>
    /// Captures lightweight telemetry for ATR calculations to validate caching effectiveness.
    /// </summary>
    public class RegimePerformanceMetrics
    {
        public TimeSpan LastAtrCalculationTime { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public long TotalCalculations { get; set; }
        public int CacheSize { get; set; }

        public double CacheHitRatio
        {
            get
            {
                int totalLookups = CacheHits + CacheMisses;
                return totalLookups > 0 ? (double)CacheHits / totalLookups : 0.0;
            }
        }

        public void Reset()
        {
            LastAtrCalculationTime = TimeSpan.Zero;
            CacheHits = 0;
            CacheMisses = 0;
            TotalCalculations = 0;
            CacheSize = 0;
        }
    }
}
