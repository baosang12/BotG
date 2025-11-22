#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BotG.MarketRegime;
using BotG.Strategies.Coordination;
using Strategies;
using Xunit;

namespace BotG.Tests.Strategies
{
    public class StrategyCoordinatorTests
    {
        [Fact]
        public async Task CoordinateAsync_SelectsHigherConfidenceSignal_WhenConflicting()
        {
            var config = new StrategyCoordinationConfig
            {
                MinimumConfidence = 0.2,
                MinimumTimeBetweenTrades = TimeSpan.Zero,
                CooldownPenalty = 0.0,
                MaxSignalsPerSymbol = 1,
                MaxSignalsPerTick = 1
            };
            var coordinator = new StrategyCoordinator(config);

            var now = DateTime.UtcNow;
            var buySignal = new Signal
            {
                StrategyName = "Momentum",
                Action = TradeAction.Buy,
                Price = 1.1010,
                Confidence = 0.8,
                TimestampUtc = now
            };

            var sellSignal = new Signal
            {
                StrategyName = "Reversion",
                Action = TradeAction.Sell,
                Price = 1.1010,
                Confidence = 0.6,
                TimestampUtc = now
            };

            var evaluations = new List<StrategyEvaluation>
            {
                new("Momentum", buySignal, new RiskScore(5, RiskLevel.Normal, true), TimeSpan.FromMilliseconds(5)),
                new("Reversion", sellSignal, new RiskScore(5, RiskLevel.Normal, true), TimeSpan.FromMilliseconds(5))
            };

            var marketData = new MarketData("EURUSD", 1.1000, 1.1002, now);
            var context = new MarketContext(marketData, 10000, 0, 0, RegimeType.Trending);

            var result = await coordinator.CoordinateAsync(context, evaluations, CancellationToken.None);

            Assert.Single(result);
            var selection = result[0];
            Assert.Equal("Momentum", selection.Signal.StrategyName);
            Assert.Contains("Reversion", selection.Signal.ConflictingSignals);
            Assert.True(selection.Signal.ConfidenceScore >= config.MinimumConfidence);
        }

        [Fact]
        public async Task ProcessSignals_TimeFilter_RespectsMinimumInterval()
        {
            var config = new StrategyCoordinationConfig
            {
                MinimumConfidence = 0.2,
                MinimumTimeBetweenTrades = TimeSpan.FromMinutes(5),
                CooldownPenalty = 0.0,
                MaxSignalsPerSymbol = 1,
                MaxSignalsPerTick = 1
            };
            var coordinator = new StrategyCoordinator(config);

            var now = DateTime.UtcNow;
            var buySignal = new Signal
            {
                StrategyName = "A",
                Action = TradeAction.Buy,
                Price = 1.1010,
                Confidence = 0.8,
                TimestampUtc = now
            };

            var evaluations = new List<StrategyEvaluation>
            {
                new("A", buySignal, new RiskScore(5, RiskLevel.Normal, true), TimeSpan.FromMilliseconds(5))
            };

            var marketData = new MarketData("EURUSD", 1.1000, 1.1002, now);
            var context = new MarketContext(marketData, 10000, 0, 0, RegimeType.Trending);

            var first = await coordinator.CoordinateAsync(context, evaluations, CancellationToken.None);
            Assert.Single(first);

            // Immediate second attempt should be blocked by cooldown
            var second = await coordinator.CoordinateAsync(context, evaluations, CancellationToken.None);
            Assert.Empty(second);
        }

        [Fact]
        public async Task ProcessSignals_LowConfidence_ReturnsNull()
        {
            var config = new StrategyCoordinationConfig
            {
                MinimumConfidence = 0.9,
                MinimumTimeBetweenTrades = TimeSpan.Zero,
                CooldownPenalty = 0.0,
                MaxSignalsPerSymbol = 1,
                MaxSignalsPerTick = 1
            };
            var coordinator = new StrategyCoordinator(config);

            var now = DateTime.UtcNow;
            var weakSignal = new Signal
            {
                StrategyName = "Weak",
                Action = TradeAction.Buy,
                Price = 1.1010,
                Confidence = 0.1,
                TimestampUtc = now
            };

            var evaluations = new List<StrategyEvaluation>
            {
                new("Weak", weakSignal, new RiskScore(5, RiskLevel.Normal, true), TimeSpan.FromMilliseconds(5))
            };

            var marketData = new MarketData("EURUSD", 1.1000, 1.1002, now);
            var context = new MarketContext(marketData, 10000, 0, 0, RegimeType.Trending);

            var result = await coordinator.CoordinateAsync(context, evaluations, CancellationToken.None);
            Assert.Empty(result);
        }

        [Fact]
        public async Task CoordinateAsync_AppliesStrategyWeights_WhenSelectingSignals()
        {
            var config = new StrategyCoordinationConfig
            {
                MinimumConfidence = 0.2,
                MinimumTimeBetweenTrades = TimeSpan.Zero,
                CooldownPenalty = 0.0,
                MaxSignalsPerSymbol = 1,
                MaxSignalsPerTick = 1,
                StrategyWeights = new Dictionary<string, double>
                {
                    ["Baseline"] = 1.0,
                    ["Weighted"] = 1.5
                }
            };

            var coordinator = new StrategyCoordinator(config);

            var now = DateTime.UtcNow;
            var weightedSignal = new Signal
            {
                StrategyName = "Weighted",
                Action = TradeAction.Buy,
                Price = 1.2010,
                Confidence = 0.6,
                TimestampUtc = now
            };

            var baselineSignal = new Signal
            {
                StrategyName = "Baseline",
                Action = TradeAction.Buy,
                Price = 1.2010,
                Confidence = 0.8,
                TimestampUtc = now
            };

            var evaluations = new List<StrategyEvaluation>
            {
                new("Weighted", weightedSignal, new RiskScore(5, RiskLevel.Normal, true), TimeSpan.FromMilliseconds(5)),
                new("Baseline", baselineSignal, new RiskScore(5, RiskLevel.Normal, true), TimeSpan.FromMilliseconds(5))
            };

            var marketData = new MarketData("EURUSD", 1.2000, 1.2003, now);
            var context = new MarketContext(marketData, 10000, 0, 0, RegimeType.Trending);

            var result = await coordinator.CoordinateAsync(context, evaluations, CancellationToken.None);

            Assert.Single(result);
            Assert.Equal("Weighted", result[0].Signal.StrategyName);
            Assert.True(result[0].Signal.ConfidenceScore >= config.MinimumConfidence);
            Assert.True(result[0].Signal.SignalMetrics.TryGetValue("strategy_weight", out var appliedWeight));
            Assert.Equal(1.5, appliedWeight, 2);
        }
    }
}
