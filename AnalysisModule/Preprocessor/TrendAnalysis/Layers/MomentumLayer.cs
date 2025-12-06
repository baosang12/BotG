using System;
using System.Linq;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using Microsoft.Extensions.Logging;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers
{
    /// <summary>
    /// Layer đánh giá động lượng dựa trên RSI, ATR, ROC và volume đa timeframe.
    /// </summary>
    public sealed class MomentumLayer : BaseLayerCalculator
    {
        private const int RsiPeriod = 14;
        private const int AtrPeriod = 14;
        private const int RocPeriod = 14;
        private const int VolumeSmaPeriod = 20;

        private const double RsiOverbought = 70.0;
        private const double RsiOversold = 30.0;
        private const double RsiNeutralHigh = 55.0;
        private const double RsiNeutralLow = 45.0;

        private static readonly TimeFrame PrimaryTimeframe = TimeFrame.H1;
        private static readonly TimeFrame[] SecondaryTimeframes = { TimeFrame.H4, TimeFrame.D1 };

        public MomentumLayer(ILogger logger) : base(logger)
        {
        }

        public override string LayerName => "Momentum";

        public override double CalculateScore(PreprocessorSnapshot snapshot, SnapshotDataAccessor accessor)
        {
            ResetDiagnostics();

            if (snapshot == null)
            {
                AddWarning("Snapshot null, trả về 50.");
                return 50.0;
            }

            if (accessor == null)
            {
                AddWarning("SnapshotDataAccessor null, không thể đánh giá Momentum.");
                return 50.0;
            }

            var rsiScore = CalculateRsiScore(accessor, PrimaryTimeframe, includeDiagnostics: true);
            var atrScore = CalculateAtrScore(accessor, PrimaryTimeframe, includeDiagnostics: true);
            var rocScore = CalculateRocScore(accessor, PrimaryTimeframe, includeDiagnostics: true);
            var volumeScore = CalculateVolumeScore(accessor, PrimaryTimeframe, includeDiagnostics: true);

            var weightedScore = rsiScore * 0.4 + atrScore * 0.25 + rocScore * 0.25 + volumeScore * 0.1;
            var confluenceBonus = CalculateMomentumConfluence(accessor, PrimaryTimeframe, weightedScore);
            var finalScore = Math.Clamp(weightedScore + confluenceBonus, 0, 100);

            AddConfirmation($"Điểm Momentum: base={weightedScore:F1}, bonus={confluenceBonus:+0.0;-0.0;0.0}, final={finalScore:F1}.");
            return finalScore;
        }

        private double CalculateRsiScore(SnapshotDataAccessor accessor, TimeFrame timeframe, bool includeDiagnostics)
        {
            var rsiValue = accessor.GetIndicatorValue($"RSI_{RsiPeriod}", timeframe);
            if (!rsiValue.HasValue)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Thiếu RSI_{RsiPeriod} nên trả về 50.");
                }

                return 50.0;
            }

            var value = rsiValue.Value;
            double score;

            if (value <= RsiOversold)
            {
                score = 30.0 * (value / Math.Max(1e-6, RsiOversold));
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] RSI {value:F1} nằm vùng quá bán → bullish potential.");
                }
            }
            else if (value <= RsiNeutralLow)
            {
                score = 30.0 + 15.0 * ((value - RsiOversold) / (RsiNeutralLow - RsiOversold));
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] RSI {value:F1} đang bearish momentum.");
                }
            }
            else if (value <= RsiNeutralHigh)
            {
                score = 45.0 + 10.0 * ((value - RsiNeutralLow) / (RsiNeutralHigh - RsiNeutralLow));
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] RSI {value:F1} trung tính.");
                }
            }
            else if (value <= RsiOverbought)
            {
                score = 55.0 + 15.0 * ((value - RsiNeutralHigh) / (RsiOverbought - RsiNeutralHigh));
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] RSI {value:F1} bullish momentum.");
                }
            }
            else
            {
                score = 70.0 + 30.0 * ((value - RsiOverbought) / (100.0 - RsiOverbought));
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] RSI {value:F1} quá mua → rủi ro đảo chiều.");
                }
            }

            return Math.Clamp(score, 0, 100);
        }

        private double CalculateAtrScore(SnapshotDataAccessor accessor, TimeFrame timeframe, bool includeDiagnostics)
        {
            var atrValue = accessor.GetIndicatorValue($"ATR_{AtrPeriod}", timeframe);
            if (!atrValue.HasValue || atrValue.Value <= 0)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Thiếu ATR_{AtrPeriod} hoặc giá trị <=0.");
                }

                return 50.0;
            }

            var latestPrice = accessor.GetLatestPrice(timeframe);
            if (!latestPrice.HasValue || latestPrice.Value <= 0)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Không đọc được giá để chuẩn hóa ATR.");
                }

                return 50.0;
            }

            var normalizedAtr = atrValue.Value / latestPrice.Value * 100.0;
            double score;

            if (normalizedAtr < 0.5)
            {
                score = 50.0;
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] ATR {normalizedAtr:F2}% rất thấp → thị trường lặng.");
                }
            }
            else if (normalizedAtr < 1.0)
            {
                score = 40.0 + 20.0 * ((normalizedAtr - 0.5) / 0.5);
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] ATR {normalizedAtr:F2}% thấp.");
                }
            }
            else if (normalizedAtr < 2.0)
            {
                score = 50.0 + 10.0 * ((normalizedAtr - 1.0) / 1.0);
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] ATR {normalizedAtr:F2}% bình thường.");
                }
            }
            else if (normalizedAtr < 3.0)
            {
                score = 60.0 + 10.0 * ((normalizedAtr - 2.0) / 1.0);
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] ATR {normalizedAtr:F2}% cao → breakout potential.");
                }
            }
            else
            {
                score = 70.0 + Math.Min(10.0, (normalizedAtr - 3.0) * 5.0);
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] ATR {normalizedAtr:F2}% rất cao → xu hướng mạnh.");
                }
            }

            return Math.Clamp(score, 0, 100);
        }

        private double CalculateRocScore(SnapshotDataAccessor accessor, TimeFrame timeframe, bool includeDiagnostics)
        {
            var rocIndicator = accessor.GetIndicatorValue($"ROC_{RocPeriod}", timeframe);
            if (rocIndicator.HasValue)
            {
                var scoreFromIndicator = Math.Clamp(50.0 + rocIndicator.Value * 5.0, 0, 100);
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] ROC {rocIndicator.Value:F2}% → score {scoreFromIndicator:F1}.");
                }

                return scoreFromIndicator;
            }

            var bars = accessor.GetBars(timeframe, RocPeriod + 5);
            if (bars == null || bars.Count < RocPeriod + 1)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Không đủ bar để tính ROC (count={bars?.Count ?? 0}).");
                }

                return 50.0;
            }

            var currentBar = bars[^1];
            var pastBar = bars[^(RocPeriod + 1)];
            if (Math.Abs(pastBar.Close) < 1e-6)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Past close quá nhỏ → ROC mặc định 50.");
                }

                return 50.0;
            }

            var rocPercent = (currentBar.Close - pastBar.Close) / pastBar.Close * 100.0;
            double calculatedScore;

            if (rocPercent > 10)
            {
                calculatedScore = 80.0;
            }
            else if (rocPercent > 5)
            {
                calculatedScore = 65.0;
            }
            else if (rocPercent > 2)
            {
                calculatedScore = 57.5;
            }
            else if (rocPercent > -2)
            {
                calculatedScore = 50.0 + rocPercent * 2.5;
            }
            else if (rocPercent > -5)
            {
                calculatedScore = 42.5;
            }
            else if (rocPercent > -10)
            {
                calculatedScore = 35.0;
            }
            else
            {
                calculatedScore = 20.0;
            }

            if (includeDiagnostics)
            {
                AddConfirmation($"[{timeframe}] ROC (tính tay) {rocPercent:F2}% → score {calculatedScore:F1}.");
            }

            return Math.Clamp(calculatedScore, 0, 100);
        }

        private double CalculateVolumeScore(SnapshotDataAccessor accessor, TimeFrame timeframe, bool includeDiagnostics)
        {
            var volumeIndicator = accessor.GetIndicatorValue($"Volume_SMA_{VolumeSmaPeriod}", timeframe);
            if (volumeIndicator.HasValue)
            {
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] Có Volume_SMA_{VolumeSmaPeriod}, giữ điểm trung tính 50.");
                }

                return 50.0;
            }

            var bars = accessor.GetBars(timeframe, VolumeSmaPeriod + 5);
            if (bars == null || bars.Count < VolumeSmaPeriod)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Không đủ bar để phân tích volume.");
                }

                return 50.0;
            }

            var lookbackBars = bars.TakeLast(VolumeSmaPeriod).ToArray();
            var averageVolume = lookbackBars.Average(b => b.Volume);
            if (averageVolume <= 0)
            {
                if (includeDiagnostics)
                {
                    AddWarning($"[{timeframe}] Volume trung bình không khả dụng.");
                }

                return 50.0;
            }

            var currentVolume = lookbackBars[^1].Volume;
            var ratio = currentVolume / averageVolume;
            double score;

            if (ratio < 0.5)
            {
                score = 40.0;
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] Volume {ratio:F2}x average → thiếu lực.");
                }
            }
            else if (ratio < 0.8)
            {
                score = 45.0;
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] Volume {ratio:F2}x average → thấp.");
                }
            }
            else if (ratio < 1.2)
            {
                score = 50.0;
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] Volume {ratio:F2}x average → bình thường.");
                }
            }
            else if (ratio < 2.0)
            {
                score = 60.0 + 10.0 * ((ratio - 1.2) / 0.8);
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] Volume {ratio:F2}x average → xác nhận xu hướng.");
                }
            }
            else
            {
                score = 70.0 + Math.Min(10.0, (ratio - 2.0) * 5.0);
                if (includeDiagnostics)
                {
                    AddConfirmation($"[{timeframe}] Volume {ratio:F2}x average → lực cực mạnh.");
                }
            }

            return Math.Clamp(score, 0, 100);
        }

        private double CalculateMomentumConfluence(
            SnapshotDataAccessor accessor,
            TimeFrame primaryTimeframe,
            double primaryScore)
        {
            double bonusSum = 0;
            var contributing = 0;

            foreach (var timeframe in SecondaryTimeframes)
            {
                try
                {
                    var tfScore = CalculateHigherTimeframeScore(accessor, timeframe);
                    if (tfScore is <= 40 or >= 60)
                    {
                        var sameDirection = (tfScore > 50) == (primaryScore > 50);
                        if (sameDirection)
                        {
                            var bonus = (tfScore - 50) / 50.0 * 3.0; // ±3 điểm
                            bonusSum += bonus;
                            contributing++;
                            AddConfirmation($"[{timeframe}] Confluence cùng hướng, bonus {bonus:+0.0;-0.0;0.0}.");
                        }
                        else
                        {
                            AddWarning($"[{timeframe}] Momentum trái chiều (score={tfScore:F1}).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddWarning($"[{timeframe}] Lỗi tính confluence: {ex.Message}.");
                }
            }

            return contributing == 0 ? 0 : bonusSum / contributing;
        }

        private double CalculateHigherTimeframeScore(SnapshotDataAccessor accessor, TimeFrame timeframe)
        {
            var rsi = CalculateRsiScore(accessor, timeframe, includeDiagnostics: false);
            var atr = CalculateAtrScore(accessor, timeframe, includeDiagnostics: false);
            var roc = CalculateRocScore(accessor, timeframe, includeDiagnostics: false);
            return rsi * 0.4 + atr * 0.25 + roc * 0.35;
        }
    }
}
