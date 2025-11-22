#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BotG.MarketRegime;
using BotG.Strategies.Coordination;
using Strategies;
using Xunit;
using cAlgo.API;

namespace BotG.Tests.Strategies
{
    public class BreakoutFusionIntegrationTests
    {
        [Fact]
        public async Task BreakoutSignal_FlowsThroughFusionPipeline()
        {
            var (mtfContext, breakoutConfig) = BreakoutStrategyFixtures.CreateBullishBreakoutScenario();
            var breakoutStrategy = BreakoutStrategyFixtures.CreateStrategy(breakoutConfig);
            var breakoutSignal = await breakoutStrategy.EvaluateDeterministicAsync(mtfContext);

            Assert.NotNull(breakoutSignal);

            var evaluation = new StrategyEvaluation(
                "BreakoutStrategy",
                breakoutSignal,
                new RiskScore(6.0, RiskLevel.Normal, true),
                TimeSpan.FromMilliseconds(5));

            var registry = new StrategyRegistry();
            var metadata = new StrategyMetadata(
                StrategyId: "BreakoutStrategy",
                DisplayName: "Breakout Strategy",
                PrimaryTimeframe: DataFetcher.Models.TimeFrame.H1,
                CompatibleRegimes: new[] { RegimeType.Trending, RegimeType.Ranging },
                DefaultWeight: 1.0,
                EnabledByDefault: true,
                Description: "Integration test entry");

            registry.RegisterStrategy(metadata);
            registry.SetStrategyEnabled(metadata.StrategyId, true);

            var config = new StrategyCoordinationConfig
            {
                EnableBayesianFusion = true,
                MinimumConfidence = 0.15,
                Fusion = new BayesianFusionConfig
                {
                    Method = FusionMethod.BayesianProbability,
                    MinimumConfidenceThreshold = 0.1
                }
            };

            var coordinator = new EnhancedStrategyCoordinator(config, registry);

            var marketContext = new MarketContext(
                mtfContext.MarketData,
                AccountEquity: 100000,
                OpenPositionExposure: 0,
                DailyDrawdown: 0,
                CurrentRegime: RegimeType.Trending,
                RegimeAnalysis: RegimeAnalysisResult.CreateFallback(RegimeType.Trending),
                RiskMetrics: null,
                Metadata: new Dictionary<string, object?>
                {
                    ["mtf_context"] = mtfContext
                })
            {
                CurrentTime = mtfContext.MarketData.TimestampUtc
            };

            var decisions = await coordinator.CoordinateAsync(
                marketContext,
                new[] { evaluation },
                CancellationToken.None);

            Assert.Single(decisions);
            var decision = decisions[0];
            Assert.Equal(TradeType.Buy, decision.Signal.Direction);
            Assert.True(decision.Signal.SignalMetrics.TryGetValue("fusion_confidence", out var fusionConfidence));
            Assert.InRange(fusionConfidence, 0.0, 1.0);
        }
    }
}
