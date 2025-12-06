using System;
using System.Collections.Generic;
using System.Linq;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using Microsoft.Extensions.Logging;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers
{
    /// <summary>
    /// Layer đánh giá market structure dựa trên swing HH/HL, LH/LL và break of structure từ dữ liệu bar thực.
    /// </summary>
    public sealed class StructureLayer : BaseLayerCalculator
    {
        private const TimeFrame PrimaryTimeFrame = TimeFrame.H1;
        private const int MinimumBars = 30;
        private const double BosThreshold = 0.001; // ~0.1%

        public StructureLayer(ILogger logger) : base(logger)
        {
        }

        public override string LayerName => "Structure";

        public override double CalculateScore(PreprocessorSnapshot snapshot, SnapshotDataAccessor accessor)
        {
            ResetDiagnostics();

            if (snapshot == null)
            {
                AddWarning("Snapshot null, trả về điểm trung lập.");
                return 50.0;
            }

            if (accessor == null)
            {
                AddWarning("SnapshotDataAccessor null, không thể phân tích structure.");
                return 50.0;
            }

            var bars = accessor.GetBars(PrimaryTimeFrame, 120);
            if (bars == null || bars.Count < MinimumBars)
            {
                AddWarning($"Không đủ dữ liệu {PrimaryTimeFrame} (Count={bars?.Count ?? 0}).");
                return 50.0;
            }

            var swingPoints = DetectSwingPoints(bars);
            if (swingPoints.Count < 4)
            {
                AddWarning("Không đủ swing point để đánh giá HH/HL hoặc LH/LL.");
                return 50.0;
            }

            var structureScore = AnalyzeMarketStructure(swingPoints);
            var bosScore = DetectBreakOfStructure(swingPoints, bars[^1]);

            var finalScore = Math.Clamp(structureScore * 0.7 + bosScore * 0.3, 0, 100);
            AddConfirmation($"StructureLayer tổng hợp base={structureScore:F1}, bos={bosScore:F1} → final={finalScore:F1}.");

            if (finalScore >= 70)
            {
                AddConfirmation("Cấu trúc bullish mạnh.");
            }
            else if (finalScore >= 55)
            {
                AddConfirmation("Cấu trúc bullish nhẹ.");
            }
            else if (finalScore <= 30)
            {
                AddConfirmation("Cấu trúc bearish mạnh.");
            }
            else if (finalScore <= 45)
            {
                AddConfirmation("Cấu trúc bearish nhẹ.");
            }
            else
            {
                AddWarning("Cấu trúc gần range hoặc chưa rõ ràng.");
            }

            return finalScore;
        }

        private static List<SwingPoint> DetectSwingPoints(IReadOnlyList<Bar> bars)
        {
            var swingPoints = new List<SwingPoint>();
            if (bars.Count < 5)
            {
                return swingPoints;
            }

            for (var i = 2; i < bars.Count - 2; i++)
            {
                var bar = bars[i];

                if (IsSwingHigh(bars, i))
                {
                    swingPoints.Add(new SwingPoint(i, bar.High, SwingPointType.High, bar.OpenTimeUtc, CalculateSwingStrength(bars, i, true)));
                }

                if (IsSwingLow(bars, i))
                {
                    swingPoints.Add(new SwingPoint(i, bar.Low, SwingPointType.Low, bar.OpenTimeUtc, CalculateSwingStrength(bars, i, false)));
                }
            }

            return swingPoints.OrderBy(sp => sp.Index).ToList();
        }

        private static bool IsSwingHigh(IReadOnlyList<Bar> bars, int index)
        {
            var high = bars[index].High;
            return high > bars[index - 1].High &&
                   high > bars[index - 2].High &&
                   high > bars[index + 1].High &&
                   high > bars[index + 2].High;
        }

        private static bool IsSwingLow(IReadOnlyList<Bar> bars, int index)
        {
            var low = bars[index].Low;
            return low < bars[index - 1].Low &&
                   low < bars[index - 2].Low &&
                   low < bars[index + 1].Low &&
                   low < bars[index + 2].Low;
        }

        private static double CalculateSwingStrength(IReadOnlyList<Bar> bars, int index, bool isHigh)
        {
            var lookback = Math.Min(5, index);
            var lookforward = Math.Min(5, bars.Count - index - 1);
            double strength = 0;

            for (var i = 1; i <= lookback; i++)
            {
                strength += isHigh
                    ? bars[index].High - bars[index - i].High
                    : bars[index - i].Low - bars[index].Low;
            }

            for (var i = 1; i <= lookforward; i++)
            {
                strength += isHigh
                    ? bars[index].High - bars[index + i].High
                    : bars[index + i].Low - bars[index].Low;
            }

            return strength / Math.Max(1, lookback + lookforward);
        }

        private double AnalyzeMarketStructure(IReadOnlyList<SwingPoint> swingPoints)
        {
            var highs = swingPoints.Where(sp => sp.Type == SwingPointType.High).Select(sp => sp.Price).ToList();
            var lows = swingPoints.Where(sp => sp.Type == SwingPointType.Low).Select(sp => sp.Price).ToList();

            var highSlope = MeasureSlope(highs);
            var lowSlope = MeasureSlope(lows);
            AddConfirmation($"Độ dốc swing: high={highSlope:F2}, low={lowSlope:F2}.");
            const double slopeThreshold = 0.3;

            var bullish = highSlope > slopeThreshold && lowSlope > slopeThreshold;
            var bearish = highSlope < -slopeThreshold && lowSlope < -slopeThreshold;

            if (bullish && !bearish)
            {
                AddConfirmation("HH/HL gần nhất đang tăng dần → bullish.");
                return 80.0;
            }

            if (!bullish && bearish)
            {
                AddConfirmation("LH/LL gần nhất giảm dần → bearish.");
                return 20.0;
            }

            if (bullish && bearish)
            {
                AddWarning("Swing highs/lows trái chiều → range.");
                return 50.0;
            }

            AddWarning("Không thấy chuỗi HH/HL hoặc LH/LL hợp lệ.");
            return 50.0;
        }

        private static double MeasureSlope(IReadOnlyList<double> values, int window = 4)
        {
            if (values.Count < 2)
            {
                return 0;
            }

            var count = Math.Min(window, values.Count);
            var start = values.Count - count;
            return values[^1] - values[start];
        }

        private double DetectBreakOfStructure(IReadOnlyList<SwingPoint> swingPoints, Bar latestBar)
        {
            if (swingPoints.Count < 2)
            {
                return 50.0;
            }

            var recentHigh = swingPoints
                .Where(sp => sp.Type == SwingPointType.High)
                .OrderByDescending(sp => sp.Index)
                .FirstOrDefault();

            var recentLow = swingPoints
                .Where(sp => sp.Type == SwingPointType.Low)
                .OrderByDescending(sp => sp.Index)
                .FirstOrDefault();

            if (recentHigh != null && latestBar.Close > recentHigh.Price * (1 + BosThreshold))
            {
                AddConfirmation($"BOS bullish: Close {latestBar.Close:F5} > swing high {recentHigh.Price:F5}.");
                return 80.0;
            }

            if (recentLow != null && latestBar.Close < recentLow.Price * (1 - BosThreshold))
            {
                AddConfirmation($"BOS bearish: Close {latestBar.Close:F5} < swing low {recentLow.Price:F5}.");
                return 20.0;
            }

            AddWarning("Chưa có break of structure gần nhất.");
            return 50.0;
        }

        private sealed record SwingPoint(int Index, double Price, SwingPointType Type, DateTime Time, double Strength);

        private enum SwingPointType
        {
            High,
            Low
        }
    }
}
