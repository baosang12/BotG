using System;
using System.Collections.Generic;
using DataFetcher.Models;
using Strategies;

namespace Analysis.Realtime
{
    /// <summary>
    /// Extracts EMA-based momentum metrics for a given timeframe.
    /// </summary>
    public sealed class TrendMomentumAnalyzer : IRealtimeAnalyzer
    {
        private readonly TimeFrame _timeframe;
        private readonly int _fastPeriod;
        private readonly int _slowPeriod;
        private readonly double _minimumSeparation;

        public TrendMomentumAnalyzer(
            TimeFrame timeframe,
            int fastPeriod,
            int slowPeriod,
            double minimumSeparation)
        {
            if (fastPeriod <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fastPeriod));
            }

            if (slowPeriod <= fastPeriod)
            {
                throw new ArgumentOutOfRangeException(nameof(slowPeriod));
            }

            _timeframe = timeframe;
            _fastPeriod = fastPeriod;
            _slowPeriod = slowPeriod;
            _minimumSeparation = minimumSeparation;
        }

        public string Name => "trend";

        public TrendAssessment LastTrend { get; private set; } = TrendAssessment.Empty;

        public RealtimeFeatureResult Analyze(RealtimeFeatureContext context)
        {
            if (!context.TryGetBars(_timeframe, out var bars))
            {
                LastTrend = TrendAssessment.Empty;
                return RealtimeFeatureResult.Empty;
            }

            LastTrend = TrendPullbackCalculations.AnalyzeTrend(bars, _fastPeriod, _slowPeriod, _minimumSeparation);

            var metrics = new[]
            {
                new KeyValuePair<string, double>("separation", LastTrend.SeparationRatio),
                new KeyValuePair<string, double>("strength", LastTrend.Strength),
                new KeyValuePair<string, double>("fast_ema", LastTrend.FastEma),
                new KeyValuePair<string, double>("slow_ema", LastTrend.SlowEma)
            };

            var tags = new[]
            {
                new KeyValuePair<string, string>("direction", LastTrend.Direction.ToString()),
                new KeyValuePair<string, string>("timeframe", _timeframe.ToString())
            };

            // Payload gives downstream components direct access to detailed trend info.
            return new RealtimeFeatureResult(Name, metrics, tags, LastTrend);
        }
    }
}
