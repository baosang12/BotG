#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BotG.MultiTimeframe;
using DataFetcher.Models;
using Strategies;
using Strategies.Config;
using Strategies.Templates;
using Xunit;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.Tests.Strategies
{
    public sealed class Ema200BreakoutStrategyTests
    {
        [Fact]
        public async Task EvaluateMultiTimeframeAsync_GeneratesBuySignal_WhenPriceCrossesAboveEma()
        {
            var strategy = Ema200BreakoutTestFixture.CreateStrategy(cfg =>
            {
                cfg.CooldownMinutes = 30;
            });

            var bars = Ema200BreakoutTestFixture.CreateBullishBreakoutBars();
            var timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var context = Ema200BreakoutTestFixture.BuildContext(bars, timestamp);

            var signal = await InvokeEvaluationAsync(strategy, context);

            Assert.NotNull(signal);
            Assert.Equal(TradeAction.Buy, signal!.Action);
            Assert.True(signal.Confidence > 0.0);
            Assert.Equal("Ema200Breakout", signal.StrategyName);
            Assert.True(signal.StopLoss.HasValue);
            Assert.True(signal.TakeProfit.HasValue);
        }

        [Fact]
        public async Task EvaluateMultiTimeframeAsync_EnforcesCooldown_ForSameDirection()
        {
            var strategy = Ema200BreakoutTestFixture.CreateStrategy(cfg =>
            {
                cfg.CooldownMinutes = 60;
            });

            var bars = Ema200BreakoutTestFixture.CreateBullishBreakoutBars();
            var baseTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            var firstContext = Ema200BreakoutTestFixture.BuildContext(bars, baseTime);
            var firstSignal = await InvokeEvaluationAsync(strategy, firstContext);
            Assert.NotNull(firstSignal);
            Assert.Equal(TradeAction.Buy, firstSignal!.Action);

            var blockedContext = Ema200BreakoutTestFixture.BuildContext(bars, baseTime.AddMinutes(30));
            var blockedSignal = await InvokeEvaluationAsync(strategy, blockedContext);
            Assert.Null(blockedSignal);

            var resumeContext = Ema200BreakoutTestFixture.BuildContext(bars, baseTime.AddMinutes(120));
            var resumedSignal = await InvokeEvaluationAsync(strategy, resumeContext);
            Assert.NotNull(resumedSignal);
            Assert.Equal(TradeAction.Buy, resumedSignal!.Action);
        }

        private static Task<Signal?> InvokeEvaluationAsync(
            Ema200BreakoutStrategy strategy,
            MultiTimeframeEvaluationContext context)
        {
            var method = typeof(Ema200BreakoutStrategy)
                .GetMethod("EvaluateMultiTimeframeAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            if (method == null)
            {
                throw new InvalidOperationException("Không thể access EvaluateMultiTimeframeAsync");
            }

            var task = (Task<Signal?>)method.Invoke(strategy, new object[] { context, CancellationToken.None })!;
            return task;
        }
    }

    internal static class Ema200BreakoutTestFixture
    {
        private const string Symbol = "EURUSD";

        public static Ema200BreakoutStrategy CreateStrategy(Action<Ema200BreakoutStrategyConfig>? configure = null)
        {
            var config = new Ema200BreakoutStrategyConfig
            {
                TriggerTimeframe = "H1",
                EmaPeriod = 5,
                AtrPeriod = 3,
                AdxPeriod = 3,
                MinimumAtr = 0.00001,
                MinimumAdx = 1.0,
                BreakoutBuffer = 0.0,
                MinimumDistanceAtrMultiple = 0.05,
                AtrStopMultiplier = 1.0,
                AtrTakeProfitMultiplier = 2.0,
                CooldownMinutes = 60,
                MinimumBars = 16
            };

            configure?.Invoke(config);
            config.Validate();

            var managerConfig = new TimeframeManagerConfig
            {
                Timeframes = new[] { ModelTimeFrame.H1 },
                RequireClosedBars = true,
                AntiRepaintGuard = TimeSpan.FromSeconds(1)
            };

            var synchronizerConfig = new TimeframeSynchronizerConfig
            {
                MinimumAlignedTimeframes = 1,
                MinimumBarsPerTimeframe = 1,
                WarmupBarsRequired = 0,
                RequiredAlignmentRatio = 1.0,
                EnableAntiRepaint = false,
                EnableSkewCheck = false
            };

            return new Ema200BreakoutStrategy(
                new TimeframeManager(managerConfig),
                new TimeframeSynchronizer(synchronizerConfig),
                new SessionAwareAnalyzer(),
                config);
        }

        public static IReadOnlyList<ModelBar> CreateBullishBreakoutBars()
        {
            var closes = new[]
            {
                0.9990,
                0.9992,
                0.9994,
                0.9996,
                0.9998,
                1.0000,
                1.0000,
                1.0002,
                1.0004,
                1.0006,
                1.0008,
                1.0010,
                1.0012,
                1.0013,
                1.0014,
                1.0015,
                1.0016,
                0.9000,
                1.2000
            };

            return BuildBars(closes);
        }

        private static IReadOnlyList<ModelBar> BuildBars(IReadOnlyList<double> closes)
        {
            var bars = new List<ModelBar>(closes.Count);
            var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < closes.Count; i++)
            {
                var close = closes[i];
                var openTime = start.AddHours(i);

                bars.Add(new ModelBar
                {
                    Tf = ModelTimeFrame.H1,
                    OpenTime = openTime,
                    Open = close - 0.0002,
                    Close = close,
                    High = close + 0.0008,
                    Low = close - 0.0008,
                    Volume = 1000 + i
                });
            }

            return bars;
        }

        public static MultiTimeframeEvaluationContext BuildContext(
            IReadOnlyList<ModelBar> bars,
            DateTime? timestampUtc = null)
        {
            if (bars.Count == 0)
            {
                throw new ArgumentException("Danh sách bar rỗng", nameof(bars));
            }

            var timestamp = timestampUtc ?? bars[^1].OpenTime.AddHours(1);
            var ordered = new[] { ModelTimeFrame.H1 };
            var map = new Dictionary<ModelTimeFrame, IReadOnlyList<ModelBar>>
            {
                [ModelTimeFrame.H1] = bars
            };

            var snapshot = new TimeframeSnapshot(Symbol, timestamp, ordered, map);
            var status = new TimeframeSeriesStatus(bars.Count, true, bars[^1], timestamp);
            var statuses = new Dictionary<ModelTimeFrame, TimeframeSeriesStatus>
            {
                [ModelTimeFrame.H1] = status
            };

            var alignment = new TimeframeAlignmentResult(
                true,
                true,
                1,
                1,
                statuses,
                snapshot,
                Reason: null,
                RequiredAlignedTimeframes: 1,
                WarmupSatisfied: true,
                ObservedSkew: TimeSpan.Zero);

            var lastClose = bars[^1].Close;
            var marketData = new MarketData(
                Symbol,
                lastClose - 0.0001,
                lastClose + 0.0001,
                timestamp);

            return new MultiTimeframeEvaluationContext(
                marketData,
                snapshot,
                alignment,
                TradingSession.Overlap);
        }
    }
}
