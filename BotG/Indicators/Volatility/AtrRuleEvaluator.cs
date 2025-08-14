using DataFetcher.Models;

namespace Indicators.Volatility
{
    /// <summary>
    /// Evaluates ATR-based rules for trading.
    /// </summary>
    public static class AtrRuleEvaluator
    {
        /// <summary>
        /// Evaluates a set of ATR-based filters and calculates stop-loss and position sizing.
        /// </summary>
        /// <param name="bar">The bar data.</param>
        /// <param name="atr">Current ATR value.</param>
        /// <param name="stopLossMultiplier">Multiplier for ATR to compute stop-loss distance.</param>
        /// <param name="spikeMultiplier">Multiplier for ATR to detect volatility spike.</param>
        public static AtrRuleResult Evaluate(Bar bar, double atr, double stopLossMultiplier, double spikeMultiplier)
        {
            var result = new AtrRuleResult { AllowTrade = true };
            double range = bar.High - bar.Low;
            // Filter out high volatility spikes (potential liquidity hunts)
            if (range > atr * spikeMultiplier)
            {
                result.AllowTrade = false;
                result.Reason = $"Volatility spike detected: range {range:F2} > {atr * spikeMultiplier:F2}";
                return result;
            }
            // Compute flexible stop-loss based on ATR
            result.StopLossDistance = atr * stopLossMultiplier;
            // Example position size factor inversely proportional to stop-loss
            result.PositionSizeFactor = 1.0 / stopLossMultiplier;
            result.Reason = "ATR rules passed";
            return result;
        }
    }
}
