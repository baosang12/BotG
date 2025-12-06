using System;
using System.Collections.Generic;
using System.Linq;

namespace BotG.MarketRegime
{
    /// <summary>
    /// Technical indicator calculations for market regime classification.
    /// Implements standard formulas for ADX, ATR, and Bollinger Bands.
    /// All calculations are stateless; input data is provided by caller.
    /// </summary>
    public class RegimeIndicators
    {
        /// <summary>
        /// Calculates the Average Directional Index (ADX) to measure trend strength.
        /// ADX ranges from 0-100; values above 25 indicate strong trend.
        /// Formula: ADX = Smoothed average of DX over period.
        /// </summary>
        /// <param name="high">High prices for each bar.</param>
        /// <param name="low">Low prices for each bar.</param>
        /// <param name="close">Close prices for each bar.</param>
        /// <param name="period">Smoothing period (default: 14).</param>
        /// <returns>Current ADX value.</returns>
        public double CalculateADX(IList<double> high, IList<double> low, IList<double> close, int period = 14)
        {
            if (high == null || low == null || close == null)
                throw new ArgumentNullException("Price arrays cannot be null");

            if (high.Count < period + 1 || low.Count < period + 1 || close.Count < period + 1)
                throw new ArgumentException($"Insufficient data: need at least {period + 1} bars");

            if (high.Count != low.Count || high.Count != close.Count)
                throw new ArgumentException("Price arrays must have equal length");

            // Step 1: Calculate True Range (TR) and Directional Movements (+DM, -DM)
            var trList = new List<double>();
            var plusDM = new List<double>();
            var minusDM = new List<double>();

            for (int i = 1; i < high.Count; i++)
            {
                // True Range = max(high-low, |high-prevClose|, |low-prevClose|)
                double tr = Math.Max(high[i] - low[i],
                    Math.Max(Math.Abs(high[i] - close[i - 1]),
                             Math.Abs(low[i] - close[i - 1])));
                trList.Add(tr);

                // +DM = current high - previous high (if positive and greater than -DM movement)
                double upMove = high[i] - high[i - 1];
                double downMove = low[i - 1] - low[i];
                plusDM.Add(upMove > 0 && upMove > downMove ? upMove : 0);
                minusDM.Add(downMove > 0 && downMove > upMove ? downMove : 0);
            }

            // Step 2: Smooth TR, +DM, -DM using Wilder's smoothing (similar to EMA)
            double smoothedTR = trList.Take(period).Sum();
            double smoothedPlusDM = plusDM.Take(period).Sum();
            double smoothedMinusDM = minusDM.Take(period).Sum();

            var dxValues = new List<double>();

            // Calculate DX for each bar after the first period
            for (int i = period; i < trList.Count; i++)
            {
                smoothedTR = smoothedTR - (smoothedTR / period) + trList[i];
                smoothedPlusDM = smoothedPlusDM - (smoothedPlusDM / period) + plusDM[i];
                smoothedMinusDM = smoothedMinusDM - (smoothedMinusDM / period) + minusDM[i];

                // Step 3: Calculate Directional Indicators (+DI, -DI)
                double plusDI = smoothedTR > 0 ? (smoothedPlusDM / smoothedTR) * 100 : 0;
                double minusDI = smoothedTR > 0 ? (smoothedMinusDM / smoothedTR) * 100 : 0;

                // Step 4: Calculate DX (Directional Movement Index)
                double diSum = plusDI + minusDI;
                double dx = diSum > 0 ? (Math.Abs(plusDI - minusDI) / diSum) * 100 : 0;
                dxValues.Add(dx);
            }

            // Step 5: ADX = Wilder's smoothing of DX over period
            // First ADX = average of first 'period' DX values
            if (dxValues.Count < period)
            {
                // Not enough data for full ADX, return average of available DX
                return dxValues.Count > 0 ? dxValues.Average() : 0;
            }

            double adx = dxValues.Take(period).Average();

            // Apply Wilder's smoothing for remaining DX values
            for (int i = period; i < dxValues.Count; i++)
            {
                adx = ((adx * (period - 1)) + dxValues[i]) / period;
            }

            return adx;
        }

        /// <summary>
        /// Calculates Average True Range (ATR) to measure volatility.
        /// ATR quantifies price movement magnitude regardless of direction.
        /// Formula: ATR = Average of True Range over period.
        /// </summary>
        /// <param name="high">High prices for each bar.</param>
        /// <param name="low">Low prices for each bar.</param>
        /// <param name="close">Close prices for each bar.</param>
        /// <param name="period">Averaging period (default: 14).</param>
        /// <returns>Current ATR value.</returns>
        public double CalculateATR(IList<double> high, IList<double> low, IList<double> close, int period = 14)
        {
            if (high == null || low == null || close == null)
                throw new ArgumentNullException("Price arrays cannot be null");

            if (high.Count < period + 1 || low.Count < period + 1 || close.Count < period + 1)
                throw new ArgumentException($"Insufficient data: need at least {period + 1} bars");

            if (high.Count != low.Count || high.Count != close.Count)
                throw new ArgumentException("Price arrays must have equal length");

            // Calculate True Range for each bar
            var trList = new List<double>();
            for (int i = 1; i < high.Count; i++)
            {
                // True Range = max of:
                //   1. Current High - Current Low
                //   2. |Current High - Previous Close|
                //   3. |Current Low - Previous Close|
                double tr = Math.Max(high[i] - low[i],
                    Math.Max(Math.Abs(high[i] - close[i - 1]),
                             Math.Abs(low[i] - close[i - 1])));
                trList.Add(tr);
            }

            if (trList.Count < period)
                throw new ArgumentException($"Insufficient TR data: need at least {period} bars");

            // Wilder's smoothing: First ATR = simple average, then smooth
            double atr = trList.Take(period).Average();

            // Apply Wilder's smoothing for remaining bars
            for (int i = period; i < trList.Count; i++)
            {
                atr = ((atr * (period - 1)) + trList[i]) / period;
            }

            return atr;
        }

        /// <summary>
        /// Calculates Bollinger Bands Width as a volatility measure.
        /// Width = (Upper Band - Lower Band) / Middle Band
        /// Normalizes volatility relative to price level.
        /// </summary>
        /// <param name="close">Close prices for each bar.</param>
        /// <param name="period">SMA period for middle band (default: 20).</param>
        /// <param name="deviations">Standard deviations for bands (default: 2.0).</param>
        /// <returns>Current Bollinger Band Width as percentage.</returns>
        public double CalculateBollingerBandWidth(IList<double> close, int period = 20, double deviations = 2.0)
        {
            if (close == null)
                throw new ArgumentNullException(nameof(close));

            if (close.Count < period)
                throw new ArgumentException($"Insufficient data: need at least {period} bars");

            // Calculate Simple Moving Average (Middle Band)
            double sma = close.Skip(close.Count - period).Take(period).Average();

            // Calculate Standard Deviation
            double sumSquares = 0;
            var recentPrices = close.Skip(close.Count - period).Take(period).ToList();
            foreach (var price in recentPrices)
            {
                double diff = price - sma;
                sumSquares += diff * diff;
            }
            double stdDev = Math.Sqrt(sumSquares / period);

            // Calculate bands
            double upperBand = sma + (deviations * stdDev);
            double lowerBand = sma - (deviations * stdDev);

            // Width as percentage of middle band
            double width = sma > 0 ? ((upperBand - lowerBand) / sma) * 100 : 0;

            return width;
        }

        /// <summary>
        /// Calculates Simple Moving Average for general use.
        /// </summary>
        public double CalculateSMA(IList<double> values, int period)
        {
            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (values.Count < period)
                throw new ArgumentException($"Insufficient data: need at least {period} values");

            return values.Skip(values.Count - period).Take(period).Average();
        }

        /// <summary>
        /// Calculates standard deviation for a dataset.
        /// </summary>
        public double CalculateStdDev(IList<double> values, int period)
        {
            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (values.Count < period)
                throw new ArgumentException($"Insufficient data: need at least {period} values");

            double mean = values.Skip(values.Count - period).Take(period).Average();
            double sumSquares = 0;
            var recentValues = values.Skip(values.Count - period).Take(period);
            foreach (var val in recentValues)
            {
                double diff = val - mean;
                sumSquares += diff * diff;
            }

            return Math.Sqrt(sumSquares / period);
        }

        /// <summary>
        /// Calculates the average of the most recent ATR samples using an O(n) sliding window.
        /// Mimics the legacy behavior of averaging up to 10 ATR readings produced from period-sized windows.
        /// </summary>
        public double CalculateRollingAverageAtr(
            IList<double> high,
            IList<double> low,
            IList<double> close,
            int period,
            int samplesToAverage)
        {
            if (high == null || low == null || close == null)
                throw new ArgumentNullException("Price arrays cannot be null");

            if (period <= 0)
                throw new ArgumentOutOfRangeException(nameof(period));

            if (high.Count != low.Count || high.Count != close.Count)
                throw new ArgumentException("Price arrays must have equal length");

            if (close.Count < period + 1)
                throw new ArgumentException($"Insufficient data: need at least {period + 1} bars");

            if (samplesToAverage <= 0)
                return CalculateATR(high, low, close, period);

            int trCount = close.Count - 1;
            if (trCount < period)
                throw new ArgumentException($"Insufficient TR data: need at least {period} bars");

            var trueRanges = new double[trCount];
            for (int i = 1; i < close.Count; i++)
            {
                double currentHigh = high[i];
                double currentLow = low[i];
                double priorClose = close[i - 1];

                double tr = Math.Max(currentHigh - currentLow,
                    Math.Max(Math.Abs(currentHigh - priorClose), Math.Abs(currentLow - priorClose)));
                trueRanges[i - 1] = tr;
            }

            int windowCount = trueRanges.Length - period + 1;
            if (windowCount <= 0)
                return CalculateATR(high, low, close, period);

            samplesToAverage = Math.Min(samplesToAverage, windowCount);
            int startWindow = windowCount - samplesToAverage;

            double windowSum = 0.0;
            for (int i = 0; i < period; i++)
            {
                windowSum += trueRanges[i];
            }

            double atr = windowSum / period;
            double atrAccumulator = 0.0;

            for (int windowIndex = 0; windowIndex < windowCount; windowIndex++)
            {
                if (windowIndex >= startWindow)
                {
                    atrAccumulator += atr;
                }

                if (windowIndex == windowCount - 1)
                {
                    break;
                }

                int nextTrIndex = windowIndex + period;
                windowSum += trueRanges[nextTrIndex] - trueRanges[windowIndex];
                atr = windowSum / period;
            }

            return atrAccumulator / samplesToAverage;
        }
    }
}
