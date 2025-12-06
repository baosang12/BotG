using System;
using System.Linq;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis.Layers
{
    public sealed class MomentumLayerTests
    {
        private readonly MomentumLayer _layer = new(NullLogger<MomentumLayer>.Instance);

        [Fact]
        public void CalculateScore_WithBullishScenario_ReturnsHighScore()
        {
            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Bullish);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            var confirmations = GetConfirmations();

            Assert.True(score > 60, $"Bullish momentum phải >60 nhưng nhận {score:F2}. {Describe(confirmations)}");
            Assert.Contains(confirmations, c => c.Contains("RSI", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithBearishScenario_ReturnsLowScore()
        {
            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Bearish);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            var confirmations = GetConfirmations();

            Assert.True(score < 45, $"Bearish momentum phải <45 nhưng nhận {score:F2}. {Describe(confirmations)}");
        }

        [Fact]
        public void CalculateScore_WithRangeScenario_ReturnsMidScore()
        {
            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Range);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);

            Assert.InRange(score, 40, 60);
        }

        [Fact]
        public void CalculateScore_WithBreakoutScenario_NotNeutral()
        {
            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Breakout);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);

            Assert.False(score is >= 45 and <= 55, $"Breakout không nên trung tính, score={score:F2}");
        }

        [Fact]
        public void CalculateScore_WhenIndicatorsMissing_ReturnsNeutralAndWarning()
        {
            var bars = TestDataFixtures.CreateBullishBars();
            var fixture = TestDataFixtures.CreateSnapshotFixture(bars);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            var warnings = GetWarnings();

            Assert.InRange(score, 45.0, 55.0);
            Assert.Contains(warnings, w => w.Contains("Thiếu RSI", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithNullSnapshot_ReturnsNeutralWarning()
        {
            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Bullish);

            var score = _layer.CalculateScore(null, fixture.Accessor);
            var warnings = GetWarnings();

            Assert.Equal(50.0, score);
            Assert.Contains(warnings, w => w.Contains("Snapshot null", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithConfluence_AddsBonus()
        {
            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Bullish);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            var confirmations = GetConfirmations();

            Assert.True(score > 65, $"Bullish có confluence nên >65, score={score:F2}");
            Assert.Contains(confirmations, c => c.Contains("Confluence", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void GetDiagnostics_ReturnsConfirmationsAndWarnings()
        {
            var fixture = TestDataFixturesExtensions.CreateMomentumTestFixture(MarketScenario.Range);

            _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            var diagnostics = _layer.GetDiagnostics();

            Assert.True(diagnostics.ContainsKey("Confirmations"));
            Assert.True(diagnostics.ContainsKey("Warnings"));
            var confirmations = GetConfirmations();
            Assert.NotEmpty(confirmations);
        }

        private string[] GetConfirmations()
        {
            return (string[])_layer.GetDiagnostics()["Confirmations"];
        }

        private string[] GetWarnings()
        {
            return (string[])_layer.GetDiagnostics()["Warnings"];
        }

        private static string Describe(string[] messages)
        {
            return string.Join(" | ", messages);
        }
    }
}
