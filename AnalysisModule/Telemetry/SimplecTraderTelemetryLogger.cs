using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AnalysisModule.Telemetry
{
    /// <summary>
    /// Phiên bản logger đơn giản, ghi thẳng CSV đồng bộ cho mục đích unit test hoặc debug nhẹ.
    /// </summary>
    public sealed class SimplecTraderTelemetryLogger : IPatternLayerTelemetryLogger
    {
        private const string Header = "TimestampUTC,Symbol,Timeframe,PatternScore,LiquidityScore,BreakoutScore,LiquidityGrabFlag,CleanBreakoutFlag,FailedBreakoutFlag,ProcessingTimeMs,MarketCondition,RSI,VolumeRatio,CandleSize,AccumulationScore,AccumulationFlags,AccumulationConfidence,PhaseDetected,MarketStructureScore,MarketStructureState,MarketStructureTrendDirection,MarketStructureBreakDetected,MarketStructureSwingPoints,LastSwingHigh,LastSwingLow,VolumeProfileScore,VolumeProfilePOC,VolumeProfileVAHigh,VolumeProfileVALow,VolumeProfileFlags,HVNCount,LVNCount,VolumeConcentration,TelemetryVersion";

        private readonly string _logDirectory;
        private readonly int _sampleRate;
        private readonly bool _enableConsoleOutput;
        private readonly bool _logProcessingTime;
        private readonly object _lock = new();
        private StreamWriter? _writer;
        private int _counter;
        private long _totalEntries;
        private long _filteredEntries;

        public SimplecTraderTelemetryLogger(
            string logDirectory,
            int sampleRate = 1,
            bool enableConsoleOutput = false,
            bool logProcessingTime = true)
        {
            if (string.IsNullOrWhiteSpace(logDirectory))
            {
                throw new ArgumentException("Log directory không hợp lệ", nameof(logDirectory));
            }

            _logDirectory = Path.GetFullPath(logDirectory);
            _sampleRate = Math.Max(1, sampleRate);
            _enableConsoleOutput = enableConsoleOutput;
            _logProcessingTime = logProcessingTime;

            Directory.CreateDirectory(_logDirectory);
            InitializeWriter();
        }

        private void InitializeWriter()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var filePath = Path.Combine(_logDirectory, $"PatternLayer_{timestamp}.csv");
            _writer = new StreamWriter(filePath, append: true, Encoding.UTF8)
            {
                AutoFlush = true
            };
            _writer.WriteLine(Header);
        }

        public void LogPatternAnalysis(
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
            int telemetryVersion = 4)
        {
            lock (_lock)
            {
                _totalEntries++;
                _counter++;
                if (_counter % _sampleRate != 0)
                {
                    _filteredEntries++;
                    return;
                }

                if (_writer == null)
                {
                    return;
                }

                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3:F2},{4:F2},{5:F2},{6},{7},{8},{9:F3},{10},{11:F2},{12:F2},{13:F2},{14:F2},{15},{16:F2},{17},{18:F2},{19},{20},{21},{22},{23:F5},{24:F5},{25:F2},{26:F5},{27:F5},{28:F5},{29},{30},{31},{32:F4},{33}",
                    timestamp,
                    symbol ?? "UNKNOWN",
                    timeframe ?? "UNKNOWN",
                    patternScore,
                    liquidityScore,
                    breakoutScore,
                    liquidityGrabFlag,
                    cleanBreakoutFlag,
                    failedBreakoutFlag,
                    _logProcessingTime ? processingTimeMs : 0,
                    marketCondition ?? string.Empty,
                    rsi,
                    volumeRatio,
                    candleSize,
                    accumulationScore,
                    accumulationFlags ?? string.Empty,
                    accumulationConfidence,
                    phaseDetected ?? string.Empty,
                    marketStructureScore,
                    marketStructureState ?? string.Empty,
                    marketStructureTrendDirection,
                    marketStructureBreakDetected,
                    marketStructureSwingPoints,
                    lastSwingHigh,
                    lastSwingLow,
                    volumeProfileScore,
                    volumeProfilePoc,
                    volumeProfileVaHigh,
                    volumeProfileVaLow,
                    volumeProfileFlags ?? string.Empty,
                    hvnCount,
                    lvnCount,
                    volumeConcentration,
                    telemetryVersion);

                _writer.WriteLine(line);

                if (_enableConsoleOutput && (liquidityGrabFlag || cleanBreakoutFlag || failedBreakoutFlag))
                {
                    Console.WriteLine(
                        $"[{DateTime.UtcNow:HH:mm:ss.fff}] {symbol ?? "UNKNOWN"} {timeframe ?? "UNKNOWN"} | Score={patternScore:F1} | L={liquidityGrabFlag} C={cleanBreakoutFlag} F={failedBreakoutFlag}");
                }
            }
        }

        public void Flush()
        {
            lock (_lock)
            {
                _writer?.Flush();
            }
        }

        public (long TotalEntries, long FilteredEntries, long QueueLength) GetStatistics()
        {
            lock (_lock)
            {
                return (_totalEntries, _filteredEntries, 0);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
