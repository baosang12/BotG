using System;
using System.Linq;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis.Layers
{
    public sealed class StructureLayerTests
    {
        [Fact]
        public void CalculateScore_WithBullishTrend_ReturnsHighScore()
        {
            var scenario = TestDataFixtures.CreateBullishTrendScenario();
            var layer = CreateLayer();

            var score = layer.CalculateScore(scenario.Snapshot, scenario.Accessor);
            var confirmations = GetConfirmations(layer);

            Assert.True(score > 60, $"Bullish scenario phải >60 nhưng nhận {score:F2}. {DescribeDiagnostics(layer)}");
            Assert.Contains(confirmations, c => c.Contains("bullish", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithBearishTrend_ReturnsLowScore()
        {
            var scenario = TestDataFixtures.CreateBearishTrendScenario();
            var layer = CreateLayer();

            var score = layer.CalculateScore(scenario.Snapshot, scenario.Accessor);
            var confirmations = GetConfirmations(layer);

            Assert.True(score < 40, $"Bearish scenario phải <40 nhưng nhận {score:F2}. {DescribeDiagnostics(layer)}");
            Assert.Contains(confirmations, c => c.Contains("bearish", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithRangeMarket_ReturnsNeutralBand()
        {
            var scenario = TestDataFixtures.CreateRangeMarketScenario();
            var layer = CreateLayer();

            var score = layer.CalculateScore(scenario.Snapshot, scenario.Accessor);

            Assert.InRange(score, 40, 60);
        }

        [Fact]
        public void CalculateScore_WithBreakoutScenario_DetectsBos()
        {
            var scenario = TestDataFixtures.CreateBreakOfStructureScenario();
            var layer = CreateLayer();

            var score = layer.CalculateScore(scenario.Snapshot, scenario.Accessor);
            var confirmations = GetConfirmations(layer);

            Assert.True(score > 60, $"BOS bullish nên >60, score {score:F2}. {DescribeDiagnostics(layer)}");
            Assert.Contains(confirmations, c => c.Contains("BOS", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithNullSnapshot_ReturnsNeutralAndWarning()
        {
            var layer = CreateLayer();

            var score = layer.CalculateScore(null, new SnapshotDataAccessor(TestDataFixtures.CreateBullishTrendScenario().Snapshot));
            var warnings = GetWarnings(layer);

            Assert.Equal(50.0, score);
            Assert.Contains(warnings, w => w.Contains("Snapshot null", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithInsufficientBars_ReturnsNeutral()
        {
            var scenario = TestDataFixtures.CreateBullishTrendScenario(barCount: 5);
            var layer = CreateLayer();

            var score = layer.CalculateScore(scenario.Snapshot, scenario.Accessor);
            var warnings = GetWarnings(layer);

            Assert.Equal(50.0, score);
            Assert.Contains(warnings, w => w.Contains("Không đủ dữ liệu", StringComparison.OrdinalIgnoreCase));
        }

        private static StructureLayer CreateLayer() => new(NullLogger<StructureLayer>.Instance);

        private static string[] GetConfirmations(StructureLayer layer)
        {
            return (string[])layer.GetDiagnostics()["Confirmations"];
        }

        private static string[] GetWarnings(StructureLayer layer)
        {
            return (string[])layer.GetDiagnostics()["Warnings"];
        }

        private static string DescribeDiagnostics(StructureLayer layer)
        {
            var diag = layer.GetDiagnostics();
            var confirmations = string.Join(" | ", (string[])diag["Confirmations"]);
            var warnings = string.Join(" | ", (string[])diag["Warnings"]);
            return $"Confirmations: {confirmations}; Warnings: {warnings}";
        }
    }
}
