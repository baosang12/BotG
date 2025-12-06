using System;
using System.Collections.Generic;
using DataFetcher.Models;
using Strategies;

namespace Analysis.Realtime
{
    /// <summary>
    /// Shared math helpers for Trend Pullback style analyzers.
    /// </summary>
    public static class TrendPullbackCalculations
    {
        public static TrendAssessment AnalyzeTrend(
            IReadOnlyList<Bar> bars,
            int fastPeriod,
            int slowPeriod,
            double minimumSeparationRatio)
        {
            if (bars == null || bars.Count < Math.Max(fastPeriod, slowPeriod) + 2)
            {
                return TrendAssessment.Empty;
            }

            var fast = CalculateEma(bars, fastPeriod);
            var slow = CalculateEma(bars, slowPeriod);
            if (!fast.HasValue || !slow.HasValue || Math.Abs(slow.Value) <= 1e-9)
            {
                return TrendAssessment.Empty;
            }

            var separation = Math.Abs(fast.Value - slow.Value) / Math.Abs(slow.Value);
            var direction = fast.Value > slow.Value
                ? TradeAction.Buy
                : fast.Value < slow.Value
                    ? TradeAction.Sell
                    : TradeAction.None;

            var strength = Math.Clamp(
                Normalize(separation, minimumSeparationRatio, minimumSeparationRatio * 2.0),
                0.0,
                1.0);

            if (separation < minimumSeparationRatio)
            {
                direction = TradeAction.None;
            }

            return new TrendAssessment(direction, separation, fast.Value, slow.Value, bars[^1].Close, strength);
        }

        public static RsiTriggerResult EvaluateRsiTrigger(
            IReadOnlyList<Bar> bars,
            int rsiPeriod,
            double oversold,
            double overbought,
            TradeAction direction,
            double releaseRange)
        {
            if (direction == TradeAction.None || bars == null || bars.Count < rsiPeriod + 2)
            {
                return RsiTriggerResult.None;
            }

            var (previous, current) = CalculateRsiPair(bars, rsiPeriod);
            if (!previous.HasValue || !current.HasValue)
            {
                return RsiTriggerResult.None;
            }

            var range = Math.Max(5.0, releaseRange);
            bool triggered = direction switch
            {
                TradeAction.Buy => previous.Value <= oversold && current.Value >= oversold && current.Value <= oversold + range,
                TradeAction.Sell => previous.Value >= overbought && current.Value <= overbought && current.Value >= overbought - range,
                _ => false
            };

            if (!triggered)
            {
                return new RsiTriggerResult(false, 0.0, current.Value, previous.Value, "no-cross");
            }

            double depth = direction == TradeAction.Buy
                ? Math.Max(0.0, oversold - Math.Min(previous.Value, current.Value))
                : Math.Max(0.0, Math.Max(previous.Value, current.Value) - overbought);

            double normalizedDepth = Math.Clamp(depth / 20.0, 0.0, 1.0);
            double recovery = direction == TradeAction.Buy
                ? 1.0 - Math.Clamp((current.Value - oversold) / range, 0.0, 1.0)
                : 1.0 - Math.Clamp((overbought - current.Value) / range, 0.0, 1.0);

            var score = Math.Clamp((normalizedDepth * 0.6) + (recovery * 0.4), 0.0, 1.0);
            var reason = direction == TradeAction.Buy ? "rsi_cross_from_oversold" : "rsi_cross_from_overbought";
            return new RsiTriggerResult(true, score, current.Value, previous.Value, reason);
        }

        public static double CalculateAtr(IReadOnlyList<Bar> bars, int period)
        {
            if (bars == null || bars.Count < period + 1)
            {
                return 0.0;
            }

            double atr = 0.0;
            for (int i = bars.Count - period; i < bars.Count; i++)
            {
                var current = bars[i];
                var previous = bars[i - 1];
                var tr = Math.Max(
                    current.High - current.Low,
                    Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
                atr += tr;
            }

            return atr / period;
        }

        private static (double? previous, double? current) CalculateRsiPair(IReadOnlyList<Bar> bars, int period)
        {
            if (bars == null || bars.Count < period + 2)
            {
                return (null, null);
            }

            var previous = CalculateRsi(bars, period, bars.Count - 1);
            var current = CalculateRsi(bars, period, bars.Count);
            return (previous, current);
        }

        private static double? CalculateRsi(IReadOnlyList<Bar> bars, int period, int barsToUse)
        {
            if (barsToUse <= period || barsToUse > bars.Count)
            {
                return null;
            }

            if (barsToUse - period < 1)
            {
                return null;
            }

            double gain = 0.0;
            double loss = 0.0;
            for (int i = barsToUse - period; i < barsToUse; i++)
            {
                var change = bars[i].Close - bars[i - 1].Close;
                if (change >= 0)
                {
                    gain += change;
                }
                else
                {
                    loss -= change;
                }
            }

            if (loss == 0 && gain == 0)
            {
                return 50.0;
            }

            if (loss == 0)
            {
                return 100.0;
            }

            var rs = gain / loss;
            return 100.0 - (100.0 / (1.0 + rs));
        }

        private static double? CalculateEma(IReadOnlyList<Bar> bars, int period)
        {
            if (bars == null || bars.Count < period)
            {
                return null;
            }

            var k = 2.0 / (period + 1);
            double ema = bars[^period].Close;
            for (int i = bars.Count - period + 1; i < bars.Count; i++)
            {
                ema = (bars[i].Close - ema) * k + ema;
            }

            return ema;
        }

        private static double Normalize(double value, double min, double max)
        {
            if (max <= min)
            {
                return value >= max ? 1.0 : 0.0;
            }

            return Math.Clamp((value - min) / (max - min), 0.0, 1.0);
        }
    }
}
