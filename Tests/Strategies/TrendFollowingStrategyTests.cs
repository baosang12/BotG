#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BotG.MultiTimeframe;
using DataFetcher.Models;
using Strategies;
using Strategies.Config;
using Strategies.Templates;
using Xunit;
using TradingSession = BotG.MultiTimeframe.TradingSession;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.Tests.Strategies
{
    public sealed class TrendFollowingStrategyTests
    {
        [Fact]
        public async Task EvaluateMultiTimeframeAsync_GeneratesLongSignal_WhenTrendAligned()
        {
            // Arrange
            var strategy = CreateStrategy();
            var timestamp = new DateTime(2025, 1, 6, 12, 0, 0, DateTimeKind.Utc);
            var context = TrendFollowingTestFixture.CreateContext(timestamp, isLong: true);

            // Act
            var signal = await InvokeEvaluationAsync(strategy, context);

            // Assert
            Assert.NotNull(signal);
            Assert.Equal(TradeAction.Buy, signal!.Action);
            Assert.True(signal.Confidence >= 0.2);
            Assert.True(signal.StopLoss.HasValue);
            Assert.True(signal.TakeProfit.HasValue);
        }

        [Fact]
        public async Task EvaluateMultiTimeframeAsync_EmitsExit_WhenTrendFlipsAgainstPosition()
        {
            var strategy = CreateStrategy();
            var entryTime = new DateTime(2025, 1, 6, 12, 0, 0, DateTimeKind.Utc);
            var longContext = TrendFollowingTestFixture.CreateContext(entryTime, isLong: true);
            var entrySignal = await InvokeEvaluationAsync(strategy, longContext);

            Assert.NotNull(entrySignal);
            Assert.Equal(TradeAction.Buy, entrySignal!.Action);

            var exitTime = entryTime.AddMinutes(120);
            var exitContext = TrendFollowingTestFixture.CreateContext(exitTime, isLong: false);
            var exitSignal = await InvokeEvaluationAsync(strategy, exitContext);

            Assert.NotNull(exitSignal);
            Assert.Equal(TradeAction.Exit, exitSignal!.Action);
        }

        [Fact]
        public async Task EvaluateMultiTimeframeAsync_SuppressesSignal_WhenPullbackExceedsLimit()
        {
            var strategy = CreateStrategy(cfg => cfg.MaxPullbackAtr = 0.3);
            var timestamp = new DateTime(2025, 1, 6, 14, 0, 0, DateTimeKind.Utc);
            var context = TrendFollowingTestFixture.CreateContext(timestamp, isLong: true, bars =>
            {
                TrendFollowingTestFixture.ApplyDeepPullback(bars, depth: 0.02);
            });

            var signal = await InvokeEvaluationAsync(strategy, context);

            Assert.Null(signal);
        }

        [Fact]
        public async Task EvaluateMultiTimeframeAsync_DelaysReentry_UntilCooldownExpires()
        {
            var strategy = CreateStrategy(cfg => cfg.ReentryCooldownMinutes = 10);
            var entryTime = new DateTime(2025, 1, 6, 12, 0, 0, DateTimeKind.Utc);
            var entryContext = TrendFollowingTestFixture.CreateContext(entryTime, isLong: true);
            var entrySignal = await InvokeEvaluationAsync(strategy, entryContext);

            Assert.NotNull(entrySignal);
            Assert.Equal(TradeAction.Buy, entrySignal!.Action);

            var exitTime = entryTime.AddSeconds(30);
            var exitContext = TrendFollowingTestFixture.CreateContext(exitTime, isLong: false);
            var exitSignal = await InvokeEvaluationAsync(strategy, exitContext);

            Assert.NotNull(exitSignal);
            Assert.Equal(TradeAction.Exit, exitSignal!.Action);

            var blockedTime = entryTime.AddMinutes(5);
            var blockedContext = TrendFollowingTestFixture.CreateContext(blockedTime, isLong: true);
            var blockedSignal = await InvokeEvaluationAsync(strategy, blockedContext);

            Assert.Null(blockedSignal);

            var resumeTime = entryTime.AddMinutes(15);
            var resumeContext = TrendFollowingTestFixture.CreateContext(resumeTime, isLong: true);
            var resumedSignal = await InvokeEvaluationAsync(strategy, resumeContext);

            Assert.NotNull(resumedSignal);
            Assert.Equal(TradeAction.Buy, resumedSignal!.Action);
        }

        [Fact]
        public async Task EvaluateMultiTimeframeAsync_BlocksSignal_WhenAtrTooLowDespiteStrongAdx()
        {
            var strategy = CreateStrategy(cfg =>
            {
                cfg.MaxPullbackAtr = 0.5;
                cfg.MinimumAdx = 3.0;
                cfg.AdxPeriod = 5;
                cfg.AtrPeriod = 5;
            });

            var timestamp = new DateTime(2025, 1, 6, 16, 0, 0, DateTimeKind.Utc);
            var context = TrendFollowingTestFixture.CreateContext(timestamp, isLong: true, bars =>
            {
                TrendFollowingTestFixture.ApplyAtrCompression(bars, ModelTimeFrame.H1, compressionRange: 0.00001);
                TrendFollowingTestFixture.ApplyDeepPullback(bars, depth: 0.0002);
            });

            var signal = await InvokeEvaluationAsync(strategy, context);

            Assert.Null(signal);

            var diagnostics = DiagnosticsProbe.Read(strategy);
            Assert.NotNull(diagnostics);
            Assert.True(diagnostics!.Adx >= 3.0 - 1e-6);
            Assert.Null(diagnostics.Action);
        }

        private static TrendFollowingStrategy CreateStrategy(Action<TrendFollowingStrategyConfig>? configure = null)
        {
            var config = new TrendFollowingStrategyConfig
            {
                TrendEmaFast = 3,
                TrendEmaSlow = 5,
                SignalEmaFast = 3,
                SignalEmaSlow = 5,
                TriggerEmaFast = 2,
                TriggerEmaSlow = 3,
                TrendSlopeLookback = 3,
                MomentumSlopeLookback = 3,
                MinimumTrendSeparationRatio = 0.00001,
                TargetTrendSeparationRatio = 0.0015,
                MinimumTrendSlopeRatio = 0.00001,
                MinimumMomentumSlopeRatio = 0.00001,
                ExitSeparationRatio = 0.00002,
                AdxPeriod = 5,
                MinimumAdx = 5.0,
                AtrPeriod = 5,
                AtrStopMultiplier = 1.2,
                AtrTakeProfitMultiplier = 2.0,
                MaxPullbackAtr = 10.0,
                TriggerReEntryAtr = 10.0,
                PullbackLookbackBars = 5,
                MinimumAlignmentRatio = 0.5,
                MinimumConfidence = 0.2,
                ReentryCooldownMinutes = 1,
                ExitCooldownMinutes = 1
            };
            configure?.Invoke(config);
            config.Validate();

            return new TrendFollowingStrategy(
                new TimeframeManager(new TimeframeManagerConfig()),
                new TimeframeSynchronizer(),
                new SessionAwareAnalyzer(),
                config);
        }

        private static Task<Signal?> InvokeEvaluationAsync(TrendFollowingStrategy strategy, MultiTimeframeEvaluationContext context)
        {
            var method = typeof(TrendFollowingStrategy)
                .GetMethod("EvaluateMultiTimeframeAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            if (method == null)
            {
                throw new InvalidOperationException("Unable to resolve evaluation method via reflection.");
            }

            var task = (Task<Signal?>)method.Invoke(strategy, new object[] { context, CancellationToken.None })!;
            return task;
        }

        private static class TrendFollowingTestFixture
        {
            private static readonly ModelTimeFrame[] OrderedTimeframes =
            {
                ModelTimeFrame.H4,
                ModelTimeFrame.H1,
                ModelTimeFrame.M15
            };

            public static MultiTimeframeEvaluationContext CreateContext(
                DateTime timestampUtc,
                bool isLong,
                Action<IDictionary<ModelTimeFrame, IReadOnlyList<Bar>>>? customize = null)
            {
                var h4 = CreateTrendSeries(ModelTimeFrame.H4, timestampUtc.AddHours(-400), 80, isLong ? 0.0020 : -0.0020);
                var h1 = CreateTrendSeries(ModelTimeFrame.H1, timestampUtc.AddHours(-120), 120, isLong ? 0.0008 : -0.0008);
                var m15 = CreateTrendSeries(ModelTimeFrame.M15, timestampUtc.AddHours(-30), 120, isLong ? 0.0003 : -0.0003);

                var barsByTf = new Dictionary<ModelTimeFrame, IReadOnlyList<Bar>>
                {
                    [ModelTimeFrame.H4] = h4,
                    [ModelTimeFrame.H1] = h1,
                    [ModelTimeFrame.M15] = m15
                };

                customize?.Invoke(barsByTf);

                var snapshot = new TimeframeSnapshot("EURUSD", timestampUtc, OrderedTimeframes, barsByTf);
                var alignment = BuildAlignment(snapshot);
                var lastClose = m15[^1].Close;
                var marketData = new MarketData("EURUSD", lastClose - 0.00005, lastClose + 0.00005, timestampUtc);

                return new MultiTimeframeEvaluationContext(
                    marketData,
                    snapshot,
                    alignment,
                    TradingSession.Overlap,
                    null);
            }

            private static TimeframeAlignmentResult BuildAlignment(TimeframeSnapshot snapshot)
            {
                var statuses = OrderedTimeframes.ToDictionary(tf => tf, tf =>
                {
                    var bars = snapshot.GetBars(tf);
                    var latest = bars[^1];
                    var closeTime = latest.OpenTime + TimeframeMath.GetDuration(tf);
                    return new TimeframeSeriesStatus(bars.Count, true, latest, closeTime);
                });

                return new TimeframeAlignmentResult(
                    true,
                    true,
                    OrderedTimeframes.Length,
                    OrderedTimeframes.Length,
                    statuses,
                    snapshot,
                    null,
                    OrderedTimeframes.Length,
                    true,
                    TimeSpan.Zero);
            }

            private static IReadOnlyList<Bar> CreateTrendSeries(ModelTimeFrame timeframe, DateTime start, int count, double step)
            {
                var bars = new List<Bar>(count);
                double price = 1.20;
                double direction = step >= 0 ? 1.0 : -1.0;
                double magnitude = Math.Abs(step);

                for (int i = 0; i < count; i++)
                {
                    var open = price;
                    var close = open + direction * magnitude;
                    var high = Math.Max(open, close) + magnitude * 0.6 + 0.0002;
                    var low = Math.Min(open, close) - magnitude * 0.6 - 0.0002;
                    var volume = 1500 + i * 5;
                    var bar = new Bar
                    {
                        OpenTime = AdvanceOpenTime(start, timeframe, i),
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume,
                        Tf = timeframe
                    };
                    bars.Add(bar);
                    price = close;
                }

                return bars;
            }

            public static void ApplyDeepPullback(IDictionary<ModelTimeFrame, IReadOnlyList<Bar>> barsByTf, double depth)
            {
                if (!barsByTf.TryGetValue(ModelTimeFrame.M15, out var series))
                {
                    throw new InvalidOperationException("M15 series unavailable for pullback injection.");
                }

                var mutable = series as List<Bar> ?? CloneSeries(series);
                barsByTf[ModelTimeFrame.M15] = mutable;

                var index = Math.Max(0, mutable.Count - 3);
                var bar = mutable[index];
                bar.Low -= depth;
                bar.Close = bar.Low + depth * 0.1;
            }

            public static void ApplyAtrCompression(
                IDictionary<ModelTimeFrame, IReadOnlyList<Bar>> barsByTf,
                ModelTimeFrame timeframe,
                double compressionRange)
            {
                if (!barsByTf.TryGetValue(timeframe, out var series))
                {
                    throw new InvalidOperationException($"{timeframe} series unavailable for ATR compression.");
                }

                var mutable = series as List<Bar> ?? CloneSeries(series);
                barsByTf[timeframe] = mutable;

                for (int i = 0; i < mutable.Count; i++)
                {
                    var bar = mutable[i];
                    var upper = Math.Max(bar.Open, bar.Close) + compressionRange;
                    var lower = Math.Min(bar.Open, bar.Close) - compressionRange;
                    bar.High = upper;
                    bar.Low = lower;
                }
            }

            private static List<Bar> CloneSeries(IReadOnlyList<Bar> source)
            {
                var clone = new List<Bar>(source.Count);
                for (int i = 0; i < source.Count; i++)
                {
                    var bar = source[i];
                    clone.Add(new Bar
                    {
                        OpenTime = bar.OpenTime,
                        Open = bar.Open,
                        High = bar.High,
                        Low = bar.Low,
                        Close = bar.Close,
                        Volume = bar.Volume,
                        Tf = bar.Tf
                    });
                }

                return clone;
            }

            private static DateTime AdvanceOpenTime(DateTime start, ModelTimeFrame timeframe, int index)
            {
                var duration = TimeframeMath.GetDuration(timeframe);
                return start + TimeSpan.FromTicks(duration.Ticks * index);
            }
        }

        private static class DiagnosticsProbe
        {
            private static readonly FieldInfo DiagnosticsField = typeof(TrendFollowingStrategy)
                .GetField("_lastDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Diagnostics field not found.");

            public static TrendDiagnosticsSnapshot? Read(TrendFollowingStrategy strategy)
            {
                var raw = DiagnosticsField.GetValue(strategy);
                if (raw == null)
                {
                    return null;
                }

                var type = raw.GetType();
                var action = (TradeAction?)type.GetProperty("Action")?.GetValue(raw);
                var confidence = (double)(type.GetProperty("Confidence")?.GetValue(raw) ?? 0.0);
                var trendStrength = (double)(type.GetProperty("TrendStrength")?.GetValue(raw) ?? 0.0);
                var adx = (double)(type.GetProperty("Adx")?.GetValue(raw) ?? 0.0);
                var alignment = (double)(type.GetProperty("Alignment")?.GetValue(raw) ?? 0.0);
                var sessionMultiplier = (double)(type.GetProperty("SessionMultiplier")?.GetValue(raw) ?? 0.0);

                return new TrendDiagnosticsSnapshot(action, confidence, trendStrength, adx, alignment, sessionMultiplier);
            }
        }

        private sealed record TrendDiagnosticsSnapshot(
            TradeAction? Action,
            double Confidence,
            double TrendStrength,
            double Adx,
            double Alignment,
            double SessionMultiplier);
    }
}
