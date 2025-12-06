using System;
using System.Collections.Generic;
using System.Linq;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;
using AnalysisModule.Preprocessor.Tests.TrendAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis.Layers.Detectors
{
    public sealed class AccumulationDistributionDetectorTests
    {
        private readonly ITestOutputHelper _output;

        public AccumulationDistributionDetectorTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Detect_BearishAbsorptionNearSupport_FlagsAccumulation()
        {
            var bars = CreateTrendingBars(slopePerBar: -0.0007);
            var swingLow = InjectSwingLow(bars, bars.Count - 15, depth: 0.004);
            BoostRecentVolume(bars, bars.Count - 5, multiplier: 2.2);
            CompressLastRange(bars, halfRange: 0.0003);
            MovePriceNearLevel(bars, bars.Count - 1, swingLow + 0.0002, 0.0006);

            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new AccumulationDistributionDetector();

            var result = detector.Detect(fixture.Accessor);

            var accumulationFlags = result.Flags ?? new List<string>();
            Assert.Contains("Accumulation", accumulationFlags);
            Assert.True(result.Score > 60, "Accumulation score nên cao hơn baseline");
            Assert.Equal("Accumulation", Assert.IsType<string>(result.Diagnostics["Phase"]));
            Assert.True((double)result.Diagnostics["VolumeRatio"] > 1.1);
            Assert.True((bool)result.Diagnostics["NearSupport"]);
        }

        [Fact]
        public void Detect_BullishDistributionNearResistance_FlagsDistribution()
        {
            var bars = CreateTrendingBars(slopePerBar: 0.0007);
            var swingHigh = InjectSwingHigh(bars, bars.Count - 18, height: 0.004);
            ReduceRecentVolume(bars, bars.Count - 5, divider: 2.5);
            MovePriceNearLevel(bars, bars.Count - 1, swingHigh - 0.0002, 0.0025);
            ExpandRangeAroundClose(bars, bars.Count - 1, halfRange: 0.005);

            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new AccumulationDistributionDetector();

            var result = detector.Detect(fixture.Accessor);

            var distributionFlags = result.Flags ?? new List<string>();
            Assert.Contains("Distribution", distributionFlags);
            Assert.True(result.Score < 45, "Distribution nên kéo điểm xuống dưới 50");
            Assert.Equal("Distribution", Assert.IsType<string>(result.Diagnostics["Phase"]));
            Assert.True((bool)result.Diagnostics["NearResistance"]);
        }

        [Fact(DisplayName = "Demo tích lũy/phân phối ghi log")]
        public void Demo_AccumulationDistribution_PrintsSummary()
        {
            var detector = new AccumulationDistributionDetector();

            var accumulationBars = CreateTrendingBars(slopePerBar: -0.0006);
            var accumulationSwing = InjectSwingLow(accumulationBars, accumulationBars.Count - 18, depth: 0.003);
            BoostRecentVolume(accumulationBars, accumulationBars.Count - 6, multiplier: 1.9);
            CompressLastRange(accumulationBars, halfRange: 0.00035);
            MovePriceNearLevel(accumulationBars, accumulationBars.Count - 1, accumulationSwing + 0.00025, 0.0008);
            var accumulationFixture = TestDataFixtures.CreateSnapshotFixture(accumulationBars, TimeFrame.H1);
            var accumulationResult = detector.Detect(accumulationFixture.Accessor);

            var distributionBars = CreateTrendingBars(slopePerBar: 0.0006);
            var swingHigh = InjectSwingHigh(distributionBars, distributionBars.Count - 16, height: 0.0035);
            ReduceRecentVolume(distributionBars, distributionBars.Count - 5, divider: 2.2);
            MovePriceNearLevel(distributionBars, distributionBars.Count - 1, swingHigh - 0.00025, 0.003);
            ExpandRangeAroundClose(distributionBars, distributionBars.Count - 1, halfRange: 0.006);
            var distributionFixture = TestDataFixtures.CreateSnapshotFixture(distributionBars, TimeFrame.H1);
            var distributionResult = detector.Detect(distributionFixture.Accessor);

            WriteDemoSummary("Accumulation", accumulationResult);
            WriteDemoSummary("Distribution", distributionResult);

            Assert.True(accumulationResult.Score > distributionResult.Score, "Accumulation nên có điểm cao hơn distribution");
            Assert.Contains("Accumulation", accumulationResult.Flags ?? new List<string>());
            Assert.Contains("Distribution", distributionResult.Flags ?? new List<string>());
        }

        [Fact]
        public void Detect_NeutralScenario_ReturnsBaseline()
        {
            var bars = CreateTrendingBars(slopePerBar: 0.0001);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new AccumulationDistributionDetector();

            var result = detector.Detect(fixture.Accessor);

            Assert.InRange(result.Score, 45, 55);
            Assert.Equal("Neutral", Assert.IsType<string>(result.Diagnostics["Phase"]));
        }

        [Fact]
        public void Detect_InsufficientBars_ReturnsNeutralDiagnostics()
        {
            var bars = CreateTrendingBars(count: 10);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new AccumulationDistributionDetector();

            var result = detector.Detect(fixture.Accessor);

            Assert.Equal(50, result.Score);
            Assert.Equal("InsufficientData", Assert.IsType<string>(result.Diagnostics["Status"]));
        }

        [Fact]
        public void Detect_CustomParameters_LowersThresholds()
        {
            var bars = CreateTrendingBars(slopePerBar: -0.00025);
            var swingLow = InjectSwingLow(bars, bars.Count - 12, depth: 0.0025);
            BoostRecentVolume(bars, bars.Count - 4, multiplier: 1.4);
            CompressLastRange(bars, halfRange: 0.0006);
            MovePriceNearLevel(bars, bars.Count - 1, swingLow + 0.0003, 0.001);

            var parameters = new AccumulationDistributionParameters
            {
                TrendSlopeThreshold = 0.0001,
                VolumeRatioThreshold = 0.05,
                RangeNarrowThreshold = 1.0,
                RangeWideThreshold = 1.4,
                SrProximityFactor = 0.5,
                LookbackBars = 50,
                PrimaryTimeFrame = "H1"
            };

            var detector = new AccumulationDistributionDetector(parameters);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);

            var result = detector.Detect(fixture.Accessor);

            Assert.True(result.Score > 55);
            Assert.Equal("Accumulation", Assert.IsType<string>(result.Diagnostics["Phase"]));
        }

        [Fact]
        public void Detect_VolumeRatioZeroFallback_ReturnsSafeValue()
        {
            var bars = CreateTrendingBars();
            ZeroOutVolume(bars);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new AccumulationDistributionDetector();

            var result = detector.Detect(fixture.Accessor);

            Assert.Equal(1.0, Math.Round((double)result.Diagnostics["VolumeRatio"], 2));
        }

        [Fact]
        public void Detect_RangeClassificationRespondsToAtr()
        {
            var bars = CreateTrendingBars(slopePerBar: -0.0006);
            CompressLastRange(bars, halfRange: 0.0002);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new AccumulationDistributionDetector();

            var narrowResult = detector.Detect(fixture.Accessor);

            ExpandLastRange(bars, halfRange: 0.004);
            fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var wideResult = detector.Detect(fixture.Accessor);

            Assert.True((double)narrowResult.Diagnostics["CurrentRangeATR"] < 0.8);
            Assert.True((double)wideResult.Diagnostics["CurrentRangeATR"] > 1.2);
        }

        [Fact]
        public void Detect_DiagnosticsIncludeSignalCounts()
        {
            var bars = CreateTrendingBars(slopePerBar: -0.0007);
            InjectSwingLow(bars, bars.Count - 20, depth: 0.003);
            BoostRecentVolume(bars, bars.Count - 4, multiplier: 1.8);
            CompressLastRange(bars, halfRange: 0.0004);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new AccumulationDistributionDetector();

            var result = detector.Detect(fixture.Accessor);

            Assert.True(result.Diagnostics.ContainsKey("AccumulationSignals"));
            Assert.True(result.Diagnostics.ContainsKey("DistributionSignals"));
            Assert.True(result.Diagnostics.ContainsKey("Phase"));
        }

        private static List<Bar> CreateTrendingBars(int count = 80, double startPrice = 1.2000, double slopePerBar = -0.0005)
        {
            var bars = new List<Bar>(count);
            var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var price = startPrice;
            for (var i = 0; i < count; i++)
            {
                price += slopePerBar;
                var open = price - 0.0005;
                var close = price + 0.0005;
                var high = close + 0.0015;
                var low = open - 0.0015;
                var volume = 1_000 + i * 4;
                bars.Add(new Bar(time, open, high, low, close, volume, TimeFrame.H1));
                time = time.AddHours(1);
            }

            return bars;
        }

        private static double InjectSwingLow(IList<Bar> bars, int index, double depth)
        {
            var bar = bars[index];
            var newLow = bar.Low - depth;
            bars[index] = new Bar(bar.OpenTimeUtc, bar.Open, bar.High, newLow, bar.Close - depth * 0.2, bar.Volume, bar.TimeFrame);
            return newLow;
        }

        private static double InjectSwingHigh(IList<Bar> bars, int index, double height)
        {
            var bar = bars[index];
            var newHigh = bar.High + height;
            bars[index] = new Bar(bar.OpenTimeUtc, bar.Open, newHigh, bar.Low, bar.Close + height * 0.2, bar.Volume, bar.TimeFrame);
            return newHigh;
        }

        private static void BoostRecentVolume(IList<Bar> bars, int startIndex, double multiplier)
        {
            for (var i = startIndex; i < bars.Count; i++)
            {
                var bar = bars[i];
                bars[i] = new Bar(bar.OpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, (long)(bar.Volume * multiplier), bar.TimeFrame);
            }
        }

        private static void ReduceRecentVolume(IList<Bar> bars, int startIndex, double divider)
        {
            for (var i = startIndex; i < bars.Count; i++)
            {
                var bar = bars[i];
                bars[i] = new Bar(bar.OpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, (long)Math.Max(100, bar.Volume / divider), bar.TimeFrame);
            }
        }

        private static void ZeroOutVolume(IList<Bar> bars)
        {
            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                bars[i] = new Bar(bar.OpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, 0, bar.TimeFrame);
            }
        }

        private static void CompressLastRange(IList<Bar> bars, double halfRange)
        {
            var index = bars.Count - 1;
            var bar = bars[index];
            var mid = (bar.High + bar.Low) / 2;
            var open = mid - halfRange * 0.6;
            var close = mid + halfRange * 0.6;
            var high = mid + halfRange;
            var low = mid - halfRange;
            bars[index] = new Bar(bar.OpenTimeUtc, open, high, low, close, bar.Volume, bar.TimeFrame);
        }

        private static void ExpandLastRange(IList<Bar> bars, double halfRange)
        {
            var index = bars.Count - 1;
            var bar = bars[index];
            var mid = (bar.High + bar.Low) / 2;
            var open = mid - halfRange;
            var close = mid + halfRange;
            var high = mid + halfRange * 1.5;
            var low = mid - halfRange * 1.5;
            bars[index] = new Bar(bar.OpenTimeUtc, open, high, low, close, bar.Volume, bar.TimeFrame);
        }

        private static void ExpandRangeAroundClose(IList<Bar> bars, int index, double halfRange)
        {
            var bar = bars[index];
            var close = bar.Close;
            var open = close - halfRange * 0.4;
            var high = close + halfRange;
            var low = close - halfRange;
            bars[index] = new Bar(bar.OpenTimeUtc, open, high, low, close, bar.Volume, bar.TimeFrame);
        }

        private static void MovePriceNearLevel(IList<Bar> bars, int index, double level, double range)
        {
            var bar = bars[index];
            var open = level - range * 0.3;
            var close = level + range * 0.2;
            var high = close + range * 0.2;
            var low = level - range * 0.2;
            bars[index] = new Bar(bar.OpenTimeUtc, open, high, low, close, bar.Volume, bar.TimeFrame);
        }

        private void WriteDemoSummary(string label, PatternDetectionResult result)
        {
            var flags = result.Flags ?? new List<string>();
            var diagnostics = string.Join(", ", result.Diagnostics.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            _output?.WriteLine($"[{label}] Score={result.Score:F1} Confidence={result.Confidence:F2} Flags={string.Join('|', flags)}");
            _output?.WriteLine($"[{label}] Diagnostics: {diagnostics}");
        }
    }
}
