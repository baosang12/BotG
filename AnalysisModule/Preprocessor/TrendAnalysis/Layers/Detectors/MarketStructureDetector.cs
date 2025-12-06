using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors
{
    /// <summary>
    /// Nhận diện cấu trúc thị trường (HH/HL/LH/LL) và cảnh báo break quan trọng.
    /// </summary>
    public sealed class MarketStructureDetector : IPatternDetector
    {
        private const int MinRequiredBars = 80;
        private MarketStructureParameters _parameters;
        private TimeFrame _primaryTimeFrame;

        public MarketStructureDetector(MarketStructureParameters? parameters = null)
        {
            _parameters = MarketStructureParameters.CreateDefault();
            UpdateParameters(parameters);
        }

        public string Name => "MarketStructure";

        public double Weight { get; set; } = 0.20;

        public bool IsEnabled { get; set; } = true;

        public PatternDetectionResult Detect(SnapshotDataAccessor accessor)
        {
            var result = PatternDetectionResult.Neutral();
            InitializeDefaultDiagnostics(result.Diagnostics);

            if (accessor == null)
            {
                result.Diagnostics["Status"] = "AccessorNull";
                return result;
            }

            var minBars = Math.Max(_parameters.MinimumSwingBars, Math.Max(MinRequiredBars, _parameters.SwingWindow * 4));
            var bars = accessor.GetBars(_primaryTimeFrame, Math.Max(_parameters.LookbackBars, minBars));
            if (bars == null || bars.Count < minBars)
            {
                result.Diagnostics["Status"] = "InsufficientData";
                result.Diagnostics["BarCount"] = bars?.Count ?? 0;
                result.Score = 50;
                result.Confidence = 0.3;
                return result;
            }

            var atr = CalculateAtr(bars, _parameters.AtrPeriod);
            var latestBar = bars[^1];
            var pipSize = _parameters.PipSize > 0 ? _parameters.PipSize : EstimatePipSize(latestBar.Close);
            var minSwingHeight = Math.Max(_parameters.MinSwingAmplitudePips * pipSize, atr * _parameters.MinSwingHeightAtr);
            var swingPoints = DetectSwingPoints(bars, _parameters.SwingWindow, minSwingHeight, _parameters.MaxSwingPoints);
            if (swingPoints.Count < 2)
            {
                var fallback = DetectFallbackSwings(bars, minSwingHeight, _parameters);
                if (fallback.Count >= 2)
                {
                    swingPoints = new List<MarketStructureSwingPoint>(fallback);
                    result.Diagnostics["Status"] = "FallbackSwings";
                }
                else
                {
                    result.Diagnostics["Status"] = "InsufficientSwings";
                    result.Diagnostics["SwingPoints"] = swingPoints.Count;
                    result.Score = 50;
                    result.Confidence = 0.35;
                    return result;
                }
            }

            var slope = CalculateLinearRegressionSlope(bars, Math.Min(_parameters.LookbackBars, bars.Count));
            var assessment = EvaluateStructure(bars, swingPoints, atr, pipSize, slope);

            result.Score = assessment.Score;
            result.Confidence = assessment.Confidence;
            if (assessment.Flags.Count > 0)
            {
                result.Flags = assessment.Flags;
            }

            var diagnostics = result.Diagnostics;
            diagnostics["Status"] = "OK";
            diagnostics["Structure"] = assessment.StructureLabel;
            diagnostics["TrendDirection"] = assessment.TrendDirection;
            diagnostics["SwingPoints"] = swingPoints.Count;
            diagnostics["SwingSpan"] = assessment.SwingSpan;
            diagnostics["LastSwingHigh"] = assessment.LastSwingHigh ?? double.NaN;
            diagnostics["LastSwingLow"] = assessment.LastSwingLow ?? double.NaN;
            diagnostics["BreakDetected"] = assessment.BreakDetected;
            diagnostics["BreakDirection"] = assessment.BreakDirection;
            diagnostics["Slope"] = slope;
            diagnostics["ATR"] = atr;
            diagnostics["MinSwingHeight"] = minSwingHeight;
            diagnostics["Close"] = latestBar.Close;

            return result;
        }

        private static void InitializeDefaultDiagnostics(Dictionary<string, object> diagnostics)
        {
            diagnostics["Status"] = "Init";
            diagnostics["Structure"] = "Unknown";
            diagnostics["TrendDirection"] = 0;
            diagnostics["SwingPoints"] = 0;
            diagnostics["SwingSpan"] = 0.0;
            diagnostics["LastSwingHigh"] = double.NaN;
            diagnostics["LastSwingLow"] = double.NaN;
            diagnostics["BreakDetected"] = false;
            diagnostics["BreakDirection"] = 0;
            diagnostics["Slope"] = 0.0;
            diagnostics["ATR"] = 0.0;
            diagnostics["MinSwingHeight"] = 0.0;
            diagnostics["Close"] = double.NaN;
        }

        public void UpdateParameters(MarketStructureParameters? parameters)
        {
            _parameters = parameters?.Clone() ?? MarketStructureParameters.CreateDefault();
            _parameters.Normalize();
            _primaryTimeFrame = ResolveTimeFrame(_parameters.PrimaryTimeFrame);
        }

        private StructureAssessment EvaluateStructure(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<MarketStructureSwingPoint> swingPoints,
            double atr,
            double pipSize,
            double slope)
        {
            var highs = swingPoints.Where(p => p.Type == MarketStructureSwingPointType.High).OrderBy(p => p.Index).ToList();
            var lows = swingPoints.Where(p => p.Type == MarketStructureSwingPointType.Low).OrderBy(p => p.Index).ToList();
            var lastHigh = highs.Count > 0 ? highs[^1].Price : (double?)null;
            var previousHigh = highs.Count > 1 ? highs[^2].Price : (double?)null;
            var lastLow = lows.Count > 0 ? lows[^1].Price : (double?)null;
            var previousLow = lows.Count > 1 ? lows[^2].Price : (double?)null;
            var swingSpan = (lastHigh ?? bars[^1].High) - (lastLow ?? bars[^1].Low);
            var tolerance = Math.Max(_parameters.TrendComparisonTolerancePips * pipSize, atr * 0.05);

            var uptrend = IsConsistentlyHigher(highs, tolerance, _parameters.MinSwingsForTrend)
                          && IsConsistentlyHigher(lows, tolerance, _parameters.MinSwingsForTrend)
                          && slope >= 0;
            var downtrend = IsConsistentlyLower(highs, tolerance, _parameters.MinSwingsForTrend)
                            && IsConsistentlyLower(lows, tolerance, _parameters.MinSwingsForTrend)
                            && slope <= 0;

            var structureLabel = "Unknown";
            var trendDirection = 0;
            if (uptrend && !downtrend)
            {
                structureLabel = "Uptrend";
                trendDirection = 1;
            }
            else if (downtrend && !uptrend)
            {
                structureLabel = "Downtrend";
                trendDirection = -1;
            }
            else
            {
                var atrReference = Math.Max(atr, pipSize * 10);
                if (swingSpan > 0 && swingSpan <= _parameters.RangeThresholdAtr * atrReference)
                {
                    structureLabel = "Range";
                }
                else if (swingPoints.Count >= Math.Max(2, _parameters.MinSwingsForTrend))
                {
                    structureLabel = "Range";
                }
            }

            if (trendDirection == 0)
            {
                var pipStep = Math.Max(pipSize, 1e-6);
                var slopeInPips = slope / pipStep;
                if (slopeInPips >= _parameters.MinSlopeTrendPips)
                {
                    structureLabel = "Uptrend";
                    trendDirection = 1;
                }
                else if (slopeInPips <= -_parameters.MinSlopeTrendPips)
                {
                    structureLabel = "Downtrend";
                    trendDirection = -1;
                }
            }

            var breakInfo = DetectStructureBreak(bars, lastHigh, previousHigh, lastLow, previousLow, atr, pipSize);
            var score = 50.0;
            if (trendDirection > 0)
            {
                score += 20;
            }
            else if (trendDirection < 0)
            {
                score -= 20;
            }
            else if (structureLabel.Equals("Range", StringComparison.OrdinalIgnoreCase))
            {
                var deviation = swingSpan / Math.Max(atr, pipSize * 10);
                score += Math.Clamp(deviation - _parameters.RangeThresholdAtr, -2, 2) * 5;
            }

            if (breakInfo.BreakDetected)
            {
                score += breakInfo.BreakDirection > 0 ? 15 : -15;
            }

            score = Math.Clamp(score, 0, 100);
            var confidence = CalculateConfidence(trendDirection, swingPoints.Count, breakInfo.BreakDetected);

            var flags = new List<string>();
            if (trendDirection > 0)
            {
                flags.Add("MarketStructureUptrend");
            }
            else if (trendDirection < 0)
            {
                flags.Add("MarketStructureDowntrend");
            }
            else if (structureLabel.Equals("Range", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("MarketStructureRange");
            }

            if (breakInfo.BreakDetected)
            {
                flags.Add(breakInfo.BreakDirection > 0 ? "StructureBreakUp" : "StructureBreakDown");
            }

            return new StructureAssessment(
                structureLabel,
                trendDirection,
                breakInfo.BreakDetected,
                breakInfo.BreakDirection,
                score,
                confidence,
                swingSpan,
                lastHigh,
                lastLow,
                flags);
        }

        private BreakInfo DetectStructureBreak(
            IReadOnlyList<Bar> bars,
            double? latestHigh,
            double? previousHigh,
            double? latestLow,
            double? previousLow,
            double atr,
            double pipSize)
        {
            if (bars.Count == 0)
            {
                return BreakInfo.None;
            }

            var buffer = Math.Max(_parameters.BreakBufferPips * pipSize, atr * 0.05);
            var confirmationCount = Math.Min(_parameters.BreakConfirmationBars, bars.Count);
            if (confirmationCount <= 0)
            {
                return BreakInfo.None;
            }

            var confirmationBars = SliceTail(bars, confirmationCount);
            var referenceHigh = previousHigh ?? latestHigh;
            if (referenceHigh.HasValue && ConfirmBreak(confirmationBars, referenceHigh.Value + buffer, true, buffer))
            {
                return new BreakInfo(true, 1);
            }

            var referenceLow = previousLow ?? latestLow;
            if (referenceLow.HasValue && ConfirmBreak(confirmationBars, referenceLow.Value - buffer, false, buffer))
            {
                return new BreakInfo(true, -1);
            }

            return BreakInfo.None;
        }

        private bool ConfirmBreak(IReadOnlyList<Bar> confirmationBars, double threshold, bool breakUp, double buffer)
        {
            var minBodyPercent = Math.Clamp(_parameters.BreakClosePercentage, 0.2, 0.9);
            for (var i = 0; i < confirmationBars.Count; i++)
            {
                var bar = confirmationBars[i];
                var range = Math.Max(bar.High - bar.Low, double.Epsilon);
                var bodyPercent = Math.Abs(bar.Close - bar.Open) / range;
                var close = bar.Close;
                if (bodyPercent < minBodyPercent)
                {
                    return false;
                }

                if (breakUp)
                {
                    if (close <= threshold)
                    {
                        return false;
                    }
                }
                else
                {
                    if (close >= threshold)
                    {
                        return false;
                    }
                }
            }

            var falseBreakBars = Math.Min(_parameters.FalseBreakLookbackBars, confirmationBars.Count);
            for (var i = confirmationBars.Count - falseBreakBars; i < confirmationBars.Count; i++)
            {
                var close = confirmationBars[i].Close;
                if (breakUp && close < threshold - buffer)
                {
                    return false;
                }

                if (!breakUp && close > threshold + buffer)
                {
                    return false;
                }
            }

            return true;
        }

        private static double CalculateConfidence(int trendDirection, int swingPoints, bool breakDetected)
        {
            var confidence = 0.45 + Math.Min(Math.Max(swingPoints - 2, 0) * 0.02, 0.15);
            if (trendDirection != 0)
            {
                confidence += 0.15;
            }

            if (breakDetected)
            {
                confidence += 0.1;
            }

            return Math.Clamp(confidence, 0.25, 0.9);
        }

        private static double CalculateAtr(IReadOnlyList<Bar> bars, int period)
        {
            period = Math.Max(5, Math.Min(period, bars.Count - 1));
            if (period <= 1)
            {
                return 0;
            }

            double sum = 0;
            for (var i = bars.Count - period; i < bars.Count; i++)
            {
                var current = bars[i];
                var prev = bars[i - 1];
                var tr = Math.Max(
                    current.High - current.Low,
                    Math.Max(Math.Abs(current.High - prev.Close), Math.Abs(current.Low - prev.Close)));
                sum += tr;
            }

            return sum / period;
        }

        private static double CalculateLinearRegressionSlope(IReadOnlyList<Bar> bars, int lookback)
        {
            lookback = Math.Max(2, Math.Min(lookback, bars.Count));
            double sumX = 0;
            double sumY = 0;
            double sumXY = 0;
            double sumX2 = 0;
            for (var i = 0; i < lookback; i++)
            {
                var price = bars[^(lookback - i)].Close;
                sumX += i;
                sumY += price;
                sumXY += i * price;
                sumX2 += i * i;
            }

            var denominator = (lookback * sumX2) - (sumX * sumX);
            if (Math.Abs(denominator) < 1e-9)
            {
                return 0;
            }

            return ((lookback * sumXY) - (sumX * sumY)) / denominator;
        }

        private static IReadOnlyList<Bar> SliceTail(IReadOnlyList<Bar> source, int count)
        {
            if (count >= source.Count)
            {
                return source;
            }

            var buffer = new Bar[count];
            var start = source.Count - count;
            for (var i = 0; i < count; i++)
            {
                buffer[i] = source[start + i];
            }

            return buffer;
        }

        private static List<MarketStructureSwingPoint> DetectSwingPoints(
            IReadOnlyList<Bar> bars,
            int window,
            double minSwingHeight,
            int maxPoints)
        {
            var points = new List<MarketStructureSwingPoint>();
            if (bars.Count < window * 2 + 3)
            {
                return points;
            }

            var startIndex = Math.Max(window, bars.Count - maxPoints * 4);
            for (var i = startIndex; i < bars.Count - window; i++)
            {
                var bar = bars[i];
                var isHigh = true;
                var isLow = true;
                double localMin = double.MaxValue;
                double localMax = double.MinValue;

                for (var j = i - window; j <= i + window; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    var compare = bars[j];
                    if (compare.High >= bar.High)
                    {
                        isHigh = false;
                    }

                    if (compare.Low <= bar.Low)
                    {
                        isLow = false;
                    }

                    localMin = Math.Min(localMin, compare.Low);
                    localMax = Math.Max(localMax, compare.High);

                    if (!isHigh && !isLow)
                    {
                        if (localMin <= bar.Low && localMax >= bar.High)
                        {
                            break;
                        }
                    }
                }

                if (isHigh && (bar.High - localMin) >= minSwingHeight)
                {
                    points.Add(new MarketStructureSwingPoint(MarketStructureSwingPointType.High, bar.High, i));
                }
                else if (isLow && (localMax - bar.Low) >= minSwingHeight)
                {
                    points.Add(new MarketStructureSwingPoint(MarketStructureSwingPointType.Low, bar.Low, i));
                }
            }

            if (points.Count > maxPoints)
            {
                points = points.OrderBy(p => p.Index).Skip(points.Count - maxPoints).ToList();
            }

            return points;
        }

        private static IReadOnlyList<MarketStructureSwingPoint> DetectFallbackSwings(
            IReadOnlyList<Bar> bars,
            double minSwingHeight,
            MarketStructureParameters parameters)
        {
            if (bars.Count < 4)
            {
                return Array.Empty<MarketStructureSwingPoint>();
            }

            var recentWindow = Math.Max(parameters.BreakConfirmationBars * 2 + 2, 6);
            var start = Math.Max(0, bars.Count - 64);
            var split = Math.Max(start + 2, bars.Count - recentWindow);

            var priorHighIndex = FindExtremeIndex(bars, start, split, lookForHigh: true);
            var priorLowIndex = FindExtremeIndex(bars, start, split, lookForHigh: false);
            var recentHighIndex = FindExtremeIndex(bars, split, bars.Count, lookForHigh: true);
            var recentLowIndex = FindExtremeIndex(bars, split, bars.Count, lookForHigh: false);

            var points = new List<MarketStructureSwingPoint>();
            AddSwingPoint(points, bars, MarketStructureSwingPointType.Low, priorLowIndex);
            AddSwingPoint(points, bars, MarketStructureSwingPointType.High, priorHighIndex);
            AddSwingPoint(points, bars, MarketStructureSwingPointType.Low, recentLowIndex);
            AddSwingPoint(points, bars, MarketStructureSwingPointType.High, recentHighIndex);

            if (points.Count < 2)
            {
                return Array.Empty<MarketStructureSwingPoint>();
            }

            points = points
                .OrderBy(p => p.Index)
                .GroupBy(p => new { p.Index, p.Type })
                .Select(g => g.First())
                .ToList();

            var span = Math.Abs(points[^1].Price - points[0].Price);
            if (span < Math.Max(minSwingHeight * 0.5, 1e-6))
            {
                return Array.Empty<MarketStructureSwingPoint>();
            }

            return points;
        }

        private static int FindExtremeIndex(IReadOnlyList<Bar> bars, int start, int end, bool lookForHigh)
        {
            if (end - start <= 0)
            {
                return -1;
            }

            var index = start;
            var extreme = lookForHigh ? double.MinValue : double.MaxValue;
            for (var i = start; i < end; i++)
            {
                var value = lookForHigh ? bars[i].High : bars[i].Low;
                if ((lookForHigh && value > extreme) || (!lookForHigh && value < extreme))
                {
                    extreme = value;
                    index = i;
                }
            }

            return index;
        }

        private static void AddSwingPoint(
            List<MarketStructureSwingPoint> points,
            IReadOnlyList<Bar> bars,
            MarketStructureSwingPointType type,
            int index)
        {
            if (index < 0 || index >= bars.Count)
            {
                return;
            }

            var price = type == MarketStructureSwingPointType.High ? bars[index].High : bars[index].Low;
            points.Add(new MarketStructureSwingPoint(type, price, index));
        }

        private static bool IsConsistentlyHigher(IReadOnlyList<MarketStructureSwingPoint> points, double tolerance, int minSwings)
        {
            if (points.Count < minSwings)
            {
                return false;
            }

            var start = points.Count - minSwings;
            for (var i = start + 1; i < points.Count; i++)
            {
                if (!(points[i].Price > points[i - 1].Price + tolerance))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsConsistentlyLower(IReadOnlyList<MarketStructureSwingPoint> points, double tolerance, int minSwings)
        {
            if (points.Count < minSwings)
            {
                return false;
            }

            var start = points.Count - minSwings;
            for (var i = start + 1; i < points.Count; i++)
            {
                if (!(points[i].Price < points[i - 1].Price - tolerance))
                {
                    return false;
                }
            }

            return true;
        }

        private static TimeFrame ResolveTimeFrame(string? timeframe)
        {
            if (!string.IsNullOrWhiteSpace(timeframe) && Enum.TryParse<TimeFrame>(timeframe, true, out var parsed))
            {
                return parsed;
            }

            return TimeFrame.H1;
        }

        private static double EstimatePipSize(double price)
        {
            if (price >= 50)
            {
                return 0.01;
            }

            if (price >= 5)
            {
                return 0.001;
            }

            return 0.0001;
        }

        private readonly struct StructureAssessment
        {
            public StructureAssessment(
                string structureLabel,
                int trendDirection,
                bool breakDetected,
                int breakDirection,
                double score,
                double confidence,
                double swingSpan,
                double? lastSwingHigh,
                double? lastSwingLow,
                List<string> flags)
            {
                StructureLabel = structureLabel;
                TrendDirection = trendDirection;
                BreakDetected = breakDetected;
                BreakDirection = breakDirection;
                Score = score;
                Confidence = confidence;
                SwingSpan = swingSpan;
                LastSwingHigh = lastSwingHigh;
                LastSwingLow = lastSwingLow;
                Flags = flags;
            }

            public string StructureLabel { get; }
            public int TrendDirection { get; }
            public bool BreakDetected { get; }
            public int BreakDirection { get; }
            public double Score { get; }
            public double Confidence { get; }
            public double SwingSpan { get; }
            public double? LastSwingHigh { get; }
            public double? LastSwingLow { get; }
            public List<string> Flags { get; }
        }

        private readonly struct BreakInfo
        {
            public static readonly BreakInfo None = new(false, 0);

            public BreakInfo(bool detected, int direction)
            {
                BreakDetected = detected;
                BreakDirection = direction;
            }

            public bool BreakDetected { get; }
            public int BreakDirection { get; }
        }

        private enum MarketStructureSwingPointType
        {
            High,
            Low
        }

        private readonly struct MarketStructureSwingPoint
        {
            public MarketStructureSwingPoint(MarketStructureSwingPointType type, double price, int index)
            {
                Type = type;
                Price = price;
                Index = index;
            }

            public MarketStructureSwingPointType Type { get; }
            public double Price { get; }
            public int Index { get; }
        }
    }

    public sealed class MarketStructureParameters
    {
        public int LookbackBars { get; set; } = 160;
        public int MinimumSwingBars { get; set; } = 60;
        public int SwingWindow { get; set; } = 5;
        public int MaxSwingPoints { get; set; } = 28;
        public double MinSwingAmplitudePips { get; set; } = 3;
        public double MinSwingHeightAtr { get; set; } = 0.25;
        public double RangeThresholdAtr { get; set; } = 1.4;
        public int MinSwingsForTrend { get; set; } = 2;
        public double TrendComparisonTolerancePips { get; set; } = 1.5;
        public double BreakBufferPips { get; set; } = 3;
        public int BreakConfirmationBars { get; set; } = 1;
        public int FalseBreakLookbackBars { get; set; } = 3;
        public double BreakClosePercentage { get; set; } = 0.4;
        public int AtrPeriod { get; set; } = 14;
        public double PipSize { get; set; } = 0.0001;
        public string PrimaryTimeFrame { get; set; } = "H1";
        public double MinSlopeTrendPips { get; set; } = 1.0;

        public static MarketStructureParameters CreateDefault()
        {
            return new MarketStructureParameters();
        }

        public MarketStructureParameters Clone()
        {
            return new MarketStructureParameters
            {
                LookbackBars = LookbackBars,
                MinimumSwingBars = MinimumSwingBars,
                SwingWindow = SwingWindow,
                MaxSwingPoints = MaxSwingPoints,
                MinSwingAmplitudePips = MinSwingAmplitudePips,
                MinSwingHeightAtr = MinSwingHeightAtr,
                RangeThresholdAtr = RangeThresholdAtr,
                MinSwingsForTrend = MinSwingsForTrend,
                TrendComparisonTolerancePips = TrendComparisonTolerancePips,
                BreakBufferPips = BreakBufferPips,
                BreakConfirmationBars = BreakConfirmationBars,
                FalseBreakLookbackBars = FalseBreakLookbackBars,
                BreakClosePercentage = BreakClosePercentage,
                AtrPeriod = AtrPeriod,
                PipSize = PipSize,
                PrimaryTimeFrame = PrimaryTimeFrame,
                MinSlopeTrendPips = MinSlopeTrendPips
            };
        }

        public void Normalize()
        {
            LookbackBars = Math.Clamp(LookbackBars, 80, 400);
            MinimumSwingBars = Math.Clamp(MinimumSwingBars, 60, 240);
            SwingWindow = Math.Clamp(SwingWindow, 5, 20);
            MaxSwingPoints = Math.Clamp(MaxSwingPoints, 6, 24);
            MinSwingAmplitudePips = Math.Max(1, MinSwingAmplitudePips);
            MinSwingHeightAtr = Math.Clamp(MinSwingHeightAtr, 0.1, 3.0);
            RangeThresholdAtr = Math.Clamp(RangeThresholdAtr, 0.5, 4.0);
            MinSwingsForTrend = Math.Clamp(MinSwingsForTrend, 2, 5);
            TrendComparisonTolerancePips = Math.Max(0.5, TrendComparisonTolerancePips);
            BreakBufferPips = Math.Max(1, BreakBufferPips);
            BreakConfirmationBars = Math.Clamp(BreakConfirmationBars, 1, 4);
            FalseBreakLookbackBars = Math.Clamp(FalseBreakLookbackBars, 1, 4);
            BreakClosePercentage = Math.Clamp(BreakClosePercentage, 0.2, 0.8);
            AtrPeriod = Math.Clamp(AtrPeriod, 10, 28);
            PipSize = PipSize <= 0 ? 0.0001 : PipSize;
            PrimaryTimeFrame = string.IsNullOrWhiteSpace(PrimaryTimeFrame) ? "H1" : PrimaryTimeFrame.Trim();
            MinSlopeTrendPips = Math.Clamp(MinSlopeTrendPips, 0.2, 5.0);
        }
    }
}
