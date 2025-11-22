using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// Chiến lược swing breakout dựa trên điểm giá đóng cửa cắt EMA200.
    /// Sử dụng ATR/ADX để lọc các pha biến động yếu và chèn cooldown giữa các tín hiệu.
    /// </summary>
    public sealed class Ema200BreakoutStrategy : MultiTimeframeStrategyBase
    {
        private readonly Ema200BreakoutStrategyConfig _config;
        private readonly RegimeIndicators _indicators = new();
        private readonly ModelTimeFrame _triggerTimeframe;
        private readonly BreakoutState _state = new();
        private BreakoutDiagnostics? _lastDiagnostics;

        public Ema200BreakoutStrategy(
            TimeframeManager timeframeManager,
            TimeframeSynchronizer synchronizer,
            SessionAwareAnalyzer sessionAnalyzer,
            Ema200BreakoutStrategyConfig? config = null)
            : base("Ema200Breakout", timeframeManager, synchronizer, sessionAnalyzer, minimumAlignedTimeframes: 1)
        {
            _config = config ?? new Ema200BreakoutStrategyConfig();
            _config.Validate();
            _triggerTimeframe = ParseTimeframe(_config.TriggerTimeframe);
        }

        protected override Task<Signal?> EvaluateMultiTimeframeAsync(
            MultiTimeframeEvaluationContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var bars = context.Snapshot.GetBars(_triggerTimeframe);
            if (!HasSufficientData(bars, Math.Max(_config.MinimumBars, _config.EmaPeriod + 5)))
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var atr = CalculateAtr(bars);
            if (atr < _config.MinimumAtr)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var adx = CalculateAdx(bars);
            if (adx < _config.MinimumAdx)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var emaCurrent = CalculateEma(bars, _config.EmaPeriod);
            var emaPrevious = CalculateEma(bars, _config.EmaPeriod, offsetFromLast: 1);
            if (!emaCurrent.HasValue || !emaPrevious.HasValue)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var trigger = DetectBreakout(bars, emaCurrent.Value, emaPrevious.Value);
            if (trigger.Action == TradeAction.None)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var now = context.MarketData.TimestampUtc;
            if (!CanEmit(now, trigger.Action))
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var distance = Math.Abs(trigger.Close - emaCurrent.Value);
            var minimumDistance = Math.Max(_config.BreakoutBuffer, atr * _config.MinimumDistanceAtrMultiple);
            if (distance < minimumDistance)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var sessionMultiplier = GetSessionMultiplier(context.Session);
            var confidence = CalculateConfidence(distance, atr, adx, sessionMultiplier);

            var price = trigger.Action == TradeAction.Buy ? context.MarketData.Ask : context.MarketData.Bid;
            var stopLoss = trigger.Action == TradeAction.Buy
                ? price - atr * _config.AtrStopMultiplier
                : price + atr * _config.AtrStopMultiplier;
            var takeProfit = trigger.Action == TradeAction.Buy
                ? price + atr * _config.AtrTakeProfitMultiplier
                : price - atr * _config.AtrTakeProfitMultiplier;

            var indicators = new Dictionary<string, double>
            {
                ["atr"] = atr,
                ["adx"] = adx,
                ["distance"] = distance,
                ["session_multiplier"] = sessionMultiplier,
                ["ema200"] = emaCurrent.Value
            };

            var notes = new Dictionary<string, string>
            {
                ["timeframe"] = _triggerTimeframe.ToString(),
                ["trigger"] = trigger.Description
            };

            var signal = new Signal
            {
                StrategyName = Name,
                Action = trigger.Action,
                Price = price,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Confidence = confidence,
                TimestampUtc = now,
                Indicators = indicators,
                Notes = notes
            };

            _state.LastDirection = trigger.Action;
            _state.LastSignalTimeUtc = now;

            SaveDiagnostics(new BreakoutDiagnostics(trigger.Action, confidence, distance / atr, adx, sessionMultiplier));
            return Task.FromResult<Signal?>(signal);
        }

        public override RiskScore CalculateRisk(MarketContext context)
        {
            var diag = _lastDiagnostics;
            if (diag == null)
            {
                return new RiskScore(4.0, RiskLevel.Elevated, false, "NoDiagnostics");
            }

            var baseScore = 4.8 + diag.Confidence * 1.2 + Math.Min(diag.DistanceAtrMultiple, 2.5) * 0.4;
            baseScore += Math.Clamp((diag.Adx - _config.MinimumAdx) / 40.0, -0.5, 0.5);
            baseScore += Math.Clamp(diag.SessionMultiplier / 2.0, 0.0, 0.4);

            var level = baseScore >= 6.5
                ? RiskLevel.Preferred
                : baseScore >= 5.5
                    ? RiskLevel.Normal
                    : RiskLevel.Elevated;

            var acceptable = baseScore >= 5.6;
            var reason = acceptable ? null : "BreakoutContextWeak";

            var factors = new Dictionary<string, double>
            {
                ["confidence"] = diag.Confidence,
                ["distance_atr_multiple"] = diag.DistanceAtrMultiple,
                ["adx"] = diag.Adx,
                ["session_multiplier"] = diag.SessionMultiplier
            };

            return new RiskScore(baseScore, level, acceptable, reason, factors);
        }

        private bool CanEmit(DateTime now, TradeAction action)
        {
            if (action == TradeAction.None)
            {
                return false;
            }

            if (_state.LastDirection == action)
            {
                var cooldown = TimeSpan.FromMinutes(_config.CooldownMinutes);
                if (now - _state.LastSignalTimeUtc < cooldown)
                {
                    return false;
                }
            }

            return true;
        }

        private BreakoutTrigger DetectBreakout(IReadOnlyList<ModelBar> bars, double emaCurrent, double emaPrevious)
        {
            var current = bars[^1];
            var previous = bars[^2];
            var prevDelta = previous.Close - emaPrevious;
            var currentDelta = current.Close - emaCurrent;

            var buffer = _config.BreakoutBuffer;
            var crossedUp = prevDelta <= -buffer && currentDelta >= buffer;
            var crossedDown = prevDelta >= buffer && currentDelta <= -buffer;

            if (crossedUp)
            {
                return new BreakoutTrigger(TradeAction.Buy, current.Close, BuildDescription(previous.Close, current.Close, emaPrevious, emaCurrent));
            }

            if (crossedDown)
            {
                return new BreakoutTrigger(TradeAction.Sell, current.Close, BuildDescription(previous.Close, current.Close, emaPrevious, emaCurrent));
            }

            return BreakoutTrigger.None;
        }

        private static string BuildDescription(double prevClose, double currentClose, double prevEma, double currentEma)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "prev={0:F5},curr={1:F5},prevEMA={2:F5},currEMA={3:F5}",
                prevClose,
                currentClose,
                prevEma,
                currentEma);
        }

        private double CalculateAtr(IReadOnlyList<ModelBar> bars)
        {
            if (bars.Count < _config.AtrPeriod + 2)
            {
                return 0.0;
            }

            var high = bars.Select(b => b.High).ToList();
            var low = bars.Select(b => b.Low).ToList();
            var close = bars.Select(b => b.Close).ToList();

            try
            {
                return _indicators.CalculateATR(high, low, close, _config.AtrPeriod);
            }
            catch
            {
                return 0.0;
            }
        }

        private double CalculateAdx(IReadOnlyList<ModelBar> bars)
        {
            if (bars.Count < _config.AdxPeriod + 2)
            {
                return 0.0;
            }

            var high = bars.Select(b => b.High).ToList();
            var low = bars.Select(b => b.Low).ToList();
            var close = bars.Select(b => b.Close).ToList();

            try
            {
                return _indicators.CalculateADX(high, low, close, _config.AdxPeriod);
            }
            catch
            {
                return 0.0;
            }
        }

        private static double? CalculateEma(IReadOnlyList<ModelBar> bars, int period, int offsetFromLast = 0)
        {
            if (bars == null || period <= 0)
            {
                return null;
            }

            var endIndex = bars.Count - offsetFromLast;
            if (endIndex <= period)
            {
                return null;
            }

            var startIndex = endIndex - period;
            double ema = bars[startIndex].Close;
            var k = 2.0 / (period + 1);

            for (int i = startIndex + 1; i < endIndex; i++)
            {
                ema = (bars[i].Close - ema) * k + ema;
            }

            return ema;
        }

        private static bool HasSufficientData(IReadOnlyList<ModelBar> bars, int required)
        {
            return bars != null && bars.Count >= required;
        }

        private double CalculateConfidence(double distance, double atr, double adx, double sessionMultiplier)
        {
            if (atr <= 0)
            {
                return 0.0;
            }

            double distanceAtr = distance / atr;
            var distanceScore = Normalize(
                distanceAtr,
                _config.MinimumDistanceAtrMultiple,
                _config.MinimumDistanceAtrMultiple * 4.0);
            var atrScore = Normalize(atr, _config.MinimumAtr, _config.MinimumAtr * 4.0);
            var adxScore = Normalize(adx, _config.MinimumAdx, _config.MinimumAdx + 25.0);
            var sessionScore = Math.Clamp(sessionMultiplier / 1.5, 0.0, 1.0);

            return Math.Clamp(
                (distanceScore * 0.4) +
                (adxScore * 0.3) +
                (atrScore * 0.2) +
                (sessionScore * 0.1),
                0.0,
                1.0);
        }

        private static double Normalize(double value, double min, double max)
        {
            if (max <= min)
            {
                return 0.0;
            }

            return Math.Clamp((value - min) / (max - min), 0.0, 1.0);
        }

        private static ModelTimeFrame ParseTimeframe(string timeframe)
        {
            if (Enum.TryParse<ModelTimeFrame>(timeframe, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            return ModelTimeFrame.H1;
        }

        private void SaveDiagnostics(BreakoutDiagnostics? diagnostics)
        {
            _lastDiagnostics = diagnostics;
        }

        private sealed record BreakoutTrigger(TradeAction Action, double Close, string Description)
        {
            public static BreakoutTrigger None { get; } = new BreakoutTrigger(TradeAction.None, 0.0, "none");
        }

        private sealed record BreakoutDiagnostics(
            TradeAction Action,
            double Confidence,
            double DistanceAtrMultiple,
            double Adx,
            double SessionMultiplier);

        private sealed class BreakoutState
        {
            public TradeAction? LastDirection { get; set; }
            public DateTime LastSignalTimeUtc { get; set; } = DateTime.MinValue;
        }
    }
}
