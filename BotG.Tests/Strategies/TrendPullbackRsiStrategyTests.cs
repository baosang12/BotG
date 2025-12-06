using System;
using System.Collections.Generic;
using Analysis.Realtime;
using DataFetcher.Models;
using Strategies;
using Xunit;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.Tests.Strategies
{
    public sealed class TrendPullbackRsiStrategyTests
    {
        [Fact]
        public void AnalyzeTrend_ReturnsBuy_WhenFastAboveSlow()
        {
            var bars = TrendPullbackTestData.BuildTrendingBars(ModelTimeFrame.H1, 260, 1.1000, 0.0007);

            var result = TrendPullbackCalculations.AnalyzeTrend(bars, 50, 200, 0.0005);

            Assert.True(result.IsActionable);
            Assert.Equal(TradeAction.Buy, result.Direction);
            Assert.True(result.Strength > 0.4);
        }

        [Fact]
        public void AnalyzeTrend_ReturnsSell_WhenFastBelowSlow()
        {
            var bars = TrendPullbackTestData.BuildTrendingBars(ModelTimeFrame.H1, 260, 1.2000, -0.0007);

            var result = TrendPullbackCalculations.AnalyzeTrend(bars, 50, 200, 0.0005);

            Assert.True(result.IsActionable);
            Assert.Equal(TradeAction.Sell, result.Direction);
            Assert.True(result.Strength > 0.4);
        }

        [Fact]
        public void EvaluateRsiTrigger_DetectsBullishCross()
        {
            var bars = TrendPullbackTestData.BuildRsiCrossBars(bullish: true);

            var trigger = TrendPullbackCalculations.EvaluateRsiTrigger(
                bars,
                rsiPeriod: 14,
                oversold: 30,
                overbought: 70,
                direction: TradeAction.Buy,
                releaseRange: 20);

            Assert.True(trigger.IsTriggered);
            Assert.True(trigger.Score > 0);
            Assert.Equal("rsi_cross_from_oversold", trigger.Reason);
            Assert.True(trigger.Current > 30);
            Assert.True(trigger.Previous <= 30);
        }

        [Fact]
        public void EvaluateRsiTrigger_DetectsBearishCross()
        {
            var bars = TrendPullbackTestData.BuildRsiCrossBars(bullish: false);

            var trigger = TrendPullbackCalculations.EvaluateRsiTrigger(
                bars,
                rsiPeriod: 14,
                oversold: 30,
                overbought: 70,
                direction: TradeAction.Sell,
                releaseRange: 20);

            Assert.True(trigger.IsTriggered);
            Assert.True(trigger.Score > 0);
            Assert.Equal("rsi_cross_from_overbought", trigger.Reason);
            Assert.True(trigger.Current < 70);
            Assert.True(trigger.Previous >= 70);
        }
    }

    internal static class TrendPullbackTestData
    {
        public static IReadOnlyList<Bar> BuildTrendingBars(ModelTimeFrame timeframe, int count, double startPrice, double step)
        {
            var result = new List<Bar>(count);
            var minutes = GetMinutes(timeframe);
            var time = DateTime.UtcNow.AddMinutes(-count * minutes);
            double price = startPrice;

            for (int i = 0; i < count; i++)
            {
                price += step;
                result.Add(new Bar
                {
                    OpenTime = time,
                    Open = price,
                    High = price + Math.Abs(step) * 0.6,
                    Low = price - Math.Abs(step) * 0.6,
                    Close = price,
                    Volume = 1_000,
                    Tf = timeframe
                });
                time = time.AddMinutes(minutes);
            }

            return result;
        }

        public static IReadOnlyList<Bar> BuildRsiCrossBars(bool bullish)
        {
            const int period = 14;
            var totalBars = period + 2;
            var diffs = new double[totalBars];

            for (int i = 1; i < totalBars - 1; i++)
            {
                diffs[i] = bullish ? -1.0 : 1.0;
            }

            diffs[totalBars - 1] = bullish ? 10.0 : -10.0;

            var bars = new List<Bar>(totalBars);
            var minutes = GetMinutes(ModelTimeFrame.M15);
            var time = DateTime.UtcNow.AddMinutes(-totalBars * minutes);
            double price = 100.0;

            bars.Add(new Bar
            {
                OpenTime = time,
                Open = price,
                High = price + 0.5,
                Low = price - 0.5,
                Close = price,
                Volume = 1_000,
                Tf = ModelTimeFrame.M15
            });

            for (int i = 1; i < totalBars; i++)
            {
                price += diffs[i];
                time = time.AddMinutes(minutes);
                bars.Add(new Bar
                {
                    OpenTime = time,
                    Open = price,
                    High = price + 0.5,
                    Low = price - 0.5,
                    Close = price,
                    Volume = 1_000,
                    Tf = ModelTimeFrame.M15
                });
            }

            return bars;
        }

        private static int GetMinutes(ModelTimeFrame timeframe)
        {
            return timeframe switch
            {
                ModelTimeFrame.M15 => 15,
                ModelTimeFrame.M30 => 30,
                ModelTimeFrame.H1 => 60,
                ModelTimeFrame.H4 => 240,
                _ => 15
            };
        }
    }
}
