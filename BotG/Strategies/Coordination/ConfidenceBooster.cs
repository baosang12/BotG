using System;
using BotG.MarketRegime;
using BotG.Runtime.Logging;
using Strategies;

namespace BotG.Strategies.Coordination
{
    public sealed class ConfidenceBooster
    {
        private DateTime _lastTradeUtc = DateTime.MinValue;

        public double BoostConfidence(double baseConfidence, Signal signal, MarketContext context)
        {
            var boosted = baseConfidence;
            var applied = false;

            if (_lastTradeUtc != DateTime.MinValue)
            {
                var hours = (DateTime.UtcNow - _lastTradeUtc).TotalHours;
                if (hours >= 2.0)
                {
                    boosted *= 2.0;
                    applied = true;
                }
            }

            if (!string.IsNullOrEmpty(signal.StrategyName))
            {
                if (signal.StrategyName.Contains("Rsi", StringComparison.OrdinalIgnoreCase) && baseConfidence > 0.1)
                {
                    boosted *= 1.8;
                    applied = true;
                }
                else if (signal.StrategyName.Contains("Sma", StringComparison.OrdinalIgnoreCase) && baseConfidence > 0.1)
                {
                    boosted *= 1.6;
                    applied = true;
                }
            }

            if (context.CurrentRegime == RegimeType.Trending)
            {
                boosted *= 1.4;
                applied = true;
            }
            else if (context.CurrentRegime == RegimeType.Calm)
            {
                boosted *= 1.3;
                applied = true;
            }

            var finalValue = Math.Min(boosted, 1.0);
            if (applied && Math.Abs(finalValue - baseConfidence) > double.Epsilon)
            {
                try
                {
                    PipelineLogger.Log(
                        "COORD",
                        "ConfidenceBoost",
                        "Confidence boost applied",
                        new
                        {
                            strategy = signal.StrategyName,
                            regime = context.CurrentRegime.ToString(),
                            base_confidence = baseConfidence,
                            boosted_confidence = finalValue
                        },
                        null);
                }
                catch
                {
                    // logging best effort
                }
            }

            return finalValue;
        }

        public void RecordTrade()
        {
            _lastTradeUtc = DateTime.UtcNow;
        }
    }
}
