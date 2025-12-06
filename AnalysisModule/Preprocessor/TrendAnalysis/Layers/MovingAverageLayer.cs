using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using Microsoft.Extensions.Logging;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers
{
    /// <summary>
    /// Layer đánh giá EMA ribbon, vị trí giá và độ dốc để xác định bias động lượng.
    /// </summary>
    public sealed class MovingAverageLayer : BaseLayerCalculator
    {
        private static readonly int[] EmaPeriods = { 9, 20, 50, 200 };
        private const int AtrPeriod = 14;
        private const int SlopeLookback = 20;
        private const double AlignmentTolerance = 0.2;
        private const double AlignmentWeight = 0.4;
        private const double PositionWeight = 0.3;
        private const double SlopeWeight = 0.3;
        private const double ConfluenceBonusPerTf = 5.0;
        private static readonly TimeFrame PrimaryTimeframe = TimeFrame.H1;
        private static readonly TimeFrame[] SecondaryTimeframes = { TimeFrame.H4, TimeFrame.D1 };

        public MovingAverageLayer(ILogger logger) : base(logger)
        {
        }

        public override string LayerName => "MovingAverages";

        public override double CalculateScore(PreprocessorSnapshot snapshot, SnapshotDataAccessor accessor)
        {
            ResetDiagnostics();

            if (snapshot == null)
            {
                AddWarning("Snapshot null, trả về điểm trung tính.");
                return 50.0;
            }

            if (accessor == null)
            {
                AddWarning("SnapshotDataAccessor null, không thể đọc dữ liệu EMA.");
                return 50.0;
            }

            var primaryScore = CalculateTimeframeScore(accessor, PrimaryTimeframe, includeDiagnostics: true);
            var confluenceBonus = CalculateMultiTimeframeConfluence(accessor, primaryScore, includeDiagnostics: true);
            var finalScore = Math.Clamp(primaryScore + confluenceBonus, 0, 100);

            AddConfirmation($"Điểm MovingAverage: base={primaryScore:F1}, bonus={confluenceBonus:F1}, final={finalScore:F1}.");
            return finalScore;
        }

        private double CalculateTimeframeScore(
            SnapshotDataAccessor accessor,
            TimeFrame timeframe,
            bool includeDiagnostics)
        {
            var alignmentScore = CalculateEmaAlignmentScore(accessor, timeframe, includeDiagnostics);
            var positionScore = CalculatePricePositionScore(accessor, timeframe, includeDiagnostics);
            var slopeScore = CalculateEmaSlopeScore(accessor, timeframe, includeDiagnostics);

            var weighted = alignmentScore * AlignmentWeight +
                           positionScore * PositionWeight +
                           slopeScore * SlopeWeight;

            var normalized = Math.Clamp(weighted * 100, 0, 100);

            if (includeDiagnostics)
            {
                AddConfirmation($"[{timeframe}] Alignment={alignmentScore:F2}, Position={positionScore:F2}, Slope={slopeScore:F2} → {normalized:F1}");
            }

            return normalized;
        }

        private double CalculateEmaAlignmentScore(
            SnapshotDataAccessor accessor,
            TimeFrame timeframe,
            bool includeDiagnostics)
        {
            var emaValues = new Dictionary<int, double>();
            foreach (var period in EmaPeriods)
            {
                var value = accessor.GetIndicatorValue($"EMA_{period}", timeframe);
                if (!value.HasValue)
                {
                    if (includeDiagnostics)
                    {
                        AddWarning($"[{timeframe}] Thiếu EMA_{period}.");
                    }

                    return 0.5;
                }

                emaValues[period] = value.Value;
            }

            var bullishConditions = 0;
            var bearishConditions = 0;

            UpdateAlignmentCounts(CompareWithTolerance(emaValues[9], emaValues[20]), ref bullishConditions, ref bearishConditions);
            UpdateAlignmentCounts(CompareWithTolerance(emaValues[20], emaValues[50]), ref bullishConditions, ref bearishConditions);
            UpdateAlignmentCounts(CompareWithTolerance(emaValues[50], emaValues[200]), ref bullishConditions, ref bearishConditions);

            if (bullishConditions == 3)
            {
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] EMA9 > EMA20 > EMA50 > EMA200 (bullish).");
                }

                return 1.0;
            }

            if (bearishConditions == 3)
            {
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] EMA9 < EMA20 < EMA50 < EMA200 (bearish).");
                }

                return 0.0;
            }

            if (bullishConditions > bearishConditions)
            {
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] EMA alignment bullish {bullishConditions}/3.");
                }

                return 0.5 + bullishConditions * 0.166;
            }

            if (bearishConditions > bullishConditions)
            {
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] EMA alignment bearish {bearishConditions}/3.");
                }

                return 0.5 - bearishConditions * 0.166;
            }

            if (includeDiagnostics)
            {
                AddConfirmation($"[{timeframe}] EMA alignment mixed (early reversal / range).");
            }

            return 0.5;
        }

        private double CalculatePricePositionScore(
            SnapshotDataAccessor accessor,
            TimeFrame timeframe,
            bool includeDiagnostics)
        {
            var latestPrice = accessor.GetLatestPrice(timeframe);
            if (!latestPrice.HasValue)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Không đọc được giá mới nhất.");
                }

                return 0.5;
            }

            var aboveCount = 0;
            var totalCount = 0;
            foreach (var period in EmaPeriods)
            {
                var ema = accessor.GetIndicatorValue($"EMA_{period}", timeframe);
                if (!ema.HasValue)
                {
                    continue;
                }

                totalCount++;
                if (latestPrice.Value > ema.Value)
                {
                    aboveCount++;
                }
            }

            if (totalCount == 0)
            {
                return 0.5;
            }

            var ratio = (double)aboveCount / totalCount;

            double score;
            if (ratio == 1.0)
            {
                score = 1.0;
            }
            else if (ratio == 0.0)
            {
                score = 0.0;
            }
            else if (ratio >= 0.75)
            {
                score = 0.75;
            }
            else if (ratio <= 0.25)
            {
                score = 0.25;
            }
            else
            {
                score = 0.5;
            }

            if (includeDiagnostics)
            {
                AddConfirmation($"[{timeframe}] Giá nằm trên {aboveCount}/{totalCount} EMA → score={score:F2}.");
            }

            return score;
        }

        private double CalculateEmaSlopeScore(
            SnapshotDataAccessor accessor,
            TimeFrame timeframe,
            bool includeDiagnostics)
        {
            var bars = accessor.GetBars(timeframe, SlopeLookback + 5);
            if (bars == null || bars.Count < SlopeLookback + 1)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Không đủ bar để tính slope ({bars?.Count ?? 0}).");
                }

                return 0.5;
            }

            var atr = accessor.GetIndicatorValue($"ATR_{AtrPeriod}", timeframe);
            if (!atr.HasValue || atr.Value <= 0)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Thiếu ATR nên slope trả về trung tính.");
                }

                return 0.5;
            }

            var startBar = bars[^SlopeLookback];
            var endBar = bars[^1];
            var priceChange = endBar.Close - startBar.Close;
            var priceChangePct = priceChange / Math.Max(1e-6, startBar.Close);
            var atrNormalized = atr.Value / Math.Max(1e-6, startBar.Close);
            if (atrNormalized <= 0)
            {
                return 0.5;
            }

            var normalizedSlope = priceChangePct / atrNormalized;
            var score = Math.Clamp(0.5 + normalizedSlope * 0.5, 0.0, 1.0);

            if (includeDiagnostics)
            {
                AddConfirmation($"[{timeframe}] Slope={normalizedSlope:F3}, Δ%={priceChangePct:P2}, ATR={atr:F2} → score={score:F2}.");
            }

            return score;
        }

        private double CalculateMultiTimeframeConfluence(
            SnapshotDataAccessor accessor,
            double primaryScore,
            bool includeDiagnostics)
        {
            double bonusSum = 0;
            var contributing = 0;
            foreach (var timeframe in SecondaryTimeframes)
            {
                try
                {
                    var tfScore = CalculateTimeframeScore(accessor, timeframe, includeDiagnostics: false);
                    if (tfScore >= 60 || tfScore <= 40)
                    {
                        var sameDirection = (tfScore > 50) == (primaryScore > 50);
                        if (sameDirection)
                        {
                            var bonus = tfScore > 50 ? ConfluenceBonusPerTf : -ConfluenceBonusPerTf;
                            bonusSum += bonus;
                            contributing++;
                            if (includeDiagnostics)
                            {
                                AddConfirmation($"Confluence {timeframe}: {bonus:+0.0;-0.0} (score={tfScore:F1}).");
                            }
                        }
                        else if (includeDiagnostics)
                        {
                            AddWarning($"{timeframe} divergence (score={tfScore:F1}).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (includeDiagnostics)
                    {
                        AddWarning($"Không tính được confluence {timeframe}: {ex.Message}.");
                    }
                }
            }

            if (contributing == 0)
            {
                return 0;
            }

            return bonusSum / contributing;
        }

        private static void UpdateAlignmentCounts(int comparison, ref int bullishCount, ref int bearishCount)
        {
            if (comparison > 0)
            {
                bullishCount++;
            }
            else if (comparison < 0)
            {
                bearishCount++;
            }
        }

        private static int CompareWithTolerance(double left, double right)
        {
            if (Math.Abs(left - right) <= AlignmentTolerance)
            {
                return 0;
            }

            return left > right ? 1 : -1;
        }
    }
}
