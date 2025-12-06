using System;

namespace AnalysisModule.Preprocessor.TrendAnalysis
{
    /// <summary>
    /// Giao diện chuẩn hóa để bridge chiến lược có thể lấy và phát tín hiệu xu hướng.
    /// </summary>
    public interface ITrendAnalysisBridge
    {
        /// <summary>
        /// Lấy tín hiệu xu hướng hiện tại (hoặc null nếu chưa có/đã hết hạn).
        /// </summary>
        TrendSignal? GetCurrentTrend();

        /// <summary>
        /// Phát hành tín hiệu mới để cache cho chiến lược tiêu thụ.
        /// </summary>
        void PublishTrendSignal(TrendSignal signal);

        /// <summary>
        /// Cho biết TrendAnalyzer đang được bật bằng config/feature flag hay không.
        /// </summary>
        bool IsTrendAnalysisEnabled { get; }

        /// <summary>
        /// Dấu thời gian lần cuối tín hiệu xu hướng được cập nhật.
        /// </summary>
        DateTime LastTrendUpdateTime { get; }
    }
}
