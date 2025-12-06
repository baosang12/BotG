using System;
using System.Collections.Generic;
using System.Linq;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors
{
    public sealed class LiquidityAnalyzer : IPatternDetector
    {
        private const double BaselineScore = 50.0;
        private const double BaseConfidence = 0.55;
        private const double MinBody = 1e-4;
        private const int DefaultAnalysisBars = 120;
        private const int MinBarsRequired = 40;
        private const int BreakoutLookahead = 3;

        private readonly TimeFrame _timeFrame;
        private readonly int _analysisBars;
        private readonly int _swingWindow;

        public LiquidityAnalyzer(
            TimeFrame timeFrame = TimeFrame.H1,
            int analysisBars = DefaultAnalysisBars,
            int swingWindow = 5)
        {
            _timeFrame = timeFrame;
            _analysisBars = Math.Max(analysisBars, MinBarsRequired);
            _swingWindow = Math.Clamp(swingWindow, 1, 12);
        }

        public string Name => "Liquidity";

        public double Weight { get; set; } = 0.3;

        public bool IsEnabled { get; set; } = true;

        public PatternDetectionResult Detect(SnapshotDataAccessor accessor)
        {
            var result = PatternDetectionResult.Neutral();

            if (accessor == null)
            {
                result.Diagnostics["Status"] = "AccessorNull";
                return result;
            }

            var bars = accessor.GetBars(_timeFrame, _analysisBars);
            if (bars == null || bars.Count < MinBarsRequired)
            {
                result.Diagnostics["Status"] = "InsufficientData";
                result.Diagnostics["BarCount"] = bars?.Count ?? 0;
                return result;
            }

            var swingPoints = DetectSwingPoints(bars);
            var wickResult = AnalyzeWickRejections(bars, swingPoints);
            var breakoutResult = DetectFalseBreakouts(bars, swingPoints);
            var cleanPriceResult = EvaluateCleanPriceAction(bars, swingPoints);

            var score = BaselineScore
                        + wickResult.ScoreAdjustment
                        + breakoutResult.ScoreAdjustment
                        + cleanPriceResult.ScoreAdjustment;

            var confidence = BaseConfidence
                             + wickResult.ConfidenceDelta
                             + breakoutResult.ConfidenceDelta
                             + cleanPriceResult.ConfidenceDelta;

            score = Math.Clamp(score, 0, 100);
            confidence = Math.Clamp(confidence, 0.1, 0.95);

            result.Score = score;
            result.Confidence = confidence;

            var flags = new List<string>();
            AppendFlags(flags, wickResult.Flags);
            AppendFlags(flags, breakoutResult.Flags);
            AppendFlags(flags, cleanPriceResult.Flags);
            if (flags.Count > 0)
            {
                result.Flags = flags;
            }

            result.Diagnostics["Status"] = "OK";
            result.Diagnostics["TimeFrame"] = _timeFrame.ToString();
            result.Diagnostics["BarCount"] = bars.Count;
            result.Diagnostics["SwingPointCount"] = swingPoints.Count;
            var swingHighIndices = swingPoints.Where(p => p.Type == SwingPointType.High).Select(p => p.Index).ToArray();
            var swingLowIndices = swingPoints.Where(p => p.Type == SwingPointType.Low).Select(p => p.Index).ToArray();
            result.Diagnostics["SwingHighCount"] = swingHighIndices.Length;
            result.Diagnostics["SwingLowCount"] = swingLowIndices.Length;
            if (swingHighIndices.Length > 0)
            {
                result.Diagnostics["SwingHighIndices"] = string.Join(',', swingHighIndices);
            }

            if (swingLowIndices.Length > 0)
            {
                result.Diagnostics["SwingLowIndices"] = string.Join(',', swingLowIndices);
            }
            result.Diagnostics["WickRejectionCount"] = wickResult.RejectionCount;
            result.Diagnostics["LiquidityClusters"] = wickResult.ClusterCount;
            result.Diagnostics["FalseBreakoutCount"] = breakoutResult.FalseBreakoutCount;
            result.Diagnostics["FalseBreakoutTolerance"] = breakoutResult.Tolerance;
            if (breakoutResult.TriggerIndices.Count > 0)
            {
                result.Diagnostics["FalseBreakoutTriggers"] = breakoutResult.TriggerIndices.ToArray();
            }
            result.Diagnostics["CleanSequence"] = cleanPriceResult.CleanStreakLength;
            result.Diagnostics["ScoreBreakdown"] = new Dictionary<string, double>
            {
                ["Wicks"] = wickResult.ScoreAdjustment,
                ["FalseBreakouts"] = breakoutResult.ScoreAdjustment,
                ["CleanPrice"] = cleanPriceResult.ScoreAdjustment
            };
            result.Diagnostics["FlagSummary"] = flags.ToArray();

            return result;
        }

        private List<SwingPoint> DetectSwingPoints(IReadOnlyList<Bar> bars)
        {
            if (bars.Count < (_swingWindow * 2) + 1)
            {
                return new List<SwingPoint>();
            }

            var points = new List<SwingPoint>();
            for (var i = _swingWindow; i < bars.Count - _swingWindow; i++)
            {
                var pivot = bars[i];
                var isHigh = true;
                var isLow = true;

                for (var j = i - _swingWindow; j <= i + _swingWindow && (isHigh || isLow); j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    if (bars[j].High >= pivot.High)
                    {
                        isHigh = false;
                    }

                    if (bars[j].Low <= pivot.Low)
                    {
                        isLow = false;
                    }
                }

                if (isHigh)
                {
                    points.Add(new SwingPoint(SwingPointType.High, pivot.High, i));
                }
                else if (isLow)
                {
                    points.Add(new SwingPoint(SwingPointType.Low, pivot.Low, i));
                }
            }

            return points;
        }

        private WickAnalysisResult AnalyzeWickRejections(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<SwingPoint> swingPoints)
        {
            if (swingPoints.Count == 0)
            {
                return WickAnalysisResult.Empty;
            }

            var result = new WickAnalysisResult();
            var rejectionSwings = new List<SwingPoint>();
            var atr = EstimateAtr(bars, 14);
            var tolerance = Math.Max(atr * 0.4, 0.15);

            foreach (var swing in swingPoints)
            {
                var bar = bars[swing.Index];
                var body = Math.Max(Math.Abs(bar.Close - bar.Open), MinBody);
                var wick = swing.Type == SwingPointType.High
                    ? bar.High - Math.Max(bar.Open, bar.Close)
                    : Math.Min(bar.Open, bar.Close) - bar.Low;

                if (wick <= 0)
                {
                    continue;
                }

                var ratio = wick / body;
                if (ratio < 1.8)
                {
                    continue;
                }

                result.RejectionCount++;
                rejectionSwings.Add(swing);

                var adjustment = -12.0;
                if (ratio >= 2.5)
                {
                    adjustment -= 4;
                }

                if (HasVolumeSpike(bars, swing.Index))
                {
                    adjustment -= 4;
                    result.ConfidenceDelta -= 0.05;
                }
                else
                {
                    result.ConfidenceDelta -= 0.02;
                }

                result.ScoreAdjustment += adjustment;
            }

            if (rejectionSwings.Count >= 2)
            {
                var clusters = CountLiquidityClusters(rejectionSwings, tolerance);
                result.ClusterCount = clusters;
                if (clusters > 0)
                {
                    result.ScoreAdjustment += -5 * clusters;
                    result.ConfidenceDelta -= 0.03 * clusters;
                }
            }

            if (result.RejectionCount >= 2)
            {
                result.Flags.Add("StrongLiquidityGrab");
                result.Flags.Add("LiquidityGrab");
            }
            else if (result.RejectionCount == 1)
            {
                result.Flags.Add("PossibleLiquidityGrab");
                result.Flags.Add("LiquidityGrab");
            }

            return result;
        }

        private FalseBreakoutResult DetectFalseBreakouts(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<SwingPoint> swingPoints)
        {
            var result = new FalseBreakoutResult();
            if (swingPoints.Count == 0)
            {
                return result;
            }

            var atr = EstimateAtr(bars, 14);
            var tolerance = Math.Max(atr * 0.1, 0.05);
            result.Tolerance = tolerance;

            foreach (var swing in swingPoints)
            {
                for (var step = 1; step <= BreakoutLookahead; step++)
                {
                    var index = swing.Index + step;
                    if (index >= bars.Count)
                    {
                        break;
                    }

                    var candidate = bars[index];

                    if (swing.Type == SwingPointType.High)
                    {
                        var breaksHigh = candidate.High > swing.Price + tolerance;
                        var closesBelow = candidate.Close < swing.Price - tolerance;
                        if (breaksHigh && closesBelow)
                        {
                            RegisterFalseBreakout(result, index, bars);
                            break;
                        }
                    }
                    else
                    {
                        var breaksLow = candidate.Low < swing.Price - tolerance;
                        var closesAbove = candidate.Close > swing.Price + tolerance;
                        if (breaksLow && closesAbove)
                        {
                            RegisterFalseBreakout(result, index, bars);
                            break;
                        }
                    }
                }
            }

            if (result.FalseBreakoutCount > 0 && !ContainsFlag(result.Flags, "FalseBreakout"))
            {
                result.Flags.Add("FalseBreakout");
            }

            return result;
        }

        private CleanPriceResult EvaluateCleanPriceAction(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<SwingPoint> swingPoints)
        {
            var result = new CleanPriceResult();
            if (bars.Count == 0)
            {
                return result;
            }

            var recentCount = Math.Min(25, bars.Count);
            var startIndex = bars.Count - recentCount;
            var cleanCount = 0;
            var trendSteps = 0;
            double totalVolume = 0;
            double prevVolumeAvg = GetAverageVolume(bars, startIndex, 10);

            for (var i = startIndex; i < bars.Count; i++)
            {
                var bar = bars[i];
                var range = Math.Max(bar.High - bar.Low, 1e-4);
                var body = Math.Abs(bar.Close - bar.Open);

                if (body / range >= 0.6)
                {
                    cleanCount++;
                }

                if (Math.Sign(bar.Close - bar.Open) == Math.Sign(bars[^1].Close - bars[startIndex].Open)
                    && body / range >= 0.5)
                {
                    trendSteps++;
                }

                totalVolume += bar.Volume;
            }

            result.CleanStreakLength = cleanCount;

            var clean = cleanCount >= recentCount * 0.65 && trendSteps >= recentCount / 2;
            var volumeBoost = totalVolume / recentCount > prevVolumeAvg * 1.25;

            if (clean)
            {
                result.ScoreAdjustment += 12;
                result.ConfidenceDelta += 0.05;
                result.Flags.Add("CleanPriceAction");
            }

            if (clean && volumeBoost && IsBreakoutContinuation(bars, swingPoints))
            {
                result.ScoreAdjustment += 8;
                result.ConfidenceDelta += 0.04;
                result.Flags.Add("CleanBreakout");
            }

            if (!clean && swingPoints.Count == 0)
            {
                result.ScoreAdjustment -= 5;
            }

            return result;
        }

        private static void AppendFlags(List<string> target, IReadOnlyCollection<string> source)
        {
            if (source == null || source.Count == 0)
            {
                return;
            }

            foreach (var flag in source)
            {
                if (string.IsNullOrWhiteSpace(flag))
                {
                    continue;
                }

                if (!ContainsFlag(target, flag))
                {
                    target.Add(flag);
                }
            }
        }

        private static bool HasVolumeSpike(IReadOnlyList<Bar> bars, int index)
        {
            var baseline = GetAverageVolume(bars, index, 10);
            var current = bars[index].Volume;
            return baseline > 0 && current >= baseline * 1.4;
        }

        private static double GetAverageVolume(IReadOnlyList<Bar> bars, int endExclusiveIndex, int window)
        {
            if (bars.Count == 0 || window <= 0)
            {
                return 0;
            }

            var end = Math.Clamp(endExclusiveIndex, 0, bars.Count);
            var start = Math.Max(0, end - window);
            if (start >= end)
            {
                return 0;
            }

            double sum = 0;
            for (var i = start; i < end; i++)
            {
                sum += bars[i].Volume;
            }

            return sum / (end - start);
        }

        private static int CountLiquidityClusters(List<SwingPoint> swings, double tolerance)
        {
            if (swings.Count < 2)
            {
                return 0;
            }

            var clusters = 0;
            var ordered = swings.OrderBy(s => s.Price).ToList();
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var current = ordered[i];
                var next = ordered[i + 1];
                if (Math.Abs(current.Price - next.Price) <= tolerance)
                {
                    clusters++;
                }
            }

            return clusters;
        }

        private static double EstimateAtr(IReadOnlyList<Bar> bars, int period)
        {
            if (bars.Count < 2)
            {
                return 0.5;
            }

            var lookback = Math.Min(period, bars.Count - 1);
            double sum = 0;
            for (var i = 0; i < lookback; i++)
            {
                var current = bars[^(i + 1)];
                var previous = bars[^(i + 2)];
                var tr = Math.Max(
                    current.High - current.Low,
                    Math.Max(
                        Math.Abs(current.High - previous.Close),
                        Math.Abs(current.Low - previous.Close)));
                sum += tr;
            }

            return Math.Max(0.1, sum / lookback);
        }

        private static void RegisterFalseBreakout(FalseBreakoutResult result, int index, IReadOnlyList<Bar> bars)
        {
            result.FalseBreakoutCount++;
            result.ScoreAdjustment -= 20;
            result.ConfidenceDelta -= 0.05;
            result.TriggerIndices.Add(index);

            if (bars[index].Volume < GetAverageVolume(bars, index, 5))
            {
                result.ScoreAdjustment -= 5;
            }
        }

        private static bool ContainsFlag(IReadOnlyCollection<string> flags, string candidate)
        {
            if (flags == null || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            foreach (var flag in flags)
            {
                if (flag != null && flag.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsBreakoutContinuation(IReadOnlyList<Bar> bars, IReadOnlyList<SwingPoint> swingPoints)
        {
            if (bars.Count < 10)
            {
                return false;
            }

            var last = bars[^1];
            var recentHigh = bars.Skip(Math.Max(0, bars.Count - 15)).Max(b => b.High);
            var previousHigh = swingPoints.Count > 0 ? swingPoints.Max(s => s.Price) : recentHigh;

            if (recentHigh <= previousHigh)
            {
                return false;
            }

            var recentDirection = Math.Sign(last.Close - bars[^6].Close);
            return recentDirection > 0;
        }

        private enum SwingPointType
        {
            High,
            Low
        }

        private sealed record SwingPoint(SwingPointType Type, double Price, int Index);

        private sealed class WickAnalysisResult
        {
            public static readonly WickAnalysisResult Empty = new();

            public int RejectionCount { get; set; }
            public int ClusterCount { get; set; }
            public double ScoreAdjustment { get; set; }
            public double ConfidenceDelta { get; set; }
            public List<string> Flags { get; } = new();
        }

        private sealed class FalseBreakoutResult
        {
            public int FalseBreakoutCount { get; set; }
            public double ScoreAdjustment { get; set; }
            public double ConfidenceDelta { get; set; }
            public double Tolerance { get; set; }
            public List<int> TriggerIndices { get; } = new();
            public List<string> Flags { get; } = new();
        }

        private sealed class CleanPriceResult
        {
            public int CleanStreakLength { get; set; }
            public double ScoreAdjustment { get; set; }
            public double ConfidenceDelta { get; set; }
            public List<string> Flags { get; } = new();
        }
    }
}
