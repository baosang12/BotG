using System;
using System.Linq;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis.Layers
{
    public sealed class MovingAverageLayerTests
    {
        private readonly MovingAverageLayer _layer = new(NullLogger<MovingAverageLayer>.Instance);

        [Fact]
        public void CalculateScore_WithBullishScenario_ReturnsHighScore()
        {
            var fixture = TestDataFixturesExtensions.CreateMovingAverageTestFixture(MarketScenario.Bullish);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            var confirmations = GetConfirmations();

            Assert.True(score > 65, $"Bullish MA phải >65 nhưng nhận {score:F2}. {Describe(confirmations)}");
            Assert.Contains(confirmations, c => c.Contains("EMA9 > EMA20", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithBearishScenario_ReturnsLowScore()
        {
            var fixture = TestDataFixturesExtensions.CreateMovingAverageTestFixture(MarketScenario.Bearish);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            var confirmations = GetConfirmations();

            Assert.True(score < 35, $"Bearish MA phải <35 nhưng nhận {score:F2}. {Describe(confirmations)}");
            Assert.Contains(confirmations, c => c.Contains("EMA9 < EMA20", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithRangeScenario_ReturnsMidScore()
        {
            var fixture = TestDataFixturesExtensions.CreateMovingAverageTestFixture(MarketScenario.Range);

            var score = _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);

            Assert.InRange(score, 40, 60);
        }

        [Fact]
        public void CalculateScore_WithBreakoutScenario_NotNeutral()
        {
            var fixture = TestDataFixturesExtensions.CreateMovingAverageTestFixture(MarketScenario.Breakout);

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

            Assert.Equal(50.0, score);
            Assert.Contains(warnings, w => w.Contains("Thiếu EMA", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WithNullSnapshot_ReturnsNeutral()
        {
            var fixture = TestDataFixturesExtensions.CreateMovingAverageTestFixture(MarketScenario.Bullish);

            var score = _layer.CalculateScore(null, fixture.Accessor);
            var warnings = GetWarnings();

            Assert.Equal(50.0, score);
            Assert.Contains(warnings, w => w.Contains("Snapshot null", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void CalculateScore_WhenHigherTimeframesAgree_AddsConfluenceDiagnostics()
        {
            var fixture = TestDataFixturesExtensions.CreateMovingAverageTestFixture(MarketScenario.Bullish);

            _layer.CalculateScore(fixture.Snapshot, fixture.Accessor);
            var confirmations = GetConfirmations();

            Assert.Contains(confirmations, c => c.Contains("Confluence", StringComparison.OrdinalIgnoreCase));
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
