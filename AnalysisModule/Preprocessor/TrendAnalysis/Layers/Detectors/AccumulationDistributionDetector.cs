using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors
{
    /// <summary>
    /// Đánh giá giai đoạn tích lũy/phân phối dựa trên slope, volume divergence và tương tác hỗ trợ/kháng cự.
    /// </summary>
    public sealed class AccumulationDistributionDetector : IPatternDetector
    {
        private const int MinRequiredBars = 30;
        private const int SlopeLookback = 20;
        private const int AtrPeriod = 14;
        private const int SwingWindow = 3;
        private const int SwingLookback = 50;

        private AccumulationDistributionParameters _parameters;
        private TimeFrame _primaryTimeFrame;

        public AccumulationDistributionDetector(AccumulationDistributionParameters? parameters = null)
        {
            _parameters = AccumulationDistributionParameters.CreateDefault();
            UpdateParameters(parameters);
        }

        public string Name => "AccumulationDistribution";

        public double Weight { get; set; } = 0.20;

        public bool IsEnabled { get; set; } = true;

        public PatternDetectionResult Detect(SnapshotDataAccessor accessor)
        {
            var result = PatternDetectionResult.Neutral();
            var memoryStartBytes = GC.GetTotalMemory(false);

            if (accessor == null)
            {
                result.Diagnostics["Status"] = "AccessorNull";
                RecordMemoryUsage(result.Diagnostics, memoryStartBytes);
                return result;
            }

            var bars = accessor.GetBars(_primaryTimeFrame, Math.Max(_parameters.LookbackBars, MinRequiredBars));
            if (bars == null || bars.Count < MinRequiredBars)
            {
                result.Diagnostics["Status"] = "InsufficientData";
                result.Diagnostics["BarCount"] = bars?.Count ?? 0;
                result.Score = 50;
                result.Confidence = 0.2;
                RecordMemoryUsage(result.Diagnostics, memoryStartBytes);
                return result;
            }

            var validBars = bars;
            var latestBar = validBars[^1];
            var priceSlope = CalculateLinearRegressionSlope(validBars, SlopeLookback);
            var atr = CalculateAtr(validBars, AtrPeriod);
            var volumeRatio = CalculateVolumeRatio(validBars);
            var currentRangeAtr = atr > 0 ? (latestBar.High - latestBar.Low) / atr : 0;
            var swingPoints = DetectSwingPoints(validBars);
            var supportLevels = ExtractLevels(swingPoints, SwingPointType.Low);
            var resistanceLevels = ExtractLevels(swingPoints, SwingPointType.High);
            var srTolerance = atr > 0 ? atr * _parameters.SrProximityFactor : (latestBar.High - latestBar.Low) * _parameters.SrProximityFactor;

            var bearishDivergence = priceSlope < -_parameters.TrendSlopeThreshold && volumeRatio > 1.0 + _parameters.VolumeRatioThreshold;
            var bullishDivergence = priceSlope > _parameters.TrendSlopeThreshold && volumeRatio < 1.0 - _parameters.VolumeRatioThreshold;
            var isNarrowRange = currentRangeAtr > 0 && currentRangeAtr < _parameters.RangeNarrowThreshold;
            var isWideRange = currentRangeAtr > _parameters.RangeWideThreshold;
            var nearSupport = IsNearLevel(latestBar.Close, supportLevels, srTolerance);
            var nearResistance = IsNearLevel(latestBar.Close, resistanceLevels, srTolerance);

            var accumulationSignals = CountTrue(bearishDivergence, isNarrowRange, nearSupport);
            var distributionSignals = CountTrue(bullishDivergence, isWideRange, nearResistance);
            var accumulation = bearishDivergence && isNarrowRange && nearSupport;
            var distribution = bullishDivergence && isWideRange && nearResistance;

            var score = 50.0
                        + accumulationSignals * 8.0
                        - distributionSignals * 8.0;

            if (accumulation)
            {
                score += 20.0;
            }
            else if (distribution)
            {
                score -= 20.0;
            }

            score = Math.Clamp(score, 0, 100);

            var confidence = CalculateConfidence(accumulationSignals, distributionSignals, validBars.Count);

            var flags = BuildFlags(bearishDivergence, bullishDivergence, isNarrowRange, isWideRange, nearSupport, nearResistance, accumulation, distribution);

            result.Score = score;
            result.Confidence = confidence;
            if (flags.Count > 0)
            {
                result.Flags = flags;
            }

            var diagnostics = result.Diagnostics;
            diagnostics["Status"] = "OK";
            diagnostics["PriceSlope"] = priceSlope;
            diagnostics["VolumeRatio"] = volumeRatio;
            diagnostics["CurrentRangeATR"] = double.IsFinite(currentRangeAtr) ? currentRangeAtr : 0;
            diagnostics["ATR"] = atr;
            diagnostics["NearSupport"] = nearSupport;
            diagnostics["NearResistance"] = nearResistance;
            diagnostics["AccumulationSignals"] = accumulationSignals;
            diagnostics["DistributionSignals"] = distributionSignals;
            diagnostics["BarsAnalyzed"] = validBars.Count;
            diagnostics["Phase"] = accumulation ? "Accumulation" : distribution ? "Distribution" : "Neutral";
            diagnostics["SupportLevels"] = supportLevels.Count > 0 ? string.Join(',', supportLevels.Select(l => l.ToString(CultureInfo.InvariantCulture))) : string.Empty;
            diagnostics["ResistanceLevels"] = resistanceLevels.Count > 0 ? string.Join(',', resistanceLevels.Select(l => l.ToString(CultureInfo.InvariantCulture))) : string.Empty;
            RecordMemoryUsage(diagnostics, memoryStartBytes);

            return result;
        }

        public void UpdateParameters(AccumulationDistributionParameters? parameters)
        {
            _parameters = parameters?.Clone() ?? AccumulationDistributionParameters.CreateDefault();
            _parameters.Normalize();
            _primaryTimeFrame = ResolveTimeFrame(_parameters.PrimaryTimeFrame);
        }

        private static int CountTrue(params bool[] values)
        {
            var count = 0;
            foreach (var value in values)
            {
                if (value)
                {
                    count++;
                }
            }

            return count;
        }

        private static TimeFrame ResolveTimeFrame(string? timeframe)
        {
            if (!string.IsNullOrWhiteSpace(timeframe) && Enum.TryParse<TimeFrame>(timeframe, true, out var parsed))
            {
                return parsed;
            }

            return TimeFrame.H1;
        }

        private static double CalculateLinearRegressionSlope(IReadOnlyList<Bar> bars, int lookback)
        {
            var count = Math.Min(lookback, bars.Count);
            if (count < 2)
            {
                return 0;
            }

            double sumX = 0;
            double sumY = 0;
            double sumXY = 0;
            double sumX2 = 0;

            for (var i = 0; i < count; i++)
            {
                var price = bars[^(count - i)].Close;
                sumX += i;
                sumY += price;
                sumXY += i * price;
                sumX2 += i * i;
            }

            var denominator = (count * sumX2) - (sumX * sumX);
            if (Math.Abs(denominator) < 1e-9)
            {
                return 0;
            }

            return ((count * sumXY) - (sumX * sumY)) / denominator;
        }

        private static double CalculateAtr(IReadOnlyList<Bar> bars, int period)
        {
            if (bars.Count < period + 1)
            {
                return 0;
            }

            double sum = 0;
            for (var i = bars.Count - period; i < bars.Count; i++)
            {
                var current = bars[i];
                var previous = bars[i - 1];
                var tr = Math.Max(
                    current.High - current.Low,
                    Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
                sum += tr;
            }

            return sum / period;
        }

        private static double CalculateVolumeRatio(IReadOnlyList<Bar> bars)
        {
            var sma5 = CalculateSma(bars, 5);
            var sma20 = CalculateSma(bars, 20);
            if (sma20 <= 0)
            {
                return 1.0;
            }

            return sma5 / sma20;
        }

        private static double CalculateSma(IReadOnlyList<Bar> bars, int window)
        {
            if (bars.Count < window || window <= 0)
            {
                return 0;
            }

            double sum = 0;
            for (var i = bars.Count - window; i < bars.Count; i++)
            {
                sum += bars[i].Volume;
            }

            return sum / window;
        }

        private static List<SwingPoint> DetectSwingPoints(IReadOnlyList<Bar> bars)
        {
            var points = new List<SwingPoint>();
            if (bars.Count < SwingWindow * 2 + 1)
            {
                return points;
            }

            var startIndex = Math.Max(SwingWindow, bars.Count - SwingLookback);
            for (var i = startIndex; i < bars.Count - SwingWindow; i++)
            {
                var bar = bars[i];
                var isHigh = true;
                var isLow = true;
                for (var j = i - SwingWindow; j <= i + SwingWindow && (isHigh || isLow); j++)
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
                }

                if (isHigh)
                {
                    points.Add(new SwingPoint(SwingPointType.High, bar.High, i));
                }
                else if (isLow)
                {
                    points.Add(new SwingPoint(SwingPointType.Low, bar.Low, i));
                }
            }

            return points;
        }

        private static List<double> ExtractLevels(IEnumerable<SwingPoint> points, SwingPointType type)
        {
            return points
                .Where(p => p.Type == type)
                .OrderByDescending(p => p.Index)
                .Take(3)
                .Select(p => p.Price)
                .ToList();
        }

        private static bool IsNearLevel(double price, IReadOnlyCollection<double> levels, double tolerance)
        {
            if (levels == null || levels.Count == 0 || tolerance <= 0)
            {
                return false;
            }

            foreach (var level in levels)
            {
                if (Math.Abs(price - level) <= tolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> BuildFlags(
            bool bearishDivergence,
            bool bullishDivergence,
            bool isNarrowRange,
            bool isWideRange,
            bool nearSupport,
            bool nearResistance,
            bool accumulation,
            bool distribution)
        {
            var flags = new List<string>();
            if (accumulation)
            {
                flags.Add("Accumulation");
            }

            if (distribution)
            {
                flags.Add("Distribution");
            }

            if (bearishDivergence)
            {
                flags.Add("VolumeAbsorption");
            }

            if (bullishDivergence)
            {
                flags.Add("VolumeDistribution");
            }

            if (isNarrowRange)
            {
                flags.Add("RangeCompression");
            }

            if (isWideRange)
            {
                flags.Add("RangeExpansion");
            }

            if (nearSupport)
            {
                flags.Add("SupportInteraction");
            }

            if (nearResistance)
            {
                flags.Add("ResistanceInteraction");
            }

            return flags;
        }

        private double CalculateConfidence(int accumulationSignals, int distributionSignals, int barCount)
        {
            var primarySignals = Math.Max(accumulationSignals, distributionSignals);
            if (primarySignals <= 0)
            {
                return barCount >= _parameters.LookbackBars ? 0.3 : 0.25;
            }

            var confidence = 0.3 + 0.2 * (primarySignals - 1);
            if (barCount >= _parameters.LookbackBars)
            {
                confidence += 0.1;
            }

            return Math.Clamp(confidence, 0.1, 0.9);
        }

        private static void RecordMemoryUsage(Dictionary<string, object> diagnostics, long startBytes)
        {
            if (diagnostics == null)
            {
                return;
            }

            var usedBytes = Math.Max(0, GC.GetTotalMemory(false) - startBytes);
            if (usedBytes > 1024)
            {
                diagnostics["MemoryUsedBytes"] = usedBytes;
            }
        }
    }

    public sealed class AccumulationDistributionParameters
    {
        public int LookbackBars { get; set; } = 60;
        public double TrendSlopeThreshold { get; set; } = 0.0005;
        public double VolumeRatioThreshold { get; set; } = 0.1;
        public double RangeNarrowThreshold { get; set; } = 0.8;
        public double RangeWideThreshold { get; set; } = 1.2;
        public double SrProximityFactor { get; set; } = 0.3;
        public string PrimaryTimeFrame { get; set; } = "H1";

        public static AccumulationDistributionParameters CreateDefault()
        {
            return new AccumulationDistributionParameters();
        }

        public AccumulationDistributionParameters Clone()
        {
            return new AccumulationDistributionParameters
            {
                LookbackBars = LookbackBars,
                TrendSlopeThreshold = TrendSlopeThreshold,
                VolumeRatioThreshold = VolumeRatioThreshold,
                RangeNarrowThreshold = RangeNarrowThreshold,
                RangeWideThreshold = RangeWideThreshold,
                SrProximityFactor = SrProximityFactor,
                PrimaryTimeFrame = PrimaryTimeFrame
            };
        }

        public void Normalize()
        {
            LookbackBars = Math.Clamp(LookbackBars, 40, 240);
            TrendSlopeThreshold = Math.Max(1e-5, TrendSlopeThreshold);
            VolumeRatioThreshold = Math.Clamp(VolumeRatioThreshold, 0.01, 0.9);
            RangeNarrowThreshold = Math.Clamp(RangeNarrowThreshold, 0.1, RangeWideThreshold);
            RangeWideThreshold = Math.Clamp(RangeWideThreshold, RangeNarrowThreshold + 0.1, 5.0);
            SrProximityFactor = Math.Clamp(SrProximityFactor, 0.05, 1.0);
            PrimaryTimeFrame = string.IsNullOrWhiteSpace(PrimaryTimeFrame) ? "H1" : PrimaryTimeFrame.Trim();
        }
    }

    internal enum SwingPointType
    {
        High,
        Low
    }

    internal readonly struct SwingPoint
    {
        public SwingPoint(SwingPointType type, double price, int index)
        {
            Type = type;
            Price = price;
            Index = index;
        }

        public SwingPointType Type { get; }
        public double Price { get; }
        public int Index { get; }
    }
}
