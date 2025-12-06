#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BotG.MarketRegime;
using BotG.Strategies.Coordination;
using Strategies;
using TradeManager;
using Xunit;

namespace BotG.Tests.MarketRegime
{
    public class MarketRegimeTests
    {
        [Fact]
        public void StrategyRegimeMapper_Matches_ByNamePatterns()
        {
            Assert.True(StrategyRegimeMapper.IsSuitable("SMA Trend", RegimeType.Trending));
            Assert.True(StrategyRegimeMapper.IsSuitable("RSI Reversion", RegimeType.Ranging));
            Assert.True(StrategyRegimeMapper.IsSuitable("Breakout", RegimeType.Volatile));
            Assert.True(StrategyRegimeMapper.IsSuitable("Scalping", RegimeType.Calm));
        }

        [Fact]
        public async Task StrategyPipeline_Filters_By_Regime()
        {
            var s1 = new StubStrategy("SMA Trend");
            var s2 = new StubStrategy("RSI Range");
            var tm = new FakeTradeManager();
            var config = new StrategyCoordinationConfig
            {
                MinimumConfidence = 0.2,
                MinimumTimeBetweenTrades = TimeSpan.Zero,
                CooldownPenalty = 0.0,
                MaxSignalsPerTick = 5,
                MaxSignalsPerSymbol = 2
            };
            var coordinator = new StrategyCoordinator(config);
            var pipeline = new StrategyPipeline(new List<IStrategy> { s1, s2 }, tm, new BotG.Threading.ExecutionSerializer(), coordinator);

            var data = new MarketData("EURUSD", 1.1000, 1.1002, DateTime.UtcNow);
            var analysis = new RegimeAnalysisResult { Regime = RegimeType.Trending, Confidence = 0.9 };
            var ctxTrending = new MarketContext(data, 10000, 0, 0, RegimeType.Trending, analysis);

            var result = await pipeline.ProcessAsync(data, ctxTrending, CancellationToken.None);
            Assert.Single(result.Evaluations);
            Assert.Single(result.CoordinatedSignals);
            Assert.Equal("SMA Trend", result.Evaluations[0].StrategyName);
        }

        [Fact]
        public void RegimeExtensions_ProvideProfiles()
        {
            var trending = RegimeType.Trending;
            Assert.True(trending.IsStrategyCompatible("EMA Momentum"));
            Assert.True(trending.GetRiskMultiplier() > 0);
            Assert.False(RegimeType.Volatile.IsStrategyCompatible("RsiReversionStrategy"));

            var result = new RegimeAnalysisResult { Regime = RegimeType.Volatile, Confidence = 0.8 };
            Assert.True(result.IsConfident());
            Assert.Contains("BreakoutStrategy", result.GetRecommendedStrategies());
        }

        private sealed class StubStrategy : IStrategy
        {
            private readonly string _name;
            public StubStrategy(string name) { _name = name; }
            public string Name => _name;
            public Task<Signal?> EvaluateAsync(MarketData data, CancellationToken ct)
                => Task.FromResult<Signal?>(new Signal { StrategyName = _name, Action = TradeAction.Buy, Price = data.Mid, Confidence = 0.5, TimestampUtc = DateTime.UtcNow });
            public RiskScore CalculateRisk(MarketContext context)
                => new RiskScore(5.0, RiskLevel.Normal, true);
        }

        private sealed class FakeTradeManager : ITradeManager
        {
            public bool CanTrade(Signal signal, RiskScore riskScore) => true;
            public void Process(Signal signal, RiskScore riskScore) { }
        }
    }
}
