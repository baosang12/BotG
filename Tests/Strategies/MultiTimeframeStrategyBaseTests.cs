#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BotG.MultiTimeframe;
using DataFetcher.Models;
using Strategies;
using Strategies.Templates;
using Xunit;

namespace BotG.Tests.Strategies
{
    public class MultiTimeframeStrategyBaseTests
    {
        private static readonly TimeFrame[] DefaultStack = { TimeFrame.H4, TimeFrame.H1, TimeFrame.M15 };

        [Fact]
        public async Task EvaluateAsync_ReturnsNull_WhenAlignmentFails()
        {
            var manager = CreateManager();
            var strategy = CreateStrategy(manager, new TimeframeSynchronizer(), new SessionAwareAnalyzer());
            var marketData = CreateMarketData(new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));

            var result = await strategy.EvaluateAsync(marketData, CancellationToken.None);

            Assert.Null(result);
            Assert.Equal(0, strategy.EvaluationCount);
        }

        [Fact]
        public async Task EvaluateAsync_ForwardsWhenAligned()
        {
            var manager = CreateManager();
            var synchronizer = new TimeframeSynchronizer(new TimeframeSynchronizerConfig
            {
                MinimumAlignedTimeframes = 2,
                MinimumBarsPerTimeframe = 1,
                MaximumAllowedSkew = TimeSpan.FromHours(4),
                AntiRepaintGuard = TimeSpan.FromSeconds(30),
                WarmupBarsRequired = 0,
                WarmupBarsPerTimeframe = new Dictionary<TimeFrame, int>()
            });

            var sessionAnalyzer = new SessionAwareAnalyzer();
            var strategy = CreateStrategy(manager, synchronizer, sessionAnalyzer);
            var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            SeedBars(manager, "EURUSD", now);

            var marketData = CreateMarketData(now);
            var signal = await strategy.EvaluateAsync(marketData, CancellationToken.None);

            Assert.NotNull(signal);
            Assert.Equal(1, strategy.EvaluationCount);
            Assert.Equal(strategy.Name, signal!.StrategyName);
        }

        private static TestStrategy CreateStrategy(
            TimeframeManager manager,
            TimeframeSynchronizer synchronizer,
            SessionAwareAnalyzer analyzer)
        {
            return new TestStrategy(manager, synchronizer, analyzer);
        }

        private static TimeframeManager CreateManager()
        {
            return new TimeframeManager(new TimeframeManagerConfig
            {
                Timeframes = DefaultStack,
                RequireClosedBars = true,
                AntiRepaintGuard = TimeSpan.FromSeconds(1)
            });
        }

        private static void SeedBars(TimeframeManager manager, string symbol, DateTime referenceTime)
        {
            manager.TryAddBar(symbol, CreateBar(TimeFrame.H4, referenceTime.AddHours(-5)), referenceTime, true);
            manager.TryAddBar(symbol, CreateBar(TimeFrame.H1, referenceTime.AddHours(-2)), referenceTime, true);
            manager.TryAddBar(symbol, CreateBar(TimeFrame.M15, referenceTime.AddMinutes(-45)), referenceTime, true);
        }

        private static MarketData CreateMarketData(DateTime timestamp)
        {
            return new MarketData("EURUSD", 1.1010, 1.1012, timestamp);
        }

        private static Bar CreateBar(TimeFrame tf, DateTime openTime)
        {
            return new Bar
            {
                OpenTime = openTime,
                Open = 1.0,
                High = 1.1,
                Low = 0.9,
                Close = 1.05,
                Volume = 1000,
                Tf = tf
            };
        }

        private sealed class TestStrategy : MultiTimeframeStrategyBase
        {
            public TestStrategy(
                TimeframeManager timeframeManager,
                TimeframeSynchronizer synchronizer,
                SessionAwareAnalyzer sessionAnalyzer)
                : base("TestStrategy", timeframeManager, synchronizer, sessionAnalyzer)
            {
            }

            public int EvaluationCount { get; private set; }

            protected override Task<Signal?> EvaluateMultiTimeframeAsync(
                MultiTimeframeEvaluationContext context,
                CancellationToken ct)
            {
                EvaluationCount++;
                var signal = new Signal
                {
                    StrategyName = Name,
                    Action = TradeAction.Buy,
                    Price = context.MarketData.Mid,
                    TimestampUtc = context.MarketData.TimestampUtc,
                    Confidence = 0.5
                };

                return Task.FromResult<Signal?>(signal);
            }

            public override RiskScore CalculateRisk(MarketContext context)
            {
                return new RiskScore(1.0, RiskLevel.Normal, true);
            }
        }
    }
}
