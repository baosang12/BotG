#nullable enable
using System;
using System.Diagnostics;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using AnalysisModule.Preprocessor.Tests.TrendAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AnalysisModule.Preprocessor.Tests.Performance
{
    public sealed class PatternLayerPerformanceTests
    {
        private const int Iterations = 200;
        private readonly ITestOutputHelper _output;

        public PatternLayerPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void PatternLayer_Performance_Under5ms()
        {
            var fixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.StrongBreakout);
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance);
            layer.UpdateConfig(CreateLightweightPatternConfig());

            // Warmup
            layer.CalculateScore(fixture.Snapshot, fixture.Accessor);

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Iterations; i++)
            {
                layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            }
            sw.Stop();

            var avgMs = sw.Elapsed.TotalMilliseconds / Iterations;
            _output?.WriteLine($"PatternLayer avg={avgMs:F3} ms");
            Assert.True(avgMs < 5.0, $"PatternLayer trung bình {avgMs:F3} ms (threshold 5 ms)");
        }

        [Fact]
        public void MarketTrendAnalyzer_WithPatternLayer_Under30ms()
        {
            var bridge = new FakeTrendAnalysisBridge();
            var telemetry = new TrendAnalysisTelemetry(NullLogger<TrendAnalysisTelemetry>.Instance);
            var analyzer = new MarketTrendAnalyzer(
                NullLogger<MarketTrendAnalyzer>.Instance,
                bridge,
                telemetry);

            analyzer.Initialize(CreatePatternAnalyzerConfig());

            var fixture = PatternLayerTestScenarios.CreatePatternLayerFixture(PatternScenario.CleanBreakout);
            analyzer.Analyze(fixture.Snapshot); // warmup

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Iterations; i++)
            {
                analyzer.Analyze(fixture.Snapshot);
            }
            sw.Stop();

            var avgMs = sw.Elapsed.TotalMilliseconds / Iterations;
            _output?.WriteLine($"MarketTrendAnalyzer avg={avgMs:F3} ms");
            Assert.True(avgMs < 30.0, $"MarketTrendAnalyzer trung bình {avgMs:F3} ms (threshold 30 ms)");
        }

        [Fact]
        public void PatternLayer_MemoryUsage_Below15KbPerCall()
        {
            var fixture = TestDataFixtures.CreateBullishTrendScenario(60);
            var layer = new PatternLayer(NullLogger<PatternLayer>.Instance);
            layer.UpdateConfig(CreateTrendAnalyzerConfigForLayer());

            for (var i = 0; i < 25; i++)
            {
                layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var overhead = MeasureLoopOverhead();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Iterations; i++)
            {
                layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            }
            var after = GC.GetAllocatedBytesForCurrentThread();

            var netBytes = Math.Max(0, (after - before) - overhead);
            var perCallBytes = netBytes / Math.Max(1, Iterations);
            const int maxBytes = 15 * 1024;
            _output?.WriteLine($"PatternLayer allocation={perCallBytes} bytes/call");
            Assert.True(perCallBytes <= maxBytes, $"PatternLayer allocation {perCallBytes} bytes/call (threshold {maxBytes} bytes)");
        }

        private static long MeasureLoopOverhead()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Iterations; i++)
            {
                GC.KeepAlive(i);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private static TrendAnalyzerConfig CreatePatternAnalyzerConfig()
        {
            var config = new TrendAnalyzerConfig
            {
                Enabled = true,
                Version = "performance",
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
                PatternLayer = new PatternLayerConfig(),
                TimeframeWeights = new TimeframeWeightsConfig()
            };

            config.PatternLayer.EnsureDefaults();
            config.PatternLayer.Liquidity.Weight = 0.6;
            config.PatternLayer.BreakoutQuality.Weight = 0.4;
            return config;
        }

        private static TrendAnalyzerConfig CreateTrendAnalyzerConfigForLayer()
        {
            return CreatePatternAnalyzerConfig();
        }

        private static TrendAnalyzerConfig CreateLightweightPatternConfig()
        {
            var config = CreatePatternAnalyzerConfig();
            config.PatternLayer.Liquidity.Enabled = false;
            config.PatternLayer.Liquidity.Weight = 0;
            config.PatternLayer.BreakoutQuality.Weight = 1.0;
            return config;
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

            public DateTime LastTrendUpdateTime { get; private set; }
        }
    }
}
