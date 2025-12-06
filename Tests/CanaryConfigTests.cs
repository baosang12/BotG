using System;
using Xunit;
using Telemetry;

namespace BotG.Tests
{
    public class CanaryConfigTests
    {
        [Fact]
        public void Default_Canary_Disabled()
        {
            var cfg = new TelemetryConfig();
            Assert.False(cfg.Preflight.Canary.Enabled);
        }

        [Fact]
        public void Env_Override_Enables_Canary()
        {
            Environment.SetEnvironmentVariable("PREFLIGHT__Canary__Enabled", "true");
            var cfg = TelemetryConfig.Load(null, ensureRunFolder: true, useCache: false);
            Assert.True(cfg.Preflight.Canary.Enabled);
            Environment.SetEnvironmentVariable("PREFLIGHT__Canary__Enabled", null);
        }

        [Fact]
        public void Canary_Config_Structure_Valid()
        {
            var cfg = new TelemetryConfig
            {
                Preflight = new PreflightConfig
                {
                    Canary = new CanaryConfig
                    {
                        Enabled = true
                    }
                }
            };

            Assert.NotNull(cfg.Preflight);
            Assert.NotNull(cfg.Preflight.Canary);
            Assert.True(cfg.Preflight.Canary.Enabled);
        }
    }
}
