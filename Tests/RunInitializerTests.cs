using System.IO;
using System.Text.Json;
using Telemetry;
using Xunit;

namespace BotG.Tests
{
    public class RunInitializerTests
    {
        [Fact]
        public void EnsureRunFolderAndMetadata_RespectsMetadataHook()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "botg-tests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var cfg = new TelemetryConfig
            {
                LogPath = tempRoot,
                Hours = 1,
                SecondsPerHour = 3600,
                DrainSeconds = 30,
                GracefulShutdownWaitSeconds = 5
            };

            try
            {
                TelemetryContext.MetadataHook = meta => meta["data_source"] = "hooked_source";
                var runDir = RunInitializer.EnsureRunFolderAndMetadata(cfg);
                var metaPath = Path.Combine(runDir, "run_metadata.json");
                Assert.True(File.Exists(metaPath));

                var json = File.ReadAllText(metaPath);
                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.TryGetProperty("data_source", out var ds));
                Assert.Equal("hooked_source", ds.GetString());
            }
            finally
            {
                TelemetryContext.MetadataHook = null;
            }
        }
    }
}
