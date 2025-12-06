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
    public sealed class BreakoutQualityEvaluatorTests
    {
        private readonly ITestOutputHelper _output;

        public BreakoutQualityEvaluatorTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Detect_StrongBreakoutAndFollowThrough_ReturnsHighScoreAndFlags()
        {
            var result = RunEvaluator(PatternLayerTestScenarios.CreateBreakoutScenarioBars());
            LogDiagnostics(nameof(Detect_StrongBreakoutAndFollowThrough_ReturnsHighScoreAndFlags), result);

            Assert.True(result.Score > 62, $"Score={result.Score:F2}");
            Assert.Equal("OK", result.Diagnostics["Status"]);
            Assert.True(ContainsFlag(result, "BreakoutFollowThrough"));
            Assert.True(ContainsFlag(result, "HighQualityBreakout"));
        }

        [Fact]
        public void Detect_WeakBreakout_ReturnsNeutralScore()
        {
            var result = RunEvaluator(PatternLayerTestScenarios.CreateBreakoutScenarioBars(weakBreakout: true, followThroughExtension: 0.8, limitedFollowThrough: true));
            LogDiagnostics(nameof(Detect_WeakBreakout_ReturnsNeutralScore), result);

            Assert.True(result.Score <= 55, $"Score={result.Score:F2}");
            Assert.False(ContainsFlag(result, "HighQualityBreakout"));
        }

        [Fact]
        public void Detect_RetestTooShallow_FlagsWarning()
        {
            var result = RunEvaluator(PatternLayerTestScenarios.CreateBreakoutScenarioBars(shallowRetest: true));
            LogDiagnostics(nameof(Detect_RetestTooShallow_FlagsWarning), result);

            Assert.True(ContainsFlag(result, "RetestTooShallow"));
            Assert.True(result.Score < 60);
        }

        [Fact]
        public void Detect_RetestTooDeep_FlagsPenalty()
        {
            var result = RunEvaluator(PatternLayerTestScenarios.CreateBreakoutScenarioBars(deepRetest: true));
            LogDiagnostics(nameof(Detect_RetestTooDeep_FlagsPenalty), result);

            Assert.True(ContainsFlag(result, "RetestTooDeep"));
            Assert.True(result.Score < 45, $"Score={result.Score:F2}");
        }

        [Fact]
        public void Detect_FalseBreakout_ReturnsFailedFlag()
        {
            var parameters = BreakoutQualityParameters.CreateDefault();
            parameters.FollowThrough.FailureGivebackFraction = 0.3;
            parameters.FollowThrough.MinExtensionAtr = 0.5;
            var evaluator = new BreakoutQualityEvaluator(parameters);
            var bars = PatternLayerTestScenarios.CreateBreakoutScenarioBars(failure: true, followThroughExtension: 1.4, limitedFollowThrough: true);
            var result = RunEvaluator(bars, evaluator: evaluator);
            LogDiagnostics(nameof(Detect_FalseBreakout_ReturnsFailedFlag), result);

            Assert.True(ContainsFlag(result, "FailedBreakout"));
            Assert.True(result.Score < 48, $"Score={result.Score:F2}");
        }

        [Fact]
        public void Detect_WithInsufficientData_ReturnsBaseline()
        {
            var fixture = TestDataFixtures.CreateSnapshotFixture(TestDataFixtures.CreateRangeBars(20), TimeFrame.H1);
            var result = new BreakoutQualityEvaluator().Detect(fixture.Accessor);
            LogDiagnostics(nameof(Detect_WithInsufficientData_ReturnsBaseline), result);

            Assert.Equal(50.0, result.Score);
            Assert.Equal("InsufficientData", result.Diagnostics["Status"]);
        }

        [Fact]
        public void Detect_ConfigOverrides_AffectThresholds()
        {
            var bars = PatternLayerTestScenarios.CreateBreakoutScenarioBars(weakBreakout: true, followThroughExtension: 0.7, limitedFollowThrough: true);
            var fixture = BuildFixture(bars);

            var baseline = new BreakoutQualityEvaluator().Detect(fixture.Accessor);

            var tunedParams = BreakoutQualityParameters.CreateDefault();
            tunedParams.Breakout.MinBodyRatio = 0.35;
            tunedParams.Breakout.TargetBodyRatio = 0.6;
            tunedParams.Breakout.MinRangeFactor = 0.9;
            tunedParams.Breakout.TargetRangeFactor = 1.2;
            tunedParams.Breakout.MinVolumeSpike = 1.05;
            tunedParams.Breakout.TargetVolumeSpike = 1.4;
            tunedParams.Scoring.BreakoutWeight = 25;
            tunedParams.Scoring.RetestWeight = 12;
            tunedParams.Scoring.FollowThroughWeight = 12;
            tunedParams.FollowThrough.MinExtensionAtr = 0.4;
            tunedParams.FollowThrough.StrongExtensionAtr = 1.0;
            tunedParams.DataRequirements.MinimumBars = 60;
            tunedParams.DataRequirements.AnalysisBars = bars.Count;

            var tuned = new BreakoutQualityEvaluator(tunedParams).Detect(fixture.Accessor);
            LogDiagnostics(nameof(Detect_ConfigOverrides_AffectThresholds) + "_Baseline", baseline);
            LogDiagnostics(nameof(Detect_ConfigOverrides_AffectThresholds) + "_Tuned", tuned);

            Assert.True(tuned.Score > baseline.Score + 5, $"Baseline={baseline.Score:F2} Tuned={tuned.Score:F2}");
        }

        [Fact]
        public void Detect_MultiTimeframeConfluence_AddsFlag()
        {
            var result = RunEvaluator(PatternLayerTestScenarios.CreateBreakoutScenarioBars(), includeHigherTimeframe: true);
            LogDiagnostics(nameof(Detect_MultiTimeframeConfluence_AddsFlag), result);

            Assert.True(ContainsFlag(result, "MultiTimeframeConfluence"));
        }

        private static PatternDetectionResult RunEvaluator(List<Bar> bars, bool includeHigherTimeframe = false, BreakoutQualityEvaluator evaluator = null)
        {
            var fixture = BuildFixture(bars, includeHigherTimeframe);
            return (evaluator ?? new BreakoutQualityEvaluator()).Detect(fixture.Accessor);
        }

        private static SnapshotFixture BuildFixture(IReadOnlyList<Bar> bars, bool includeHigherTimeframe = false)
        {
            IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> history;
            if (includeHigherTimeframe)
            {
                history = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
                {
                    [TimeFrame.H1] = bars,
                    [TimeFrame.H4] = AggregateBars(bars, 4, TimeFrame.H4)
                };
            }
            else
            {
                history = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
                {
                    [TimeFrame.H1] = bars
                };
            }

            return TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1, null, history);
        }


        private static IReadOnlyList<Bar> AggregateBars(IReadOnlyList<Bar> source, int groupSize, TimeFrame target)
        {
            if (source.Count == 0)
            {
                return Array.Empty<Bar>();
            }

            var result = new List<Bar>();
            for (var i = 0; i < source.Count; i += groupSize)
            {
                var slice = source.Skip(i).Take(groupSize).ToList();
                if (slice.Count == 0)
                {
                    break;
                }

                var openTime = slice[0].OpenTimeUtc;
                var open = slice[0].Open;
                var close = slice[^1].Close;
                var high = slice.Max(b => b.High);
                var low = slice.Min(b => b.Low);
                var volume = slice.Sum(b => b.Volume);
                result.Add(new Bar(openTime, open, high, low, close, volume, target));
            }

            return result;
        }

        private static bool ContainsFlag(PatternDetectionResult result, string flag)
        {
            if (result.Flags == null || result.Flags.Count == 0)
            {
                return false;
            }

            return result.Flags.Any(f => f != null && f.Equals(flag, StringComparison.OrdinalIgnoreCase));
        }

        private void LogDiagnostics(string scenario, PatternDetectionResult result)
        {
            if (_output == null)
            {
                return;
            }

            _output.WriteLine($"{scenario}: Score={result.Score:F2}, Confidence={result.Confidence:F2}");
            if (result.Flags != null && result.Flags.Count > 0)
            {
                _output.WriteLine($" - Flags: {string.Join(", ", result.Flags)}");
            }
            foreach (var pair in result.Diagnostics)
            {
                if (pair.Value is IDictionary<string, object> dict)
                {
                    _output.WriteLine($" - {pair.Key}:");
                    foreach (var inner in dict)
                    {
                        _output.WriteLine($"    * {inner.Key}={inner.Value}");
                    }
                }
                else
                {
                    _output.WriteLine($" - {pair.Key}: {pair.Value}");
                }
            }
        }
    }
}
