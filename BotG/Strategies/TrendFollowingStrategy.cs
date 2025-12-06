using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BotG.MarketRegime;
using BotG.MultiTimeframe;
using Strategies.Config;
using Strategies.Templates;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace Strategies
{
    /// <summary>
    /// Multi-timeframe trend-following strategy that aligns EMA50/EMA200 bias across H4/H1 and uses M15 triggers.
    /// </summary>
    public sealed class TrendFollowingStrategy : MultiTimeframeStrategyBase
    {
        private readonly TrendFollowingStrategyConfig _config;
        private readonly RegimeIndicators _indicators = new();
        private readonly TrendState _state = new();
        private TrendDiagnostics? _lastDiagnostics;

        public TrendFollowingStrategy(
            TimeframeManager timeframeManager,
            TimeframeSynchronizer synchronizer,
            SessionAwareAnalyzer sessionAnalyzer,
            TrendFollowingStrategyConfig? config = null)
            : base("TrendFollowing", timeframeManager, synchronizer, sessionAnalyzer, minimumAlignedTimeframes: 2)
        {
            _config = config ?? new TrendFollowingStrategyConfig();
            _config.Validate();
        }

        protected override Task<Signal?> EvaluateMultiTimeframeAsync(
            MultiTimeframeEvaluationContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var h4 = context.Snapshot.GetBars(ModelTimeFrame.H4);
            var h1 = context.Snapshot.GetBars(ModelTimeFrame.H1);
            var m15 = context.Snapshot.GetBars(ModelTimeFrame.M15);

            if (!HasSufficientData(h4, _config.TrendEmaSlow + 5) ||
                !HasSufficientData(h1, Math.Max(_config.SignalEmaSlow + 5, _config.AtrPeriod + 5)) ||
                !HasSufficientData(m15, _config.TriggerEmaSlow + _config.PullbackLookbackBars))
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var h4Trend = AnalyzeTrend(h4, _config.TrendEmaFast, _config.TrendEmaSlow, _config.TrendSlopeLookback, _config.MinimumTrendSlopeRatio);
            var alignmentRatio = Math.Clamp(context.Alignment.AlignedTimeframes / Math.Max(context.Alignment.TotalTimeframes, 1.0), 0.0, 1.0);
            var sessionMultiplier = GetSessionMultiplier(context.Session);
            var now = context.MarketData.TimestampUtc;

            var exitSignal = EvaluateExit(context, h4Trend, now);
            if (exitSignal != null)
            {
                SaveDiagnostics(new TrendDiagnostics(exitSignal.Action, 1.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    0.0,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(exitSignal);
            }

            if (!h4Trend.IsActionable)
            {
                SaveDiagnostics(new TrendDiagnostics(null, 0.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    0.0,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(null);
            }

            var h1Trend = AnalyzeTrend(h1, _config.SignalEmaFast, _config.SignalEmaSlow, _config.MomentumSlopeLookback, _config.MinimumMomentumSlopeRatio);
            if (!h1Trend.IsActionable || h1Trend.Action != h4Trend.Action)
            {
                SaveDiagnostics(new TrendDiagnostics(null, 0.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    0.0,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(null);
            }

            var atr = CalculateAtr(h1);
            if (atr <= double.Epsilon)
            {
                SaveDiagnostics(new TrendDiagnostics(null, 0.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    0.0,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(null);
            }

            var adx = CalculateAdx(h1);
            if (adx < _config.MinimumAdx)
            {
                SaveDiagnostics(new TrendDiagnostics(null, 0.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    adx,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(null);
            }

            if (alignmentRatio < _config.MinimumAlignmentRatio)
            {
                SaveDiagnostics(new TrendDiagnostics(null, 0.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    adx,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(null);
            }

            var trigger = EvaluateTrigger(m15, h4Trend.Action, atr);
            if (!trigger.IsTriggered)
            {
                SaveDiagnostics(new TrendDiagnostics(null, 0.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    adx,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(null);
            }

            if (!CanEmitNewSignal(now, h4Trend.Action))
            {
                SaveDiagnostics(new TrendDiagnostics(null, 0.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    adx,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(null);
            }

            var signal = BuildEntrySignal(context, h4Trend, h1Trend, adx, atr, trigger, alignmentRatio, sessionMultiplier);
            if (signal == null)
            {
                SaveDiagnostics(new TrendDiagnostics(null, 0.0,
                    Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                    adx,
                    alignmentRatio,
                    sessionMultiplier));
                return Task.FromResult<Signal?>(null);
            }

            _state.ActiveDirection = signal.Action;
            _state.LastDirection = signal.Action;
            _state.LastSignalTimeUtc = now;

            SaveDiagnostics(new TrendDiagnostics(signal.Action, signal.Confidence,
                Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio),
                adx,
                alignmentRatio,
                sessionMultiplier));

            return Task.FromResult<Signal?>(signal);
        }

        public override RiskScore CalculateRisk(MarketContext context)
        {
            var diag = _lastDiagnostics;
            double baseScore = 4.7;

            if (diag != null)
            {
                baseScore += diag.Confidence * 1.2;
                baseScore += diag.TrendStrength * 0.8;
                baseScore += Math.Clamp(diag.Alignment, 0.0, 1.0) * 0.4;
            }

            baseScore += context.CurrentRegime switch
            {
                RegimeType.Trending => 0.4,
                RegimeType.Ranging => -0.6,
                RegimeType.Volatile => -0.2,
                _ => 0.0
            };

            var level = baseScore >= 6.5
                ? RiskLevel.Preferred
                : baseScore >= 5.5
                    ? RiskLevel.Normal
                    : RiskLevel.Elevated;

            var acceptable = diag != null && baseScore >= 5.5;
            var reason = acceptable ? null : "TrendContextWeak";

            var factors = new Dictionary<string, double>
            {
                ["confidence"] = diag?.Confidence ?? 0.0,
                ["trend_strength"] = diag?.TrendStrength ?? 0.0,
                ["adx"] = diag?.Adx ?? 0.0,
                ["alignment"] = diag?.Alignment ?? 0.0,
                ["session_multiplier"] = diag?.SessionMultiplier ?? 0.0
            };

            return new RiskScore(baseScore, level, acceptable, reason, factors);
        }

        private Signal? BuildEntrySignal(
            MultiTimeframeEvaluationContext context,
            TrendAssessment h4Trend,
            TrendAssessment h1Trend,
            double adx,
            double atr,
            TriggerAssessment trigger,
            double alignmentScore,
            double sessionMultiplier)
        {
            if (!h4Trend.IsActionable)
            {
                return null;
            }

            var trendStrength = Normalize(h4Trend.SeparationRatio, _config.MinimumTrendSeparationRatio, _config.TargetTrendSeparationRatio);
            var momentumScore = Normalize(Math.Abs(h1Trend.SlopeRatio), _config.MinimumMomentumSlopeRatio, _config.MinimumMomentumSlopeRatio * 3.0);
            var adxScore = Normalize(adx, _config.MinimumAdx, _config.MinimumAdx + 20);
            var sessionScore = Math.Clamp(sessionMultiplier / 1.5, 0.0, 1.0);

            var confidence = (0.30 * trendStrength) +
                              (0.20 * momentumScore) +
                              (0.15 * adxScore) +
                              (0.20 * trigger.Score) +
                              (0.10 * alignmentScore) +
                              (0.05 * sessionScore);

            if (confidence < _config.MinimumConfidence)
            {
                return null;
            }

            var data = context.MarketData;
            var price = h4Trend.Action == TradeAction.Buy ? data.Ask : data.Bid;
            var stopLoss = h4Trend.Action == TradeAction.Buy
                ? price - atr * _config.AtrStopMultiplier
                : price + atr * _config.AtrStopMultiplier;
            var takeProfit = h4Trend.Action == TradeAction.Buy
                ? price + atr * _config.AtrTakeProfitMultiplier
                : price - atr * _config.AtrTakeProfitMultiplier;

            var indicators = new Dictionary<string, double>
            {
                ["confidence"] = confidence,
                ["atr"] = atr,
                ["adx"] = adx,
                ["trend_strength"] = trendStrength,
                ["momentum_slope"] = h1Trend.SlopeRatio,
                ["trigger_score"] = trigger.Score,
                ["alignment"] = alignmentScore,
                ["session_multiplier"] = sessionMultiplier
            };

            var notes = new Dictionary<string, string>
            {
                ["direction"] = h4Trend.Action == TradeAction.Buy ? "long" : "short",
                ["trigger"] = trigger.Reason
            };

            return new Signal
            {
                StrategyName = Name,
                Action = h4Trend.Action,
                Price = price,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Confidence = confidence,
                TimestampUtc = data.TimestampUtc,
                Indicators = indicators,
                Notes = notes
            };
        }

        private Signal? EvaluateExit(MultiTimeframeEvaluationContext context, TrendAssessment h4Trend, DateTime now)
        {
            if (!_state.ActiveDirection.HasValue)
            {
                _state.LastTrendDirection = h4Trend.Action;
                return null;
            }

            var cooldown = TimeSpan.FromMinutes(_config.ExitCooldownMinutes);
            var lostTrend = !h4Trend.IsActionable || h4Trend.Action != _state.ActiveDirection;
            var weakened = h4Trend.SeparationRatio < _config.ExitSeparationRatio;

            if ((lostTrend || weakened) && now - _state.LastExitTimeUtc >= cooldown)
            {
                _state.ActiveDirection = null;
                _state.LastExitTimeUtc = now;
                _state.LastTrendDirection = h4Trend.Action;

                var reason = lostTrend ? "H4TrendFlip" : "TrendWeakening";
                return new Signal
                {
                    StrategyName = Name,
                    Action = TradeAction.Exit,
                    Price = context.MarketData.Mid,
                    Confidence = 1.0,
                    TimestampUtc = now,
                    Indicators = new Dictionary<string, double>
                    {
                        ["trend_sep_ratio"] = h4Trend.SeparationRatio
                    },
                    Notes = new Dictionary<string, string>
                    {
                        ["reason"] = reason
                    }
                };
            }

            _state.LastTrendDirection = h4Trend.Action;
            return null;
        }

        private TrendAssessment AnalyzeTrend(
            IReadOnlyList<ModelBar> bars,
            int fastPeriod,
            int slowPeriod,
            int slopeLookback,
            double minSlopeRatio)
        {
            if (!HasSufficientData(bars, Math.Max(fastPeriod, slowPeriod) + 2))
            {
                return TrendAssessment.Empty;
            }

            var fast = IndicatorMath.CalculateEma(bars, fastPeriod);
            var slow = IndicatorMath.CalculateEma(bars, slowPeriod);
            if (!fast.HasValue || !slow.HasValue || slow.Value <= 0)
            {
                return TrendAssessment.Empty;
            }

            var separationRatio = Math.Abs(fast.Value - slow.Value) / Math.Max(Math.Abs(slow.Value), 1e-6);
            var direction = fast.Value > slow.Value
                ? TradeAction.Buy
                : fast.Value < slow.Value
                    ? TradeAction.Sell
                    : TradeAction.None;

            var slopeRatio = IndicatorMath.CalculateSlopeRatio(bars, slopeLookback);
            var slopeOk = direction switch
            {
                TradeAction.Buy => slopeRatio >= minSlopeRatio,
                TradeAction.Sell => slopeRatio <= -minSlopeRatio,
                _ => false
            };

            if (!slopeOk || separationRatio < _config.MinimumTrendSeparationRatio)
            {
                direction = TradeAction.None;
            }

            return new TrendAssessment(direction, separationRatio, slopeRatio, fast.Value, slow.Value, bars[^1].Close);
        }

        private TriggerAssessment EvaluateTrigger(IReadOnlyList<ModelBar> bars, TradeAction direction, double referenceAtr)
        {
            if (direction == TradeAction.None || !HasSufficientData(bars, _config.TriggerEmaSlow + 2))
            {
                return TriggerAssessment.None;
            }

            var fast = IndicatorMath.CalculateEma(bars, _config.TriggerEmaFast);
            var slow = IndicatorMath.CalculateEma(bars, _config.TriggerEmaSlow);
            if (!fast.HasValue || !slow.HasValue)
            {
                return TriggerAssessment.None;
            }

            bool alignment = direction == TradeAction.Buy ? fast.Value > slow.Value : fast.Value < slow.Value;
            if (!alignment)
            {
                return TriggerAssessment.None;
            }

            var latest = bars[^1];
            bool priceOk = direction == TradeAction.Buy ? latest.Close > fast.Value : latest.Close < fast.Value;
            if (!priceOk)
            {
                return TriggerAssessment.None;
            }

            var lookbackStart = Math.Max(0, bars.Count - _config.PullbackLookbackBars);
            double maxPullback = 0.0;
            for (int i = lookbackStart; i < bars.Count - 1; i++)
            {
                var bar = bars[i];
                double distance = direction == TradeAction.Buy
                    ? Math.Max(0.0, fast.Value - Math.Min(bar.Close, bar.Low))
                    : Math.Max(0.0, Math.Max(bar.Close, bar.High) - fast.Value);
                maxPullback = Math.Max(maxPullback, distance);
            }

            var atrRef = Math.Max(referenceAtr, 1e-5);
            if (maxPullback / atrRef > _config.MaxPullbackAtr)
            {
                return TriggerAssessment.None;
            }

            var triggerDistance = Math.Abs(latest.Close - fast.Value);
            var pullbackScore = Normalize(maxPullback, atrRef * 0.1, atrRef * _config.MaxPullbackAtr);
            var triggerScore = 1.0 - Normalize(triggerDistance, 0.0, atrRef * _config.TriggerReEntryAtr);
            triggerScore = Math.Clamp(triggerScore, 0.0, 1.0);
            var score = Math.Clamp((pullbackScore * 0.4) + (triggerScore * 0.6), 0.0, 1.0);

            var reason = string.Format(CultureInfo.InvariantCulture, "pullback={0:F5},dist={1:F5}", maxPullback, triggerDistance);
            return new TriggerAssessment(true, score, reason);
        }

        private double CalculateAdx(IReadOnlyList<ModelBar> bars)
        {
            if (!HasSufficientData(bars, _config.AdxPeriod + 2))
            {
                return 0.0;
            }

            var high = new double[bars.Count];
            var low = new double[bars.Count];
            var close = new double[bars.Count];

            for (int i = 0; i < bars.Count; i++)
            {
                high[i] = bars[i].High;
                low[i] = bars[i].Low;
                close[i] = bars[i].Close;
            }

            try
            {
                return _indicators.CalculateADX(high, low, close, _config.AdxPeriod);
            }
            catch
            {
                return 0.0;
            }
        }

        private double CalculateAtr(IReadOnlyList<ModelBar> bars)
        {
            if (!HasSufficientData(bars, _config.AtrPeriod + 2))
            {
                return 0.0;
            }

            var high = new double[bars.Count];
            var low = new double[bars.Count];
            var close = new double[bars.Count];

            for (int i = 0; i < bars.Count; i++)
            {
                high[i] = bars[i].High;
                low[i] = bars[i].Low;
                close[i] = bars[i].Close;
            }

            try
            {
                return _indicators.CalculateATR(high, low, close, _config.AtrPeriod);
            }
            catch
            {
                return 0.0;
            }
        }

        private bool HasSufficientData(IReadOnlyList<ModelBar> bars, int required)
        {
            return bars != null && bars.Count >= Math.Max(required, 1);
        }

        private bool CanEmitNewSignal(DateTime now, TradeAction action)
        {
            if (action == TradeAction.None)
            {
                return false;
            }

            if (_state.ActiveDirection == action)
            {
                return false;
            }

            if (_state.LastDirection == action)
            {
                var cooldown = TimeSpan.FromMinutes(_config.ReentryCooldownMinutes);
                if (now - _state.LastSignalTimeUtc < cooldown)
                {
                    return false;
                }
            }

            return true;
        }

        private void SaveDiagnostics(TrendDiagnostics? diagnostics)
        {
            _lastDiagnostics = diagnostics;
        }

        private sealed record TrendAssessment(
            TradeAction Action,
            double SeparationRatio,
            double SlopeRatio,
            double FastEma,
            double SlowEma,
            double LatestClose)
        {
            public bool IsActionable => Action == TradeAction.Buy || Action == TradeAction.Sell;

            public static TrendAssessment Empty { get; } = new TrendAssessment(TradeAction.None, 0.0, 0.0, 0.0, 0.0, 0.0);
        }

        private sealed record TriggerAssessment(bool IsTriggered, double Score, string Reason)
        {
            public static TriggerAssessment None { get; } = new TriggerAssessment(false, 0.0, "none");
        }

        private sealed record TrendDiagnostics(
            TradeAction? Action,
            double Confidence,
            double TrendStrength,
            double Adx,
            double Alignment,
            double SessionMultiplier);

        private sealed class TrendState
        {
            public TradeAction? ActiveDirection { get; set; }
            public TradeAction? LastDirection { get; set; }
            public TradeAction? LastTrendDirection { get; set; }
            public DateTime LastSignalTimeUtc { get; set; } = DateTime.MinValue;
            public DateTime LastExitTimeUtc { get; set; } = DateTime.MinValue;
        }

        private static double Normalize(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }

            if (max <= min)
            {
                return value >= max ? 1.0 : 0.0;
            }

            return Math.Clamp((value - min) / (max - min), 0.0, 1.0);
        }

        private static class IndicatorMath
        {
            public static double? CalculateEma(IReadOnlyList<ModelBar> bars, int period)
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

            public static double CalculateSlopeRatio(IReadOnlyList<ModelBar> bars, int lookback)
            {
                if (bars == null || bars.Count < lookback + 1)
                {
                    return 0.0;
                }

                var start = bars[bars.Count - lookback - 1].Close;
                var end = bars[^1].Close;
                var denominator = Math.Max(Math.Abs(start), 1e-6) * lookback;
                if (denominator <= 0)
                {
                    return 0.0;
                }

                return (end - start) / denominator;
            }
        }
    }
}
