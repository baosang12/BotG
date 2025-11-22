#nullable enable

using System;
using System.Collections.Generic;
using BotG.MultiTimeframe;
using DataFetcher.Models;
using Strategies;
using Strategies.Breakout;
using Strategies.Config;
using Strategies.Confirmation;
using Strategies.Templates;
using TradingSession = BotG.MultiTimeframe.TradingSession;

namespace BotG.Tests.Strategies
{
    internal static class BreakoutStrategyFixtures
    {
        public static (MultiTimeframeEvaluationContext Context, BreakoutStrategyConfig Config) CreateBullishBreakoutScenario()
        {
            var config = CreateTestConfig();
            var timestamp = new DateTime(2025, 1, 3, 12, 0, 0, DateTimeKind.Utc);

            var h4 = CreateTrendSeries(TimeFrame.H4, timestamp.AddHours(-520), 260, 0.0008);
            var h1 = CreateKeyLevelSeries(TimeFrame.H1, timestamp.AddHours(-12), isLong: true, strongVolume: true);
            var m15 = CreateExecutionSeries(TimeFrame.M15, timestamp.AddHours(-4), isLong: true, strongVolume: true);

            var context = BuildContext(timestamp, h4, h1, m15);
            return (context, config);
        }

        public static (MultiTimeframeEvaluationContext Context, BreakoutStrategyConfig Config) CreateBearishBreakoutScenario()
        {
            var config = CreateTestConfig();
            var timestamp = new DateTime(2025, 1, 3, 12, 0, 0, DateTimeKind.Utc);

            var h4 = CreateTrendSeries(TimeFrame.H4, timestamp.AddHours(-520), 260, -0.0008);
            var h1 = CreateKeyLevelSeries(TimeFrame.H1, timestamp.AddHours(-12), isLong: false, strongVolume: true);
            var m15 = CreateExecutionSeries(TimeFrame.M15, timestamp.AddHours(-4), isLong: false, strongVolume: true);

            var context = BuildContext(timestamp, h4, h1, m15);
            return (context, config);
        }

        public static (MultiTimeframeEvaluationContext Context, BreakoutStrategyConfig Config) CreateLowVolumeScenario()
        {
            var config = CreateTestConfig();
            var timestamp = new DateTime(2025, 1, 3, 12, 0, 0, DateTimeKind.Utc);

            var h4 = CreateTrendSeries(TimeFrame.H4, timestamp.AddHours(-520), 260, 0.0008);
            var h1 = CreateKeyLevelSeries(TimeFrame.H1, timestamp.AddHours(-12), isLong: true, strongVolume: false);
            var m15 = CreateExecutionSeries(TimeFrame.M15, timestamp.AddHours(-4), isLong: true, strongVolume: false);

            var context = BuildContext(timestamp, h4, h1, m15);
            return (context, config);
        }

        public static (MultiTimeframeEvaluationContext Context, BreakoutStrategyConfig Config) CreateFalseBreakoutScenario()
        {
            var config = CreateTestConfig();
            var timestamp = new DateTime(2025, 1, 3, 12, 0, 0, DateTimeKind.Utc);

            var h4 = CreateTrendSeries(TimeFrame.H4, timestamp.AddHours(-520), 260, 0.0006);
            var h1 = CreateFalseKeyLevelSeries(TimeFrame.H1, timestamp.AddHours(-12));
            var m15 = CreateExecutionSeries(TimeFrame.M15, timestamp.AddHours(-4), isLong: true, strongVolume: false);

            var context = BuildContext(timestamp, h4, h1, m15);
            return (context, config);
        }

        public static BreakoutStrategy CreateStrategy(BreakoutStrategyConfig config, ConfirmationConfig? confirmationConfig = null)
        {
            var manager = new TimeframeManager(new TimeframeManagerConfig());
            var synchronizer = new TimeframeSynchronizer();
            var sessionAnalyzer = new SessionAwareAnalyzer();
            return new BreakoutStrategy(manager, synchronizer, sessionAnalyzer, config, confirmationConfig);
        }

        public static (MultiTimeframeEvaluationContext Context, BreakoutStrategyConfig Config) CreateMinimalWarmupScenario()
        {
            var config = CreateTestConfig();
            var timestamp = new DateTime(2025, 1, 3, 12, 0, 0, DateTimeKind.Utc);

            var h4Bars = Math.Max(8, config.TrendEmaSlow + 5);
            var h4 = CreateTrendSeries(TimeFrame.H4, timestamp.AddHours(-h4Bars * 4), h4Bars, 0.001);
            var h1 = CreateKeyLevelSeries(TimeFrame.H1, timestamp.AddHours(-20), isLong: true, strongVolume: true, totalBars: 20);
            var m15 = CreateExecutionSeries(TimeFrame.M15, timestamp.AddHours(-12), isLong: true, strongVolume: true, totalBars: 48);

            var context = BuildContext(timestamp, h4, h1, m15);
            return (context, config);
        }

        private static MultiTimeframeEvaluationContext BuildContext(
            DateTime timestamp,
            IReadOnlyList<Bar> h4,
            IReadOnlyList<Bar> h1,
            IReadOnlyList<Bar> m15)
        {
            var ordered = new[] { TimeFrame.H4, TimeFrame.H1, TimeFrame.M15 };
            var barsByTf = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
            {
                [TimeFrame.H4] = h4,
                [TimeFrame.H1] = h1,
                [TimeFrame.M15] = m15
            };

            var snapshot = new TimeframeSnapshot("EURUSD", timestamp, ordered, barsByTf);
            var alignment = BuildAlignmentResult(snapshot);

            var marketData = new global::Strategies.MarketData("EURUSD", 1.2048, 1.2052, timestamp);
            return new MultiTimeframeEvaluationContext(
                marketData,
                snapshot,
                alignment,
                TradingSession.Overlap);
        }

        private static TimeframeAlignmentResult BuildAlignmentResult(TimeframeSnapshot snapshot)
        {
            var statuses = new Dictionary<TimeFrame, TimeframeSeriesStatus>();
            foreach (var tf in snapshot.OrderedTimeframes)
            {
                var series = snapshot.GetBars(tf);
                var latest = series[^1];
                statuses[tf] = new TimeframeSeriesStatus(series.Count, true, latest, latest.OpenTime.AddMinutes(tf switch
                {
                    TimeFrame.H4 => 240,
                    TimeFrame.H1 => 60,
                    TimeFrame.M15 => 15,
                    _ => 60
                }));
            }

            return new TimeframeAlignmentResult(
                true,
                true,
                snapshot.TotalTimeframes,
                snapshot.TotalTimeframes,
                statuses,
                snapshot,
                null,
                snapshot.TotalTimeframes,
                true,
                TimeSpan.Zero);
        }

        private static BreakoutStrategyConfig CreateTestConfig()
        {
            return new BreakoutStrategyConfig
            {
                MinimumStrength = 0.1,
                VolumeMultiplier = 1.1,
                RetestWindowBars = 3,
                MaxBreakoutBars = 2,
                TouchTolerancePercent = 0.3,
                WeeklyVolumeThreshold = 0.01,
                OrderBlockDensityMin = 0,
                TrendEmaFast = 3,
                TrendEmaSlow = 5,
                MaximumRetestPercent = 0.6,
                AtrConfirmationMultiplier = 0.2,
                AtrPeriod = 3,
                VolumeSmaPeriod = 3,
                TouchLookbackBars = 15,
                MinimumTouches = 2,
                MinimumH1Bars = 20,
                MinimumM15Bars = 48,
                EnableMultiTimeframeConfirmation = true,
                MinimumConfirmationThreshold = 0.6
            };
        }

        private static IReadOnlyList<Bar> CreateTrendSeries(TimeFrame timeframe, DateTime start, int count, double step)
        {
            var bars = new List<Bar>(count);
            double price = 1.10;
            for (int i = 0; i < count; i++)
            {
                var open = price;
                var close = open + step;
                bars.Add(CreateBar(timeframe, start.AddMinutes(i * IntervalMinutes(timeframe)), open, close + 0.0003, close - 0.0003, close, 1000 + i));
                price = close;
            }
            return bars;
        }

        private static IReadOnlyList<Bar> CreateKeyLevelSeries(TimeFrame timeframe, DateTime start, bool isLong, bool strongVolume, int totalBars = 20)
        {
            totalBars = Math.Max(totalBars, 6);
            var bars = new List<Bar>(totalBars);
            double keyLevel = 1.2000;
            double price = keyLevel - (isLong ? 0.01 : -0.01);
            var minutes = IntervalMinutes(timeframe);
            var direction = isLong ? 1.0 : -1.0;

            int lastTouchIndex = Math.Max(0, totalBars - 3);
            int spacing = Math.Max(1, totalBars / 5);
            int midTouchIndex = Math.Max(0, lastTouchIndex - spacing);
            int earlyTouchIndex = Math.Max(0, midTouchIndex - spacing);
            var touchIndices = new HashSet<int>(new[] { earlyTouchIndex, midTouchIndex, lastTouchIndex });

            for (int i = 0; i < totalBars; i++)
            {
                var openTime = start.AddMinutes(i * minutes);
                bool touching = touchIndices.Contains(i);
                bool isRetest = i == totalBars - 2;
                bool isBreakout = i == totalBars - 1;

                double close;
                long volume;

                if (isBreakout)
                {
                    close = keyLevel + direction * 0.006;
                    volume = strongVolume ? 2600 : 1400;
                }
                else if (isRetest)
                {
                    close = keyLevel - direction * 0.0005;
                    volume = 1200;
                }
                else if (touching)
                {
                    close = keyLevel - direction * 0.0002;
                    volume = strongVolume ? 2000 : 1500;
                }
                else
                {
                    close = price + direction * (0.00015 + 0.00005 * Math.Sin(i));
                    volume = 900;
                }

                var high = Math.Max(price, close) + 0.0004;
                var low = Math.Min(price, close) - 0.0004;

                bars.Add(CreateBar(timeframe, openTime, price, high, low, close, volume));
                price = close;
            }

            return bars;
        }

        private static IReadOnlyList<Bar> CreateFalseKeyLevelSeries(TimeFrame timeframe, DateTime start, int totalBars = 20)
        {
            totalBars = Math.Max(totalBars, 6);
            var bars = new List<Bar>(totalBars);
            double keyLevel = 1.2000;
            double price = keyLevel - 0.01;
            var minutes = IntervalMinutes(timeframe);
            int earlyTouch = Math.Max(1, totalBars / 4);
            int lateTouch = Math.Max(earlyTouch + 1, totalBars / 2);
            var touchIndices = new HashSet<int>(new[] { earlyTouch, lateTouch });

            for (int i = 0; i < totalBars; i++)
            {
                var openTime = start.AddMinutes(i * minutes);
                bool touching = touchIndices.Contains(i);
                bool fakeBreakout = i == totalBars - 1;

                double close;
                long volume;

                if (fakeBreakout)
                {
                    close = keyLevel - 0.0002;
                    volume = 550;
                }
                else if (touching)
                {
                    close = keyLevel - 0.0005;
                    volume = 700;
                }
                else
                {
                    close = price + 0.00012;
                    volume = 600;
                }

                var high = Math.Max(price, close) + 0.0003;
                var low = Math.Min(price, close) - 0.0003;

                bars.Add(CreateBar(timeframe, openTime, price, high, low, close, volume));
                price = close;
            }

            return bars;
        }

        private static IReadOnlyList<Bar> CreateExecutionSeries(TimeFrame timeframe, DateTime start, bool isLong, bool strongVolume, int totalBars = 48)
        {
            totalBars = Math.Max(totalBars, 6);
            var bars = new List<Bar>(totalBars);
            double keyLevel = 1.2000;
            double price = keyLevel - (isLong ? 0.004 : -0.004);
            var minutes = IntervalMinutes(timeframe);
            var direction = isLong ? 1.0 : -1.0;
            int rampStart = Math.Max(1, totalBars - 6);
            int retestIndex = Math.Max(1, totalBars - 3);
            int breakoutIndex = totalBars - 1;

            for (int i = 0; i < totalBars; i++)
            {
                var openTime = start.AddMinutes(i * minutes);
                bool isRampBar = i >= rampStart && i < breakoutIndex;
                bool isRetest = i == retestIndex;
                bool isBreakout = i == breakoutIndex;

                double close;
                long volume;

                if (isBreakout)
                {
                    close = keyLevel + direction * 0.0045;
                    volume = strongVolume ? 1800 : 600;
                }
                else if (isRetest)
                {
                    close = keyLevel - direction * 0.0004;
                    volume = 800;
                }
                else if (isRampBar)
                {
                    close = price + direction * 0.00035;
                    volume = 900;
                }
                else
                {
                    close = price + direction * 0.00015;
                    volume = 600;
                }

                var high = Math.Max(price, close) + 0.0002;
                var low = Math.Min(price, close) - 0.0002;

                bars.Add(CreateBar(timeframe, openTime, price, high, low, close, volume));
                price = close;
            }

            return bars;
        }

        private static Bar CreateBar(TimeFrame timeframe, DateTime openTime, double open, double high, double low, double close, long volume)
        {
            return new Bar
            {
                OpenTime = openTime,
                Open = open,
                High = Math.Max(high, Math.Max(open, close)),
                Low = Math.Min(low, Math.Min(open, close)),
                Close = close,
                Volume = Math.Max(1, volume),
                Tf = timeframe
            };
        }

        private static int IntervalMinutes(TimeFrame timeframe)
        {
            return timeframe switch
            {
                TimeFrame.H4 => 240,
                TimeFrame.H1 => 60,
                TimeFrame.M15 => 15,
                _ => 60
            };
        }
    }
}
