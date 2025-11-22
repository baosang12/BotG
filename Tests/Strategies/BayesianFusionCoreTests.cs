using System;
using System.Collections.Generic;
using BotG.Strategies.Coordination;
using DataFetcher.Models;
using Strategies;
using Xunit;
using Xunit.Abstractions;

#nullable enable

namespace BotG.Tests.Strategies
{
    public class BayesianFusionCoreTests
    {
        private readonly ITestOutputHelper _output;

        public BayesianFusionCoreTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void WeightedAverage_SelectsHighestWeightedDirection()
        {
            var config = new BayesianFusionConfig
            {
                Method = FusionMethod.WeightedAverage,
                MinimumConfidenceThreshold = 0.5,
                StrategyWeights = new Dictionary<string, double>
                {
                    ["trend-follow"] = 2.0,
                    ["mean-revert"] = 1.0
                }
            };

            var core = new BayesianFusionCore(config);

            var signals = new[]
            {
                CreateSignal("trend-follow", TradeAction.Buy, 0.8),
                CreateSignal("mean-revert", TradeAction.Sell, 0.7),
                CreateSignal("trend-follow", TradeAction.Buy, 0.6)
            };

            var result = core.Fuse(signals);

            _output.WriteLine($"HasDecision={result.HasDecision}, Direction={result.Direction}, Combined={result.CombinedConfidence:F4}, Reason={result.Reason}");

            Assert.True(result.HasDecision, $"HasDecision={result.HasDecision}, Direction={result.Direction}, Combined={result.CombinedConfidence:F4}, Reason={result.Reason}");
            Assert.Equal(TradeAction.Buy, result.Direction);
            Assert.True(result.CombinedConfidence > 0.7);
            Assert.Equal(FusionMethod.WeightedAverage, result.Method);
        }

        [Fact]
        public void ConsensusVoting_RequiresMargin()
        {
            var config = new BayesianFusionConfig
            {
                Method = FusionMethod.ConsensusVoting,
                MinimumConfidenceThreshold = 0.5,
                VotingConsensusMargin = 0.2
            };

            var core = new BayesianFusionCore(config);

            var signals = new[]
            {
                CreateSignal("strat-a", TradeAction.Buy, 0.65),
                CreateSignal("strat-b", TradeAction.Buy, 0.55),
                CreateSignal("strat-c", TradeAction.Sell, 0.60)
            };

            var result = core.Fuse(signals);

            Assert.True(result.HasDecision);
            Assert.Equal(TradeAction.Buy, result.Direction);
            Assert.Null(result.Reason);
        }

        [Fact]
        public void ConsensusVoting_BlocksWhenNoMargin()
        {
            var config = new BayesianFusionConfig
            {
                Method = FusionMethod.ConsensusVoting,
                MinimumConfidenceThreshold = 0.5,
                VotingConsensusMargin = 2.0
            };

            var core = new BayesianFusionCore(config);

            var signals = new[]
            {
                CreateSignal("strat-a", TradeAction.Buy, 0.8),
                CreateSignal("strat-b", TradeAction.Sell, 0.8)
            };

            var result = core.Fuse(signals);

            Assert.False(result.HasDecision);
            Assert.Equal("no-consensus", result.Reason);
        }

        [Fact]
        public void BayesianProbability_FusesEvidence()
        {
            var config = new BayesianFusionConfig
            {
                Method = FusionMethod.BayesianProbability,
                MinimumConfidenceThreshold = 0.6,
                BayesianPrior = 0.55,
                EvidenceFloor = 0.1,
                EvidenceCeiling = 0.95
            };

            var core = new BayesianFusionCore(config);

            var signals = new[]
            {
                CreateSignal("breakout", TradeAction.Buy, 0.75, new Dictionary<string, double>{{"likelihood", 0.8}}),
                CreateSignal("momentum", TradeAction.Buy, 0.7),
                CreateSignal("fade", TradeAction.Sell, 0.55)
            };

            var result = core.Fuse(signals);

            Assert.True(result.HasDecision);
            Assert.Equal(TradeAction.Buy, result.Direction);
            Assert.True(result.CombinedConfidence >= 0.6);
            Assert.Equal(FusionMethod.BayesianProbability, result.Method);
        }

        [Fact]
        public void Fuse_ReturnsEmptyWhenNoSignals()
        {
            var core = new BayesianFusionCore();
            var result = core.Fuse(Array.Empty<StrategyFusionSignal>());
            Assert.False(result.HasDecision);
            Assert.Equal("no-signals", result.Reason);
        }

        private static StrategyFusionSignal CreateSignal(
            string strategyId,
            TradeAction direction,
            double confidence,
            IReadOnlyDictionary<string, double>? evidence = null)
        {
            return new StrategyFusionSignal(
                strategyId,
                direction,
                confidence,
                evidence,
                DateTime.UtcNow,
                TimeFrame.H1);
        }
    }
}
