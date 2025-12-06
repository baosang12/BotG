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
    public sealed class LiquidityAnalyzerTests
    {
        private readonly ITestOutputHelper _output;

        public LiquidityAnalyzerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Detect_WithWickRejections_ReturnsStrongLiquidityFlag()
        {
            var bars = CreateRangeBars(80);
            InjectUpperWick(bars, 25, wickSize: 4.5, volumeMultiplier: 2.2);
            InjectUpperWick(bars, 40, wickSize: 4.0, volumeMultiplier: 2.0);

            var result = RunAnalyzer(bars);
            LogDiagnostics(nameof(Detect_WithWickRejections_ReturnsStrongLiquidityFlag), result);

            Assert.True(result.Score < 45, $"Score={result.Score}");
            Assert.Contains("StrongLiquidityGrab", result.Flags);
            Assert.Equal("OK", result.Diagnostics["Status"]);
            Assert.True((int)result.Diagnostics["WickRejectionCount"] >= 2);
        }

        [Fact]
        public void Detect_WithFalseBreakouts_ReturnsFalseBreakoutFlag()
        {
            var bars = CreateRangeBars(80);
            var pivotIndex = 34;
            MakeSharpSwingHigh(bars, pivotIndex, heightBoost: 3.5);
            ForceFalseBreakout(bars, pivotIndex + 3, pivotIndex, retraceBelowPivot: 1.5);
            _output.WriteLine($"PivotHigh={bars[pivotIndex].High:F2}, BreakHigh={bars[pivotIndex + 3].High:F2}, BreakClose={bars[pivotIndex + 3].Close:F2}");
            for (var i = pivotIndex - 2; i <= pivotIndex + 2; i++)
            {
                _output.WriteLine($"idx {i} high {bars[i].High:F2}");
            }

            var swingHighs = FindSwingHighIndices(bars, window: 2);
            _output.WriteLine("SwingHighIndices=" + string.Join(',', swingHighs));
            var result = RunAnalyzer(bars, swingWindow: 2);
            LogDiagnostics(nameof(Detect_WithFalseBreakouts_ReturnsFalseBreakoutFlag), result);

            Assert.Contains("FalseBreakout", result.Flags);
            Assert.True(result.Score < 48, $"Score={result.Score}");
        }

        [Fact]
        public void Detect_WithCleanPriceAction_BoostsScore()
        {
            var bars = CreateBullishBars(90);
            StrengthenRecentMomentum(bars, recentCount: 25, bodyBoost: 1.6, volumeMultiplier: 1.8);

            var result = RunAnalyzer(bars);
            LogDiagnostics(nameof(Detect_WithCleanPriceAction_BoostsScore), result);

            Assert.True(result.Score > 58, $"Score={result.Score}");
            Assert.Contains("CleanPriceAction", result.Flags);
        }

        [Fact]
        public void Detect_WithMixedSignals_ReturnsModerateScore()
        {
            var bars = CreateBullishBars(90);
            InjectUpperWick(bars, 30, wickSize: 4.0, volumeMultiplier: 2.0);
            StrengthenRecentMomentum(bars, recentCount: 20, bodyBoost: 1.3, volumeMultiplier: 1.5);

            var result = RunAnalyzer(bars);
            LogDiagnostics(nameof(Detect_WithMixedSignals_ReturnsModerateScore), result);

            Assert.InRange(result.Score, 45, 60);
            Assert.Contains("LiquidityGrab", result.Flags);
            Assert.Contains("CleanPriceAction", result.Flags);
        }

        [Fact]
        public void Detect_WithInsufficientData_ReturnsBaseline()
        {
            var bars = CreateRangeBars(20);

            var result = RunAnalyzer(bars);
            LogDiagnostics(nameof(Detect_WithInsufficientData_ReturnsBaseline), result);

            Assert.Equal(50.0, result.Score);
            Assert.Equal("InsufficientData", result.Diagnostics["Status"]);
        }

        [Fact]
        public void Detect_EmitsDiagnosticsForCounts()
        {
            var bars = CreateRangeBars(80);
            InjectUpperWick(bars, 20, wickSize: 4.2, volumeMultiplier: 1.9);
            var pivot = 34;
            MakeSharpSwingHigh(bars, pivot, heightBoost: 3.0);
            ForceFalseBreakout(bars, pivot + 3, pivot, retraceBelowPivot: 1.4);

            var result = RunAnalyzer(bars);
            LogDiagnostics(nameof(Detect_EmitsDiagnosticsForCounts), result);

            Assert.True(result.Diagnostics.ContainsKey("WickRejectionCount"));
            Assert.True(result.Diagnostics.ContainsKey("FalseBreakoutCount"));
            Assert.True(result.Diagnostics.ContainsKey("ScoreBreakdown"));
        }

        private static PatternDetectionResult RunAnalyzer(List<Bar> bars, int swingWindow = 3)
        {
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1);
            var analyzer = new LiquidityAnalyzer(TimeFrame.H1, analysisBars: Math.Max(bars.Count, 60), swingWindow: swingWindow);
            return analyzer.Detect(fixture.Accessor);
        }

        private static List<Bar> CreateRangeBars(int count)
        {
            return CreateSyntheticBars(count, basePrice: 100, driftPerBar: 0.0, oscillationAmplitude: 0.6, alternateDirection: true);
        }

        private static List<Bar> CreateBullishBars(int count)
        {
            return CreateSyntheticBars(count, basePrice: 100, driftPerBar: 0.35, oscillationAmplitude: 0.2, alternateDirection: false);
        }

        private static void InjectUpperWick(List<Bar> bars, int index, double wickSize, double volumeMultiplier)
        {
            var template = bars[index];
            var open = template.Open;
            var close = template.Close;
            var high = Math.Max(open, close) + wickSize;
            var low = Math.Min(open, close) - 0.2;
            var volume = (long)Math.Max(template.Volume * volumeMultiplier, template.Volume + 1);
            bars[index] = new Bar(template.OpenTimeUtc, open, high, low, close, volume, template.TimeFrame);
        }

        private static void MakeSharpSwingHigh(List<Bar> bars, int index, double heightBoost)
        {
            var template = bars[index];
            var open = template.Open;
            var close = template.Close + 0.6;
            var high = template.High + heightBoost;
            var low = Math.Min(template.Low, open - 0.4);
            var volume = (long)(template.Volume * 1.5);
            bars[index] = new Bar(template.OpenTimeUtc, open, high, low, close, volume, template.TimeFrame);

            for (var offset = 1; offset <= 3; offset++)
            {
                var cap = high - (0.6 + offset * 0.15);
                ReduceNeighborHigh(bars, index - offset, cap);
                ReduceNeighborHigh(bars, index + offset, cap);
            }
        }

        private static void ReduceNeighborHigh(List<Bar> bars, int index, double cap)
        {
            if (index < 0 || index >= bars.Count)
            {
                return;
            }

            var template = bars[index];
            var high = Math.Min(template.High, cap);
            var low = Math.Min(template.Low, high - 0.3);
            bars[index] = new Bar(template.OpenTimeUtc, template.Open, high, low, template.Close, template.Volume, template.TimeFrame);
        }

        private static void ForceFalseBreakout(List<Bar> bars, int breakoutIndex, int pivotIndex, double retraceBelowPivot)
        {
            if (breakoutIndex >= bars.Count)
            {
                breakoutIndex = bars.Count - 1;
            }

            pivotIndex = Math.Clamp(pivotIndex, 0, bars.Count - 1);

            var template = bars[breakoutIndex];
            var pivot = bars[pivotIndex];
            var pivotHigh = pivot.High;
            var high = pivotHigh + 1.8;
            var close = pivotHigh - Math.Max(0.4, Math.Abs(retraceBelowPivot));
            var open = pivotHigh + 0.4;
            var low = Math.Min(template.Low, close - 0.6);
            var volume = (long)(template.Volume * 0.95);
            bars[breakoutIndex] = new Bar(template.OpenTimeUtc, open, high, low, close, volume, template.TimeFrame);
        }

        private static void StrengthenRecentMomentum(List<Bar> bars, int recentCount, double bodyBoost, double volumeMultiplier)
        {
            var start = Math.Max(0, bars.Count - recentCount);
            for (var i = start; i < bars.Count; i++)
            {
                var template = bars[i];
                var body = Math.Abs(template.Close - template.Open) * bodyBoost;
                var open = template.Open;
                var close = open + Math.Max(body, 0.4);
                var high = Math.Max(open, close) + 0.15;
                var low = Math.Min(open, close) - 0.15;
                var volume = (long)Math.Max(template.Volume * volumeMultiplier, template.Volume + 1);
                bars[i] = new Bar(template.OpenTimeUtc, open, high, low, close, volume, template.TimeFrame);
            }
        }

        private static List<int> FindSwingHighIndices(IReadOnlyList<Bar> bars, int window)
        {
            var indices = new List<int>();
            for (var i = window; i < bars.Count - window; i++)
            {
                var pivot = bars[i];
                var isHigh = true;
                for (var j = i - window; j <= i + window && isHigh; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    if (bars[j].High >= pivot.High)
                    {
                        isHigh = false;
                    }
                }

                if (isHigh)
                {
                    indices.Add(i);
                }
            }

            return indices;
        }

        private static List<Bar> CreateSyntheticBars(
            int count,
            double basePrice,
            double driftPerBar,
            double oscillationAmplitude,
            bool alternateDirection)
        {
            var bars = new List<Bar>(count);
            var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            for (var i = 0; i < count; i++)
            {
                var wave = Math.Sin(i / 4.0) * oscillationAmplitude;
                var center = basePrice + i * driftPerBar + wave;
                var direction = alternateDirection ? (i % 2 == 0 ? 1 : -1) : 1;
                var body = alternateDirection ? 0.35 : 0.45;
                var open = center - direction * body * 0.5;
                var close = open + direction * body;
                var high = Math.Max(open, close) + 0.2;
                var low = Math.Min(open, close) - 0.2;
                var volume = 950 + i * 8;
                bars.Add(new Bar(time, open, high, low, close, volume, TimeFrame.H1));
                time = time.AddHours(1);
            }

            return bars;
        }

        private void LogDiagnostics(string testName, PatternDetectionResult result)
        {
            if (_output == null)
            {
                return;
            }

            var flags = result.Flags != null && result.Flags.Count > 0
                ? string.Join(",", result.Flags)
                : "<none>";
            _output.WriteLine($"{testName}: Score={result.Score:F2}, Confidence={result.Confidence:F2}, Flags={flags}");
            foreach (var pair in result.Diagnostics)
            {
                _output.WriteLine($" - {pair.Key}: {pair.Value}");
            }
        }
    }
}
