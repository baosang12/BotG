using Xunit;

namespace BotG.Tests
{
    public class SmokeOnceTests
    {
        [Fact]
        [Trait("Category", "SmokeOnce")]
        public void SmokeOnceService_FiresOnlyOnce()
        {
            // Arrange: Enable trading, disable simulation, turn on debug.smoke_once
            var cfg = new Telemetry.TelemetryConfig
            {
                Ops = new Telemetry.OpsConfig { EnableTrading = true },
                Simulation = new Telemetry.SimulationConfig { Enabled = false },
                Debug = new Telemetry.DebugConfig { SmokeOnce = true }
            };
            var svc = new BotG.Runtime.SmokeOnceService();
            int fired = 0;

            // Act: attempt twice
            if (svc.ShouldFire(cfg)) { fired++; svc.MarkFired(); }
            if (svc.ShouldFire(cfg)) { fired++; svc.MarkFired(); }

            // Assert
            Assert.Equal(1, fired);
            Assert.True(svc.Fired);
        }
    }
}
