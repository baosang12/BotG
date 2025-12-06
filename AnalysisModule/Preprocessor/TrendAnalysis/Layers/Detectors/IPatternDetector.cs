using AnalysisModule.Preprocessor.TrendAnalysis;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors
{
    /// <summary>
    /// Giao diện chuẩn cho các detector trong PatternLayer.
    /// </summary>
    public interface IPatternDetector
    {
        string Name { get; }

        /// <summary>
        /// Trọng số đóng góp vào PatternLayer (0-1).
        /// </summary>
        double Weight { get; set; }

        /// <summary>
        /// Cho phép bật/tắt detector qua config.
        /// </summary>
        bool IsEnabled { get; set; }

        /// <summary>
        /// Trả về kết quả phân tích pattern dựa trên dữ liệu snapshot.
        /// </summary>
        PatternDetectionResult Detect(SnapshotDataAccessor accessor);
    }
}
