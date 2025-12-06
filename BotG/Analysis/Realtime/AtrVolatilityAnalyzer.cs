using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.Realtime
{
    /// <summary>
    /// Computes ATR-based volatility metrics for a timeframe.
    /// </summary>
    public sealed class AtrVolatilityAnalyzer : IRealtimeAnalyzer
    {
        private readonly TimeFrame _timeframe;
        private readonly int _period;

        public AtrVolatilityAnalyzer(TimeFrame timeframe, int period)
        {
            if (period < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(period));
            }

            _timeframe = timeframe;
            _period = period;
        }

        public string Name => "atr";

        public double LastAtr { get; private set; }

        public RealtimeFeatureResult Analyze(RealtimeFeatureContext context)
        {
            if (!context.TryGetBars(_timeframe, out var bars))
            {
                LastAtr = 0.0;
                return RealtimeFeatureResult.Empty;
            }

            LastAtr = TrendPullbackCalculations.CalculateAtr(bars, _period);

            var metrics = new[]
            {
                new KeyValuePair<string, double>("value", LastAtr)
            };

            var tags = new[]
            {
                new KeyValuePair<string, string>("timeframe", _timeframe.ToString())
            };

            return new RealtimeFeatureResult(Name, metrics, tags, LastAtr);
        }
    }
}
