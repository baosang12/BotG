#nullable enable

using System.Threading.Tasks;
using Strategies;
using Xunit;

namespace BotG.Tests.Strategies
{
    public class BreakoutStrategyTests
    {
        [Fact]
        public async Task EvaluateDeterministic_ReturnsBuySignal_WhenBullishScenario()
        {
            var (context, config) = BreakoutStrategyFixtures.CreateBullishBreakoutScenario();
            var strategy = BreakoutStrategyFixtures.CreateStrategy(config);

            var signal = await strategy.EvaluateDeterministicAsync(context);

            Assert.NotNull(signal);
            Assert.Equal(TradeAction.Buy, signal!.Action);
            Assert.True(signal.Confidence > 0.2);
        }

        [Fact]
        public async Task EvaluateDeterministic_ReturnsSellSignal_WhenBearishScenario()
        {
            var (context, config) = BreakoutStrategyFixtures.CreateBearishBreakoutScenario();
            var strategy = BreakoutStrategyFixtures.CreateStrategy(config);

            var signal = await strategy.EvaluateDeterministicAsync(context);

            Assert.NotNull(signal);
            Assert.Equal(TradeAction.Sell, signal!.Action);
            Assert.True(signal.Confidence > 0.2);
        }

        [Fact]
        public async Task EvaluateDeterministic_ReturnsNull_WhenVolumeInsufficient()
        {
            var (context, config) = BreakoutStrategyFixtures.CreateLowVolumeScenario();
            var strategy = BreakoutStrategyFixtures.CreateStrategy(config);

            var signal = await strategy.EvaluateDeterministicAsync(context);

            Assert.Null(signal);
        }

        [Fact]
        public async Task EvaluateDeterministic_ReturnsNull_WhenFalseBreakout()
        {
            var (context, config) = BreakoutStrategyFixtures.CreateFalseBreakoutScenario();
            var strategy = BreakoutStrategyFixtures.CreateStrategy(config);

            var signal = await strategy.EvaluateDeterministicAsync(context);

            Assert.Null(signal);
        }

        [Fact]
        public async Task EvaluateDeterministic_Works_WithMinimalWarmupData()
        {
            var (context, config) = BreakoutStrategyFixtures.CreateMinimalWarmupScenario();
            var strategy = BreakoutStrategyFixtures.CreateStrategy(config);

            var signal = await strategy.EvaluateDeterministicAsync(context);

            Assert.NotNull(signal);
            Assert.Equal(TradeAction.Buy, signal!.Action);
            Assert.True(signal.Confidence > 0.15);
        }
    }
}
