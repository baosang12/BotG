#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;
using Xunit;

namespace BotG.Tests.Preprocessor.TrendAnalysis.Layers.Detectors
{
    public sealed class VolumeProfileDetectorTests
    {
        [Fact]
        public void BuildVolumeProfile_CorrectVolumeDistribution()
        {
            var bars = CreateSequentialBars(
                count: 24,
                startPrice: 100,
                step: 0.4,
                volumeSelector: i => 1_500 + (i * 100));

            var parameters = new VolumeProfileParameters
            {
                LookbackBars = 60,
                MinBars = 20,
                NumberOfBuckets = 12,
                ValueAreaPercentage = 0.70,
                HighVolumeThreshold = 1.4,
                LowVolumeThreshold = 0.6,
                PrimaryTimeFrame = "H1"
            };

            var result = CreateDetector(parameters).Detect(BuildAccessor(bars));

            var expectedVolume = bars.Sum(b => (double)b.Volume);
            var totalVolume = GetDouble(result.Diagnostics, "TotalVolume");
            Assert.Equal(expectedVolume, totalVolume, 1);
            Assert.Equal(12, GetInt(result.Diagnostics, "BucketCount"));
            Assert.Equal(bars.Count, GetInt(result.Diagnostics, "BarsAnalyzed"));
        }

        [Fact]
        public void CalculatePOC_IdentifiesHighestVolumeBucket()
        {
            const int peakIndex = 10;
            var bars = CreateSequentialBars(
                count: 24,
                startPrice: 100,
                step: 0.5,
                volumeSelector: i => i == peakIndex ? 20_000 : 1_500);

            var result = CreateDetector().Detect(BuildAccessor(bars));

            var pocPrice = GetDouble(result.Diagnostics, "POCPrice");
            var expectedPrice = 100 + 0.5 * peakIndex;
            Assert.InRange(pocPrice, expectedPrice - 0.5, expectedPrice + 0.5);

            var pocVolume = GetDouble(result.Diagnostics, "POCVolume");
            Assert.True(pocVolume > 5_000);
        }

        [Fact]
        public void CalculateValueArea_Contains70PercentVolume()
        {
            var bars = CreateSequentialBars(
                count: 30,
                startPrice: 85,
                step: 0.35,
                volumeSelector: i => 1_200 + (i % 5) * 700);

            var parameters = new VolumeProfileParameters
            {
                LookbackBars = 80,
                MinBars = 20,
                NumberOfBuckets = 14,
                ValueAreaPercentage = 0.70,
                HighVolumeThreshold = 1.35,
                LowVolumeThreshold = 0.55,
                PrimaryTimeFrame = "H1"
            };

            var result = CreateDetector(parameters).Detect(BuildAccessor(bars));

            var concentration = GetDouble(result.Diagnostics, "Concentration");
            Assert.True(concentration >= 0.68);
            Assert.True(concentration <= 0.9);
        }

        [Fact]
        public void Detect_HighVolumeNode_CorrectlyIdentified()
        {
            var hvnIndex = 18;
            var hvnPrice = 95 + 0.4 * hvnIndex;
            var bars = CreateSequentialBars(
                count: 24,
                startPrice: 95,
                step: 0.4,
                volumeSelector: i => i == hvnIndex ? 30_000 : 1_600,
                closeSelector: i => i == 23 ? hvnPrice : (double?)null);

            var result = CreateDetector().Detect(BuildAccessor(bars));

            Assert.Contains("HVN", result.Flags);
            Assert.True(GetBool(result.Diagnostics, "PriceAtHVN"));
            Assert.True(GetInt(result.Diagnostics, "HVN_Count") >= 1);
        }

        [Fact]
        public void Detect_ValueAreaBreak_FlagsBreak()
        {
            var bars = CreateSequentialBars(
                count: 22,
                startPrice: 100,
                step: 0.4,
                volumeSelector: i => 1_400 + (i * 80),
                closeSelector: i => i == 21 ? 120 : (double?)null);

            var result = CreateDetector().Detect(BuildAccessor(bars));

            Assert.Contains("ValueAreaBreakUp", result.Flags);
            Assert.Equal(1, GetInt(result.Diagnostics, "ValueAreaBreakDirection"));
        }

        [Fact]
        public void Detect_InsideValueArea_CorrectScore()
        {
            var pocIndex = 14;
            var bars = CreateSequentialBars(
                count: 28,
                startPrice: 88,
                step: 0.5,
                volumeSelector: i => i >= pocIndex - 2 && i <= pocIndex + 2 ? 8_000 : 1_500,
                closeSelector: i => i == 27 ? 88 + 0.5 * pocIndex : (double?)null);

            var result = CreateDetector().Detect(BuildAccessor(bars));

            Assert.Contains("NearPOC", result.Flags);
            Assert.True(result.Score >= 55);
            Assert.Equal(0, GetInt(result.Diagnostics, "ValueAreaBreakDirection"));
        }

        [Fact]
        public void Detect_InsufficientData_ReturnsNeutral()
        {
            var bars = CreateSequentialBars(
                count: 6,
                startPrice: 100,
                step: 0.5);

            var parameters = new VolumeProfileParameters
            {
                LookbackBars = 40,
                MinBars = 12,
                NumberOfBuckets = 10,
                ValueAreaPercentage = 0.70,
                PrimaryTimeFrame = "H1"
            };

            var result = CreateDetector(parameters).Detect(BuildAccessor(bars));

            Assert.Equal(50, result.Score);
            Assert.Equal("InsufficientData", result.Diagnostics["Status"]);
        }

        [Fact]
        public void Detect_ParametersOverride_AppliesCorrectly()
        {
            var bars = CreateSequentialBars(
                count: 30,
                startPrice: 70,
                step: 0.6,
                volumeSelector: i => 1_800 + (i * 50),
                timeFrame: TimeFrame.H4);

            var detector = CreateDetector();
            var custom = new VolumeProfileParameters
            {
                LookbackBars = 70,
                MinBars = 18,
                NumberOfBuckets = 30,
                ValueAreaPercentage = 0.75,
                PrimaryTimeFrame = "H4"
            };
            detector.UpdateParameters(custom);

            var result = detector.Detect(BuildAccessor(bars, TimeFrame.H4));

            Assert.Equal(30, GetInt(result.Diagnostics, "BucketCount"));
            Assert.True(GetDouble(result.Diagnostics, "BucketSize") > 0);
        }

        [Fact]
        public void VolumeProfileDetector_Performance_LessThan5ms()
        {
            var bars = CreateSequentialBars(
                count: 120,
                startPrice: 100,
                step: 0.25,
                volumeSelector: i => 2_000 + (i % 10) * 250);
            var accessor = BuildAccessor(bars);
            var detector = CreateDetector(new VolumeProfileParameters
            {
                LookbackBars = 150,
                MinBars = 80,
                NumberOfBuckets = 30,
                ValueAreaPercentage = 0.70,
                HighVolumeThreshold = 1.35,
                LowVolumeThreshold = 0.55,
                PrimaryTimeFrame = "H1"
            });

            detector.Detect(accessor); // warm-up JIT

            const int iterations = 200;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                detector.Detect(accessor);
            }

            sw.Stop();
            var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
            Assert.True(avgMs < 5.0, $"Expected <5ms, thực tế {avgMs:F3}ms");
        }

        private static VolumeProfileDetector CreateDetector(VolumeProfileParameters? parameters = null)
        {
            parameters ??= new VolumeProfileParameters
            {
                LookbackBars = 60,
                MinBars = 20,
                NumberOfBuckets = 16,
                ValueAreaPercentage = 0.70,
                HighVolumeThreshold = 1.4,
                LowVolumeThreshold = 0.6,
                PrimaryTimeFrame = "H1"
            };

            return new VolumeProfileDetector(parameters);
        }

        private static SnapshotDataAccessor BuildAccessor(IReadOnlyList<Bar> bars, TimeFrame? timeFrameOverride = null)
        {
            if (bars == null || bars.Count == 0)
            {
                throw new ArgumentException("Bars collection must contain at least one element", nameof(bars));
            }

            var timeframe = timeFrameOverride ?? bars[0].TimeFrame;
            var history = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
            {
                [timeframe] = bars
            };

            var latest = new Dictionary<TimeFrame, Bar>
            {
                [timeframe] = bars[^1]
            };

            var snapshot = new PreprocessorSnapshot(
                DateTime.UtcNow,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                latest,
                null,
                false,
                null);

            return new SnapshotDataAccessor(snapshot, history);
        }

        private static List<Bar> CreateSequentialBars(
            int count,
            double startPrice,
            double step,
            Func<int, long>? volumeSelector = null,
            Func<int, double?>? closeSelector = null,
            TimeFrame timeFrame = TimeFrame.H1)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            volumeSelector ??= _ => 1_500L;
            var bars = new List<Bar>(count);
            var baseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < count; i++)
            {
                var nominalPrice = startPrice + step * i;
                var lowGuess = nominalPrice - 0.25;
                var highGuess = nominalPrice + 0.25;
                var close = closeSelector?.Invoke(i) ?? nominalPrice;
                var actualLow = Math.Min(Math.Min(lowGuess, close), highGuess);
                var actualHigh = Math.Max(Math.Max(highGuess, close), actualLow);
                var open = Math.Clamp(nominalPrice, actualLow, actualHigh);
                var volume = Math.Max(1L, volumeSelector(i));
                bars.Add(new Bar(
                    baseTime.AddMinutes(i * 60),
                    open,
                    actualHigh,
                    actualLow,
                    close,
                    volume,
                    timeFrame));
            }

            return bars;
        }

        private static double GetDouble(IDictionary<string, object> diagnostics, string key)
        {
            return diagnostics.TryGetValue(key, out var value)
                ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
                : double.NaN;
        }

        private static int GetInt(IDictionary<string, object> diagnostics, string key)
        {
            return diagnostics.TryGetValue(key, out var value)
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : 0;
        }

        private static bool GetBool(IDictionary<string, object> diagnostics, string key)
        {
            return diagnostics.TryGetValue(key, out var value)
                && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
                && parsed;
        }
    }
}
