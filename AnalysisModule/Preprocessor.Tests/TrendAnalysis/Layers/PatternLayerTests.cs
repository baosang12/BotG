using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis.Layers
{
    public sealed class PatternLayerTests
    {
        private readonly SnapshotFixture _fixture = TestDataFixtures.CreateBullishTrendScenario();

        [Fact]
        public void CalculateScore_WithNoDetectorsEnabled_ReturnsBaseline()
        {
            var detectors = new List<IPatternDetector>
            {
                new StubDetector("Disabled", 0.5, score: 90) { IsEnabled = false }
            };

            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance, detectors, baselineScore: 55.0);

            var score = layer.CalculateScore(_fixture.Snapshot, _fixture.Accessor);

            Assert.Equal(55.0, score);
        }

        [Fact]
        public void CalculateScore_WithWeightedDetectors_UsesWeights()
        {
            var detectors = new List<IPatternDetector>
            {
                new StubDetector("A", 0.3, 80),
                new StubDetector("B", 0.2, 20)
            };

            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance, detectors, baselineScore: 50.0);

            var score = layer.CalculateScore(_fixture.Snapshot, _fixture.Accessor);

            Assert.InRange(score, 52.9, 53.1);
        }

        [Fact]
        public void CalculateScore_WhenDetectorAddsFlags_AggregatesDiagnostics()
        {
            var detector = new StubDetector("Liquidity", 0.4, 30, new[] { "LiquidityGrab" });
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance, new[] { detector }, baselineScore: 50);

            _ = layer.CalculateScore(_fixture.Snapshot, _fixture.Accessor);
            var diagnostics = layer.GetDiagnostics();

            Assert.True(diagnostics.ContainsKey("PatternFlags"));
            var flags = diagnostics["PatternFlags"] as string[];
            Assert.NotNull(flags);
            Assert.Contains("LiquidityGrab", flags);
        }

        [Fact]
        public void UpdateDetectorConfig_TogglesState()
        {
            var detector = new StubDetector("Custom", 0.3, 60);
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance, new[] { detector }, baselineScore: 50);

            layer.UpdateDetectorConfig("Custom", isEnabled: false, weight: 0.1);

            Assert.False(detector.IsEnabled);
            Assert.Equal(0.1, detector.Weight);
        }

        [Fact]
        public void CalculateScore_WithNullSnapshot_ReturnsBaseline()
        {
            var detector = new StubDetector("Any", 0.3, 70);
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance, new[] { detector }, baselineScore: 45);

            var score = layer.CalculateScore(null, _fixture.Accessor);

            Assert.Equal(45, score);
        }

        [Fact]
        public void UpdateConfig_OverridesDetectorSettingsFromConfig()
        {
            var liquidity = new LiquidityAnalyzer();
            var breakout = new BreakoutQualityEvaluator();
            var detectors = new List<IPatternDetector> { liquidity, breakout };
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance, detectors, baselineScore: 50);

            var config = new TrendAnalyzerConfig
            {
                PatternLayer = new PatternLayerConfig
                {
                    Liquidity = new PatternDetectorConfig
                    {
                        Enabled = false,
                        Weight = 0.1
                    },
                    BreakoutQuality = new BreakoutQualityDetectorConfig
                    {
                        Enabled = true,
                        Weight = 0.4,
                        Parameters = new BreakoutQualityParameters
                        {
                            Scoring = new ScoringParams { Baseline = 64.0 },
                            DataRequirements = new DataRequirements { MinimumBars = 80 }
                        }
                    }
                }
            };

            layer.UpdateConfig(config);

            Assert.False(liquidity.IsEnabled);
            Assert.Equal(0.1, liquidity.Weight);
            Assert.Equal(0.4, breakout.Weight);

            var fixture = TestDataFixtures.CreateSnapshotFixture(TestDataFixtures.CreateRangeBars(35), TimeFrame.H1);
            var detection = ((BreakoutQualityEvaluator)breakout).Detect(fixture.Accessor);
            Assert.Equal(64.0, detection.Score);
        }

        private sealed class StubDetector : IPatternDetector
        {
            private readonly double _score;
            private readonly string[] _flags;

            public StubDetector(string name, double weight, double score, string[] flags = null)
            {
                Name = name;
                Weight = weight;
                _score = score;
                _flags = flags ?? Array.Empty<string>();
            }

            public string Name { get; }

            public double Weight { get; set; }

            public bool IsEnabled { get; set; } = true;

            public PatternDetectionResult Detect(SnapshotDataAccessor accessor)
            {
                return new PatternDetectionResult
                {
                    Score = _score,
                    Flags = new List<string>(_flags)
                };
            }
        }
    }
}
