using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;

namespace AnalysisModule.Preprocessor.TrendAnalysis
{
    /// <summary>
    /// Giao diện cho MarketTrendAnalyzer, chịu trách nhiệm đọc snapshot và phát TrendSignal.
    /// </summary>
    public interface IMarketTrendAnalyzer : IDisposable
    {
        /// <summary>
        /// Phân tích snapshot hiện tại và trả về TrendSignal (hoặc null nếu bị vô hiệu hóa).
        /// </summary>
        TrendSignal? Analyze(PreprocessorSnapshot snapshot);

        /// <summary>
        /// Khởi tạo analyzer với config ban đầu (gọi một lần lúc bootstrap).
        /// </summary>
        void Initialize(TrendAnalyzerConfig config);

        /// <summary>
        /// Cho phép cập nhật config nóng khi file runtime thay đổi.
        /// </summary>
        void UpdateConfig(TrendAnalyzerConfig config);

        /// <summary>
        /// Cho biết analyzer đang được bật qua config/feature flag.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Thời lượng xử lý lần phân tích gần nhất, phục vụ telemetry.
        /// </summary>
        TimeSpan LastAnalysisDuration { get; }

        /// <summary>
        /// Bắn sự kiện khi hoàn tất phân tích để downstream có thể log hoặc đo lường.
        /// </summary>
        event EventHandler<TrendAnalysisCompletedEventArgs>? AnalysisCompleted;
    }

    /// <summary>
    /// Payload tiêu chuẩn cho sự kiện hoàn tất phân tích xu hướng.
    /// </summary>
    public sealed class TrendAnalysisCompletedEventArgs : EventArgs
    {
        public TrendSignal? Signal { get; init; }
        public TimeSpan AnalysisDuration { get; init; }
        public IReadOnlyDictionary<string, double> LayerScores { get; init; } = new Dictionary<string, double>();
        public Exception? Error { get; init; }
    }
}
