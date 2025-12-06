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

        [Fact]
        public void UpsertSymbolFeeProfile_PersistsSnapshotPerSymbol()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "botg-tests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var writer = new RunMetadataWriter(tempDir);
            writer.WriteOnce(new { run_id = "test" });

            var snapshot = new SymbolFeeProfileSnapshot
            {
                SpreadPips = 1.2,
                CommissionRoundtripUsdPerLot = 7.0,
                FeePipsPerRoundtrip = 1.3,
                FeePipsPerSide = 0.65,
                SwapLongPipsPerDay = -0.4,
                SwapShortPipsPerDay = -0.1,
                SwapType = "Points",
                SwapTripleDay = "Wednesday",
                CapturedAtIso = "2025-11-18T00:00:00Z"
            };

            writer.UpsertSymbolFeeProfile("EURUSD", snapshot);

            var json = File.ReadAllText(Path.Combine(tempDir, "run_metadata.json"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var profiles = root.GetProperty("symbol_fee_profiles");
            var eurusd = profiles.GetProperty("EURUSD");

            Assert.Equal(1.2, eurusd.GetProperty("SpreadPips").GetDouble());
            Assert.Equal(-0.4, eurusd.GetProperty("SwapLongPipsPerDay").GetDouble());
            Assert.Equal("Wednesday", eurusd.GetProperty("SwapTripleDay").GetString());
            Assert.Equal("Points", eurusd.GetProperty("SwapType").GetString());
        }
    }
}
