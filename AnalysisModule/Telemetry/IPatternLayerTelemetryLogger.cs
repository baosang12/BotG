using System;

namespace AnalysisModule.Telemetry
{
    /// <summary>
    /// Giao diện trừu tượng hoá việc ghi telemetry cho PatternLayer khi chạy trên cTrader.
    /// </summary>
    public interface IPatternLayerTelemetryLogger : IDisposable
    {
        /// <summary>
        /// Ghi log cho một lần phân tích pattern.
        /// </summary>
        void LogPatternAnalysis(
            string symbol,
            string timeframe,
            double patternScore,
            double liquidityScore,
            double breakoutScore,
            bool liquidityGrabFlag,
            bool cleanBreakoutFlag,
            bool failedBreakoutFlag,
            double processingTimeMs = 0,
            string marketCondition = "",
            double rsi = 0,
            double volumeRatio = 0,
            double candleSize = 0,
            double accumulationScore = 0,
            double accumulationConfidence = 0,
            string accumulationFlags = "",
            string phaseDetected = "",
            double marketStructureScore = 0,
            string marketStructureState = "",
            int marketStructureTrendDirection = 0,
            bool marketStructureBreakDetected = false,
            int marketStructureSwingPoints = 0,
            double lastSwingHigh = 0,
            double lastSwingLow = 0,
            double volumeProfileScore = 0,
            double volumeProfilePoc = 0,
            double volumeProfileVaHigh = 0,
            double volumeProfileVaLow = 0,
            string volumeProfileFlags = "",
            int hvnCount = 0,
            int lvnCount = 0,
            double volumeConcentration = 0,
            int telemetryVersion = 4);

        /// <summary>
        /// Ép flush dữ liệu còn lại xuống ổ đĩa.
        /// </summary>
        void Flush();

        /// <summary>
        /// Trả về thống kê nội bộ (tổng log, số log bị bỏ qua, chiều dài queue).
        /// </summary>
        (long TotalEntries, long FilteredEntries, long QueueLength) GetStatistics();
    }
}
