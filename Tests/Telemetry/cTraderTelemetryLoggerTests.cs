using System;
using System.IO;
using System.Linq;
using System.Threading;
using AnalysisModule.Telemetry;
using Xunit;

namespace BotG.Tests.PatternLayerTelemetry
{
    public sealed class cTraderTelemetryLoggerTests : IDisposable
    {
        private readonly string _logDir;

        public cTraderTelemetryLoggerTests()
        {
            _logDir = Path.Combine(Path.GetTempPath(), $"PatternLayerCTrader_{Guid.NewGuid():N}");
        }

        [Fact]
        public void LogPatternAnalysis_WritesCsv()
        {
            string file;
            using (var logger = new cTraderTelemetryLogger(
                       _logDir,
                       sampleRate: 1,
                       minScoreThreshold: 0,
                       maxScoreThreshold: 100,
                       enableConsoleOutput: false))
            {
                logger.LogPatternAnalysis(
                    "GBPUSD",
                    "H1",
                    78.5,
                    70.1,
                    74.2,
                    liquidityGrabFlag: true,
                    cleanBreakoutFlag: false,
                    failedBreakoutFlag: false,
                    processingTimeMs: 0.42,
                    marketCondition: "trend",
                    rsi: 55,
                    volumeRatio: 1.2,
                    candleSize: 0.8,
                    accumulationScore: 64,
                    accumulationConfidence: 0.6,
                    accumulationFlags: "Accumulation|RangeCompression",
                    phaseDetected: "Accumulation",
                    marketStructureScore: 71.2,
                    marketStructureState: "Uptrend",
                    marketStructureTrendDirection: 1,
                    marketStructureBreakDetected: true,
                    marketStructureSwingPoints: 6,
                    lastSwingHigh: 1.2785,
                    lastSwingLow: 1.2633,
                    volumeProfileScore: 66.4,
                    volumeProfilePoc: 1.2711,
                    volumeProfileVaHigh: 1.279,
                    volumeProfileVaLow: 1.268,
                    volumeProfileFlags: "ValueAreaBreakUp|NearPOC",
                    hvnCount: 3,
                    lvnCount: 1,
                    volumeConcentration: 0.71,
                    telemetryVersion: 4);

                file = Directory.GetFiles(_logDir, "*.csv").Single();
                Assert.True(SpinWait.SpinUntil(() => FileHasLines(file, 2), 2000), "Telemetry entry not written in time");
                logger.Flush();
            }

            var lines = File.ReadAllLines(file);
            Assert.True(lines.Length >= 2);
            var csvContent = string.Join(Environment.NewLine, lines);
            Assert.Contains("AccumulationScore", csvContent);
            Assert.Contains("PhaseDetected", csvContent);
            Assert.Contains("MarketStructureScore", csvContent);
            Assert.Contains("MarketStructureTrendDirection", csvContent);
            Assert.Contains("VolumeProfileScore", csvContent);
            Assert.Contains("VolumeProfilePOC", csvContent);
            Assert.Contains("VolumeProfileFlags", csvContent);
            Assert.Contains("HVNCount", csvContent);
            Assert.Contains("TelemetryVersion", csvContent);
            Assert.Contains("Uptrend", csvContent);
            var dataLine = lines.FirstOrDefault(line => line.Contains("GBPUSD", StringComparison.Ordinal));
            Assert.False(string.IsNullOrEmpty(dataLine));
            Assert.Contains("GBPUSD", dataLine!);
            Assert.Contains("66.40", dataLine);
            Assert.Contains("ValueAreaBreakUp|NearPOC", dataLine);
        }

        [Fact]
        public void SampleRateAndThreshold_FilterEntries()
        {
            using var logger = new cTraderTelemetryLogger(
                _logDir,
                sampleRate: 3,
                minScoreThreshold: 40,
                maxScoreThreshold: 90,
                enableConsoleOutput: false);

            logger.LogPatternAnalysis("EURUSD", "M5", 30, 0, 0, false, false, false);
            logger.LogPatternAnalysis("EURUSD", "M5", 60, 0, 0, false, false, false);
            logger.LogPatternAnalysis("EURUSD", "M5", 70, 0, 0, false, false, false);

            var stats = logger.GetStatistics();
            Assert.Equal(3, stats.TotalEntries);
            Assert.True(stats.FilteredEntries >= 2);
        }

        [Fact]
        public void Dispose_ShutsDownGracefully()
        {
            var logger = new cTraderTelemetryLogger(_logDir, enableConsoleOutput: false);
            logger.LogPatternAnalysis("USDJPY", "M1", 55, 50, 45, false, true, false);
            var exception = Record.Exception(() => logger.Dispose());
            Assert.Null(exception);
        }

        private static bool FileHasLines(string filePath, int minimumLines)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return false;
                }

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var count = 0;
                while (!reader.EndOfStream && count < minimumLines)
                {
                    reader.ReadLine();
                    count++;
                }

                return count >= minimumLines;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_logDir))
            {
                try
                {
                    Directory.Delete(_logDir, recursive: true);
                }
                catch
                {
                    // bỏ qua lỗi cleanup
                }
            }
        }
    }
}
