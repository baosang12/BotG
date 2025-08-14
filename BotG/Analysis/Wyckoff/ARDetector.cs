using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using DataFetcher.Models;

namespace Analysis.Wyckoff
{
    // AR (Automatic Rally) hoặc ARc (Automatic Reaction) là sự kiện thuộc Phase A của Wyckoff
    public class AREvent
    {
        public int Index { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; } // Có thể là High/Low/Close tuỳ hướng
        public bool IsRally { get; set; } // true: Rally, false: Reaction
        public Bar Bar { get; set; }
        public int ClimaxIndex { get; set; }
        public double Score { get; set; }
    // --- Enriched metrics ---
    public double MoveFrac { get; set; }           // |move| / climax range
    public double MoveAtr { get; set; }            // |move| / ATR (nếu có)
    public double BodyRatio { get; set; }          // body / range của AR bar
    public double VolumeRatio { get; set; }        // volume / avgVolume
    public string Tier { get; set; }               // Tiny / Normal / Deep
    public bool StructureBreak { get; set; }       // phá micro high/low cụm climax
    public bool Immediate { get; set; }            // có phải bar ngay sau climax
    public bool IsClusterAr { get; set; }          // AR/ARc dạng cụm 2-3 bar
    public int ClusterSize { get; set; }           // số bar trong cụm
    }

    public class ARDetector
    {
        // Volume multiplier loaded from systemRules.json
        public double AtrMultiplier { get; set; }
        public double MinBodyRatio { get; set; } = 0.5; // thân nến >= MinBodyRatio * range
        public int LookaheadBars { get; set; } = 10; // số bar tối đa sau climax để tìm AR
        public bool DebugMode { get; set; } = true;
    // New configurable thresholds
    public double TinyFracMax { get; set; } = 0.30;     // MoveFrac < TinyFracMax => Tiny
    public double DeepFracMin { get; set; } = 0.60;     // MoveFrac >= DeepFracMin => Deep
    public double BreakBufferFrac { get; set; } = 0.05; // thêm 5% climax range làm buffer phá cấu trúc
    public double MinVolumeRatio { get; set; } = 0.0;   // 0 = tắt filter volume (dùng log)
    public int VolumeLookback { get; set; } = 10;       // lookback tính avgVolume
    public double MinAtrMove { get; set; } = 0.0;       // yêu cầu tối thiểu theo ATR (0 = tắt)
    public bool PreferImmediate { get; set; } = true;   // ưu tiên bar ngay sau climax nếu đạt body & direction
    public bool ReturnImmediateIfValid { get; set; } = true; // nếu true: trả về ngay nến đầu tiên đạt điều kiện (AR/ARc tức thì)
    // Adaptive body ratio theo thanh khoản (ATR thấp / cao)
    public double AtrLowFactor { get; set; } = 0.7;
    public double AtrHighFactor { get; set; } = 1.3;
    public double LowLiquidityBodyRelax { get; set; } = 0.1; // giảm yêu cầu body khi ATR thấp
    public double HighLiquidityBodyAdd { get; set; } = 0.05;  // tăng yêu cầu body khi ATR cao

        private readonly Action<string> _logger;
        public ARDetector(double atrMultiplier = double.NaN, double minBodyRatio = 0.5, Action<string> logger = null)
        {
            // Load default multiplier from config if not provided
            double configMult = LoadVolumeMultiplierFromConfig();
            AtrMultiplier = double.IsNaN(atrMultiplier) ? configMult : atrMultiplier;
            MinBodyRatio = minBodyRatio;
            _logger = logger;
        }

        /// <summary>
        /// Phát hiện AR (Automatic Rally) hoặc ARc (Automatic Reaction) sau ClimaxEvent. Sự kiện này thuộc Phase A của Wyckoff.
        /// </summary>
        /// <param name="bars">Danh sách bar</param>
        /// <param name="climax">ClimaxEvent đã phát hiện</param>
        /// <param name="swings">Danh sách index swing high/low (nếu có)</param>
        /// <param name="atr">ATR đã tính sẵn (nếu có)</param>
        /// <returns>AREvent hoặc null nếu không tìm thấy</returns>
        public AREvent DetectAR(IList<Bar> bars, ClimaxEvent climax, IList<int> swings = null, IList<double> atr = null)
        {
            if (bars == null || climax == null) {
                _logger?.Invoke($"[ARDetector] Input bars or climax is null. bars: {(bars == null ? "null" : bars.Count.ToString())}, climax: {(climax == null ? "null" : climax.Index.ToString())}");
                return null;
            }
            int i = climax.Index;
            // Skip weekend climaxes
            var dt = bars[i].OpenTime;
            if (dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday)
            {
                _logger?.Invoke($"[ARDetector] Skipping climax at {i} on weekend {dt.DayOfWeek}");
                return null;
            }
            int n = bars.Count;
            int firstIdx = i + 1;
            if (firstIdx >= n) return null;

            // Pre-compute climax metrics
            var climaxBar = bars[i];
            double climaxRange = Math.Max(1e-9, climaxBar.High - climaxBar.Low);
            // For structure break we approximate local cluster: scan backwards consecutive same-direction bars.
            int back = i - 1;
            double clusterHigh = climaxBar.High;
            double clusterLow = climaxBar.Low;
            while (back >= 0 && bars[back].Close < bars[back].Open == (climax.Type == ClimaxType.SellingClimax)) // cùng hướng với đợt climax (bear run trước SC hoặc bull run trước BC)
            {
                clusterHigh = Math.Max(clusterHigh, bars[back].High);
                clusterLow = Math.Min(clusterLow, bars[back].Low);
                back--;
            }

            // Average volume for ratio
            double avgVolume = 0;
            if (VolumeLookback > 0)
            {
                int volStart = Math.Max(0, i - VolumeLookback);
                int count = i - volStart;
                if (count > 0) avgVolume = bars.Skip(volStart).Take(count).Average(b => b.Volume);
            }
            if (avgVolume <= 0) avgVolume = bars[i].Volume; // fallback

            // ATR adapt metrics
            double medianAtr = 0;
            if (atr != null && atr.Count == bars.Count)
            {
                int atrStart = Math.Max(0, i - 20);
                var atrSlice = atr.Skip(atrStart).Take(i - atrStart + 1).ToList();
                if (atrSlice.Count > 0)
                {
                    var sorted = atrSlice.OrderBy(x => x).ToList();
                    medianAtr = sorted[sorted.Count / 2];
                }
            }
            if (medianAtr <= 0) medianAtr = climaxRange; // degrade gracefully

            // Nhánh 1: Climax đơn (không cluster) -> AR là nến hồi duy nhất có thân >= 1/2 range climax (SC: nến xanh mạnh)
            if (!climax.IsCluster)
            {
                int j = firstIdx;
                if (j < n)
                {
                    var bar = bars[j];
                    bool directionOk = climax.Type == ClimaxType.SellingClimax ? (bar.Close > bar.Open) : (bar.Close < bar.Open);
                    double rangeClimax = climaxBar.High - climaxBar.Low;
                    double body = Math.Abs(bar.Close - bar.Open);
                    double range = Math.Max(1e-9, bar.High - bar.Low);
                    double bodyRatio = body / range;
                    bool bigRetrace = body >= 0.5 * rangeClimax; // điều kiện bạn yêu cầu > 1/2 range Climax
                    if (directionOk && bigRetrace)
                    {
                        double arPrice = climax.Type == ClimaxType.SellingClimax ? bar.High : bar.Low;
                        double move = Math.Abs(arPrice - (climax.Type == ClimaxType.SellingClimax ? climaxBar.Low : climaxBar.High));
                        double moveFrac = move / Math.Max(1e-9, rangeClimax);
                        var evt = new AREvent
                        {
                            Index = j,
                            Time = bar.OpenTime,
                            Price = arPrice,
                            IsRally = climax.Type == ClimaxType.SellingClimax,
                            Bar = bar,
                            ClimaxIndex = i,
                            Score = 1,
                            MoveFrac = moveFrac,
                            MoveAtr = 0,
                            BodyRatio = bodyRatio,
                            VolumeRatio = 0,
                            Tier = moveFrac >= DeepFracMin ? "Deep" : (moveFrac < TinyFracMax ? "Tiny" : "Normal"),
                            StructureBreak = false,
                            Immediate = true,
                            IsClusterAr = false,
                            ClusterSize = 1
                        };
                        _logger?.Invoke($"[ARDetector] AR_SINGLE_ACCEPT idx={evt.Index} bodyRetraceFrac={(body/ rangeClimax):0.00} moveFrac={evt.MoveFrac:0.00}");
                        return evt;
                    }
                }
                // nếu không đạt điều kiện single -> rơi xuống logic chung (có thể vẫn tìm AR thường)
            }

            // Nhánh 2: Climax cluster -> tìm cụm 2-3 bar hồi/phản ứng (cluster AR)
            int clusterMin = 2;
            int clusterMax = 3;
            int maxScan = Math.Min(n - 1, i + 1 + LookaheadBars);
            for (int start = firstIdx; start <= maxScan - clusterMin; start++)
            {
                for (int size = clusterMin; size <= clusterMax && start + size -1 <= maxScan; size++)
                {
                    int endIdx = start + size -1;
                    // kiểm tra tất cả bar trong cụm đều có hướng kỳ vọng (hoặc cho phép 1 bar doji nhỏ?)
                    bool allDir = true; double peakPrice = 0; double bodySum = 0;
                    double rangeClimax2 = climaxBar.High - climaxBar.Low;
                    peakPrice = climax.Type == ClimaxType.SellingClimax ? bars[start].High : bars[start].Low;
                    for (int k = start; k <= endIdx; k++)
                    {
                        var b = bars[k];
                        bool dirOk = climax.Type == ClimaxType.SellingClimax ? (b.Close >= b.Open) : (b.Close <= b.Open);
                        if (!dirOk) { allDir = false; break; }
                        if (climax.Type == ClimaxType.SellingClimax)
                            peakPrice = Math.Max(peakPrice, b.High);
                        else
                            peakPrice = Math.Min(peakPrice, b.Low);
                        double bd = Math.Abs(b.Close - b.Open);
                        bodySum += bd;
                    }
                    if (!allDir) continue;
                    double move = Math.Abs(peakPrice - (climax.Type == ClimaxType.SellingClimax ? climaxBar.Low : climaxBar.High));
                    double moveFrac = move / Math.Max(1e-9, rangeClimax2);
                    double avgBody = bodySum / size;
                    bool bigRetraceCluster = avgBody >= 0.5 * rangeClimax2 / size; // trung bình thân * size >= 0.5 range climax => tương đương tổng thân >= 0.5 range
                    if (bigRetraceCluster)
                    {
                        var lastBar = bars[endIdx];
                        var evt = new AREvent
                        {
                            Index = endIdx,
                            Time = lastBar.OpenTime,
                            Price = climax.Type == ClimaxType.SellingClimax ? peakPrice : peakPrice,
                            IsRally = climax.Type == ClimaxType.SellingClimax,
                            Bar = lastBar,
                            ClimaxIndex = i,
                            Score = size, // tạm dùng size làm score cơ bản
                            MoveFrac = moveFrac,
                            MoveAtr = 0,
                            BodyRatio = 0, // không áp dụng cho cụm mức nến đơn
                            VolumeRatio = 0,
                            Tier = moveFrac >= DeepFracMin ? "Deep" : (moveFrac < TinyFracMax ? "Tiny" : "Normal"),
                            StructureBreak = false,
                            Immediate = (start == firstIdx),
                            IsClusterAr = true,
                            ClusterSize = size
                        };
                        _logger?.Invoke($"[ARDetector] AR_CLUSTER_ACCEPT idx={evt.Index} size={size} avgBodyRetraceFrac={(avgBody * size / rangeClimax2):0.00} moveFrac={evt.MoveFrac:0.00}");
                        return evt; // nhận cụm đầu tiên đạt yêu cầu
                    }
                }
            }

            AREvent bestEvent = null; // fallback logic cũ
            for (int j = firstIdx; j < n && (j - i) <= LookaheadBars; j++)
            {
                var bar = bars[j];
                if (bar.OpenTime.DayOfWeek == DayOfWeek.Saturday || bar.OpenTime.DayOfWeek == DayOfWeek.Sunday) continue;
                double body = Math.Abs(bar.Close - bar.Open);
                double range = Math.Max(1e-9, bar.High - bar.Low);
                double bodyRatio = body / range;
                double arPrice = climax.Type == ClimaxType.SellingClimax ? bar.High : bar.Low;
                double move = Math.Abs(arPrice - (climax.Type == ClimaxType.SellingClimax ? climaxBar.Low : climaxBar.High));
                double moveFrac = move / climaxRange;
                double moveAtr = 0;
                if (atr != null && atr.Count == bars.Count)
                {
                    double refAtr = atr[j];
                    if (refAtr > 0) moveAtr = move / refAtr;
                }
                double volumeRatio = avgVolume > 0 ? bar.Volume / avgVolume : 1;
                // Adaptive body requirement
                double effMinBody = MinBodyRatio;
                if (atr != null && atr.Count == bars.Count)
                {
                    double currentAtr = atr[j];
                    if (currentAtr > 0 && medianAtr > 0)
                    {
                        double atrFactor = currentAtr / medianAtr;
                        if (atrFactor < AtrLowFactor) effMinBody = Math.Max(0, MinBodyRatio - LowLiquidityBodyRelax);
                        else if (atrFactor > AtrHighFactor) effMinBody = MinBodyRatio + HighLiquidityBodyAdd;
                    }
                }
                bool directionOk = climax.Type == ClimaxType.SellingClimax ? (bar.Close > bar.Open) : (bar.Close < bar.Open);
                bool bodyOk = bodyRatio >= effMinBody;
                bool volumeOk = MinVolumeRatio <= 0 || volumeRatio >= MinVolumeRatio;
                // Structure break: SC -> bar.High > clusterHigh + buffer; BC -> bar.Low < clusterLow - buffer
                double buffer = climaxRange * BreakBufferFrac;
                bool structureBreak = climax.Type == ClimaxType.SellingClimax
                    ? (bar.High >= clusterHigh + buffer)
                    : (bar.Low <= clusterLow - buffer);
                bool atrMoveOk = MinAtrMove <= 0 || moveAtr >= MinAtrMove;

                // Tier classification
                string tier = moveFrac < TinyFracMax ? "Tiny" : (moveFrac >= DeepFracMin ? "Deep" : "Normal");

                int score = 0;
                if (directionOk) score++;
                if (bodyOk) score++;
                if (structureBreak) score++;
                if (volumeOk) score++;
                if (atrMoveOk) score++;

                if (DebugMode)
                {
                    _logger?.Invoke($"[ARDetector] idx={j} dirOk={directionOk} bodyOk={bodyOk} volOk={volumeOk} breakOk={structureBreak} atrOk={atrMoveOk} bodyRatio={bodyRatio:0.00} effMinBody={effMinBody:0.00} moveFrac={moveFrac:0.00} moveAtr={moveAtr:0.00} tier={tier} volRatio={volumeRatio:0.00} score={score}");
                }

                // Acceptance logic:
                // - Always require direction & body.
                // - For immediate bar (j == firstIdx) we can accept even without break if PreferImmediate.
                // - Later bars: prefer those with structureBreak or higher tier; accept Tiny if no better found by end.
                bool immediate = (j == firstIdx);
                bool acceptable = directionOk && bodyOk && volumeOk && atrMoveOk;
                if (!acceptable) continue;
                if (!structureBreak && !immediate && tier == "Tiny")
                {
                    // keep scanning for better confirmation
                    continue;
                }
                // If we have already an event: replace only if new tier is higher (Deep > Normal > Tiny) or adds structureBreak
                if (bestEvent != null)
                {
                    int TierRank(string t) => t == "Deep" ? 3 : (t == "Normal" ? 2 : 1);
                    bool better = TierRank(tier) > TierRank(bestEvent.Tier) || (!bestEvent.StructureBreak && structureBreak);
                    if (!better) continue;
                }
                // Create / update bestEvent
                bestEvent = new AREvent
                {
                    Index = j,
                    Time = bar.OpenTime,
                    Price = arPrice,
                    IsRally = climax.Type == ClimaxType.SellingClimax,
                    Bar = bar,
                    ClimaxIndex = i,
                    Score = score,
                    MoveFrac = moveFrac,
                    MoveAtr = moveAtr,
                    BodyRatio = bodyRatio,
                    VolumeRatio = volumeRatio,
                    Tier = tier,
                    StructureBreak = structureBreak,
                    Immediate = immediate
                };
                // Nếu là nến đầu tiên và cho phép trả về ngay => return luôn (AR tức thì)
                if (ReturnImmediateIfValid && (j == firstIdx))
                {
                    string evtNameImm = bestEvent.IsRally ? "AR" : "ARc";
                    _logger?.Invoke($"[ARDetector] {evtNameImm}_IMMEDIATE_ACCEPT idx={bestEvent.Index} tier={bestEvent.Tier} moveFrac={bestEvent.MoveFrac:0.00} break={bestEvent.StructureBreak} immediate={bestEvent.Immediate} bodyRatio={bestEvent.BodyRatio:0.00} volRatio={bestEvent.VolumeRatio:0.00}");
                    return bestEvent;
                }
                // Ngược lại: Early exit nếu đạt tiêu chí mạnh (Deep + phá cấu trúc)
                if (tier == "Deep" && structureBreak) break;
            }
            if (bestEvent != null)
            {
                string evtName = bestEvent.IsRally ? "AR" : "ARc";
                _logger?.Invoke($"[ARDetector] {evtName}_ACCEPT idx={bestEvent.Index} tier={bestEvent.Tier} moveFrac={bestEvent.MoveFrac:0.00} break={bestEvent.StructureBreak} immediate={bestEvent.Immediate} bodyRatio={bestEvent.BodyRatio:0.00} volRatio={bestEvent.VolumeRatio:0.00}");
                return bestEvent;
            }
            if (DebugMode) _logger?.Invoke($"[ARDetector] No AR event found after climax idx={i} ({climaxBar.OpenTime})");
            return null;
        }
        
        // Load VolumeMultiplier from systemRules.json located at working directory
        private double LoadVolumeMultiplierFromConfig()
        {
            var defaultVal = 1.5;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "systemRules.json");
                if (!File.Exists(path)) return defaultVal;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("ARDetector", out var section) &&
                    section.TryGetProperty("VolumeMultiplier", out var vm))
                {
                    return vm.GetDouble();
                }
                _logger?.Invoke("[ARDetector] Configuration missing 'ARDetector.VolumeMultiplier', using default");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[ARDetector] Error loading config: {ex.Message}");
            }
            return defaultVal;
        }
    }
}
