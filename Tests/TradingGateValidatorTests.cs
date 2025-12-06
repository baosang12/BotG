using Xunit;
using BotG.Preflight;
using Telemetry;
using System;
using System.IO;

namespace BotG.Tests
{
    public class TradingGateValidatorTests
    {
        [Fact]
        public void ValidateOrThrow_ThrowsWhen_TradingEnabledInLiveMode()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var cfg = new TelemetryConfig
            {
                Ops = new OpsConfig { EnableTrading = true },
                Mode = "live",
                LogPath = tempDir
            };

            try
            {
                // Act & Assert
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    TradingGateValidator.ValidateOrThrow(cfg));

                Assert.Contains("TRADING_VIOLATION", ex.Message);
                Assert.Contains("paper", ex.Message);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ValidateOrThrow_PassesWhen_TradingDisabled()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var cfg = new TelemetryConfig
            {
                Ops = new OpsConfig { EnableTrading = false },
                Mode = "live",
                LogPath = tempDir
            };

            try
            {
                // Act & Assert (should not throw)
                var exception = Record.Exception(() =>
                    TradingGateValidator.ValidateOrThrow(cfg));

                Assert.Null(exception);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ValidateOrThrow_ThrowsWhen_StopSentinelExists()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var stopFile = Path.Combine(tempDir, "RUN_STOP");
            File.WriteAllText(stopFile, "STOP");

            var cfg = new TelemetryConfig
            {
                Ops = new OpsConfig { EnableTrading = true },
                Mode = "paper",
                LogPath = tempDir
            };

            try
            {
                // Act & Assert
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    TradingGateValidator.ValidateOrThrow(cfg));

                Assert.Contains("SENTINEL_VIOLATION", ex.Message);
            }
            finally
            {
                if (File.Exists(stopFile))
                    File.Delete(stopFile);
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ValidateOrThrow_ThrowsWhen_NoRecentPreflight()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var cfg = new TelemetryConfig
            {
                Ops = new OpsConfig { EnableTrading = true },
                Mode = "paper",
                LogPath = tempDir
            };

            try
            {
                // Act & Assert
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    TradingGateValidator.ValidateOrThrow(cfg));

                Assert.Contains("PREFLIGHT_VIOLATION", ex.Message);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ValidateOrThrow_PassesWhen_RecentPreflightExists()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            var preflightDir = Path.Combine(tempDir, "preflight");
            Directory.CreateDirectory(preflightDir);

            // Create recent preflight file
            var wireproofFile = Path.Combine(preflightDir, "executor_wireproof.json");
            File.WriteAllText(wireproofFile, "{\"ok\": true}");

            var cfg = new TelemetryConfig
            {
                Ops = new OpsConfig { EnableTrading = true },
                Mode = "paper",
                LogPath = tempDir
            };

            try
            {
                // Act & Assert (should not throw)
                var exception = Record.Exception(() =>
                    TradingGateValidator.ValidateOrThrow(cfg));

                Assert.Null(exception);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ValidateOrThrow_PassesInPaperModeWithPreflight()
        {
            // Arrange - Full green path
            var tempDir = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            var preflightDir = Path.Combine(tempDir, "preflight");
            Directory.CreateDirectory(preflightDir);

            var connectionFile = Path.Combine(preflightDir, "connection_ok.json");
            File.WriteAllText(connectionFile, "{\"ok\": true}");

            var cfg = new TelemetryConfig
            {
                Ops = new OpsConfig { EnableTrading = true },
                Mode = "paper",
                LogPath = tempDir
            };

            try
            {
                // Act
                TradingGateValidator.ValidateOrThrow(cfg);

                // Assert - Check log was written
                var logFile = Path.Combine(tempDir, "trading_gate.log");
                Assert.True(File.Exists(logFile));

                var logContent = File.ReadAllText(logFile);
                Assert.Contains("TRADING GATE VALIDATION PASSED", logContent);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
