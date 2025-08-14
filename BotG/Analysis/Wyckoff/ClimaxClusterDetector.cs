using System;
using System.Collections.Generic;
using System.Linq;
using DataFetcher.Models;

namespace Analysis.Wyckoff
{
    public static class ClimaxClusterDetector
    {
        /// <summary>
        /// Detects a selling climax cluster starting at startIdx over a sliding window.
        /// </summary>
        public static (ClimaxEvent clusterEvent, int endIdx)? DetectSellingCluster(
            int startIdx,
            IList<Bar> bars,
            IList<int> swings,
            IList<double> atr,
            int clusterWindowSize,
            double volumeMultiplier,
            double atrMultiplier,
            double marubozuRatio,
            double minScore,
            IList<Bar> prevBars,
            double avgVolume,
            double maxVolume,
            IList<double> localRanges,
            Action<string> logger)
        {
            double clusterScore = 0;
            // initial bar
            var bar = bars[startIdx];
            double range0 = bar.High - bar.Low;
            bool isVol0 = bar.Volume >= avgVolume * volumeMultiplier && bar.Volume >= maxVolume;
            double body0 = Math.Abs(bar.Close - bar.Open);
            bool isMar0 = range0 > 0 && (body0 / range0) >= marubozuRatio;
            bool isAtr0 = range0 >= atr[startIdx] * atrMultiplier || range0 >= localRanges.Average() * atrMultiplier;
            clusterScore += (isVol0 ? 1 : 0) + (isMar0 ? 1 : 0) + (isAtr0 ? 1 : 0);

            int end = startIdx + 1;
            for (; end < bars.Count && end <= startIdx + clusterWindowSize; end++)
            {
                var b2 = bars[end];
                if (b2.OpenTime.DayOfWeek == DayOfWeek.Saturday || b2.OpenTime.DayOfWeek == DayOfWeek.Sunday)
                    continue;
                double r2 = b2.High - b2.Low;
                double bd2 = Math.Abs(b2.Close - b2.Open);
                bool v2 = b2.Volume >= avgVolume * volumeMultiplier && b2.Volume >= maxVolume;
                bool m2 = r2 > 0 && (bd2 / r2) >= marubozuRatio;
                bool a2 = r2 >= atr[end] * atrMultiplier || r2 >= localRanges.Average() * atrMultiplier;
                int s2 = (v2 ? 1 : 0) + (m2 ? 1 : 0) + (a2 ? 1 : 0);
                if (s2 > 0) clusterScore += s2;
                else break;
            }
            if (clusterScore >= minScore && swings.Any(sw => sw >= startIdx && sw < end))
            {
                // find bar with max range
                int idxMax = startIdx;
                double maxR = bars[startIdx].High - bars[startIdx].Low;
                for (int k = startIdx + 1; k < end; k++)
                {
                    double r = bars[k].High - bars[k].Low;
                    if (r > maxR) { maxR = r; idxMax = k; }
                }
                var cb = bars[idxMax];
                logger?.Invoke($"[ClimaxClusterDetector] FOUND SellingClimax Cluster at idx={idxMax} time={cb.OpenTime:yyyy-MM-dd HH:mm} price={cb.Low} score={clusterScore}");
                var evt = new ClimaxEvent
                {
                    Index = idxMax,
                    Time = cb.OpenTime,
                    Price = cb.Low,
                    Type = ClimaxType.SellingClimax,
                    Bar = cb,
                    Score = clusterScore,
                    Swing = SwingType.Unknown
                };
                return (evt, end);
            }
            return null;
        }
    }
}
