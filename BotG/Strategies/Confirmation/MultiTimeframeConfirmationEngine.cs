using System;
using System.Collections.Generic;
using System.Linq;
using BotG.MultiTimeframe;
using BotG.Runtime.Preprocessor;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;
using Strategies.Templates;

namespace Strategies.Confirmation
{
    public class MultiTimeframeConfirmationEngine
    {
        private readonly ConfirmationConfig _config;
        private readonly WeightProfile _weights;

        public MultiTimeframeConfirmationEngine(ConfirmationConfig? config = null)
        {
            _config = (config?.Clone() ?? new ConfirmationConfig());
            _config.Normalize();
            _weights = WeightProfile.FromConfig(_config);
        }

        public ConfirmationResult CheckConfirmation(MultiTimeframeEvaluationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var series = BuildSeriesContext(context);
            var result = new ConfirmationResult
            {
                Threshold = _config.MinimumConfirmationThreshold
            };

            result.TrendAlignment = CheckTrendAlignment(series, result.Details);
            result.KeyLevelConfirmation = CheckKeyLevelConfirmation(context, series, result.Details);
            result.VolumeConfirmation = CheckVolumeConfirmation(series, result.Details);
            result.MomentumConfirmation = CheckMomentumConfirmation(series, result.Details);
            result.OverallScore = CalculateOverallConfidence(result);

            if (IsDebugEnabled())
            {
                try
                {
                    Console.WriteLine(
                        "[ConfirmationEngine] score={0:F3} threshold={1:F3} trend={2:F3} key={3:F3} volume={4:F3} momentum={5:F3}",
                        result.OverallScore,
                        result.Threshold,
                        result.TrendAlignment,
                        result.KeyLevelConfirmation,
                        result.VolumeConfirmation,
                        result.MomentumConfirmation);
                }
                catch
                {
                    // debug only
                }
            }

            return result;
        }

        private static bool IsDebugEnabled()
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

        private MultiTimeframeSeriesContext BuildSeriesContext(MultiTimeframeEvaluationContext context)
        {
            var snapshot = context.Snapshot;
            var h4 = BuildSeriesData(context, snapshot, ModelTimeFrame.H4, _config.VolumeSmaPeriodH1);
            var h1 = BuildSeriesData(context, snapshot, ModelTimeFrame.H1, _config.VolumeSmaPeriodH1);
            var m15 = BuildSeriesData(context, snapshot, ModelTimeFrame.M15, _config.VolumeSmaPeriodM15);
            return new MultiTimeframeSeriesContext(h4, h1, m15);
        }

        private TimeframeSeriesData BuildSeriesData(
            MultiTimeframeEvaluationContext context,
            TimeframeSnapshot snapshot,
            ModelTimeFrame timeframe,
            int volumePeriod)
        {
            var bars = snapshot.GetBars(timeframe) ?? Array.Empty<ModelBar>();
            var data = new TimeframeSeriesData(timeframe, bars);
            data.VolumeSma = TryGetIndicator(context, PreprocessorIndicatorNames.Sma(timeframe, volumePeriod))
                ?? CalculateSma(bars, volumePeriod, b => Math.Max(1.0, b.Volume));
            data.Atr = TryGetIndicator(context, PreprocessorIndicatorNames.Atr(timeframe, _config.MomentumAtrPeriod))
                ?? CalculateAtr(bars, _config.MomentumAtrPeriod);
            data.Rsi = TryGetIndicator(context, PreprocessorIndicatorNames.Rsi(timeframe, _config.MomentumRsiPeriod))
                ?? (CalculateRsi(bars, _config.MomentumRsiPeriod) ?? double.NaN);
            data.PriceSlope = CalculatePriceSlope(bars, _config.MomentumLookbackBars);
            return data;
        }

        private double? TryGetIndicator(MultiTimeframeEvaluationContext context, string indicatorName)
        {
            if (string.IsNullOrWhiteSpace(indicatorName))
            {
                return null;
            }

            var bridge = context.PreprocessorBridge;
            if (bridge == null)
            {
                return null;
            }

            var snapshotTime = bridge.LatestSnapshotTime;
            if (!snapshotTime.HasValue)
            {
                return null;
            }

            var age = context.MarketData.TimestampUtc - snapshotTime.Value;
            if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(2))
            {
                return null;
            }

            var value = bridge.GetIndicator(indicatorName);
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                return null;
            }

            return value.Value;
        }

        private double CheckTrendAlignment(MultiTimeframeSeriesContext series, IDictionary<string, string> details)
        {
            var trendMap = new Dictionary<ModelTimeFrame, TrendDirection>
            {
                [ModelTimeFrame.H4] = AnalyzeTrend(series.H4),
                [ModelTimeFrame.H1] = AnalyzeTrend(series.H1),
                [ModelTimeFrame.M15] = AnalyzeTrend(series.M15)
            };

            var alignedDirection = trendMap.Values
                .Where(t => t != TrendDirection.Range)
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? TrendDirection.Range;

            if (alignedDirection == TrendDirection.Range)
            {
                details["TrendAlignment"] = "no-consensus";
                return 0.0;
            }

            var alignedCount = trendMap.Count(kvp => kvp.Value == alignedDirection);
            if (alignedCount < _config.RequiredTimeframeAlignment)
            {
                details["TrendAlignment"] = $"aligned={alignedCount}/3";
                return 0.0;
            }

            var ratio = alignedCount / 3.0;
            details["TrendAlignment"] = $"{alignedDirection} {alignedCount}/3";
            return Math.Clamp(ratio, 0.0, 1.0);
        }

        private TrendDirection AnalyzeTrend(TimeframeSeriesData data)
        {
            if (data.Bars.Count < _config.TrendSlowEma + 2)
            {
                return TrendDirection.Range;
            }

            var emaFast = CalculateEma(data.Bars, _config.TrendFastEma);
            var emaSlow = CalculateEma(data.Bars, _config.TrendSlowEma);
            if (!emaFast.HasValue || !emaSlow.HasValue)
            {
                return TrendDirection.Range;
            }

            var delta = emaFast.Value - emaSlow.Value;
            var relative = Math.Abs(delta) / Math.Max(Math.Abs(emaSlow.Value), 1e-5);
            if (relative < _config.TrendAlignmentTolerance)
            {
                return TrendDirection.Range;
            }

            return delta > 0 ? TrendDirection.Up : TrendDirection.Down;
        }

        private double CheckKeyLevelConfirmation(
            MultiTimeframeEvaluationContext context,
            MultiTimeframeSeriesContext series,
            IDictionary<string, string> details)
        {
            var h4 = ExtractKeyLevels(series.H4);
            var h1 = ExtractKeyLevels(series.H1);
            var m15 = ExtractKeyLevels(series.M15);

            if (!h4.HasAny && !h1.HasAny && !m15.HasAny)
            {
                details["KeyLevelConfluence"] = "neutral";
                details["KeyLevelBreakout"] = "neutral";
                return 0.5;
            }

            var confluence = CalculateKeyLevelConfluence(h4, h1, m15, details);
            var breakout = CheckBreakoutAlignment(context, series, h4, h1, m15);

            details["KeyLevelConfluence"] = confluence.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            details["KeyLevelBreakout"] = breakout.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            return (confluence + breakout) / 2.0;
        }

        private double CalculateKeyLevelConfluence(
            KeyLevelSummary h4,
            KeyLevelSummary h1,
            KeyLevelSummary m15,
            IDictionary<string, string> details)
        {
            var tolerance = _config.KeyLevelTolerance;
            var resistAlignment = AlignmentScore(new[] { h4.Resistance, h1.Resistance, m15.Resistance }, tolerance);
            var supportAlignment = AlignmentScore(new[] { h4.Support, h1.Support, m15.Support }, tolerance);
            var best = Math.Max(resistAlignment, supportAlignment);
            details["KeyLevelSide"] = resistAlignment >= supportAlignment ? "resistance" : "support";
            return best;
        }

        private static double AlignmentScore(IReadOnlyList<double?> levels, double tolerance)
        {
            var valid = levels.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (valid.Count == 0)
            {
                return 0.0;
            }

            if (valid.Count == 1)
            {
                return 0.5;
            }

            int matches = 0;
            int comparisons = 0;
            for (int i = 0; i < valid.Count; i++)
            {
                for (int j = i + 1; j < valid.Count; j++)
                {
                    comparisons++;
                    if (Math.Abs(valid[i] - valid[j]) <= tolerance)
                    {
                        matches++;
                    }
                }
            }

            if (comparisons == 0)
            {
                return 0.0;
            }

            return Math.Clamp(matches / (double)comparisons, 0.0, 1.0);
        }

        private double CheckBreakoutAlignment(
            MultiTimeframeEvaluationContext context,
            MultiTimeframeSeriesContext series,
            KeyLevelSummary h4,
            KeyLevelSummary h1,
            KeyLevelSummary m15)
        {
            var price = context.MarketData.Mid;
            var tolerance = _config.KeyLevelTolerance;

            var resistance = DominantLevel(new[] { h4.Resistance, h1.Resistance, m15.Resistance }, tolerance);
            var support = DominantLevel(new[] { h4.Support, h1.Support, m15.Support }, tolerance);

            if (!resistance.HasValue && !support.HasValue)
            {
                return 0.5;
            }

            double close = series.H1.LatestClose;
            if (double.IsNaN(close) || close <= 0)
            {
                close = price;
            }

            double slopeH1 = series.H1.PriceSlope;
            double slopeM15 = series.M15.PriceSlope;
            bool bullishMomentum = slopeH1 >= 0 && slopeM15 >= 0;
            bool bearishMomentum = slopeH1 <= 0 && slopeM15 <= 0;

            double score = 0.0;
            if (bullishMomentum && resistance.HasValue)
            {
                score = Math.Max(score, ScoreBreakoutDistance(close - resistance.Value, tolerance));
            }

            if (bearishMomentum && support.HasValue)
            {
                score = Math.Max(score, ScoreBreakoutDistance(support.Value - close, tolerance));
            }

            if (score <= 0 && resistance.HasValue && support.HasValue)
            {
                double range = resistance.Value - support.Value;
                if (range > 0)
                {
                    var normalized = (close - support.Value) / range;
                    score = Math.Clamp(normalized, 0.0, 1.0) * 0.4;
                }
            }

            if (score <= 0)
            {
                score = 0.25;
            }

            var volumeWeight = Math.Min(
                ScoreVolumeSpike(series.H1, _config.VolumeSpikeMultiplierH1),
                ScoreVolumeSpike(series.M15, _config.VolumeSpikeMultiplierM15));
            score *= Math.Clamp(volumeWeight, 0.2, 1.0);
            return Math.Clamp(score, 0.0, 1.0);
        }

        private static double ScoreBreakoutDistance(double distance, double tolerance)
        {
            if (distance <= 0)
            {
                return 0.0;
            }

            if (distance >= tolerance * 1.5)
            {
                return 1.0;
            }

            if (distance >= tolerance)
            {
                return 0.85;
            }

            if (distance >= tolerance * 0.5)
            {
                return 0.6;
            }

            return 0.4;
        }

        private static double? DominantLevel(IEnumerable<double?> levels, double tolerance)
        {
            var candidates = levels.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            return candidates
                .GroupBy(value => candidates.First(refValue => Math.Abs(refValue - value) <= tolerance))
                .OrderByDescending(g => g.Count())
                .First().Key;
        }

        private double CheckVolumeConfirmation(MultiTimeframeSeriesContext series, IDictionary<string, string> details)
        {
            var h1Score = ScoreVolumeSpike(series.H1, _config.VolumeSpikeMultiplierH1);
            var m15Score = ScoreVolumeSpike(series.M15, _config.VolumeSpikeMultiplierM15);
            var trendScore = CheckVolumeTrend(series);

            details["VolumeH1"] = h1Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            details["VolumeM15"] = m15Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            details["VolumeTrend"] = trendScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            return (h1Score + m15Score + trendScore) / 3.0;
        }

        private double ScoreVolumeSpike(TimeframeSeriesData data, double multiplier)
        {
            if (data.VolumeSma <= double.Epsilon)
            {
                return 0.0;
            }

            var ratio = data.LatestVolume / data.VolumeSma;
            if (ratio >= multiplier)
            {
                return 1.0;
            }

            if (ratio >= 1.0)
            {
                return 0.6;
            }

            return 0.3;
        }

        private double CheckVolumeTrend(MultiTimeframeSeriesContext series)
        {
            var h1Slope = MeasureSlope(series.H1.Bars, b => Math.Max(1.0, b.Volume));
            var m15Slope = MeasureSlope(series.M15.Bars, b => Math.Max(1.0, b.Volume));
            double score = 0.0;

            if (h1Slope >= _config.VolumeTrendMinimumSlope)
            {
                score += 0.5;
            }

            if (m15Slope >= _config.VolumeTrendMinimumSlope)
            {
                score += 0.5;
            }

            return score;
        }

        private double CheckMomentumConfirmation(MultiTimeframeSeriesContext series, IDictionary<string, string> details)
        {
            var rsi = CheckRsiAlignment(series);
            var atr = CheckAtrStrength(series);
            var priceMomentum = CheckPriceMomentum(series);

            details["MomentumRSI"] = rsi.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            details["MomentumATR"] = atr.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            details["MomentumPrice"] = priceMomentum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            return (rsi + atr + priceMomentum) / 3.0;
        }

        private double CheckRsiAlignment(MultiTimeframeSeriesContext series)
        {
            var readings = new[] { series.H4.Rsi, series.H1.Rsi, series.M15.Rsi }
                .Where(v => !double.IsNaN(v) && v > 0)
                .ToList();

            if (readings.Count == 0)
            {
                return 0.0;
            }

            var bullish = readings.Count(v => v >= 55);
            var bearish = readings.Count(v => v <= 45);
            var dominant = Math.Max(bullish, bearish);
            if (dominant < _config.RequiredTimeframeAlignment)
            {
                return 0.0;
            }

            return Math.Clamp(dominant / 3.0, 0.0, 1.0);
        }

        private double CheckAtrStrength(MultiTimeframeSeriesContext series)
        {
            var atrs = new[] { series.H1, series.M15 };
            var scores = new List<double>();
            foreach (var tf in atrs)
            {
                if (tf.Atr <= double.Epsilon || double.IsNaN(tf.LatestClose) || tf.LatestClose == 0)
                {
                    continue;
                }

                var ratio = tf.Atr / (tf.LatestClose * 0.002);
                scores.Add(Math.Clamp(ratio, 0.0, 1.0));
            }

            if (scores.Count == 0)
            {
                return 0.0;
            }

            return scores.Average();
        }

        private double CheckPriceMomentum(MultiTimeframeSeriesContext series)
        {
            var slopes = new[] { series.H1.PriceSlope, series.M15.PriceSlope };
            if (slopes.Any(double.IsNaN))
            {
                return 0.0;
            }

            var sameDirection = slopes.All(s => s >= 0) || slopes.All(s => s <= 0);
            var magnitude = slopes.Select(s => Math.Clamp(Math.Abs(s) / (_config.MomentumPriceSlopeThreshold * 3), 0.0, 1.0)).Average();
            return sameDirection ? magnitude : magnitude * 0.5;
        }

        private double CalculateOverallConfidence(ConfirmationResult result)
        {
            return
                (result.TrendAlignment * _weights.Trend) +
                (result.KeyLevelConfirmation * _weights.KeyLevel) +
                (result.VolumeConfirmation * _weights.Volume) +
                (result.MomentumConfirmation * _weights.Momentum);
        }

        private static double CalculatePriceSlope(IReadOnlyList<ModelBar> bars, int lookback)
        {
            if (bars.Count <= lookback)
            {
                return double.NaN;
            }

            var recent = bars[^1].Close;
            var pastIndex = bars.Count - lookback - 1;
            var past = bars[pastIndex].Close;
            return recent - past;
        }

        private static double MeasureSlope(IReadOnlyList<ModelBar> bars, Func<ModelBar, double> selector, int lookback = 5)
        {
            if (bars.Count <= lookback)
            {
                return 0.0;
            }

            var startIndex = bars.Count - lookback - 1;
            var start = selector(bars[startIndex]);
            var end = selector(bars[^1]);
            return (end - start) / Math.Max(Math.Abs(start), 1.0);
        }

        private static double CalculateAtr(IReadOnlyList<ModelBar> bars, int period)
        {
            if (bars.Count < period + 1)
            {
                return 0.0;
            }

            double atr = 0;
            var start = bars.Count - period - 1;
            for (int i = start + 1; i < bars.Count; i++)
            {
                var current = bars[i];
                var previous = bars[i - 1];
                var tr = Math.Max(
                    current.High - current.Low,
                    Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
                atr += tr;
            }

            return atr / period;
        }

        private static double? CalculateEma(IReadOnlyList<ModelBar> bars, int period)
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

        private static double CalculateSma(IReadOnlyList<ModelBar> bars, int period, Func<ModelBar, double> selector)
        {
            period = Math.Min(period, bars.Count);
            if (period <= 0)
            {
                return 0.0;
            }

            double sum = 0;
            for (int i = bars.Count - period; i < bars.Count; i++)
            {
                sum += selector(bars[i]);
            }

            return sum / period;
        }

        private static double? CalculateRsi(IReadOnlyList<ModelBar> bars, int period)
        {
            if (bars.Count <= period)
            {
                return null;
            }

            double gain = 0;
            double loss = 0;
            for (int i = bars.Count - period; i < bars.Count; i++)
            {
                var change = bars[i].Close - bars[i - 1].Close;
                if (change >= 0)
                {
                    gain += change;
                }
                else
                {
                    loss -= change;
                }
            }

            if (loss == 0 && gain == 0)
            {
                return 50;
            }

            if (loss == 0)
            {
                return 100;
            }

            var rs = gain / loss;
            return 100 - (100 / (1 + rs));
        }

        private KeyLevelSummary ExtractKeyLevels(TimeframeSeriesData data)
        {
            var lookback = Math.Min(_config.KeyLevelLookback, data.Bars.Count);
            if (lookback < _config.PivotRadius * 2)
            {
                return KeyLevelSummary.Empty;
            }

            var windowStart = data.Bars.Count - lookback;
            var resistance = FindPivot(data.Bars, windowStart, _config.PivotRadius, true);
            var support = FindPivot(data.Bars, windowStart, _config.PivotRadius, false);
            return new KeyLevelSummary(resistance, support);
        }

        private static double? FindPivot(IReadOnlyList<ModelBar> bars, int startIndex, int radius, bool high)
        {
            for (int i = bars.Count - radius - 1; i >= Math.Max(startIndex, radius); i--)
            {
                bool isPivot = true;
                var candidate = high ? bars[i].High : bars[i].Low;
                for (int offset = 1; offset <= radius; offset++)
                {
                    var compare = high ? bars[i - offset].High : bars[i - offset].Low;
                    var compareForward = high ? bars[i + offset].High : bars[i + offset].Low;
                    if (high)
                    {
                        if (candidate < compare || candidate < compareForward)
                        {
                            isPivot = false;
                            break;
                        }
                    }
                    else
                    {
                        if (candidate > compare || candidate > compareForward)
                        {
                            isPivot = false;
                            break;
                        }
                    }
                }

                if (isPivot)
                {
                    return candidate;
                }
            }

            return null;
        }

        private sealed class MultiTimeframeSeriesContext
        {
            public MultiTimeframeSeriesContext(TimeframeSeriesData h4, TimeframeSeriesData h1, TimeframeSeriesData m15)
            {
                H4 = h4;
                H1 = h1;
                M15 = m15;
            }

            public TimeframeSeriesData H4 { get; }
            public TimeframeSeriesData H1 { get; }
            public TimeframeSeriesData M15 { get; }
        }

        private sealed class TimeframeSeriesData
        {
            public TimeframeSeriesData(ModelTimeFrame timeframe, IReadOnlyList<ModelBar> bars)
            {
                Timeframe = timeframe;
                Bars = bars ?? Array.Empty<ModelBar>();
            }

            public ModelTimeFrame Timeframe { get; }
            public IReadOnlyList<ModelBar> Bars { get; }
            public ModelBar? LatestBar => Bars.Count > 0 ? Bars[^1] : null;
            public double LatestClose => LatestBar?.Close ?? double.NaN;
            public double LatestVolume => LatestBar?.Volume ?? 0.0;
            public double VolumeSma { get; set; }
            public double Atr { get; set; }
            public double Rsi { get; set; }
            public double PriceSlope { get; set; }
        }

        private readonly record struct KeyLevelSummary(double? Resistance, double? Support)
        {
            public static KeyLevelSummary Empty => new(null, null);
            public bool HasAny => Resistance.HasValue || Support.HasValue;
        }

        private readonly record struct WeightProfile(double Trend, double KeyLevel, double Volume, double Momentum)
        {
            public static WeightProfile FromConfig(ConfirmationConfig config)
            {
                var weights = config.ConfirmationWeights;
                return new WeightProfile(
                    weights.GetValueOrDefault("TrendAlignment", 0.35),
                    weights.GetValueOrDefault("KeyLevelConfirmation", 0.30),
                    weights.GetValueOrDefault("VolumeConfirmation", 0.20),
                    weights.GetValueOrDefault("MomentumConfirmation", 0.15));
            }
        }
    }
}
