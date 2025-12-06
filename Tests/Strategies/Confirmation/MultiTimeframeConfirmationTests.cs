#nullable enable

using System.Linq;
using BotG.Tests.Strategies;
using Strategies.Confirmation;
using Xunit;
using Xunit.Abstractions;

namespace BotG.Tests.Strategies.Confirmation
{
    public class MultiTimeframeConfirmationTests
    {
        private readonly ITestOutputHelper _output;

        public MultiTimeframeConfirmationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ConfirmationEngine_StrongScenario_OutperformsWeakScenario()
        {
            var (strongContext, _) = BreakoutStrategyFixtures.CreateBullishBreakoutScenario();
            var (weakContext, _) = BreakoutStrategyFixtures.CreateFalseBreakoutScenario();
            var config = new ConfirmationConfig
            {
                MinimumConfirmationThreshold = 0.3
            };
            var engine = new MultiTimeframeConfirmationEngine(config);

            var strong = engine.CheckConfirmation(strongContext);
            var weak = engine.CheckConfirmation(weakContext);

            _output.WriteLine($"Strong={strong.OverallScore:F3}, Weak={weak.OverallScore:F3}");
            _output.WriteLine("Strong details: " + string.Join(", ", strong.Details.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            _output.WriteLine("Weak details: " + string.Join(", ", weak.Details.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            Assert.True(strong.OverallScore > weak.OverallScore);
            Assert.True(strong.IsConfirmed);
            Assert.False(weak.IsConfirmed);
        }

        [Fact]
        public void ConfirmationEngine_HonorsConfiguredThreshold()
        {
            var (context, _) = BreakoutStrategyFixtures.CreateBullishBreakoutScenario();

            var strictEngine = new MultiTimeframeConfirmationEngine(new ConfirmationConfig
            {
                MinimumConfirmationThreshold = 0.6
            });
            var lenientEngine = new MultiTimeframeConfirmationEngine(new ConfirmationConfig
            {
                MinimumConfirmationThreshold = 0.25
            });

            var strictResult = strictEngine.CheckConfirmation(context);
            var lenientResult = lenientEngine.CheckConfirmation(context);

            _output.WriteLine($"Strict={strictResult.OverallScore:F3}, Lenient={lenientResult.OverallScore:F3}");
            Assert.False(strictResult.IsConfirmed);
            Assert.True(lenientResult.IsConfirmed);
        }
    }
}
