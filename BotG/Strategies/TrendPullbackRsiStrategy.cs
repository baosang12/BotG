using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Analysis.Realtime;
using BotG.MarketRegime;
using BotG.MultiTimeframe;
using BotG.Runtime.Preprocessor;
using Strategies.Config;
using Strategies.Templates;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace Strategies
{
    /// <summary>
    /// Chiến lược Trend Pullback RSI: xác định xu hướng trên H1 (EMA 50/200), vào lệnh tại pullback RSI trên M15.
    /// </summary>
    public sealed class TrendPullbackRsiStrategy : MultiTimeframeStrategyBase
    {
        private readonly TrendPullbackRsiStrategyConfig _config;
        private readonly RealtimeAnalysisHub _analysisHub = new();
        private readonly TrendMomentumAnalyzer _trendAnalyzer;
        private readonly RsiPullbackAnalyzer _pullbackAnalyzer;
        private readonly AtrVolatilityAnalyzer _atrAnalyzer;
        private readonly TrendState _state = new();
        private StrategyDiagnostics? _lastDiagnostics;

        public TrendPullbackRsiStrategy(
            TimeframeManager timeframeManager,
            TimeframeSynchronizer synchronizer,
            SessionAwareAnalyzer sessionAnalyzer,
            TrendPullbackRsiStrategyConfig? config = null,
            IPreprocessorStrategyDataBridge? preprocessorBridge = null)
            : base("TrendPullbackRSI", timeframeManager, synchronizer, sessionAnalyzer, preprocessorBridge, minimumAlignedTimeframes: 2)
        {
            _config = config ?? new TrendPullbackRsiStrategyConfig();
            _config.Validate();

            _trendAnalyzer = new TrendMomentumAnalyzer(
                ModelTimeFrame.H1,
                _config.TrendEmaFast,
                _config.TrendEmaSlow,
                _config.MinimumTrendSeparationRatio);

            _pullbackAnalyzer = new RsiPullbackAnalyzer(
                ModelTimeFrame.M15,
                _config.EntryRsiPeriod,
                _config.Oversold,
                _config.Overbought,
                _config.TriggerReleaseRange,
                () => _trendAnalyzer.LastTrend);

            _atrAnalyzer = new AtrVolatilityAnalyzer(ModelTimeFrame.H1, _config.AtrPeriod);

            _analysisHub.RegisterAnalyzer(_trendAnalyzer);
            _analysisHub.RegisterAnalyzer(_pullbackAnalyzer);
            _analysisHub.RegisterAnalyzer(_atrAnalyzer);
        }

        protected override Task<Signal?> EvaluateMultiTimeframeAsync(
            MultiTimeframeEvaluationContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var h1 = context.Snapshot.GetBars(ModelTimeFrame.H1);
            var m15 = context.Snapshot.GetBars(ModelTimeFrame.M15);
            var sessionMultiplier = GetSessionMultiplier(context.Session);

            if (!HasSufficientData(h1, _config.TrendEmaSlow + 5) ||
                !HasSufficientData(m15, _config.EntryRsiPeriod + 5) ||
                !HasSufficientData(h1, _config.AtrPeriod + 2))
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var featureContext = BuildFeatureContext(context, h1, m15);
            _analysisHub.Process(featureContext);

            var trend = OverrideTrendWithPreprocessor(context, _trendAnalyzer.LastTrend);

            var now = context.MarketData.TimestampUtc;
            var exit = EvaluateExit(context, trend, now);
            if (exit != null)
            {
                SaveDiagnostics(new StrategyDiagnostics(
                    exit.Action,
                    1.0,
                    trend.Strength,
                    0.0,
                    GetAlignmentRatio(context),
                    sessionMultiplier,
                    0.0));
                return Task.FromResult<Signal?>(exit);
            }

            if (!trend.IsActionable)
            {
                SaveDiagnostics(new StrategyDiagnostics(
                    null,
                    0.0,
                    trend.Strength,
                    0.0,
                    GetAlignmentRatio(context),
                    sessionMultiplier,
                    0.0));
                return Task.FromResult<Signal?>(null);
            }

            var alignmentRatio = GetAlignmentRatio(context);
            if (alignmentRatio < _config.MinimumAlignmentRatio)
            {
                SaveDiagnostics(new StrategyDiagnostics(
                    null,
                    0.0,
                    trend.Strength,
                    0.0,
                    alignmentRatio,
                    sessionMultiplier,
                    0.0));
                return Task.FromResult<Signal?>(null);
            }

            var atr = ResolveAtr(context);
            if (atr <= _config.MinimumAtr)
            {
                SaveDiagnostics(new StrategyDiagnostics(
                    null,
                    0.0,
                    trend.Strength,
                    0.0,
                    alignmentRatio,
                    sessionMultiplier,
                    atr));
                return Task.FromResult<Signal?>(null);
            }

            var trigger = MergeTriggerWithPreprocessor(context, trend, _pullbackAnalyzer.LastActive ?? RsiTriggerResult.None);

            if (!trigger.IsTriggered)
            {
                SaveDiagnostics(new StrategyDiagnostics(
                    null,
                    0.0,
                    trend.Strength,
                    trigger.Score,
                    alignmentRatio,
                    sessionMultiplier,
                    atr));
                return Task.FromResult<Signal?>(null);
            }

            if (!CanEmitNewSignal(now, trend.Direction))
            {
                SaveDiagnostics(new StrategyDiagnostics(
                    null,
                    0.0,
                    trend.Strength,
                    trigger.Score,
                    alignmentRatio,
                    sessionMultiplier,
                    atr));
                return Task.FromResult<Signal?>(null);
            }

            var signal = BuildEntrySignal(context, trend, trigger, atr, alignmentRatio, sessionMultiplier);
            if (signal == null)
            {
                SaveDiagnostics(new StrategyDiagnostics(
                    null,
                    0.0,
                    trend.Strength,
                    trigger.Score,
                    alignmentRatio,
                    sessionMultiplier,
                    atr));
                return Task.FromResult<Signal?>(null);
            }

            _state.ActiveDirection = signal.Action;
            _state.LastDirection = signal.Action;
            _state.LastSignalTimeUtc = now;

            SaveDiagnostics(new StrategyDiagnostics(
                signal.Action,
                signal.Confidence,
                trend.Strength,
                trigger.Score,
                alignmentRatio,
                sessionMultiplier,
                atr));

            return Task.FromResult<Signal?>(signal);
        }

        public override RiskScore CalculateRisk(MarketContext context)
        {
            var diag = _lastDiagnostics;
            double baseScore = 4.2;

            if (diag != null)
            {
                baseScore += diag.TrendStrength * 1.1;
                baseScore += diag.TriggerScore * 0.9;
                baseScore += Math.Clamp(diag.Alignment, 0.0, 1.0) * 0.5;
            }

            baseScore += context.CurrentRegime switch
            {
                RegimeType.Trending => 0.6,
                RegimeType.Ranging => -0.5,
                RegimeType.Volatile => -0.2,
                _ => 0.0
            };

            var level = baseScore >= 6.2
                ? RiskLevel.Preferred
                : baseScore >= 5.2
                    ? RiskLevel.Normal
                    : RiskLevel.Elevated;

            var acceptable = diag != null && baseScore >= 5.2;
            var reason = acceptable ? null : "PullbackContextWeak";

            var factors = new Dictionary<string, double>
            {
                ["trend_strength"] = diag?.TrendStrength ?? 0.0,
                ["trigger_score"] = diag?.TriggerScore ?? 0.0,
                ["alignment"] = diag?.Alignment ?? 0.0,
                ["session_multiplier"] = diag?.SessionMultiplier ?? 0.0,
                ["atr"] = diag?.Atr ?? 0.0
            };

            return new RiskScore(baseScore, level, acceptable, reason, factors);
        }

        private Signal? BuildEntrySignal(
            MultiTimeframeEvaluationContext context,
            TrendAssessment trend,
            RsiTriggerResult trigger,
            double atr,
            double alignmentRatio,
            double sessionMultiplier)
        {
            var data = context.MarketData;
            var sessionScore = Math.Clamp(sessionMultiplier / 1.5, 0.0, 1.0);
            var confidence = (0.45 * trend.Strength) +
                             (0.35 * trigger.Score) +
                             (0.15 * alignmentRatio) +
                             (0.05 * sessionScore);

            if (confidence < _config.MinimumConfidence)
            {
                return null;
            }

            var price = trend.Direction == TradeAction.Buy ? data.Ask : data.Bid;
            var stopLoss = trend.Direction == TradeAction.Buy
                ? price - atr * _config.AtrStopMultiplier
                : price + atr * _config.AtrStopMultiplier;
            var takeProfit = trend.Direction == TradeAction.Buy
                ? price + atr * _config.AtrTakeProfitMultiplier
                : price - atr * _config.AtrTakeProfitMultiplier;

            var indicators = new Dictionary<string, double>
            {
                ["confidence"] = confidence,
                ["ema_fast"] = trend.FastEma,
                ["ema_slow"] = trend.SlowEma,
                ["trend_strength"] = trend.Strength,
                ["rsi"] = trigger.Current,
                ["atr"] = atr,
                ["alignment"] = alignmentRatio,
                ["session_multiplier"] = sessionMultiplier
            };

            var notes = new Dictionary<string, string>
            {
                ["direction"] = trend.Direction == TradeAction.Buy ? "long" : "short",
                ["trigger_reason"] = trigger.Reason
            };

            return new Signal
            {
                StrategyName = Name,
                Action = trend.Direction,
                Price = price,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Confidence = confidence,
                TimestampUtc = data.TimestampUtc,
                Indicators = indicators,
                Notes = notes
            };
        }

        private Signal? EvaluateExit(MultiTimeframeEvaluationContext context, TrendAssessment trend, DateTime now)
        {
            if (!_state.ActiveDirection.HasValue)
            {
                _state.LastTrendDirection = trend.Direction;
                return null;
            }

            bool lostTrend = !trend.IsActionable || trend.Direction != _state.ActiveDirection;
            if (!lostTrend)
            {
                _state.LastTrendDirection = trend.Direction;
                return null;
            }

            var cooldown = TimeSpan.FromMinutes(_config.ExitCooldownMinutes);
            if (now - _state.LastExitTimeUtc < cooldown)
            {
                return null;
            }

            _state.LastExitTimeUtc = now;
            _state.ActiveDirection = null;
            _state.LastTrendDirection = trend.Direction;

            return new Signal
            {
                StrategyName = Name,
                Action = TradeAction.Exit,
                Price = context.MarketData.Mid,
                Confidence = 1.0,
                TimestampUtc = now,
                Indicators = new Dictionary<string, double>
                {
                    ["trend_strength"] = trend.Strength
                },
                Notes = new Dictionary<string, string>
                {
                    ["reason"] = "TrendFlip"
                }
            };
        }

        private bool CanEmitNewSignal(DateTime now, TradeAction direction)
        {
            if (direction == TradeAction.None)
            {
                return false;
            }

            if (_state.ActiveDirection == direction)
            {
                return false;
            }

            if (_state.LastDirection == direction)
            {
                var cooldown = TimeSpan.FromMinutes(_config.ReentryCooldownMinutes);
                if (now - _state.LastSignalTimeUtc < cooldown)
                {
                    return false;
                }
            }

            return true;
        }

        private void SaveDiagnostics(StrategyDiagnostics? diagnostics)
        {
            _lastDiagnostics = diagnostics;
        }

        private static bool HasSufficientData(IReadOnlyList<ModelBar> bars, int required)
        {
            return bars != null && bars.Count >= Math.Max(required, 1);
        }

        private double GetAlignmentRatio(MultiTimeframeEvaluationContext context)
        {
            if (context.Alignment.TotalTimeframes <= 0)
            {
                return 0.0;
            }

            return Math.Clamp(context.Alignment.AlignedTimeframes / (double)context.Alignment.TotalTimeframes, 0.0, 1.0);
        }

        private static RealtimeFeatureContext BuildFeatureContext(
            MultiTimeframeEvaluationContext context,
            IReadOnlyList<ModelBar> h1,
            IReadOnlyList<ModelBar> m15)
        {
            var bars = new Dictionary<ModelTimeFrame, IReadOnlyList<ModelBar>>(2)
            {
                [ModelTimeFrame.H1] = h1,
                [ModelTimeFrame.M15] = m15
            };

            return new RealtimeFeatureContext(context.MarketData.TimestampUtc, bars);
        }

        private TrendAssessment OverrideTrendWithPreprocessor(
            MultiTimeframeEvaluationContext context,
            TrendAssessment current)
        {
            var fast = GetPreprocessorIndicatorValue(
                context,
                PreprocessorIndicatorNames.Ema(ModelTimeFrame.H1, _config.TrendEmaFast));
            var slow = GetPreprocessorIndicatorValue(
                context,
                PreprocessorIndicatorNames.Ema(ModelTimeFrame.H1, _config.TrendEmaSlow));

            if (!fast.HasValue || !slow.HasValue)
            {
                return current;
            }

            var direction = fast.Value > slow.Value
                ? TradeAction.Buy
                : fast.Value < slow.Value
                    ? TradeAction.Sell
                    : TradeAction.None;

            var separation = Math.Abs(fast.Value - slow.Value) / Math.Max(Math.Abs(slow.Value), 1e-6);
            if (separation < _config.MinimumTrendSeparationRatio)
            {
                direction = TradeAction.None;
            }

            var strength = Math.Clamp(
                separation / Math.Max(_config.MinimumTrendSeparationRatio, 1e-6),
                0.0,
                1.0);

            return new TrendAssessment(direction, separation, fast.Value, slow.Value, current.LatestClose, strength);
        }

        private double ResolveAtr(MultiTimeframeEvaluationContext context)
        {
            return GetPreprocessorIndicatorValue(
                       context,
                       PreprocessorIndicatorNames.Atr(ModelTimeFrame.H1, _config.AtrPeriod))
                   ?? _atrAnalyzer.LastAtr;
        }

        private RsiTriggerResult MergeTriggerWithPreprocessor(
            MultiTimeframeEvaluationContext context,
            TrendAssessment trend,
            RsiTriggerResult existing)
        {
            var rsi = GetPreprocessorIndicatorValue(
                context,
                PreprocessorIndicatorNames.Rsi(ModelTimeFrame.M15, _config.EntryRsiPeriod));

            if (!rsi.HasValue)
            {
                return existing;
            }

            if (trend.Direction == TradeAction.None)
            {
                return existing with { Current = rsi.Value };
            }

            var releaseRange = Math.Max(5.0, _config.TriggerReleaseRange);
            bool qualifies = trend.Direction == TradeAction.Buy
                ? rsi.Value <= _config.Oversold + releaseRange
                : rsi.Value >= _config.Overbought - releaseRange;

            if (!qualifies)
            {
                return existing with { Current = rsi.Value };
            }

            var depth = trend.Direction == TradeAction.Buy
                ? Math.Max(0.0, _config.Oversold - rsi.Value)
                : Math.Max(0.0, rsi.Value - _config.Overbought);
            var score = Math.Clamp(depth / 20.0, 0.0, 1.0);
            var reason = trend.Direction == TradeAction.Buy ? "preproc_rsi_pullback" : "preproc_rsi_retrace";

            if (existing.IsTriggered && existing.Score >= score)
            {
                return existing with { Current = rsi.Value };
            }

            return new RsiTriggerResult(true, score, rsi.Value, existing.Previous, reason);
        }

        private sealed class TrendState
        {
            public TradeAction? ActiveDirection { get; set; }
            public TradeAction? LastDirection { get; set; }
            public TradeAction? LastTrendDirection { get; set; }
            public DateTime LastSignalTimeUtc { get; set; } = DateTime.MinValue;
            public DateTime LastExitTimeUtc { get; set; } = DateTime.MinValue;
        }

        private sealed record StrategyDiagnostics(
            TradeAction? Action,
            double Confidence,
            double TrendStrength,
            double TriggerScore,
            double Alignment,
            double SessionMultiplier,
            double Atr);
    }

}
