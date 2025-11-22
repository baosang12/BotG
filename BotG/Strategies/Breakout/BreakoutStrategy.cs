using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BotG.MarketRegime;
using BotG.MultiTimeframe;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;
using Strategies.Config;
using Strategies.Confirmation;
using Strategies.Templates;

namespace Strategies.Breakout
{
    public sealed class BreakoutStrategy : MultiTimeframeStrategyBase
    {
        private readonly BreakoutStrategyConfig _config;
        private readonly ConfirmationConfig _confirmationConfig;
        private readonly MultiTimeframeConfirmationEngine? _confirmationEngine;
        private BreakoutDiagnostics? _lastDiagnostics;

        public BreakoutStrategy(
            TimeframeManager timeframeManager,
            TimeframeSynchronizer synchronizer,
            SessionAwareAnalyzer sessionAnalyzer,
            BreakoutStrategyConfig? config = null,
            ConfirmationConfig? confirmationConfig = null,
            MultiTimeframeConfirmationEngine? confirmationEngine = null)
            : base("Breakout", timeframeManager, synchronizer, sessionAnalyzer, minimumAlignedTimeframes: 2)
        {
            _config = config ?? new BreakoutStrategyConfig();
            _config.Validate();

            _confirmationConfig = BuildConfirmationConfig(_config, confirmationConfig);
            _confirmationEngine = _confirmationConfig.EnableMultiTimeframeConfirmation
                ? confirmationEngine ?? new MultiTimeframeConfirmationEngine(_confirmationConfig)
                : null;
        }

        private static ConfirmationConfig BuildConfirmationConfig(
            BreakoutStrategyConfig strategyConfig,
            ConfirmationConfig? confirmationOverride)
        {
            var effective = (confirmationOverride?.Clone() ?? new ConfirmationConfig());

            if (confirmationOverride == null)
            {
                effective.MinimumConfirmationThreshold = strategyConfig.MinimumConfirmationThreshold;
                effective.TrendFastEma = Math.Max(2, strategyConfig.TrendEmaFast);
                effective.TrendSlowEma = Math.Max(effective.TrendFastEma + 1, strategyConfig.TrendEmaSlow);
                effective.KeyLevelLookback = Math.Max(strategyConfig.TouchLookbackBars, effective.KeyLevelLookback);
                effective.VolumeSmaPeriodH1 = Math.Max(5, strategyConfig.VolumeSmaPeriod);
                effective.VolumeSmaPeriodM15 = Math.Max(5, Math.Max(strategyConfig.VolumeSmaPeriod / 2, 3));
                effective.VolumeSpikeMultiplierH1 = Math.Max(0.5, strategyConfig.VolumeMultiplier);
                effective.VolumeSpikeMultiplierM15 = Math.Max(0.5, strategyConfig.VolumeMultiplier);
            }

            effective.EnableMultiTimeframeConfirmation =
                strategyConfig.EnableMultiTimeframeConfirmation && effective.EnableMultiTimeframeConfirmation;
            effective.Normalize();
            return effective;
        }

        protected override Task<Signal?> EvaluateMultiTimeframeAsync(
            MultiTimeframeEvaluationContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var h4 = context.Snapshot.GetBars(ModelTimeFrame.H4);
            var h1 = context.Snapshot.GetBars(ModelTimeFrame.H1);
            var m15 = context.Snapshot.GetBars(ModelTimeFrame.M15);

            if (h4.Count < _config.TrendEmaSlow + 5 ||
                h1.Count < _config.MinimumH1Bars ||
                m15.Count < _config.MinimumM15Bars)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var atr = Technicals.CalculateAtr(h1, _config.AtrPeriod);
            if (atr <= double.Epsilon)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            var emaFast = Technicals.CalculateEma(h4, _config.TrendEmaFast);
            var emaSlow = Technicals.CalculateEma(h4, _config.TrendEmaSlow);
            if (emaFast == null || emaSlow == null)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            ConfirmationResult? confirmation = null;
            if (_confirmationEngine != null)
            {
                confirmation = _confirmationEngine.CheckConfirmation(context);
                if (!confirmation.IsConfirmed)
                {
                    LogConfirmationFailure(confirmation);
                    SaveDiagnostics(null);
                    return Task.FromResult<Signal?>(null);
                }
            }

            var longCandidate = EvaluateDirection(context, h1, m15, atr, emaFast.Value, emaSlow.Value, isLong: true);
            var shortCandidate = EvaluateDirection(context, h1, m15, atr, emaFast.Value, emaSlow.Value, isLong: false);

            var selected = SelectCandidate(longCandidate, shortCandidate);
            if (selected == null)
            {
                SaveDiagnostics(null);
                return Task.FromResult<Signal?>(null);
            }

            if (confirmation != null)
            {
                selected = selected with
                {
                    Confidence = Math.Clamp(selected.Confidence * confirmation.OverallScore, 0.0, 1.0)
                };
            }

            SaveDiagnostics(new BreakoutDiagnostics(
                selected.Action,
                selected.Strength,
                selected.Confidence,
                selected.SessionMultiplier,
                atr,
                selected.KeyLevel,
                confirmation?.OverallScore ?? 0.0));

            return Task.FromResult<Signal?>(BuildSignal(context, selected, atr, confirmation));
        }

        private static void LogConfirmationFailure(ConfirmationResult confirmation)
        {
            if (!IsConfirmationDebugEnabled())
            {
                return;
            }

            try
            {
                Console.WriteLine(
                    "[BreakoutStrategy] confirmation rejected: score={0:F3} threshold={1:F3} trend={2:F3} key={3:F3} volume={4:F3} momentum={5:F3}",
                    confirmation.OverallScore,
                    confirmation.Threshold,
                    confirmation.TrendAlignment,
                    confirmation.KeyLevelConfirmation,
                    confirmation.VolumeConfirmation,
                    confirmation.MomentumConfirmation);
            }
            catch
            {
                // debug only
            }
        }

        private static bool IsConfirmationDebugEnabled()
        {
            try
            {
                var value = Environment.GetEnvironmentVariable("BOTG_DEBUG_CONFIRMATION");
                return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal Task<Signal?> EvaluateDeterministicAsync(MultiTimeframeEvaluationContext context, CancellationToken ct = default)
        {
            return EvaluateMultiTimeframeAsync(context, ct);
        }

        public override RiskScore CalculateRisk(MarketContext context)
        {
            var diagnostics = _lastDiagnostics;
            var strength = diagnostics?.Strength ?? 0.0;
            var confidence = diagnostics?.Confidence ?? 0.0;

            var baseScore = 4.5 + Math.Min(strength, 1.0) + confidence * 0.8;
            var regimePenalty = context.CurrentRegime switch
            {
                RegimeType.Trending => 0.3,
                RegimeType.Volatile => -0.5,
                RegimeType.Calm => -0.2,
                _ => 0.0
            };
            baseScore += regimePenalty;

            var level = baseScore >= 6.5
                ? RiskLevel.Preferred
                : baseScore >= 5.5
                    ? RiskLevel.Normal
                    : RiskLevel.Elevated;

            var acceptable = diagnostics != null && baseScore >= 5.5;
            var reason = acceptable ? null : "BreakoutWeakContext";

            var factors = new Dictionary<string, double>
            {
                ["strength"] = strength,
                ["confidence"] = confidence,
                ["session_multiplier"] = diagnostics?.SessionMultiplier ?? 0.0,
                ["confirmation"] = diagnostics?.ConfirmationScore ?? 0.0
            };

            return new RiskScore(baseScore, level, acceptable, reason, factors);
        }

        private BreakoutCandidate? EvaluateDirection(
            MultiTimeframeEvaluationContext context,
            IReadOnlyList<ModelBar> h1,
            IReadOnlyList<ModelBar> m15,
            double atr,
            double emaFast,
            double emaSlow,
            bool isLong)
        {
            bool trendAligned = isLong ? emaFast > emaSlow : emaFast < emaSlow;
            if (!trendAligned)
            {
                return null;
            }

            var keyLevel = FindKeyLevel(h1, isLong);
            if (keyLevel == null)
            {
                return null;
            }

            var breakoutBar = h1[^1];
            var previousBar = h1[^2];
            var distance = isLong ? breakoutBar.Close - keyLevel.Level : keyLevel.Level - breakoutBar.Close;
            if (distance <= 0)
            {
                return null;
            }

            if (distance < _config.AtrConfirmationMultiplier * atr ||
                distance / atr < _config.MinimumStrength)
            {
                return null;
            }

            var barsSinceTouch = h1.Count - 1 - keyLevel.LastTouchIndex;
            if (barsSinceTouch > _config.MaxBreakoutBars)
            {
                return null;
            }

            if (!IsWithinTimingConstraint(previousBar, keyLevel.Level, isLong))
            {
                return null;
            }

            if (HasRetestViolation(h1, keyLevel.Level, isLong))
            {
                return null;
            }

            var h1VolumeFactor = VolumeFactor(h1, breakoutBar, _config.VolumeSmaPeriod);
            if (h1VolumeFactor < _config.VolumeMultiplier)
            {
                return null;
            }

            var m15Bar = m15[^1];
            var m15VolumeFactor = VolumeFactor(m15, m15Bar, Math.Min(_config.VolumeSmaPeriod, Math.Max(5, _config.VolumeSmaPeriod / 2)));
            var m15DirectionOk = isLong ? m15Bar.Close > keyLevel.Level : m15Bar.Close < keyLevel.Level;
            if (!m15DirectionOk || m15VolumeFactor < _config.VolumeMultiplier)
            {
                return null;
            }

            var sessionMultiplier = GetSessionMultiplier(context.Session);
            var alignmentScore = context.Alignment.AlignedTimeframes / Math.Max(context.Alignment.TotalTimeframes, 1.0);

            var confidence = CalculateConfidence(distance / atr, h1VolumeFactor, m15VolumeFactor, alignmentScore, sessionMultiplier, keyLevel.OrderBlockCount);

            var indicators = new Dictionary<string, double>
            {
                ["strength"] = distance / atr,
                ["atr"] = atr,
                ["volume_factor_h1"] = h1VolumeFactor,
                ["volume_factor_m15"] = m15VolumeFactor,
                ["touches"] = keyLevel.TouchCount,
                ["alignment"] = alignmentScore,
                ["session_multiplier"] = sessionMultiplier
            };

            var notes = new Dictionary<string, string>
            {
                ["key_level"] = keyLevel.Level.ToString("F5", System.Globalization.CultureInfo.InvariantCulture),
                ["direction"] = isLong ? "long" : "short",
                ["reason"] = "Multi-timeframe breakout confirmation"
            };

            return new BreakoutCandidate(
                Action: isLong ? TradeAction.Buy : TradeAction.Sell,
                KeyLevel: keyLevel.Level,
                BreakoutPrice: breakoutBar.Close,
                Confidence: confidence,
                Strength: distance / atr,
                SessionMultiplier: sessionMultiplier,
                Indicators: indicators,
                Notes: notes);
        }

        private BreakoutCandidate? SelectCandidate(BreakoutCandidate? longCandidate, BreakoutCandidate? shortCandidate)
        {
            if (longCandidate == null && shortCandidate == null)
            {
                return null;
            }

            if (longCandidate == null)
            {
                return shortCandidate;
            }

            if (shortCandidate == null)
            {
                return longCandidate;
            }

            return longCandidate.Confidence >= shortCandidate.Confidence ? longCandidate : shortCandidate;
        }

        private BreakoutLevel? FindKeyLevel(IReadOnlyList<ModelBar> bars, bool isLong)
        {
            var tolerance = _config.TouchTolerancePercent / 100.0;
            var totalVolume = bars.Sum(b => Math.Max(1.0, b.Volume));
            var lookback = Math.Min(_config.TouchLookbackBars, bars.Count - _config.MaxBreakoutBars - 1);
            if (lookback <= _config.MinimumTouches)
            {
                return null;
            }

            var startIndex = bars.Count - lookback - _config.MaxBreakoutBars;
            var candidates = new List<BreakoutLevel>();

            for (int i = startIndex; i < bars.Count - _config.MaxBreakoutBars; i++)
            {
                var level = isLong ? bars[i].High : bars[i].Low;
                if (level <= 0)
                {
                    continue;
                }

                var candidate = candidates.FirstOrDefault(c => Math.Abs(c.Level - level) / level <= tolerance);
                if (candidate == null)
                {
                    candidate = new BreakoutLevel(level);
                    candidates.Add(candidate);
                }

                candidate.TouchCount++;
                candidate.LastTouchIndex = i;
                candidate.TouchVolume += Math.Max(1.0, bars[i].Volume);
                if (IsOrderBlockBar(bars[i], level, tolerance, isLong))
                {
                    candidate.OrderBlockCount++;
                }
            }

            var chosen = candidates
                .Where(c =>
                    c.TouchCount >= _config.MinimumTouches &&
                    (c.TouchVolume / totalVolume) >= _config.WeeklyVolumeThreshold &&
                    c.OrderBlockCount >= _config.OrderBlockDensityMin)
                .OrderByDescending(c => c.TouchCount)
                .ThenByDescending(c => c.TouchVolume)
                .FirstOrDefault();

            return chosen;
        }

        private bool HasRetestViolation(IReadOnlyList<ModelBar> bars, double keyLevel, bool isLong)
        {
            var limit = Math.Min(_config.RetestWindowBars, bars.Count);
            var threshold = keyLevel + (isLong ? -_config.MaximumRetestPercent * keyLevel : _config.MaximumRetestPercent * keyLevel);

            for (int i = bars.Count - limit; i < bars.Count; i++)
            {
                if (i < 0)
                {
                    continue;
                }

                var bar = bars[i];
                if (isLong)
                {
                    if (bar.Low < keyLevel - Math.Abs(keyLevel - threshold))
                    {
                        return true;
                    }
                }
                else
                {
                    if (bar.High > keyLevel + Math.Abs(keyLevel - threshold))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsWithinTimingConstraint(ModelBar previousBar, double keyLevel, bool isLong)
        {
            return isLong
                ? previousBar.Close <= keyLevel * 1.0005
                : previousBar.Close >= keyLevel * 0.9995;
        }

        private static double VolumeFactor(IReadOnlyList<ModelBar> series, ModelBar breakoutBar, int period)
        {
            var sma = Technicals.CalculateSma(series, period, b => Math.Max(1.0, b.Volume));
            return breakoutBar.Volume / Math.Max(1.0, sma);
        }

        private static double CalculateConfidence(
            double strengthRatio,
            double h1VolumeFactor,
            double m15VolumeFactor,
            double alignmentScore,
            double sessionMultiplier,
            int orderBlockCount)
        {
            var strengthScore = Math.Clamp(strengthRatio / 1.5, 0.0, 1.0);
            var volumeScore = Math.Clamp(Math.Min(h1VolumeFactor, m15VolumeFactor) / 3.0, 0.0, 1.0);
            var sessionScore = Math.Clamp(sessionMultiplier / 1.5, 0.0, 1.0);
            var orderBlockScore = Math.Clamp(orderBlockCount / 5.0, 0.0, 1.0);

            var confidence = (0.4 * strengthScore) +
                             (0.25 * volumeScore) +
                             (0.2 * alignmentScore) +
                             (0.1 * sessionScore) +
                             (0.05 * orderBlockScore);

            return Math.Clamp(confidence, 0.0, 1.0);
        }

        private static bool IsOrderBlockBar(ModelBar bar, double level, double tolerance, bool isLong)
        {
            var range = Math.Max(bar.High - bar.Low, 1e-6);
            var body = Math.Abs(bar.Close - bar.Open);
            var nearLevel = isLong
                ? Math.Abs(bar.High - level) / level <= tolerance
                : Math.Abs(bar.Low - level) / level <= tolerance;

            if (!nearLevel)
            {
                return false;
            }

            return body / range >= 0.6;
        }

        private Signal BuildSignal(MultiTimeframeEvaluationContext context, BreakoutCandidate candidate, double atr, ConfirmationResult? confirmation)
        {
            var data = context.MarketData;
            var price = candidate.Action == TradeAction.Buy ? data.Ask : data.Bid;
            var stopLoss = candidate.Action == TradeAction.Buy
                ? candidate.KeyLevel - atr
                : candidate.KeyLevel + atr;
            var takeProfit = candidate.Action == TradeAction.Buy
                ? price + atr * 2.0
                : price - atr * 2.0;

            var indicators = new Dictionary<string, double>(candidate.Indicators);
            var notes = new Dictionary<string, string>(candidate.Notes);

            if (confirmation != null)
            {
                indicators["mtf_confirmation"] = confirmation.OverallScore;
                indicators["mtf_trend"] = confirmation.TrendAlignment;
                indicators["mtf_keylevel"] = confirmation.KeyLevelConfirmation;
                indicators["mtf_volume"] = confirmation.VolumeConfirmation;
                indicators["mtf_momentum"] = confirmation.MomentumConfirmation;

                foreach (var detail in confirmation.Details)
                {
                    notes[$"confirm_{detail.Key}"] = detail.Value;
                }
            }

            return new Signal
            {
                StrategyName = Name,
                Action = candidate.Action,
                Price = price,
                Confidence = candidate.Confidence,
                TimestampUtc = data.TimestampUtc,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Indicators = indicators,
                Notes = notes
            };
        }

        private void SaveDiagnostics(BreakoutDiagnostics? diagnostics)
        {
            _lastDiagnostics = diagnostics;
        }

        private sealed class BreakoutLevel
        {
            public BreakoutLevel(double level)
            {
                Level = level;
                LastTouchIndex = -1;
            }

            public double Level { get; }
            public int TouchCount { get; set; }
            public int LastTouchIndex { get; set; }
            public double TouchVolume { get; set; }
            public int OrderBlockCount { get; set; }
        }

        private sealed record BreakoutCandidate(
            TradeAction Action,
            double KeyLevel,
            double BreakoutPrice,
            double Confidence,
            double Strength,
            double SessionMultiplier,
            IReadOnlyDictionary<string, double> Indicators,
            IReadOnlyDictionary<string, string> Notes);

        private sealed record BreakoutDiagnostics(
            TradeAction Action,
            double Strength,
            double Confidence,
            double SessionMultiplier,
            double Atr,
            double KeyLevel,
            double ConfirmationScore);

        private static class Technicals
        {
            public static double CalculateAtr(IReadOnlyList<ModelBar> bars, int period)
            {
                if (bars.Count < period + 1)
                {
                    return 0;
                }

                double atr = 0;
                var start = bars.Count - period - 1;

                for (int i = start + 1; i < bars.Count; i++)
                {
                    var current = bars[i];
                    var previous = bars[i - 1];
                    var tr = Math.Max(
                        current.High - current.Low,
                        Math.Max(
                            Math.Abs(current.High - previous.Close),
                            Math.Abs(current.Low - previous.Close)));
                    atr += tr;
                }

                return atr / period;
            }

            public static double? CalculateEma(IReadOnlyList<ModelBar> bars, int period)
            {
                if (bars.Count < period)
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

            public static double CalculateSma(IReadOnlyList<ModelBar> bars, int period, Func<ModelBar, double> selector)
            {
                period = Math.Min(period, bars.Count);
                if (period == 0)
                {
                    return 0;
                }

                double sum = 0;
                for (int i = bars.Count - period; i < bars.Count; i++)
                {
                    sum += selector(bars[i]);
                }

                return sum / period;
            }
        }
    }
}
