using System;
using Connectivity.Synthetic;
using Telemetry;
using Xunit;

namespace BotG.Tests
{
    public class OrderQuoteTelemetryTests
    {
        [Fact]
        public void TryGetQuote_ReturnsLatestSnapshotWithSpread()
        {
            var provider = new SyntheticMarketDataProvider();
            var telemetry = new OrderQuoteTelemetry(provider);

            telemetry.TrackSymbol("EURUSD");
            provider.PublishQuote("EURUSD", 1.2345, 1.2347, DateTime.UtcNow);

            Assert.True(telemetry.TryGetQuote("EURUSD", out var snapshot));
            Assert.Equal(1.2345, snapshot.Bid!.Value, 5);
            Assert.Equal(1.2347, snapshot.Ask!.Value, 5);
            Assert.InRange(snapshot.SpreadPips!.Value, 1.9, 2.1);
            Assert.NotNull(snapshot.TimestampUtc);
        }
    }
}
