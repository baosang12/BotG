using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BotG.MarketRegime;
using Xunit;

namespace BotG.Tests.MarketRegime
{
    public class RegimeIndicatorsTests
    {
    private static readonly TimeSpan PerformanceBaseline = TimeSpan.FromMilliseconds(20);

        [Fact]
        public void CalculateRollingAverageAtr_MatchesNaiveImplementation()
        {
            var indicators = new RegimeIndicators();
            const int period = 14;
            const int sampleCount = 10;

            var (highs, lows, closes) = BuildSyntheticSeries(150);

            double optimized = indicators.CalculateRollingAverageAtr(highs, lows, closes, period, sampleCount);
            double naive = CalculateNaiveAverageAtr(indicators, highs, lows, closes, period, sampleCount);

            Assert.InRange(optimized, naive - 1e-6, naive + 1e-6);
        }

        [Fact]
        public void CalculateRollingAverageAtr_Performance_Improved()
        {
            var indicators = new RegimeIndicators();
            const int period = 14;
            const int sampleCount = 50;

            var (highs, lows, closes) = BuildSyntheticSeries(1000);

            // Warm up JIT
            indicators.CalculateRollingAverageAtr(highs, lows, closes, period, sampleCount);
            CalculateNaiveAverageAtr(indicators, highs, lows, closes, period, sampleCount);

            var optimizedTime = Measure(() => indicators.CalculateRollingAverageAtr(highs, lows, closes, period, sampleCount));
            var naiveTime = Measure(() => CalculateNaiveAverageAtr(indicators, highs, lows, closes, period, sampleCount));

            Assert.True(optimizedTime < naiveTime, $"Optimized ATR should be faster. Optimized={optimizedTime.TotalMilliseconds:F2}ms, Naive={naiveTime.TotalMilliseconds:F2}ms");
            Assert.True(optimizedTime < PerformanceBaseline, $"Optimized ATR exceeded baseline {PerformanceBaseline.TotalMilliseconds}ms with {optimizedTime.TotalMilliseconds:F2}ms");
        }

        private static (List<double> highs, List<double> lows, List<double> closes) BuildSyntheticSeries(int length)
        {
            var highs = new List<double>(length);
            var lows = new List<double>(length);
            var closes = new List<double>(length);
            double price = 100.0;
            var random = new Random(1234);

            for (int i = 0; i < length; i++)
            {
                double move = (random.NextDouble() - 0.5) * 2;
                double high = price + Math.Abs(move) + 0.5;
                double low = price - Math.Abs(move) - 0.5;
                price += move;
                double close = price;

                highs.Add(high);
                lows.Add(low);
                closes.Add(close);
            }

            return (highs, lows, closes);
        }

        private static double CalculateNaiveAverageAtr(
            RegimeIndicators indicators,
            IList<double> highs,
            IList<double> lows,
            IList<double> closes,
            int period,
            int samplesToAverage)
        {
            if (samplesToAverage <= 0)
            {
                return indicators.CalculateATR(highs, lows, closes, period);
            }

            var atrSamples = new List<double>();
            int maxStart = closes.Count - period - 1;
            for (int offset = 0; offset <= maxStart; offset++)
            {
                var sliceHigh = highs.Skip(offset).Take(period + 1).ToList();
                var sliceLow = lows.Skip(offset).Take(period + 1).ToList();
                var sliceClose = closes.Skip(offset).Take(period + 1).ToList();
                atrSamples.Add(indicators.CalculateATR(sliceHigh, sliceLow, sliceClose, period));
            }

            if (atrSamples.Count == 0)
            {
                return indicators.CalculateATR(highs, lows, closes, period);
            }

            int countToAverage = Math.Min(samplesToAverage, atrSamples.Count);
            return atrSamples.Skip(Math.Max(0, atrSamples.Count - countToAverage)).Take(countToAverage).Average();
        }

        private static TimeSpan Measure(Action action)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            return stopwatch.Elapsed;
        }
    }
}
