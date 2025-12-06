#nullable enable
using System;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.TrendAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis
{
    public sealed class MarketTrendAnalyzerIntegrationTests
    {
        private readonly FakeTrendAnalysisBridge _bridge;
        private readonly TrendAnalysisTelemetry _telemetry;
        private readonly MarketTrendAnalyzer _analyzer;

        public MarketTrendAnalyzerIntegrationTests()
        {
            _bridge = new FakeTrendAnalysisBridge();
            _telemetry = new TrendAnalysisTelemetry(NullLogger<TrendAnalysisTelemetry>.Instance);
            _analyzer = new MarketTrendAnalyzer(
                NullLogger<MarketTrendAnalyzer>.Instance,
                _bridge,
                _telemetry);
        }

        [Fact]
        public void Analyze_WithMomentumEnabled_PopulatesMomentumScore()
        {
            var config = CreateConfig(useMomentumLayer: true);
            _analyzer.Initialize(config);

            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Bullish);
            var signal = _analyzer.Analyze(fixture.Snapshot);

            Assert.NotNull(signal);
            Assert.True(signal!.MomentumScore > 0, "Momentum score phải lớn hơn 0 khi layer bật.");
            Assert.Same(signal, _bridge.LastPublishedSignal);
            Assert.Contains(signal.Confirmations, c => c.Contains("[Momentum]", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Analyze_WithMomentumDisabled_ReturnsZeroMomentumScore()
        {
            var config = CreateConfig(useMomentumLayer: false);
            _analyzer.Initialize(config);

            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Bullish);
            var signal = _analyzer.Analyze(fixture.Snapshot);

            Assert.NotNull(signal);
            Assert.Equal(0, signal!.MomentumScore);
        }

        [Fact]
        public void UpdateConfig_TogglesMomentumLayer()
        {
            var config = CreateConfig(useMomentumLayer: false);
            _analyzer.Initialize(config);
            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Bullish);

            var disabledSignal = _analyzer.Analyze(fixture.Snapshot);
            Assert.NotNull(disabledSignal);
            Assert.Equal(0, disabledSignal!.MomentumScore);

            config.FeatureFlags.UseMomentumLayer = true;
            _analyzer.UpdateConfig(config);

            var enabledSignal = _analyzer.Analyze(fixture.Snapshot);
            Assert.NotNull(enabledSignal);
            Assert.True(enabledSignal!.MomentumScore > 0);
        }

        [Fact]
        public void Analyze_WithPatternLayerEnabled_IncludesPatternScoreAndFlags()
        {
            var config = CreatePatternOnlyConfig();
            _analyzer.Initialize(config);

            var fixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.StrongBreakout);
            var signal = _analyzer.Analyze(fixture.Snapshot);

            Assert.NotNull(signal);
            Assert.True(signal!.PatternScore > 0);
            Assert.NotEmpty(signal.PatternFlags);
        }

        [Fact]
        public void PatternFlags_AdjustTrendSignalConfidence_Correctly()
        {
            var config = CreatePatternOnlyConfig(pattern =>
            {
                pattern.Liquidity.Weight = 0.5;
                pattern.BreakoutQuality.Weight = 0.5;
            });
            _analyzer.Initialize(config);

            var cleanFixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.CleanBreakout);
            var liquidityFixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.LiquidityGrab);

            var cleanSignal = _analyzer.Analyze(cleanFixture.Snapshot);
            var liquiditySignal = _analyzer.Analyze(liquidityFixture.Snapshot);

            Assert.NotNull(cleanSignal);
            Assert.NotNull(liquiditySignal);
            Assert.True(cleanSignal!.Confidence > liquiditySignal!.Confidence);
            Assert.True(cleanSignal.PatternScore > liquiditySignal.PatternScore);
        }

        [Fact]
        public void PatternLayer_DetectorWeights_AppliedCorrectly()
        {
            var fixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.StrongBreakout);

            var withLiquidity = CreatePatternOnlyConfig(pattern =>
            {
                DisableOtherPatternDetectors(pattern);
                pattern.Liquidity.Weight = 0.6;
                pattern.BreakoutQuality.Weight = 0.4;
            });
            _analyzer.Initialize(withLiquidity);
            var signalWithLiquidity = _analyzer.Analyze(fixture.Snapshot);
            Assert.NotNull(signalWithLiquidity);

            var breakoutOnly = CreatePatternOnlyConfig(pattern =>
            {
                DisableOtherPatternDetectors(pattern);
                pattern.Liquidity.Weight = 0.0;
                pattern.BreakoutQuality.Weight = 1.0;
            });
            _analyzer.UpdateConfig(breakoutOnly);
            var breakoutOnlySignal = _analyzer.Analyze(fixture.Snapshot);
            Assert.NotNull(breakoutOnlySignal);

            Assert.NotEqual(signalWithLiquidity!.PatternScore, breakoutOnlySignal!.PatternScore);
        }

        [Fact]
        public void PatternLayer_Disabled_ExcludesPatternScore()
        {
            var config = CreatePatternOnlyConfig();
            config.FeatureFlags.UsePatternLayer = false;
            config.LayerWeights.Patterns = 0;
            _analyzer.Initialize(config);

            var fixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.StrongBreakout);
            var signal = _analyzer.Analyze(fixture.Snapshot);

            Assert.NotNull(signal);
            Assert.Equal(0, signal!.PatternScore);
            Assert.Empty(signal.PatternFlags);
        }

        private static TrendAnalyzerConfig CreateConfig(bool useMomentumLayer)
        {
            return new TrendAnalyzerConfig
            {
                Enabled = true,
                Version = "test",
                FeatureFlags = new FeatureFlagsConfig
                {
                    UseStructureLayer = true,
                    UseMALayer = true,
                    UseMomentumLayer = useMomentumLayer,
                    UsePatternLayer = false,
                    EnableTelemetry = false
                },
                LayerWeights = new LayerWeightsConfig
                {
                    Structure = 0.40,
                    MovingAverages = 0.30,
                    Momentum = 0.20,
                    Patterns = 0.10
                },
                TimeframeWeights = new TimeframeWeightsConfig()
            };
        }

        private static TrendAnalyzerConfig CreatePatternOnlyConfig(Action<PatternLayerConfig>? configurePattern = null)
        {
            var config = new TrendAnalyzerConfig
            {
                Enabled = true,
                Version = "pattern-only",
                FeatureFlags = new FeatureFlagsConfig
                {
                    UseStructureLayer = false,
                    UseMALayer = false,
                    UseMomentumLayer = false,
                    UsePatternLayer = true,
                    EnableTelemetry = false
                },
                LayerWeights = new LayerWeightsConfig
                {
                    Structure = 0,
                    MovingAverages = 0,
                    Momentum = 0,
                    Patterns = 1.0
                },
                TimeframeWeights = new TimeframeWeightsConfig(),
                PatternLayer = new PatternLayerConfig()
            };

            configurePattern?.Invoke(config.PatternLayer);
            config.PatternLayer.EnsureDefaults();
            return config;
        }

        private static void DisableOtherPatternDetectors(PatternLayerConfig pattern)
        {
            if (pattern.AccumulationDistribution != null)
            {
                pattern.AccumulationDistribution.Enabled = false;
                pattern.AccumulationDistribution.Weight = 0;
            }

            if (pattern.MarketStructure != null)
            {
                pattern.MarketStructure.Enabled = false;
                pattern.MarketStructure.Weight = 0;
            }

            if (pattern.VolumeProfile != null)
            {
                pattern.VolumeProfile.Enabled = false;
                pattern.VolumeProfile.Weight = 0;
            }
        }

        private sealed class FakeTrendAnalysisBridge : ITrendAnalysisBridge
        {
            public TrendSignal? LastPublishedSignal { get; private set; }

            public TrendSignal? GetCurrentTrend() => LastPublishedSignal;

            public void PublishTrendSignal(TrendSignal signal)
            {
                LastPublishedSignal = signal;
                LastTrendUpdateTime = signal.GeneratedAtUtc;
            }

            public bool IsTrendAnalysisEnabled => true;

            public DateTime LastTrendUpdateTime { get; private set; } = DateTime.MinValue;
        }
    }
}
