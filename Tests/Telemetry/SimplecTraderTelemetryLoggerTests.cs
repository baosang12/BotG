using System;
using System.IO;
using System.Linq;
using AnalysisModule.Telemetry;
using Xunit;

namespace BotG.Tests.PatternLayerTelemetry
{
    public sealed class SimplecTraderTelemetryLoggerTests : IDisposable
    {
        private readonly string _logDir;

        public SimplecTraderTelemetryLoggerTests()
        {
            _logDir = Path.Combine(Path.GetTempPath(), $"PatternLayerTests_{Guid.NewGuid():N}");
        }

        [Fact]
        public void Constructor_CreatesDirectoryAndHeader()
        {
            string file;
            using (var logger = new SimplecTraderTelemetryLogger(_logDir))
            {
                Assert.True(Directory.Exists(_logDir));
                file = Directory.GetFiles(_logDir, "*.csv").Single();
                logger.Flush();
            }

            var header = File.ReadLines(file).FirstOrDefault();
            Assert.NotNull(header);
            Assert.StartsWith("TimestampUTC", header, StringComparison.Ordinal);
            var headerColumns = header.Split(',');
            Assert.Contains("AccumulationScore", headerColumns);
            Assert.Contains("AccumulationFlags", headerColumns);
            Assert.Contains("PhaseDetected", headerColumns);
            Assert.Contains("MarketStructureScore", headerColumns);
            Assert.Contains("MarketStructureTrendDirection", headerColumns);
            Assert.Contains("VolumeProfileScore", headerColumns);
            Assert.Contains("VolumeProfilePOC", headerColumns);
            Assert.Contains("VolumeProfileVAHigh", headerColumns);
            Assert.Contains("VolumeProfileVALow", headerColumns);
            Assert.Contains("VolumeProfileFlags", headerColumns);
            Assert.Contains("HVNCount", headerColumns);
            Assert.Contains("LVNCount", headerColumns);
            Assert.Contains("VolumeConcentration", headerColumns);
            Assert.Contains("TelemetryVersion", headerColumns);
        }

        [Fact]
        public void LogPatternAnalysis_WritesSingleRow()
        {
            string file;
            using (var logger = new SimplecTraderTelemetryLogger(_logDir))
            {
                logger.LogPatternAnalysis(
                    "EURUSD",
                    "M5",
                    80,
                    75,
                    70,
                    true,
                    false,
                    false,
                    0.5,
                    marketCondition: "trend",
                    rsi: 52,
                    volumeRatio: 1.1,
                    candleSize: 0.3,
                    accumulationScore: 55,
                    accumulationConfidence: 0.45,
                    accumulationFlags: "Distribution",
                    phaseDetected: "Distribution",
                    marketStructureScore: 62.8,
                    marketStructureState: "Downtrend",
                    marketStructureTrendDirection: -1,
                    marketStructureBreakDetected: false,
                    marketStructureSwingPoints: 5,
                    lastSwingHigh: 1.105,
                    lastSwingLow: 1.097,
                    volumeProfileScore: 68.2,
                    volumeProfilePoc: 1.101,
                    volumeProfileVaHigh: 1.107,
                    volumeProfileVaLow: 1.099,
                    volumeProfileFlags: "HVN|NearPOC",
                    hvnCount: 2,
                    lvnCount: 1,
                    volumeConcentration: 0.73,
                    telemetryVersion: 4);
                logger.Flush();
                file = Directory.GetFiles(_logDir, "*.csv").Single();
            }

            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            Assert.Contains("EURUSD", lines[1]);
            Assert.Contains("80.00", lines[1]);
            Assert.Contains("Downtrend", lines[1]);
            Assert.Contains("68.20", lines[1]);
            Assert.Contains("HVN|NearPOC", lines[1]);
            var columns = lines[0].Split(',');
            Assert.Contains("AccumulationScore", columns);
            Assert.Contains("AccumulationConfidence", columns);
            Assert.Contains("AccumulationFlags", columns);
            Assert.Contains("PhaseDetected", columns);
            Assert.Contains("MarketStructureScore", columns);
            Assert.Contains("MarketStructureTrendDirection", columns);
            Assert.Contains("MarketStructureBreakDetected", columns);
            Assert.Contains("MarketStructureSwingPoints", columns);
            Assert.Contains("LastSwingHigh", columns);
            Assert.Contains("LastSwingLow", columns);
            Assert.Contains("VolumeProfileScore", columns);
            Assert.Contains("VolumeProfilePOC", columns);
            Assert.Contains("VolumeProfileFlags", columns);
            Assert.Contains("HVNCount", columns);
            Assert.Contains("VolumeConcentration", columns);
            Assert.Contains("TelemetryVersion", columns);
        }

        [Fact]
        public void LogPatternAnalysis_RespectsSampleRate()
        {
            string file;
            (long total, long filtered, long queue) stats;
            using (var logger = new SimplecTraderTelemetryLogger(_logDir, sampleRate: 2))
            {
                logger.LogPatternAnalysis("EURUSD", "M5", 80, 70, 60, false, false, false, 0.1);
                logger.LogPatternAnalysis("EURUSD", "M5", 82, 72, 62, false, false, false, 0.1);
                logger.LogPatternAnalysis("EURUSD", "M5", 84, 74, 64, false, false, false, 0.1);
                stats = logger.GetStatistics();
                logger.Flush();
                file = Directory.GetFiles(_logDir, "*.csv").Single();
            }

            Assert.Equal(3, stats.total);

            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length); // header + only every second entry
        }

        [Fact]
        public void Dispose_ReleasesFileHandle()
        {
            var logger = new SimplecTraderTelemetryLogger(_logDir);
            var file = Directory.GetFiles(_logDir, "*.csv").Single();
            logger.Dispose();

            var exception = Record.Exception(() => File.AppendAllText(file, "#test"));
            Assert.Null(exception);
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
                    // bỏ qua lỗi cleanup trong môi trường test
                }
            }
        }
    }
}
