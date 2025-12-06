using System;
using System.Collections.Generic;
using System.Linq;
using BotG.MultiTimeframe;
using DataFetcher.Models;
using Xunit;

namespace BotG.Tests.MultiTimeframe
{
    public class TimeframeManagerTests
    {
        private static readonly TimeFrame[] DefaultStack = { TimeFrame.H4, TimeFrame.H1, TimeFrame.M15 };

        [Fact]
        public void TryAddBar_SkipsUnclosedBars_WhenRequireClosed()
        {
            var config = new TimeframeManagerConfig
            {
                Timeframes = new[] { TimeFrame.H1 },
                RequireClosedBars = true,
                AntiRepaintGuard = TimeSpan.FromSeconds(5)
            };

            var manager = new TimeframeManager(config);
            var symbol = "EURUSD";
            var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var incomingBar = CreateBar(TimeFrame.H1, now.AddMinutes(-30));

            var stored = manager.TryAddBar(symbol, incomingBar, now, isClosedBar: true);

            Assert.False(stored);

            var snapshot = manager.CaptureSnapshot(symbol, now);
            Assert.Empty(snapshot.GetBars(TimeFrame.H1));
        }

        [Fact]
        public void CaptureSnapshot_FiltersBarsUsingGuard()
        {
            var config = new TimeframeManagerConfig
            {
                Timeframes = new[] { TimeFrame.M15 },
                RequireClosedBars = false,
                AntiRepaintGuard = TimeSpan.FromMinutes(1)
            };

            var manager = new TimeframeManager(config);
            var symbol = "EURUSD";
            var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            var closedBar = CreateBar(TimeFrame.M15, now.AddMinutes(-60));
            var stillForming = CreateBar(TimeFrame.M15, now.AddMinutes(-10));

            Assert.True(manager.TryAddBar(symbol, closedBar, now, isClosedBar: true));
            Assert.True(manager.TryAddBar(symbol, stillForming, now, isClosedBar: false));

            var snapshot = manager.CaptureSnapshot(symbol, now);
            var series = snapshot.GetBars(TimeFrame.M15);

            Assert.Single(series);
            Assert.Equal(closedBar.OpenTime, series[0].OpenTime);
        }

        [Fact]
        public void Synchronizer_RespectsAntiRepaintLogic()
        {
            var synchronizer = new TimeframeSynchronizer(new TimeframeSynchronizerConfig
            {
                MinimumAlignedTimeframes = 3,
                MaximumAllowedSkew = TimeSpan.FromHours(3),
                AntiRepaintGuard = TimeSpan.FromSeconds(30),
                WarmupBarsRequired = 0,
                WarmupBarsPerTimeframe = new Dictionary<TimeFrame, int>()
            });
            var referenceTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            var snapshotWithOpenPrimary = CreateSnapshot(
                referenceTime,
                (TimeFrame.H4, referenceTime.AddHours(-2)),
                (TimeFrame.H1, referenceTime.AddHours(-1)),
                (TimeFrame.M15, referenceTime.AddMinutes(-5)));

            var resultNotClosed = synchronizer.GetAlignmentResult(snapshotWithOpenPrimary);

            Assert.False(resultNotClosed.AntiRepaintSafe);
            Assert.False(resultNotClosed.IsAligned);

            var snapshotClosed = CreateSnapshot(
                referenceTime,
                (TimeFrame.H4, referenceTime.AddHours(-6)),
                (TimeFrame.H1, referenceTime.AddHours(-2)),
                (TimeFrame.M15, referenceTime.AddMinutes(-30)));

            Assert.Single(snapshotClosed.GetBars(TimeFrame.H4));
            Assert.Single(snapshotClosed.GetBars(TimeFrame.H1));
            Assert.Single(snapshotClosed.GetBars(TimeFrame.M15));

            var resultClosed = synchronizer.GetAlignmentResult(snapshotClosed);

            var description = DescribeStatus(resultClosed);
            Assert.True(resultClosed.AntiRepaintSafe, description);
            Assert.True(resultClosed.IsAligned);
        }

        [Theory]
        [InlineData(0, TradingSession.Asian, 0.5)]
        [InlineData(7, TradingSession.Asian, 0.5)]
        [InlineData(8, TradingSession.London, 1.0)]
        [InlineData(13, TradingSession.Overlap, 1.5)]
        [InlineData(16, TradingSession.Overlap, 1.5)]
        [InlineData(17, TradingSession.NewYork, 1.2)]
        [InlineData(21, TradingSession.NewYork, 1.2)]
        [InlineData(22, TradingSession.Night, 0.3)]
        public void TestSessionMultipliers_CorrectValues(int hourUtc, TradingSession expectedSession, double expectedMultiplier)
        {
            var analyzer = new SessionAwareAnalyzer();
            var time = new DateTime(2025, 1, 1, hourUtc % 24, 0, 0, DateTimeKind.Utc);

            var session = analyzer.GetCurrentSession(time);
            var multiplier = analyzer.GetPositionSizeMultiplier(session);

            Assert.Equal(expectedSession, session);
            Assert.Equal(expectedMultiplier, multiplier, 3);
        }

        private static TimeframeSnapshot CreateSnapshot(DateTime timestampUtc, params (TimeFrame tf, DateTime openTime)[] definitions)
        {
            var bars = DefaultStack.ToDictionary(tf => tf, _ => (IReadOnlyList<Bar>)Array.Empty<Bar>());

            foreach (var definition in definitions)
            {
                bars[definition.tf] = new List<Bar> { CreateBar(definition.tf, definition.openTime) };
            }

            return new TimeframeSnapshot("EURUSD", timestampUtc, DefaultStack, bars);
        }

        private static Bar CreateBar(TimeFrame tf, DateTime openTime)
        {
            return new Bar
            {
                OpenTime = openTime,
                Open = 1.0,
                High = 1.0,
                Low = 1.0,
                Close = 1.0,
                Volume = 1,
                Tf = tf
            };
        }

        private static string DescribeStatus(TimeframeAlignmentResult result)
        {
            var segments = result.SeriesStatuses;
            return string.Join(
                "; ",
                segments.Select(s =>
                {
                    var close = s.Value.LatestCloseTime.HasValue
                        ? s.Value.LatestCloseTime.Value.ToString("O")
                        : "null";
                    return $"{s.Key}:count={s.Value.AvailableBars},close={close}";
                }));
        }
    }
}
