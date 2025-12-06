using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnalysisModule.Telemetry
{
    /// <summary>
    /// Logger đầy đủ cho PatternLayer trên cTrader: ghi CSV, console và xử lý bất đồng bộ để không chặn main thread.
    /// </summary>
    public sealed class cTraderTelemetryLogger : IPatternLayerTelemetryLogger
    {
        private const string CsvHeader = "TimestampUTC,Symbol,Timeframe,PatternScore,LiquidityScore,BreakoutScore,LiquidityGrabFlag,CleanBreakoutFlag,FailedBreakoutFlag,ProcessingTimeMs,MarketCondition,RSI,VolumeRatio,CandleSize,AccumulationScore,AccumulationFlags,AccumulationConfidence,PhaseDetected,MarketStructureScore,MarketStructureState,MarketStructureTrendDirection,MarketStructureBreakDetected,MarketStructureSwingPoints,LastSwingHigh,LastSwingLow,VolumeProfileScore,VolumeProfilePOC,VolumeProfileVAHigh,VolumeProfileVALow,VolumeProfileFlags,HVNCount,LVNCount,VolumeConcentration,TelemetryVersion";
        private const string FilePrefix = "PatternLayer_";
        private const string FileExtension = ".csv";
        private readonly string _logDirectory;
        private readonly int _sampleRate;
        private readonly double _minScoreThreshold;
        private readonly double _maxScoreThreshold;
        private readonly bool _enableConsoleOutput;
        private readonly bool _logProcessingTime;
        private readonly BlockingCollection<LogEntry> _queue;
        private readonly CancellationTokenSource _cts;
        private readonly Task _processorTask;
        private readonly object _fileLock = new();
        private StreamWriter? _writer;
        private string? _currentFilePath;
        private DateTime _currentLogDate;
        private long _totalEntries;
        private long _filteredEntries;
        private bool _disposed;

        private sealed class LogEntry
        {
            public DateTime Timestamp { get; init; }
            public string Symbol { get; init; } = string.Empty;
            public string Timeframe { get; init; } = string.Empty;
            public double PatternScore { get; init; }
            public double LiquidityScore { get; init; }
            public double BreakoutScore { get; init; }
            public bool LiquidityGrabFlag { get; init; }
            public bool CleanBreakoutFlag { get; init; }
            public bool FailedBreakoutFlag { get; init; }
            public double ProcessingTimeMs { get; init; }
            public string MarketCondition { get; init; } = string.Empty;
            public double Rsi { get; init; }
            public double VolumeRatio { get; init; }
            public double CandleSize { get; init; }
            public double AccumulationScore { get; init; }
            public string AccumulationFlags { get; init; } = string.Empty;
            public double AccumulationConfidence { get; init; }
            public string PhaseDetected { get; init; } = string.Empty;
            public double MarketStructureScore { get; init; }
            public string MarketStructureState { get; init; } = string.Empty;
            public int MarketStructureTrendDirection { get; init; }
            public bool MarketStructureBreakDetected { get; init; }
            public int MarketStructureSwingPoints { get; init; }
            public double LastSwingHigh { get; init; }
            public double LastSwingLow { get; init; }
            public double VolumeProfileScore { get; init; }
            public double VolumeProfilePoc { get; init; }
            public double VolumeProfileVaHigh { get; init; }
            public double VolumeProfileVaLow { get; init; }
            public string VolumeProfileFlags { get; init; } = string.Empty;
            public int HvnCount { get; init; }
            public int LvnCount { get; init; }
            public double VolumeConcentration { get; init; }
            public int TelemetryVersion { get; init; }
        }

        public cTraderTelemetryLogger(
            string logDirectory,
            int sampleRate = 1,
            double minScoreThreshold = 0.0,
            double maxScoreThreshold = 100.0,
            bool enableConsoleOutput = false,
            bool logProcessingTime = true,
            int queueCapacity = 2048)
        {
            if (string.IsNullOrWhiteSpace(logDirectory))
            {
                throw new ArgumentException("Log directory không hợp lệ", nameof(logDirectory));
            }

            if (queueCapacity < 128)
            {
                queueCapacity = 128;
            }

            _logDirectory = Path.GetFullPath(logDirectory);
            _sampleRate = Math.Max(1, sampleRate);
            _minScoreThreshold = Math.Clamp(minScoreThreshold, 0.0, 100.0);
            _maxScoreThreshold = Math.Clamp(Math.Max(minScoreThreshold, maxScoreThreshold), 0.0, 100.0);
            _enableConsoleOutput = enableConsoleOutput;
            _logProcessingTime = logProcessingTime;
            _currentLogDate = DateTime.UtcNow.Date;

            Directory.CreateDirectory(_logDirectory);
            CreateNewCsvFile();

            _cts = new CancellationTokenSource();
            _queue = new BlockingCollection<LogEntry>(queueCapacity);
            _processorTask = Task.Run(() => ProcessQueueAsync(_cts.Token));

            LogSystemStartup();
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
            if (_disposed)
            {
                return;
            }

            var totalCount = Interlocked.Increment(ref _totalEntries);
            if (totalCount % _sampleRate != 0)
            {
                Interlocked.Increment(ref _filteredEntries);
                return;
            }

            if (patternScore < _minScoreThreshold || patternScore > _maxScoreThreshold)
            {
                Interlocked.Increment(ref _filteredEntries);
                return;
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Symbol = symbol ?? "UNKNOWN",
                Timeframe = timeframe ?? "UNKNOWN",
                PatternScore = patternScore,
                LiquidityScore = liquidityScore,
                BreakoutScore = breakoutScore,
                LiquidityGrabFlag = liquidityGrabFlag,
                CleanBreakoutFlag = cleanBreakoutFlag,
                FailedBreakoutFlag = failedBreakoutFlag,
                ProcessingTimeMs = _logProcessingTime ? processingTimeMs : 0,
                MarketCondition = marketCondition ?? string.Empty,
                Rsi = rsi,
                VolumeRatio = volumeRatio,
                CandleSize = candleSize,
                AccumulationScore = accumulationScore,
                AccumulationFlags = accumulationFlags ?? string.Empty,
                AccumulationConfidence = accumulationConfidence,
                PhaseDetected = phaseDetected ?? string.Empty,
                MarketStructureScore = marketStructureScore,
                MarketStructureState = marketStructureState ?? string.Empty,
                MarketStructureTrendDirection = marketStructureTrendDirection,
                MarketStructureBreakDetected = marketStructureBreakDetected,
                MarketStructureSwingPoints = marketStructureSwingPoints,
                LastSwingHigh = lastSwingHigh,
                LastSwingLow = lastSwingLow,
                VolumeProfileScore = volumeProfileScore,
                VolumeProfilePoc = volumeProfilePoc,
                VolumeProfileVaHigh = volumeProfileVaHigh,
                VolumeProfileVaLow = volumeProfileVaLow,
                VolumeProfileFlags = volumeProfileFlags ?? string.Empty,
                HvnCount = hvnCount,
                LvnCount = lvnCount,
                VolumeConcentration = volumeConcentration,
                TelemetryVersion = telemetryVersion
            };

            if (!_queue.TryAdd(entry))
            {
                Interlocked.Increment(ref _filteredEntries);
                if (_enableConsoleOutput)
                {
                    Console.WriteLine($"[TELEMETRY WARNING] Queue đầy, bỏ qua log của {entry.Symbol}");
                }
            }
        }

        private void ProcessQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                foreach (var entry in _queue.GetConsumingEnumerable(cancellationToken))
                {
                    try
                    {
                        CheckFileRotation();
                        var csvLine = FormatCsvLine(entry);
                        lock (_fileLock)
                        {
                            _writer?.WriteLine(csvLine);
                        }

                        if (_enableConsoleOutput && (entry.LiquidityGrabFlag || entry.CleanBreakoutFlag || entry.FailedBreakoutFlag))
                        {
                            Console.WriteLine(FormatConsoleLine(entry));
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_enableConsoleOutput)
                        {
                            Console.WriteLine($"[TELEMETRY ERROR] Không ghi được log: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // bình thường khi dispose
            }
            catch (Exception ex)
            {
                if (_enableConsoleOutput)
                {
                    Console.WriteLine($"[TELEMETRY ERROR] Processor dừng đột ngột: {ex.Message}");
                }
            }
        }

        private void CreateNewCsvFile()
        {
            lock (_fileLock)
            {
                _writer?.Dispose();
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                _currentFilePath = Path.Combine(_logDirectory, $"{FilePrefix}{timestamp}{FileExtension}");
                var fileAlreadyExists = File.Exists(_currentFilePath);
                _writer = new StreamWriter(_currentFilePath, append: true, Encoding.UTF8)
                {
                    AutoFlush = true
                };
                if (!fileAlreadyExists)
                {
                    _writer.WriteLine(CsvHeader);
                }
            }

            if (_enableConsoleOutput)
            {
                Console.WriteLine($"[TELEMETRY] Tạo file log: {_currentFilePath}");
            }
        }

        private void CheckFileRotation()
        {
            var today = DateTime.UtcNow.Date;
            if (today == _currentLogDate)
            {
                return;
            }

            lock (_fileLock)
            {
                if (today != _currentLogDate)
                {
                    _currentLogDate = today;
                    CreateNewCsvFile();
                }
            }
        }

        private static string FormatCsvLine(LogEntry entry)
        {
            return string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff},{1},{2},{3:F2},{4:F2},{5:F2},{6},{7},{8},{9:F3},{10},{11:F2},{12:F2},{13:F2},{14:F2},{15},{16:F2},{17},{18:F2},{19},{20},{21},{22},{23:F5},{24:F5},{25:F2},{26:F5},{27:F5},{28:F5},{29},{30},{31},{32:F4},{33}",
            entry.Timestamp,
            Escape(entry.Symbol),
            Escape(entry.Timeframe),
            entry.PatternScore,
            entry.LiquidityScore,
            entry.BreakoutScore,
            entry.LiquidityGrabFlag,
            entry.CleanBreakoutFlag,
            entry.FailedBreakoutFlag,
            entry.ProcessingTimeMs,
            Escape(entry.MarketCondition),
            entry.Rsi,
            entry.VolumeRatio,
            entry.CandleSize,
            entry.AccumulationScore,
            Escape(entry.AccumulationFlags),
            entry.AccumulationConfidence,
            Escape(entry.PhaseDetected),
            entry.MarketStructureScore,
            Escape(entry.MarketStructureState),
            entry.MarketStructureTrendDirection,
            entry.MarketStructureBreakDetected,
            entry.MarketStructureSwingPoints,
            entry.LastSwingHigh,
            entry.LastSwingLow,
            entry.VolumeProfileScore,
            entry.VolumeProfilePoc,
            entry.VolumeProfileVaHigh,
            entry.VolumeProfileVaLow,
            Escape(entry.VolumeProfileFlags),
            entry.HvnCount,
            entry.LvnCount,
            entry.VolumeConcentration,
            entry.TelemetryVersion);
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        private static string FormatConsoleLine(LogEntry entry)
        {
            var flagBuilder = new StringBuilder();
            if (entry.LiquidityGrabFlag)
            {
                flagBuilder.Append("LIQ ");
            }
            if (entry.CleanBreakoutFlag)
            {
                flagBuilder.Append("CLEAN ");
            }
            if (entry.FailedBreakoutFlag)
            {
                flagBuilder.Append("FAIL ");
            }

            return $"[{entry.Timestamp:HH:mm:ss.fff}] {entry.Symbol} {entry.Timeframe} | Score={entry.PatternScore:F1} | {flagBuilder.ToString().Trim()}" +
                   (entry.ProcessingTimeMs > 0 ? $" | {entry.ProcessingTimeMs:F1}ms" : string.Empty);
        }

        private void LogSystemStartup()
        {
            if (_enableConsoleOutput)
            {
                Console.WriteLine($"[TELEMETRY] Logger khởi tạo tại {_logDirectory}");
                Console.WriteLine($"[TELEMETRY] SampleRate=1/{_sampleRate}, Threshold={_minScoreThreshold:F1}-{_maxScoreThreshold:F1}");
            }

            lock (_fileLock)
            {
                if (_writer != null)
                {
                    var columns = new string[34];
                    columns[0] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    columns[1] = "SYSTEM";
                    columns[2] = "STARTUP";
                    columns[10] = "Logger initialized";
                    columns[^1] = "4";
                    _writer.WriteLine(string.Join(',', columns));
                }
            }
        }

        public (long TotalEntries, long FilteredEntries, long QueueLength) GetStatistics()
        {
            return (
                Interlocked.Read(ref _totalEntries),
                Interlocked.Read(ref _filteredEntries),
                _queue.Count);
        }

        public void Flush()
        {
            lock (_fileLock)
            {
                _writer?.Flush();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _queue.CompleteAdding();
                _cts.Cancel();
                _processorTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // bỏ qua lỗi khi shutdown
            }
            finally
            {
                _cts.Dispose();
                _queue.Dispose();
                lock (_fileLock)
                {
                    _writer?.Dispose();
                    _writer = null;
                }

                if (_enableConsoleOutput)
                {
                    Console.WriteLine($"[TELEMETRY] Logger dừng. Total={_totalEntries}, Filtered={_filteredEntries}");
                }
            }
        }
    }
}
