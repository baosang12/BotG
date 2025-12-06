using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using AnalysisModule.Telemetry;
using BotG.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotG.Tests.Integration
{
    [Collection("PatternLayerDebuggerTests")]
    public sealed class PatternLayerTelemetryIntegrationTests : IDisposable
    {
        private readonly List<string> _tempDirectories = new();

        [Fact]
        public void PatternTelemetry_WhenLoggingEnabled_WritesCsvEntry()
        {
            var logDir = CreateTempDirectory();
            var config = TestPatternLayerData.CreateTelemetryConfig(enableLogging: true, enableDebug: false, logDirectory: logDir);

            using var telemetry = new TrendAnalysisTelemetry(NullLogger<TrendAnalysisTelemetry>.Instance);
            telemetry.ConfigurePatternTelemetry(config);

            telemetry.StartPatternAnalysis("EURUSD", "M5");
            telemetry.LogPatternLayerResults("EURUSD", "M5", TestPatternLayerData.CreateTelemetrySnapshot(), 0.82);

            var csvFile = WaitForCsvFile(logDir);
            var lines = ReadAllLinesWithRetry(csvFile);

            Assert.True(lines.Length >= 3, "CSV phải có header, bản ghi hệ thống và ít nhất một dòng dữ liệu");
            var csvContent = string.Join(Environment.NewLine, lines);
            Assert.Contains("AccumulationScore", csvContent);
            Assert.Contains("AccumulationFlags", csvContent);
            Assert.Contains("PhaseDetected", csvContent);
            Assert.Contains("MarketStructureScore", csvContent);
            Assert.Contains("MarketStructureTrendDirection", csvContent);
            var dataLine = lines.FirstOrDefault(line => line.Contains("EURUSD", StringComparison.Ordinal));
            Assert.False(string.IsNullOrEmpty(dataLine));
            Assert.Contains("Accumulation", dataLine!);
        }

        [Fact]
        public void PatternTelemetry_WithDebugMode_WritesDebuggerOutput()
        {
            var logDir = CreateTempDirectory();
            var config = TestPatternLayerData.CreateTelemetryConfig(enableLogging: false, enableDebug: true, logDirectory: logDir);

            using var telemetry = new TrendAnalysisTelemetry(NullLogger<TrendAnalysisTelemetry>.Instance);
            telemetry.ConfigurePatternTelemetry(config);

            var originalConsole = Console.Out;
            using var capture = new StringWriter();
            Console.SetOut(capture);
            try
            {
                telemetry.StartPatternAnalysis("GBPUSD", "H1");
                telemetry.LogPatternLayerResults("GBPUSD", "H1", TestPatternLayerData.CreateTelemetrySnapshot(), 0.65);
            }
            finally
            {
                Console.SetOut(originalConsole);
            }

            var output = capture.ToString();
            Assert.Contains("PATTERN LAYER ANALYSIS", output);
            Assert.Contains("GBPUSD H1", output);
            Assert.Contains("LiquidityGrab", output);
        }

        [Fact]
        public void PatternTelemetry_SampleRate_FiltersEntries()
        {
            var logDir = CreateTempDirectory();
            var config = TestPatternLayerData.CreateTelemetryConfig(enableLogging: true, enableDebug: false, logDirectory: logDir, sampleRate: 2);

            using var telemetry = new TrendAnalysisTelemetry(NullLogger<TrendAnalysisTelemetry>.Instance);
            telemetry.ConfigurePatternTelemetry(config);

            for (var i = 0; i < 3; i++)
            {
                telemetry.StartPatternAnalysis($"SYMBOL{i}", "M15");
                telemetry.LogPatternLayerResults($"SYMBOL{i}", "M15", TestPatternLayerData.CreateTelemetrySnapshot(patternScore: 70 + i), 0.5);
                Thread.Sleep(10);
            }

            var csvFile = WaitForCsvFile(logDir);
            var lines = ReadAllLinesWithRetry(csvFile);

            Assert.Equal(3, lines.Length);
            var compactContent = string.Join(Environment.NewLine, lines);
            Assert.Contains("AccumulationScore", compactContent);
            Assert.Contains("MarketStructureScore", compactContent);
            var symbolLines = lines.Where(line => line.Contains("SYMBOL", StringComparison.Ordinal)).ToArray();
            Assert.Single(symbolLines);
            Assert.Contains("SYMBOL1", symbolLines[0]);
        }

        [Fact]
        public void PatternTelemetry_WhenLoggerThrows_DoesNotBubbleException()
        {
            var logDir = CreateTempDirectory();
            var config = TestPatternLayerData.CreateTelemetryConfig(enableLogging: true, enableDebug: false, logDirectory: logDir);

            using var telemetry = new TrendAnalysisTelemetry(NullLogger<TrendAnalysisTelemetry>.Instance);
            telemetry.ConfigurePatternTelemetry(config);

            var failingLogger = new ThrowingPatternLogger();
            var field = typeof(TrendAnalysisTelemetry).GetField("_patternLogger", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(telemetry, failingLogger);

            telemetry.StartPatternAnalysis("USDJPY", "H4");
            var exception = Record.Exception(() =>
                telemetry.LogPatternLayerResults("USDJPY", "H4", TestPatternLayerData.CreateTelemetrySnapshot(), 0.51));

            Assert.Null(exception);
            Assert.True(failingLogger.InvocationCount > 0);
        }

        private string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), $"PatternLayerTelemetry_{Guid.NewGuid():N}");
            _tempDirectories.Add(path);
            return path;
        }

        private static string WaitForCsvFile(string directory, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (Directory.Exists(directory))
                {
                    var file = Directory.GetFiles(directory, "*.csv").FirstOrDefault();
                    if (!string.IsNullOrEmpty(file))
                    {
                        try
                        {
                            if (new FileInfo(file).Length > 0)
                            {
                                return file;
                            }
                        }
                        catch (IOException)
                        {
                            // tệp đang được ghi, thử lại
                        }
                    }
                }

                Thread.Sleep(20);
            }

            throw new TimeoutException($"Không tìm thấy file CSV trong thư mục {directory}");
        }

        private static string[] ReadAllLinesWithRetry(string filePath, int attempts = 25)
        {
            for (var i = 0; i < attempts; i++)
            {
                try
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var lines = new List<string>();
                    while (!reader.EndOfStream)
                    {
                        lines.Add(reader.ReadLine() ?? string.Empty);
                    }

                    if (lines.Count > 0)
                    {
                        return lines.ToArray();
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                }
            }

            throw new IOException($"Không thể đọc file telemetry {filePath} vì đang bị khoá.");
        }

        public void Dispose()
        {
            foreach (var dir in _tempDirectories)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                    // bỏ qua lỗi cleanup
                }
            }
        }

        private sealed class ThrowingPatternLogger : IPatternLayerTelemetryLogger
        {
            public int InvocationCount { get; private set; }

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
                InvocationCount++;
                throw new InvalidOperationException("Logger failure");
            }

            public void Flush()
            {
            }

            public (long TotalEntries, long FilteredEntries, long QueueLength) GetStatistics()
            {
                return (0, 0, 0);
            }

            public void Dispose()
            {
            }
        }
    }
}
