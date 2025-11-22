#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BotG.Strategies.Coordination;
using BotG.Threading;
using Strategies;
using TradeManager;
using Xunit;

namespace BotG.Tests.Strategies
{
    public class StrategyPipelineTests
    {
        [Fact]
        public async Task SmaCrossoverStrategy_GeneratesBuySignal_OnBullishCross()
        {
            var strategy = new SmaCrossoverStrategy("SMA", fastPeriod:2, slowPeriod:3);
            var now = DateTime.UtcNow;

            await strategy.EvaluateAsync(CreateMarketData(1.1000, now), CancellationToken.None);
            await strategy.EvaluateAsync(CreateMarketData(1.0500, now.AddSeconds(1)), CancellationToken.None);
            await strategy.EvaluateAsync(CreateMarketData(1.0600, now.AddSeconds(2)), CancellationToken.None);
            var signal = await strategy.EvaluateAsync(CreateMarketData(1.1300, now.AddSeconds(3)), CancellationToken.None);

            Assert.NotNull(signal);
            Assert.Equal(TradeAction.Buy, signal!.Action);
            Assert.True(signal.Confidence > 0);
        }

        [Fact]
        public async Task RsiStrategy_GeneratesSellSignal_OnOverboughtExit()
        {
            var strategy = new RsiStrategy("RSI", period:4, oversold:30, overbought:70);
            var now = DateTime.UtcNow;
            double price = 1.1000;

            // Prime the RSI with rising prices
            for (int i = 0; i < 6; i++)
            {
                price += 0.0010;
                await strategy.EvaluateAsync(CreateMarketData(price, now.AddSeconds(i)), CancellationToken.None);
            }

            // Trigger reversal
            price -= 0.0030;
            var signal = await strategy.EvaluateAsync(CreateMarketData(price, now.AddSeconds(6)), CancellationToken.None);

            Assert.NotNull(signal);
            Assert.Equal(TradeAction.Sell, signal!.Action);
        }

        [Fact]
        public async Task StrategyPipeline_DispatchesSignal_ToTradeManager()
        {
            var signal = new Signal
            {
                StrategyName = "Stub",
                Action = TradeAction.Buy,
                Price = 1.1010,
                Confidence = 0.8,
                TimestampUtc = DateTime.UtcNow
            };
            var risk = new RiskScore(6.0, RiskLevel.Normal, true);
            var strategy = new StubStrategy(signal, risk);
            var tradeManager = new FakeTradeManager();
            using var serializer = new ExecutionSerializer();
            var config = new StrategyCoordinationConfig
            {
                MinimumConfidence = 0.2,
                MinimumTimeBetweenTrades = TimeSpan.Zero,
                CooldownPenalty = 0.0,
                MaxSignalsPerTick = 5,
                MaxSignalsPerSymbol = 2,
                ConfidenceFloor = 0.1
            };
            var coordinator = new StrategyCoordinator(config);
            var pipeline = new StrategyPipeline(new List<IStrategy> { strategy }, tradeManager, serializer, coordinator);

            var data = CreateMarketData(1.1010, DateTime.UtcNow);
            var context = new MarketContext(data, 10000, 0, 0);

            var result = await pipeline.ProcessAsync(data, context, CancellationToken.None);

            Assert.Single(result.Evaluations);
            Assert.Single(result.CoordinatedSignals);
            Assert.Equal(1, tradeManager.ProcessCallCount);
            Assert.Equal(signal.Action, tradeManager.LastSignal?.Action);
        }

        private static MarketData CreateMarketData(double mid, DateTime timestamp)
        {
            double spread = 0.0002;
            return new MarketData("EURUSD", mid - spread / 2.0, mid + spread / 2.0, timestamp);
        }

        private sealed class StubStrategy : IStrategy
        {
            private readonly Signal? _signal;
            private readonly RiskScore _risk;
            private bool _emitted;

            public StubStrategy(Signal? signal, RiskScore risk)
            {
                _signal = signal;
                _risk = risk;
            }

            public string Name => "StubStrategy";

            public Task<Signal?> EvaluateAsync(MarketData data, CancellationToken ct)
            {
                if (_emitted)
                {
                    return Task.FromResult<Signal?>(null);
                }

                _emitted = true;
                return Task.FromResult(_signal);
            }

            public RiskScore CalculateRisk(MarketContext context) => _risk;
        }

        private sealed class FakeTradeManager : ITradeManager
        {
            public int ProcessCallCount { get; private set; }
            public Signal? LastSignal { get; private set; }
            public RiskScore? LastRisk { get; private set; }

            public bool CanTrade(Signal signal, RiskScore riskScore) => true;

            public void Process(Signal signal, RiskScore riskScore)
            {
                ProcessCallCount++;
                LastSignal = signal;
                LastRisk = riskScore;
            }
        }
    }
}
