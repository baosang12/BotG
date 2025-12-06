using System;
using System.Linq;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis.Layers
{
    public sealed class PatternLayerIntegrationTests
    {
        [Fact]
        public void PatternLayer_WithBothDetectors_ReturnsWeightedScore()
        {
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance);
            layer.UpdateConfig(new TrendAnalyzerConfig
            {
                PatternLayer = new PatternLayerConfig
                {
                    Liquidity = new PatternDetectorConfig
                    {
                        Enabled = true,
                        Weight = 0.6
                    },
                    BreakoutQuality = new BreakoutQualityDetectorConfig
                    {
                        Enabled = true,
                        Weight = 0.4,
                        Parameters = BreakoutQualityParameters.CreateDefault()
                    }
                }
            });

            var fixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.StrongBreakout);
            var score = layer.CalculateScore(fixture.Snapshot, fixture.Accessor);

            Assert.True(score > 55);

            var diagnostics = layer.GetDiagnostics();
            Assert.True(diagnostics.ContainsKey("[Liquidity]Diagnostics"));
            Assert.True(diagnostics.ContainsKey("[BreakoutQuality]Diagnostics"));
            Assert.True(diagnostics.ContainsKey("PatternFlags"));
        }

        [Fact]
        public void PatternLayer_DetectorDisabled_ExcludesFromCalculation()
        {
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance);
            var config = new TrendAnalyzerConfig
            {
                PatternLayer = new PatternLayerConfig
                {
                    Liquidity = new PatternDetectorConfig
                    {
                        Enabled = false,
                        Weight = 0.0
                    },
                    BreakoutQuality = new BreakoutQualityDetectorConfig
                    {
                        Enabled = true,
                        Weight = 1.0
                    }
                }
            };
            layer.UpdateConfig(config);

            var fixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.LiquidityGrab);
            var scoreWithoutLiquidity = layer.CalculateScore(fixture.Snapshot, fixture.Accessor);

            config.PatternLayer!.Liquidity.Enabled = true;
            config.PatternLayer.Liquidity.Weight = 0.5;
            config.PatternLayer.BreakoutQuality.Weight = 0.5;
            layer.UpdateConfig(config);

            var scoreWithLiquidity = layer.CalculateScore(fixture.Snapshot, fixture.Accessor);

            Assert.NotEqual(scoreWithLiquidity, scoreWithoutLiquidity);
        }

        [Fact]
        public void PatternLayer_Diagnostics_AggregatesAllDetectors()
        {
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance);
            layer.UpdateConfig(new TrendAnalyzerConfig());

            var fixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.CleanBreakout);
            _ = layer.CalculateScore(fixture.Snapshot, fixture.Accessor);

            var diagnostics = layer.GetDiagnostics();
            Assert.True(diagnostics.TryGetValue("[Liquidity]Diagnostics", out var liquidityDiagnostics));
            Assert.True(diagnostics.TryGetValue("[BreakoutQuality]Diagnostics", out var breakoutDiagnostics));
            Assert.NotNull(liquidityDiagnostics);
            Assert.NotNull(breakoutDiagnostics);

            Assert.True(diagnostics.TryGetValue("PatternFlags", out var flagsObj));
            var flags = flagsObj as string[] ?? Array.Empty<string>();
            Assert.Contains(flags, f => !string.IsNullOrWhiteSpace(f));
        }
    }
}
