using System.IO;
using System.Text.Json;
using Telemetry;
using Xunit;

namespace BotG.Tests
{
    public class RunMetadataWriterTests
    {
        [Fact]
        public void UpsertSourceMetadata_WritesBrokerFields()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "botg-tests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var writer = new RunMetadataWriter(tempDir);
            writer.WriteOnce(new { run_id = "test" });

            writer.UpsertSourceMetadata("ctrader_demo", "DemoBroker", "DemoServer", "Acct123");

            var json = File.ReadAllText(Path.Combine(tempDir, "run_metadata.json"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("ctrader_demo", root.GetProperty("data_source").GetString());
            Assert.Equal("DemoBroker", root.GetProperty("broker_name").GetString());
            Assert.Equal("DemoServer", root.GetProperty("server").GetString());
            Assert.Equal("Acct123", root.GetProperty("account_id").GetString());
            Assert.True(root.TryGetProperty("timestamp_connect", out _));
        }
    }
}
