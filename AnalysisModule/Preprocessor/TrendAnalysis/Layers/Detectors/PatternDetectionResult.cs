using System;
using System.Collections.Generic;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors
{
    /// <summary>
    /// Kết quả trả về từ mỗi pattern detector.
    /// </summary>
    public sealed class PatternDetectionResult
    {
        public double Score { get; set; } = 50.0;

        public double Confidence { get; set; } = 0.5;

        public Dictionary<string, object> Diagnostics { get; set; }
            = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public List<string> Flags { get; set; } = new();

        public static PatternDetectionResult Neutral()
        {
            return new PatternDetectionResult();
        }
    }
}
