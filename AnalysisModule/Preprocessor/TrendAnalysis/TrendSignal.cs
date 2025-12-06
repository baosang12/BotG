using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.TrendAnalysis
{
    /// <summary>
    /// Tín hiệu xu hướng chuẩn hóa được phát ra từ MarketTrendAnalyzer.
    /// </summary>
    public sealed class TrendSignal
    {
        public Guid SignalId { get; init; } = Guid.NewGuid();
        public TrendDirection Direction { get; init; }
        public TrendStrength Strength { get; init; }
        public double Score { get; init; }
        public double Confidence { get; init; }
        public double StructureScore { get; init; }
        public double MovingAverageScore { get; init; }
        public double MomentumScore { get; init; }
        public double PatternScore { get; init; }
        public IReadOnlyList<string> Confirmations { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> PatternFlags { get; init; } = Array.Empty<string>();
        public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
        public TimeFrame PrimaryTimeFrame { get; init; } = TimeFrame.H1;
        public IReadOnlyDictionary<TimeFrame, double> TimeFrameScores { get; init; } = new Dictionary<TimeFrame, double>();
        public string Version { get; init; } = "1.0";

        public bool IsValidForTrading(TimeSpan? maxAge = null, double minConfidence = 0.7, double minScore = 40.0, int minConfirmations = 3)
        {
            var freshness = maxAge ?? TimeSpan.FromSeconds(30);
            return Confidence >= minConfidence
                   && Score >= minScore
                   && Confirmations.Count >= minConfirmations
                   && (DateTime.UtcNow - GeneratedAtUtc) <= freshness;
        }
    }

    public enum TrendDirection
    {
        StrongBullish,
        Bullish,
        NeutralBullish,
        Range,
        NeutralBearish,
        Bearish,
        StrongBearish
    }

    public enum TrendStrength
    {
        VeryStrong,
        Strong,
        Moderate,
        Weak,
        VeryWeak
    }
}
