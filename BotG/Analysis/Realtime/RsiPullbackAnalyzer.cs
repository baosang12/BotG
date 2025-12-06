using System;
using System.Collections.Generic;
using DataFetcher.Models;
using Strategies;

namespace Analysis.Realtime
{
    /// <summary>
    /// Evaluates RSI pullback triggers for both bullish and bearish cases.
    /// </summary>
    public sealed class RsiPullbackAnalyzer : IRealtimeAnalyzer
    {
        private readonly TimeFrame _timeframe;
        private readonly int _period;
        private readonly double _oversold;
        private readonly double _overbought;
        private readonly double _releaseRange;
        private readonly Func<TrendAssessment>? _trendProvider;

        public RsiPullbackAnalyzer(
            TimeFrame timeframe,
            int period,
            double oversold,
            double overbought,
            double releaseRange,
            Func<TrendAssessment>? trendProvider = null)
        {
            _timeframe = timeframe;
            _period = period;
            _oversold = oversold;
            _overbought = overbought;
            _releaseRange = releaseRange;
            _trendProvider = trendProvider;
        }

        public string Name => "pullback";

        public RsiTriggerResult? LastBullish { get; private set; }
        public RsiTriggerResult? LastBearish { get; private set; }
        public RsiTriggerResult? LastActive { get; private set; }

        public RealtimeFeatureResult Analyze(RealtimeFeatureContext context)
        {
            if (!context.TryGetBars(_timeframe, out var bars))
            {
                LastBullish = LastBearish = LastActive = null;
                return RealtimeFeatureResult.Empty;
            }

            LastBullish = TrendPullbackCalculations.EvaluateRsiTrigger(
                bars,
                _period,
                _oversold,
                _overbought,
                TradeAction.Buy,
                _releaseRange);

            LastBearish = TrendPullbackCalculations.EvaluateRsiTrigger(
                bars,
                _period,
                _oversold,
                _overbought,
                TradeAction.Sell,
                _releaseRange);

            var trend = _trendProvider?.Invoke() ?? TrendAssessment.Empty;
            LastActive = trend.Direction switch
            {
                TradeAction.Buy => LastBullish,
                TradeAction.Sell => LastBearish,
                _ => null
            };

            var metrics = new List<KeyValuePair<string, double>>(8)
            {
                new("bullish.triggered", LastBullish?.IsTriggered == true ? 1.0 : 0.0),
                new("bullish.score", LastBullish?.Score ?? 0.0),
                new("bullish.current", LastBullish?.Current ?? 50.0),
                new("bearish.triggered", LastBearish?.IsTriggered == true ? 1.0 : 0.0),
                new("bearish.score", LastBearish?.Score ?? 0.0),
                new("bearish.current", LastBearish?.Current ?? 50.0)
            };

            if (LastActive != null)
            {
                metrics.Add(new KeyValuePair<string, double>("active.score", LastActive.Score));
                metrics.Add(new KeyValuePair<string, double>("active.triggered", LastActive.IsTriggered ? 1.0 : 0.0));
            }

            var tags = new[]
            {
                new KeyValuePair<string, string>("timeframe", _timeframe.ToString())
            };

            var payload = new RsiAnalyzerState(LastBullish, LastBearish, LastActive, trend.Direction);
            return new RealtimeFeatureResult(Name, metrics, tags, payload);
        }

        public sealed record RsiAnalyzerState(
            RsiTriggerResult? Bullish,
            RsiTriggerResult? Bearish,
            RsiTriggerResult? Active,
            TradeAction TrendDirection);
    }
}
