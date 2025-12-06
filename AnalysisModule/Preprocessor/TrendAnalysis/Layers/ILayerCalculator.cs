using System.Collections.Generic;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers
{
    /// <summary>
    /// Chuẩn hóa cách mỗi layer tính điểm xu hướng.
    /// </summary>
    public interface ILayerCalculator
    {
        string LayerName { get; }
        bool IsEnabled { get; }

        double CalculateScore(PreprocessorSnapshot snapshot, SnapshotDataAccessor accessor);
        void UpdateConfig(TrendAnalyzerConfig config);
        IReadOnlyDictionary<string, object> GetDiagnostics();
    }
}
