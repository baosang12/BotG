using System;
using System.Collections.Generic;
using System.Linq;
using DataFetcher.Models;

namespace Analysis.Wyckoff
{
    public enum ClimaxType
    {
        None,
        BuyingClimax,
        SellingClimax
    }

    public enum SwingType
    {
        Unknown,
        High,
        Low
    }

    public class ClimaxEvent
    {
        public int Index { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public ClimaxType Type { get; set; }
        public Bar Bar { get; set; }
        public double Score { get; set; } // Đánh giá mức độ mạnh/yếu
        public SwingType Swing { get; set; } // Phân biệt swing high/low
    public bool IsCluster { get; set; } // Climax hình thành từ cụm (cluster) hay đơn lẻ
    }

    public class ClimaxDetector
    {
        public int Lookback { get; set; } = 20;
        // Relaxed thresholds: require weaker spikes
        public double VolumeSpikeMultiplier { get; set; } = 1.5;
        public double AtrMultiplier { get; set; } = 1.2;
        public double MarubozuBodyRatio { get; set; } = 0.6; // >=60% thân nến
        // Number of bars to cluster for climax detection (e.g., 6-7 for H1 timeframe)
        public int ClusterWindowSize { get; set; } = 6;
        // Minimum aggregated score for cluster to qualify as Selling Climax
    public double SellingClimaxClusterMinScore { get; set; } = 3; // raised to reduce weak clusters
        // Enable cluster-based detection for Selling Climax
        public bool UseCluster { get; set; } = true;

    // --- New consecutive bearish cluster SC mode (2-4 red bars then green AR) ---
    public bool UseConsecutiveBearScMode { get; set; } = true; // bật logic mới
    public int ScClusterMinBars { get; set; } = 2; // tối thiểu 2 nến đỏ
    public int ScClusterMaxBars { get; set; } = 4; // tối đa 4 nến đỏ liên tiếp
    public double ScClusterVolSpike { get; set; } = 1.3; // tổng vol / avgVol >= 1.3
    public double ScClusterRangeSpike { get; set; } = 1.2; // avg range hoặc max range / avgLocalRange >= 1.2
    public double ScClusterBodyMin { get; set; } = 0.5; // ít nhất 1 nến bodyRatio >= 50%
    public double ArBodyMin { get; set; } = 0.5; // nến xanh AR body >=50% range
    public int ArMaxWaitBars { get; set; } = 0; // 0 = chỉ chấp nhận nến xanh ngay sau cụm
    public bool ScRequireSwing { get; set; } = false; // tùy chọn yêu cầu trùng swing low
    public bool LogNewScMode { get; set; } = true; // bật log chi tiết

        private readonly Action<string> _logger;
        public ClimaxDetector(int lookback = 20, double volumeSpikeMultiplier = 1.5, double atrMultiplier = 1.2, double marubozuBodyRatio = 0.6, Action<string> logger = null)
        {
            Lookback = lookback;
            VolumeSpikeMultiplier = volumeSpikeMultiplier;
            AtrMultiplier = atrMultiplier;
            MarubozuBodyRatio = marubozuBodyRatio;
            _logger = logger;
        }

        /// <summary>
        /// Tìm các sự kiện Buying Climax/Selling Climax tại swing high/low.
        /// </summary>
        /// <param name="bars">Danh sách bar (cần đủ dữ liệu volume, high, low, close, open)</param>
        /// <param name="swings">Danh sách index swing high/low (ví dụ: output từ module cấu trúc thị trường)</param>
        /// <param name="atr">ATR đã tính sẵn (cùng độ dài với bars)</param>
        /// <param name="swingTypes">(Optional) Danh sách loại swing tại mỗi index (High/Low), cùng độ dài với bars hoặc null</param>
        /// <returns>Danh sách ClimaxEvent</returns>
        public List<ClimaxEvent> DetectClimax(IList<Bar> bars, IList<int> swings, IList<double> atr, IList<SwingType> swingTypes = null)
        {
            var result = new List<ClimaxEvent>();
            // Only require bars and atr; ignore swings list
            if (bars == null || atr == null) return result;
            if (bars.Count < Lookback + 2 || atr.Count != bars.Count) return result;
            // Pass 1: Buying Climax detection (giữ nguyên logic cũ)
            for (int i = Lookback; i < bars.Count; i++)
            {
                var bar = bars[i];
                if (bar.OpenTime.DayOfWeek == DayOfWeek.Saturday || bar.OpenTime.DayOfWeek == DayOfWeek.Sunday) continue;
                var prevBars = bars.Skip(i - Lookback).Take(Lookback).ToList();
                var prevVolumes = prevBars.Select(b => b.Volume).ToList();
                var avgVolume = prevVolumes.Average();
                var maxVolume = prevVolumes.Max();
                var barAtr = atr[i];
                double body = Math.Abs(bar.Close - bar.Open);
                double range = bar.High - bar.Low;
                var ranges = prevBars.Select(b => b.High - b.Low).ToList();
                var localRanges = ranges.Skip(Math.Max(0, ranges.Count - 4)).ToList();
                var avgLocalRange = localRanges.Average();
                bool isMarubozu = range > 0 && (body / range) >= MarubozuBodyRatio;
                bool isVolumeSpike = bar.Volume >= avgVolume * VolumeSpikeMultiplier && bar.Volume >= maxVolume;
                bool isAtrSpike = range >= barAtr * AtrMultiplier || range >= avgLocalRange * AtrMultiplier;
                double score = 0; if (isVolumeSpike) score++; if (isMarubozu) score++; if (isAtrSpike) score++;
                if (isMarubozu && bar.Close > bar.Open && score >= 2)
                {
                    var recent = bars.Skip(Math.Max(0, i - Lookback)).Take(Lookback).ToList();
                    double highRec = recent.Max(b => b.High);
                    double lowRec = recent.Min(b => b.Low);
                    double zoneBottom = highRec - (highRec - lowRec) * 0.2;
                    if (bar.Close >= zoneBottom)
                    {
                        _logger?.Invoke($"[ClimaxDetector] FOUND BuyingClimax at idx={i} time={bar.OpenTime:yyyy-MM-dd HH:mm} price={bar.High} score={score} in PremiumZone (scoreFilter)");
                        result.Add(new ClimaxEvent { Index = i, Time = bar.OpenTime, Price = bar.High, Type = ClimaxType.BuyingClimax, Bar = bar, Score = score, Swing = SwingType.Unknown });
                    }
                }
            }

            // Pass 2: Selling Climax - new consecutive bearish cluster mode
            if (UseConsecutiveBearScMode)
            {
                int i = Lookback;
                while (i < bars.Count)
                {
                    var bar = bars[i];
                    if (bar.OpenTime.DayOfWeek == DayOfWeek.Saturday || bar.OpenTime.DayOfWeek == DayOfWeek.Sunday) { i++; continue; }
                    if (bar.Close < bar.Open) // start/continue a bearish run
                    {
                        int start = i;
                        var idxList = new List<int>();
                        while (i < bars.Count && bars[i].Close < bars[i].Open && idxList.Count < ScClusterMaxBars)
                        {
                            if (!(bars[i].OpenTime.DayOfWeek == DayOfWeek.Saturday || bars[i].OpenTime.DayOfWeek == DayOfWeek.Sunday))
                                idxList.Add(i);
                            i++;
                        }
                        // evaluate cluster
                        int len = idxList.Count;
                        if (len >= ScClusterMinBars)
                        {
                            int clusterStart = idxList[0];
                            int clusterEndExclusive = idxList[len - 1] + 1; // after last bearish
                            var prevBars = bars.Skip(clusterStart - Lookback).Take(Lookback).ToList();
                            if (clusterStart - Lookback < 0) { /* not enough history */ }
                            if (clusterStart - Lookback >= 0)
                            {
                                double avgVol = prevBars.Average(b => b.Volume);
                                double totalVol = idxList.Sum(ix => bars[ix].Volume);
                                double volSpikeRatio = avgVol > 0 ? (totalVol / (avgVol * len)) : 0; // normalize by len
                                var rangesPrev = prevBars.Select(b => b.High - b.Low).ToList();
                                double avgLocalRange = rangesPrev.Skip(Math.Max(0, rangesPrev.Count - 4)).DefaultIfEmpty(0).Average();
                                double maxRange = idxList.Max(ix => bars[ix].High - bars[ix].Low);
                                double avgRangeCluster = idxList.Average(ix => bars[ix].High - bars[ix].Low);
                                double rangeSpikeRatio = avgLocalRange > 0 ? Math.Max(maxRange / avgLocalRange, avgRangeCluster / avgLocalRange) : 0;
                                double maxBodyRatio = idxList.Max(ix => { var b = bars[ix]; double rg = b.High - b.Low; double bd = Math.Abs(b.Close - b.Open); return rg > 0 ? bd / rg : 0; });
                                bool bodyOk = maxBodyRatio >= ScClusterBodyMin;
                                bool volOk = volSpikeRatio >= ScClusterVolSpike;
                                bool rangeOk = rangeSpikeRatio >= ScClusterRangeSpike;
                                bool swingOk = !ScRequireSwing || idxList.Any(ix => swings != null && swings.Contains(ix));
                                if (LogNewScMode)
                                {
                                    _logger?.Invoke($"[ClimaxDetector] SC_CLUSTER cand={len} idx[{clusterStart}->{idxList[len-1]}] volSpikeRatio={volSpikeRatio:0.00} rangeSpikeRatio={rangeSpikeRatio:0.00} maxBodyRatio={maxBodyRatio:0.00} bodyOk={bodyOk} volOk={volOk} rangeOk={rangeOk} swingOk={swingOk}");
                                }
                                if (bodyOk && volOk && rangeOk && swingOk)
                                {
                                    // candidate SC = last bearish in cluster (or widest bearish)
                                    int scIdx = idxList[len - 1];
                                    // Prefer widest bearish body inside cluster
                                    double bestScore = -1; int bestIdx = scIdx;
                                    foreach (var ix in idxList)
                                    {
                                        var b = bars[ix];
                                        double rg = b.High - b.Low; double bd = Math.Abs(b.Close - b.Open);
                                        double br = rg > 0 ? bd / rg : 0;
                                        double comp = (bd) + (br * 0.5); // heuristic
                                        if (comp > bestScore) { bestScore = comp; bestIdx = ix; }
                                    }
                                    scIdx = bestIdx;
                                    var scBar = bars[scIdx];
                                    double scScore = 1; // base
                                    if (volOk) scScore += 1; if (rangeOk) scScore += 1; if (bodyOk) scScore += 1;
                                    _logger?.Invoke($"[ClimaxDetector] FOUND SellingClimax (ConsecutiveCluster) idx={scIdx} time={scBar.OpenTime:yyyy-MM-dd HH:mm} price={scBar.Low} len={len} score={scScore} volRatio={volSpikeRatio:0.00} rangeRatio={rangeSpikeRatio:0.00} maxBody={maxBodyRatio:0.00}");
                                    result.Add(new ClimaxEvent { Index = scIdx, Time = scBar.OpenTime, Price = scBar.Low, Type = ClimaxType.SellingClimax, Bar = scBar, Score = scScore, Swing = SwingType.Unknown, IsCluster = true });
                                    // Optional AR immediate confirmation check
                                    if (ArMaxWaitBars >= 0)
                                    {
                                        int nextIdx = scIdx + 1;
                                        if (nextIdx < bars.Count)
                                        {
                                            var arBar = bars[nextIdx];
                                            bool isBull = arBar.Close > arBar.Open;
                                            double rg2 = arBar.High - arBar.Low; double bd2 = Math.Abs(arBar.Close - arBar.Open);
                                            double bodyRatio2 = rg2 > 0 ? bd2 / rg2 : 0;
                                            if (isBull && bodyRatio2 >= ArBodyMin && nextIdx <= scIdx + 1 + ArMaxWaitBars)
                                            {
                                                _logger?.Invoke($"[ClimaxDetector] AR_CONFIRM after SC at idx={scIdx} -> arIdx={nextIdx} bodyRatio={bodyRatio2:0.00}");
                                            }
                                            else if (isBull && bodyRatio2 < ArBodyMin && LogNewScMode)
                                            {
                                                _logger?.Invoke($"[ClimaxDetector] AR_REJECT weak body after SC idx={scIdx} -> arIdx={nextIdx} bodyRatio={bodyRatio2:0.00}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        // if cluster length hit max but next bar still bearish (len==max and we truncated), DO NOT treat as SC yet -> continue scanning from current i
                        continue; // do not i++ here (already advanced)
                    }
                    i++; // advance when not in bearish run
                }
            }
            else
            {
                // Fallback to legacy cluster/simple SellingClimax logic if new mode tắt
                for (int i = Lookback; i < bars.Count; i++)
                {
                    var bar = bars[i];
                    if (bar.OpenTime.DayOfWeek == DayOfWeek.Saturday || bar.OpenTime.DayOfWeek == DayOfWeek.Sunday) continue;
                    if (bar.Close >= bar.Open) continue; // only bearish candidates
                    var prevBars = bars.Skip(i - Lookback).Take(Lookback).ToList();
                    var prevVolumes = prevBars.Select(b => b.Volume).ToList();
                    var avgVolume = prevVolumes.Average();
                    var maxVolume = prevVolumes.Max();
                    double body = Math.Abs(bar.Close - bar.Open);
                    double range = bar.High - bar.Low;
                    bool isMaru = range > 0 && (body / range) >= MarubozuBodyRatio;
                    var ranges = prevBars.Select(b => b.High - b.Low).ToList();
                    var localRanges = ranges.Skip(Math.Max(0, ranges.Count - 4)).ToList();
                    bool isVolumeSpike = bar.Volume >= avgVolume * VolumeSpikeMultiplier && bar.Volume >= maxVolume;
                    bool isAtrSpike = range >= atr[i] * AtrMultiplier || range >= localRanges.Average() * AtrMultiplier;
                    double score = (isVolumeSpike?1:0)+(isMaru?1:0)+(isAtrSpike?1:0);
                    if (UseCluster)
                    {
                        double clusterScore = score;
                        int end = i + 1;
                        for (; end < bars.Count && end <= i + ClusterWindowSize; end++)
                        {
                            var b2 = bars[end];
                            if (b2.OpenTime.DayOfWeek == DayOfWeek.Saturday || b2.OpenTime.DayOfWeek == DayOfWeek.Sunday) continue;
                            double body2 = Math.Abs(b2.Close - b2.Open);
                            double range2 = b2.High - b2.Low;
                            bool isVol2 = b2.Volume >= avgVolume * VolumeSpikeMultiplier && b2.Volume >= maxVolume;
                            bool isMaru2 = range2 > 0 && (body2 / range2) >= MarubozuBodyRatio;
                            bool isAtr2 = range2 >= atr[end] * AtrMultiplier || range2 >= localRanges.Average() * AtrMultiplier;
                            int s2 = (isVol2?1:0)+(isMaru2?1:0)+(isAtr2?1:0);
                            if (s2>0) clusterScore += s2; else break;
                        }
                        if (clusterScore >= SellingClimaxClusterMinScore && swings.Any(sw => sw >= i && sw < end))
                        {
                            int idxMaxAny = i; double maxRangeAny = bars[i].High - bars[i].Low; int idxMaxBear = bars[i].Close < bars[i].Open ? i : -1; double maxRangeBear = idxMaxBear==i?maxRangeAny:0;
                            for (int k=i+1;k<end;k++)
                            { double r=bars[k].High - bars[k].Low; if (r>maxRangeAny){maxRangeAny=r;idxMaxAny=k;} if (bars[k].Close<bars[k].Open && r>maxRangeBear){maxRangeBear=r;idxMaxBear=k;} }
                            int chosenIdx = idxMaxBear!=-1?idxMaxBear:idxMaxAny; if (!(bars[chosenIdx].Close<bars[chosenIdx].Open)) chosenIdx=i; var cb=bars[chosenIdx];
                            _logger?.Invoke($"[ClimaxDetector] FOUND SellingClimax Cluster (legacy) idx={chosenIdx} time={cb.OpenTime:yyyy-MM-dd HH:mm} price={cb.Low} score={clusterScore}");
                            result.Add(new ClimaxEvent{Index=chosenIdx,Time=cb.OpenTime,Price=cb.Low,Type=ClimaxType.SellingClimax,Bar=cb,Score=clusterScore,Swing=SwingType.Unknown});
                        }
                        i = end -1;
                    }
                    else if (swings.Contains(i) && isMaru && score >= 3)
                    {
                        _logger?.Invoke($"[ClimaxDetector] FOUND SellingClimax (legacy simple) idx={i} time={bar.OpenTime:yyyy-MM-dd HH:mm} price={bar.Low} score={score}");
                        result.Add(new ClimaxEvent{Index=i,Time=bar.OpenTime,Price=bar.Low,Type=ClimaxType.SellingClimax,Bar=bar,Score=score,Swing=SwingType.Unknown});
                    }
                }
            }
            // Sort events by index to keep chronological consistency
            result = result.OrderBy(e => e.Index).ToList();
            _logger?.Invoke($"[ClimaxDetector] Total events found: {result.Count}");
            return result;
        }
    }
}
