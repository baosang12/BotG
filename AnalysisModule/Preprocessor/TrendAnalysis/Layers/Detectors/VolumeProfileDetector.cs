using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors
{
    /// <summary>
    /// Volume profile detector implementing weekly spec logic.
    /// Buckets volume per price, identifies POC, value area, HVN/LVN, and flags.
    /// </summary>
    public sealed class VolumeProfileDetector : IPatternDetector
    {
        private sealed class VolumeBucket
        {
            public VolumeBucket(double low, double high)
            {
                Low = low;
                High = high;
                Mid = (low + high) * 0.5;
            }

            public double Low { get; }
            public double High { get; }
            public double Mid { get; }
            public double Volume { get; private set; }
            public bool IsHvn { get; set; }
            public bool IsLvn { get; set; }

            public void AddVolume(double volume)
            {
                if (volume > 0)
                {
                    Volume += volume;
                }
            }
        }

        private VolumeProfileParameters _parameters;
        private TimeFrame _primaryTimeFrame;

        public VolumeProfileDetector(VolumeProfileParameters? parameters = null)
        {
            _parameters = VolumeProfileParameters.CreateDefault();
            UpdateParameters(parameters);
        }

        public string Name => "VolumeProfile";

        public double Weight { get; set; } = 0.2;

        public bool IsEnabled { get; set; } = true;

        public PatternDetectionResult Detect(SnapshotDataAccessor accessor)
        {
            var result = PatternDetectionResult.Neutral();
            InitializeDiagnostics(result.Diagnostics);

            if (!IsEnabled)
            {
                result.Diagnostics["Status"] = "Disabled";
                result.Score = 50;
                result.Confidence = 0.3;
                return result;
            }

            if (accessor == null)
            {
                result.Diagnostics["Status"] = "AccessorNull";
                result.Score = 50;
                result.Confidence = 0.3;
                return result;
            }

            var requestCount = Math.Max(_parameters.LookbackBars, _parameters.MinBars);
            var bars = accessor.GetBars(_primaryTimeFrame, requestCount);
            if (bars == null || bars.Count < _parameters.MinBars)
            {
                result.Diagnostics["Status"] = "InsufficientData";
                result.Diagnostics["BarCount"] = bars?.Count ?? 0;
                result.Score = 50;
                result.Confidence = 0.35;
                return result;
            }

            AnalyzeProfile(bars, result);
            return result;
        }

        public void UpdateParameters(VolumeProfileParameters? parameters)
        {
            _parameters = parameters?.Clone() ?? VolumeProfileParameters.CreateDefault();
            _parameters.Normalize();
            _primaryTimeFrame = ResolveTimeFrame(_parameters.PrimaryTimeFrame);
        }

        private void AnalyzeProfile(IReadOnlyList<Bar> bars, PatternDetectionResult result)
        {
            var extrema = GetPriceExtrema(bars);
            var priceRange = extrema.Max - extrema.Min;
            if (!(priceRange > 1e-6))
            {
                result.Diagnostics["Status"] = "FlatRange";
                result.Diagnostics["BarCount"] = bars.Count;
                result.Score = 50;
                result.Confidence = 0.35;
                return;
            }

            var bucketCount = Math.Max(2, _parameters.NumberOfBuckets);
            var bucketSize = Math.Max(priceRange / bucketCount, Math.Max(1e-4, priceRange * 1e-4));
            var buckets = CreateBuckets(extrema.Min, extrema.Max, bucketCount, bucketSize);

            var totalVolume = DistributeVolume(bars, extrema.Min, bucketSize, buckets);
            if (!(totalVolume > 0))
            {
                result.Diagnostics["Status"] = "ZeroVolume";
                result.Score = 50;
                result.Confidence = 0.35;
                return;
            }

            var pocInfo = IdentifyPointOfControl(buckets);
            var avgVolume = totalVolume / bucketCount;
            var nodeStats = ClassifyVolumeNodes(buckets, avgVolume);

            var valueArea = BuildValueArea(buckets, pocInfo.Index, totalVolume, _parameters.ValueAreaPercentage);

            var currentPrice = bars[bars.Count - 1].Close;
            var currentBucketIndex = GetBucketIndex(currentPrice, extrema.Min, bucketSize, bucketCount);
            var currentBucket = buckets[currentBucketIndex];
            var pocPrice = buckets[pocInfo.Index].Mid;

            var priceAtHvn = currentBucket.IsHvn;
            var priceAtLvn = currentBucket.IsLvn;
            var nearPoc = Math.Abs(currentPrice - buckets[pocInfo.Index].Mid) <= bucketSize;
            var breakDirection = DetectValueAreaBreak(currentPrice, valueArea.Low, valueArea.High, bucketSize);

            var flags = BuildFlags(priceAtHvn, priceAtLvn, nearPoc, breakDirection);
            var score = ComputeScore(valueArea, pocInfo, priceAtHvn, priceAtLvn, breakDirection, nearPoc);
            var confidence = ComputeConfidence(bars.Count, valueArea.Concentration, pocInfo.Clarity, totalVolume);

            result.Score = score;
            result.Confidence = confidence;
            result.Flags = flags;

            PopulateDiagnostics(result.Diagnostics, bars.Count, bucketSize, bucketCount, totalVolume, valueArea,
                pocInfo, pocPrice, nodeStats, priceAtHvn, priceAtLvn, breakDirection, currentPrice, currentBucket, flags);
        }

        private (double Min, double Max) GetPriceExtrema(IReadOnlyList<Bar> bars)
        {
            var min = double.MaxValue;
            var max = double.MinValue;
            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                if (bar.Low < min)
                {
                    min = bar.Low;
                }

                if (bar.High > max)
                {
                    max = bar.High;
                }
            }

            return (min, max);
        }

        private VolumeBucket[] CreateBuckets(double minPrice, double maxPrice, int bucketCount, double bucketSize)
        {
            var buckets = new VolumeBucket[bucketCount];
            for (var i = 0; i < bucketCount; i++)
            {
                var low = minPrice + i * bucketSize;
                var high = i == bucketCount - 1 ? maxPrice + bucketSize * 0.001 : low + bucketSize;
                buckets[i] = new VolumeBucket(low, high);
            }

            return buckets;
        }

        private double DistributeVolume(IReadOnlyList<Bar> bars, double minPrice, double bucketSize, VolumeBucket[] buckets)
        {
            double totalVolume = 0;
            var bucketCount = buckets.Length;
            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                var barLow = Math.Min(bar.Low, bar.High);
                var barHigh = Math.Max(bar.Low, bar.High);
                var barRange = barHigh - barLow;
                if (!(barRange > 0) || bar.Volume <= 0)
                {
                    continue;
                }

                var startIndex = GetBucketIndex(barLow, minPrice, bucketSize, bucketCount);
                var endIndex = GetBucketIndex(barHigh, minPrice, bucketSize, bucketCount);
                for (var bucketIndex = startIndex; bucketIndex <= endIndex; bucketIndex++)
                {
                    var bucket = buckets[bucketIndex];
                    var overlapLow = Math.Max(barLow, bucket.Low);
                    var overlapHigh = Math.Min(barHigh, bucket.High);
                    var overlapRange = overlapHigh - overlapLow;
                    if (!(overlapRange > 0))
                    {
                        continue;
                    }

                    var contribution = bar.Volume * (overlapRange / barRange);
                    bucket.AddVolume(contribution);
                    totalVolume += contribution;
                }
            }

            return totalVolume;
        }

        private (int Index, double Volume, double SecondVolume, double Clarity) IdentifyPointOfControl(VolumeBucket[] buckets)
        {
            var pocIndex = 0;
            var maxVolume = double.MinValue;
            var secondVolume = double.MinValue;
            for (var i = 0; i < buckets.Length; i++)
            {
                var volume = buckets[i].Volume;
                if (volume > maxVolume)
                {
                    secondVolume = maxVolume;
                    maxVolume = volume;
                    pocIndex = i;
                }
                else if (volume > secondVolume)
                {
                    secondVolume = volume;
                }
            }

            if (secondVolume < 0)
            {
                secondVolume = 0;
            }

            var clarity = secondVolume > 0
                ? (maxVolume - secondVolume) / secondVolume
                : double.PositiveInfinity;
            return (pocIndex, maxVolume, secondVolume, clarity);
        }

        private (int HvnCount, int LvnCount) ClassifyVolumeNodes(VolumeBucket[] buckets, double avgVolume)
        {
            var hvnCount = 0;
            var lvnCount = 0;
            var highThreshold = avgVolume * _parameters.HighVolumeThreshold;
            var lowThreshold = avgVolume * _parameters.LowVolumeThreshold;

            for (var i = 0; i < buckets.Length; i++)
            {
                var bucket = buckets[i];
                bucket.IsHvn = bucket.Volume >= highThreshold;
                bucket.IsLvn = bucket.Volume <= lowThreshold;
                if (bucket.IsHvn)
                {
                    hvnCount++;
                }
                else if (bucket.IsLvn)
                {
                    lvnCount++;
                }
            }

            return (hvnCount, lvnCount);
        }

        private (double Low, double High, double Volume, double Concentration) BuildValueArea(VolumeBucket[] buckets,
            int pocIndex, double totalVolume, double targetRatio)
        {
            var targetVolume = totalVolume * targetRatio;
            var included = targetVolume <= 0 ? Array.Empty<bool>() : new bool[buckets.Length];
            if (included.Length == 0)
            {
                return (double.NaN, double.NaN, 0, 0);
            }

            included[pocIndex] = true;
            var valueAreaVolume = Math.Max(0, buckets[pocIndex].Volume);
            var left = pocIndex - 1;
            var right = pocIndex + 1;

            while (valueAreaVolume < targetVolume)
            {
                var leftVolume = left >= 0 ? buckets[left].Volume : double.NegativeInfinity;
                var rightVolume = right < buckets.Length ? buckets[right].Volume : double.NegativeInfinity;
                if (leftVolume == double.NegativeInfinity && rightVolume == double.NegativeInfinity)
                {
                    break;
                }

                int chosen;
                if (rightVolume > leftVolume)
                {
                    chosen = right;
                    right++;
                }
                else
                {
                    chosen = left;
                    left--;
                }

                if (chosen < 0 || chosen >= buckets.Length || included[chosen])
                {
                    continue;
                }

                included[chosen] = true;
                valueAreaVolume += Math.Max(0, buckets[chosen].Volume);
            }

            double vaLow = double.NaN;
            double vaHigh = double.NaN;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (!included[i])
                {
                    continue;
                }

                var bucket = buckets[i];
                if (double.IsNaN(vaLow) || bucket.Low < vaLow)
                {
                    vaLow = bucket.Low;
                }

                if (double.IsNaN(vaHigh) || bucket.High > vaHigh)
                {
                    vaHigh = bucket.High;
                }
            }

            var concentration = totalVolume > 0 ? valueAreaVolume / totalVolume : 0;
            return (vaLow, vaHigh, valueAreaVolume, concentration);
        }

        private int DetectValueAreaBreak(double price, double vaLow, double vaHigh, double bucketSize)
        {
            var tolerance = Math.Max(bucketSize * 0.05, 1e-6);
            if (!double.IsNaN(vaHigh) && price > vaHigh + tolerance)
            {
                return 1;
            }

            if (!double.IsNaN(vaLow) && price < vaLow - tolerance)
            {
                return -1;
            }

            return 0;
        }

        private List<string> BuildFlags(bool priceAtHvn, bool priceAtLvn, bool nearPoc, int breakDirection)
        {
            var flags = new List<string>(4);
            if (priceAtHvn)
            {
                flags.Add("HVN");
            }

            if (priceAtLvn)
            {
                flags.Add("LVN");
            }

            if (nearPoc)
            {
                flags.Add("NearPOC");
            }

            if (breakDirection > 0)
            {
                flags.Add("ValueAreaBreakUp");
            }
            else if (breakDirection < 0)
            {
                flags.Add("ValueAreaBreakDown");
            }

            return flags;
        }

        private double ComputeScore((double Low, double High, double Volume, double Concentration) valueArea,
            (int Index, double Volume, double SecondVolume, double Clarity) pocInfo,
            bool priceAtHvn, bool priceAtLvn, int breakDirection, bool nearPoc)
        {
            var score = 50.0;

            var vaComponent = valueArea.Concentration - _parameters.ValueAreaPercentage;
            score += Math.Clamp(vaComponent * 100, -20, 20);

            if (double.IsPositiveInfinity(pocInfo.Clarity))
            {
                score += 10; // Perfect single-node POC.
            }
            else
            {
                score += Math.Clamp(pocInfo.Clarity * 25, -10, 15);
            }

            if (priceAtHvn)
            {
                score += 10;
            }
            else if (priceAtLvn)
            {
                score -= 10;
            }

            if (nearPoc)
            {
                score += 5;
            }

            if (breakDirection > 0)
            {
                score += 15;
            }
            else if (breakDirection < 0)
            {
                score -= 15;
            }

            return Math.Clamp(score, 0, 100);
        }

        private double ComputeConfidence(int barCount, double concentration, double pocClarity, double totalVolume)
        {
            var confidence = 0.4;

            var sampleCoverage = (double)barCount / Math.Max(1, _parameters.LookbackBars);
            confidence += Math.Clamp(sampleCoverage - 0.5, -0.2, 0.3);

            confidence += Math.Clamp(concentration - _parameters.ValueAreaPercentage, -0.1, 0.2);

            if (double.IsPositiveInfinity(pocClarity))
            {
                confidence += 0.1;
            }
            else
            {
                confidence += Math.Clamp(pocClarity * 0.2, -0.05, 0.15);
            }

            confidence += Math.Clamp(Math.Log10(Math.Max(totalVolume, 1)) * 0.02, 0, 0.1);

            return Math.Clamp(confidence, 0.3, 0.95);
        }

        private void PopulateDiagnostics(IDictionary<string, object> diagnostics, int barsAnalyzed, double bucketSize,
            int bucketCount, double totalVolume, (double Low, double High, double Volume, double Concentration) valueArea,
            (int Index, double Volume, double SecondVolume, double Clarity) pocInfo, double pocPrice,
            (int HvnCount, int LvnCount) nodeStats, bool priceAtHvn, bool priceAtLvn, int breakDirection,
            double currentPrice, VolumeBucket currentBucket, List<string> flags)
        {
            diagnostics["Status"] = "OK";
            diagnostics["BarsAnalyzed"] = barsAnalyzed;
            diagnostics["BucketSize"] = bucketSize;
            diagnostics["BucketCount"] = bucketCount;
            diagnostics["TotalVolume"] = totalVolume;
            diagnostics["ValueAreaVolume"] = valueArea.Volume;
            diagnostics["Concentration"] = valueArea.Concentration;
            diagnostics["POCIndex"] = pocInfo.Index;
            diagnostics["POCVolume"] = pocInfo.Volume;
            diagnostics["POCPrice"] = pocPrice;
            diagnostics["POCClarity"] = pocInfo.Clarity;
            diagnostics["SecondVolume"] = pocInfo.SecondVolume;
            diagnostics["VAHigh"] = valueArea.High;
            diagnostics["VALow"] = valueArea.Low;
            diagnostics["ValueAreaWidth"] = !double.IsNaN(valueArea.High) && !double.IsNaN(valueArea.Low)
                ? valueArea.High - valueArea.Low
                : double.NaN;
            diagnostics["HVN_Count"] = nodeStats.HvnCount;
            diagnostics["LVN_Count"] = nodeStats.LvnCount;
            diagnostics["PriceAtHVN"] = priceAtHvn;
            diagnostics["PriceAtLVN"] = priceAtLvn;
            diagnostics["ValueAreaBreakDirection"] = breakDirection;
            diagnostics["CurrentPrice"] = currentPrice;
            diagnostics["CurrentBucketVolume"] = currentBucket.Volume;
            diagnostics["Flags"] = flags.ToArray();
        }

        private static void InitializeDiagnostics(IDictionary<string, object> diagnostics)
        {
            diagnostics["Version"] = 4;
            diagnostics["Status"] = "Init";
            diagnostics["Flags"] = Array.Empty<string>();
        }

        private static int GetBucketIndex(double price, double minPrice, double bucketSize, int bucketCount)
        {
            var clamped = Math.Max(minPrice, price);
            var index = (int)((clamped - minPrice) / bucketSize);
            if (index < 0)
            {
                return 0;
            }

            if (index >= bucketCount)
            {
                return bucketCount - 1;
            }

            return index;
        }

        private static TimeFrame ResolveTimeFrame(string raw)
        {
            var upper = raw?.ToUpperInvariant();
            switch (upper)
            {
                case "M30":
                    return TimeFrame.M30;
                case "H1":
                    return TimeFrame.H1;
                case "H4":
                    return TimeFrame.H4;
                case "D1":
                    return TimeFrame.D1;
                default:
                    return TimeFrame.H1;
            }
        }
    }
}
