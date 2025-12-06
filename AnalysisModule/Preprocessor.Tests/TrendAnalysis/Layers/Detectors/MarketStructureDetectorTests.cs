using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;
using AnalysisModule.Preprocessor.Tests.TrendAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis.Layers.Detectors
{
    public sealed class MarketStructureDetectorTests
    {
        private readonly ITestOutputHelper _output;

        public MarketStructureDetectorTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Detect_WithHigherHighsAndHigherLows_ReturnsUptrend()
        {
            var bars = CreateTrendBars(count: 140, slopePerBar: 0.00065);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new MarketStructureDetector();

            var result = detector.Detect(fixture.Accessor);
            DumpDiagnostics("Uptrend", result);

            Assert.True(result.Score > 60, "Uptrend score phải cao hơn baseline");
            Assert.Equal("Uptrend", Assert.IsType<string>(result.Diagnostics["Structure"]));
            Assert.Equal(1, Assert.IsType<int>(result.Diagnostics["TrendDirection"]));
            Assert.Contains("MarketStructureUptrend", result.Flags);
        }

        [Fact]
        public void Detect_WithLowerHighsAndLowerLows_ReturnsDowntrend()
        {
            var bars = CreateTrendBars(count: 140, slopePerBar: -0.0006);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new MarketStructureDetector();

            var result = detector.Detect(fixture.Accessor);
            DumpDiagnostics("Downtrend", result);

            Assert.True(result.Score < 45);
            Assert.Equal("Downtrend", Assert.IsType<string>(result.Diagnostics["Structure"]));
            Assert.Equal(-1, Assert.IsType<int>(result.Diagnostics["TrendDirection"]));
            Assert.Contains("MarketStructureDowntrend", result.Flags);
        }

        [Fact]
        public void Detect_WithSidewaysStaircase_ClassifiesRange()
        {
            var bars = CreateRangeBars();
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new MarketStructureDetector();

            var result = detector.Detect(fixture.Accessor);
            DumpDiagnostics("Range", result);

            Assert.Equal("Range", Assert.IsType<string>(result.Diagnostics["Structure"]));
            Assert.Contains("MarketStructureRange", result.Flags);
            Assert.InRange(result.Score, 40, 60);
        }

        [Fact]
        public void Detect_WhenPriceBreaksAboveSwing_FlagsBreakUp()
        {
            var bars = CreateTrendBars(140, 0.0004);
            InjectSwingPlateau(bars, bars.Count - 30, height: 0.0035);
            ForceBreakout(bars, breakoutHeight: 0.0045);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new MarketStructureDetector();

            var result = detector.Detect(fixture.Accessor);
            DumpDiagnostics("Break", result);

            Assert.True((bool)result.Diagnostics["BreakDetected"]);
            Assert.Equal(1, Assert.IsType<int>(result.Diagnostics["BreakDirection"]));
            Assert.Contains("StructureBreakUp", result.Flags);
            Assert.True(result.Score > 65);
        }

        [Fact]
        public void Detect_InsufficientBars_ReturnsNeutral()
        {
            var bars = CreateTrendBars(40, 0.0005);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new MarketStructureDetector();

            var result = detector.Detect(fixture.Accessor);

            Assert.Equal(50, result.Score);
            Assert.Equal("InsufficientData", Assert.IsType<string>(result.Diagnostics["Status"]));
        }

        [Fact]
        public void Detect_CustomParameters_AllowsTighterSwingFilter()
        {
            var parameters = new MarketStructureParameters
            {
                MinSwingAmplitudePips = 5,
                MinSwingHeightAtr = 0.25,
                RangeThresholdAtr = 1.2,
                BreakBufferPips = 3,
                PipSize = 0.0001
            };
            var bars = CreateTrendBars(120, 0.0003);
            InjectSwingPlateau(bars, bars.Count - 40, height: 0.0025);
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var detector = new MarketStructureDetector(parameters);

            var result = detector.Detect(fixture.Accessor);
            DumpDiagnostics("Custom", result);

            Assert.True(result.Score > 55);
            Assert.Equal("Uptrend", Assert.IsType<string>(result.Diagnostics["Structure"]));
        }

        private void DumpDiagnostics(string label, PatternDetectionResult result)
        {
            if (_output == null)
            {
                return;
            }

            var flags = result.Flags != null && result.Flags.Count > 0
                ? string.Join('|', result.Flags)
                : "(none)";
            _output.WriteLine(
                "[{0}] Score={1:F2} Trend={2} Structure={3} Status={4} Break={5} SwingPoints={6} Flags={7}",
                label,
                result.Score,
                result.Diagnostics.TryGetValue("TrendDirection", out var trend) ? trend : "?",
                result.Diagnostics.TryGetValue("Structure", out var structure) ? structure : "?",
                result.Diagnostics.TryGetValue("Status", out var status) ? status : "?",
                result.Diagnostics.TryGetValue("BreakDetected", out var brk) ? brk : "?",
                result.Diagnostics.TryGetValue("SwingPoints", out var swings) ? swings : "?",
                flags);
        }

        private static List<Bar> CreateTrendBars(int count, double slopePerBar)
        {
            var bars = new List<Bar>(count);
            var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var price = 1.1200;
            for (var i = 0; i < count; i++)
            {
                price += slopePerBar;
                var oscillation = Math.Sin(i / 6.0) * 0.0008;
                var basePrice = price + oscillation;
                var open = basePrice - 0.0007;
                var close = basePrice + 0.0007;
                var high = close + (i % 12 == 0 ? 0.0025 : 0.0015);
                var low = open - (i % 10 == 5 ? 0.0022 : 0.0014);
                bars.Add(new Bar(time, open, high, low, close, 1200 + i * 3, TimeFrame.H1));
                time = time.AddHours(1);
            }

            return bars;
        }

        private static List<Bar> CreateRangeBars()
        {
            var bars = new List<Bar>();
            var time = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            var price = 1.0850;
            for (var i = 0; i < 130; i++)
            {
                var wave = Math.Sin(i / 4.0) * 0.0006;
                var open = price + wave - 0.0004;
                var close = price + wave + 0.0004;
                var high = close + 0.0009;
                var low = open - 0.0009;
                bars.Add(new Bar(time, open, high, low, close, 900 + i * 2, TimeFrame.H1));
                time = time.AddHours(1);
            }

            return bars;
        }

        private static void InjectSwingPlateau(IList<Bar> bars, int index, double height)
        {
            var bar = bars[index];
            var newHigh = bar.High + height;
            bars[index] = new Bar(bar.OpenTimeUtc, bar.Open, newHigh, bar.Low, bar.Close + height * 0.2, bar.Volume, bar.TimeFrame);
        }

        private static void ForceBreakout(IList<Bar> bars, double breakoutHeight)
        {
            for (var i = bars.Count - 3; i < bars.Count; i++)
            {
                var bar = bars[i];
                var high = bar.High + breakoutHeight;
                var low = bar.Low + breakoutHeight * 0.3;
                var open = low + breakoutHeight * 0.2;
                var close = high - breakoutHeight * 0.1;
                bars[i] = new Bar(bar.OpenTimeUtc, open, high, low, close, bar.Volume * 2, bar.TimeFrame);
            }
        }
    }
}
