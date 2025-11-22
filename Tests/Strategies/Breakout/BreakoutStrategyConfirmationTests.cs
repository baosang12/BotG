#nullable enable

using System.Threading.Tasks;
using BotG.Tests.Strategies;
using Strategies.Confirmation;
using Xunit;

namespace BotG.Tests.Strategies.Breakout
{
    public class BreakoutStrategyConfirmationTests
    {
        [Fact]
        public async Task BreakoutStrategy_StrictConfirmation_RejectsOtherwiseValidSignal()
        {
            var (context, config) = BreakoutStrategyFixtures.CreateBullishBreakoutScenario();
            var confirmation = new ConfirmationConfig
            {
                MinimumConfirmationThreshold = 0.95
            };
            var strategy = BreakoutStrategyFixtures.CreateStrategy(config, confirmation);

            var signal = await strategy.EvaluateDeterministicAsync(context);

            Assert.Null(signal);
        }

        [Fact]
        public async Task BreakoutStrategy_LenientConfirmation_PermitsStrongSignal()
        {
            var (context, config) = BreakoutStrategyFixtures.CreateBullishBreakoutScenario();
            var confirmation = new ConfirmationConfig
            {
                MinimumConfirmationThreshold = 0.25
            };
            var strategy = BreakoutStrategyFixtures.CreateStrategy(config, confirmation);

            var signal = await strategy.EvaluateDeterministicAsync(context);

            Assert.NotNull(signal);
            Assert.True(signal!.Confidence > 0.2);
        }
    }
}
