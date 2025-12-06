using Xunit;
using Telemetry;
using System;
using System.IO;

namespace BotG.Tests
{
    public class ConfigTests
    {
        [Fact]
        public void Env_overrides_File()
        {
            // Arrange: Set ENV to override file
            Environment.SetEnvironmentVariable("Simulation__Enabled", "false");
            Environment.SetEnvironmentVariable("Mode", "paper");

            try
            {
                // Act
                var cfg = TelemetryConfig.Load();

                // Assert: ENV wins over file
                Assert.False(cfg.UseSimulation);
                Assert.False(cfg.Simulation.Enabled);
                Assert.Equal("paper", cfg.Mode.ToLowerInvariant());
            }
            finally
            {
                // Cleanup
                Environment.SetEnvironmentVariable("Simulation__Enabled", null);
                Environment.SetEnvironmentVariable("Mode", null);
            }
        }

        [Fact]
        public void Default_paper_false()
        {
            // Arrange: Clear ENV
            Environment.SetEnvironmentVariable("Simulation__Enabled", null);
            Environment.SetEnvironmentVariable("BOTG__Simulation__Enabled", null);
            Environment.SetEnvironmentVariable("Mode", "paper");

            try
            {
                // Act
                var cfg = TelemetryConfig.Load();

                // Assert: paper mode defaults to sim=false
                Assert.False(cfg.UseSimulation);
                Assert.False(cfg.Simulation.Enabled);
                Assert.Equal("paper", cfg.Mode.ToLowerInvariant());
            }
            finally
            {
                Environment.SetEnvironmentVariable("Mode", null);
            }
        }

        [Fact]
        public void File_only_false()
        {
            // Arrange: Clear ENV, rely on file
            Environment.SetEnvironmentVariable("Simulation__Enabled", null);
            Environment.SetEnvironmentVariable("BOTG__Simulation__Enabled", null);
            Environment.SetEnvironmentVariable("Mode", null);

            // Create temp config file with sim=false
            var tempConfig = Path.Combine(Path.GetTempPath(), "config.runtime.json");
            File.WriteAllText(tempConfig, @"{
                ""Mode"": ""paper"",
                ""Simulation"": {
                    ""Enabled"": false
                },
                ""UseSimulation"": false
            }");

            try
            {
                // Act: Load with temp directory as hint
                var cfg = TelemetryConfig.Load(Path.GetTempPath());

                // Assert: File value should be respected (sim=false)
                Assert.False(cfg.UseSimulation);
                Assert.False(cfg.Simulation.Enabled);
            }
            finally
            {
                // Cleanup
                if (File.Exists(tempConfig)) File.Delete(tempConfig);
            }
        }
    }
}
