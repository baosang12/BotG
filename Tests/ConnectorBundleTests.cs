using System;
using Connectivity;
using Connectivity.Synthetic;
using Xunit;

namespace BotG.Tests
{
    public class ConnectorBundleTests
    {
        [Fact]
        public void CreateSyntheticBundle_WhenModeExplicit_ReturnsSyntheticImplementations()
        {
            var bundle = ConnectorBundle.Create(explicitMode: "synthetic");

            Assert.Equal("synthetic", bundle.Mode);
            Assert.IsType<SyntheticMarketDataProvider>(bundle.MarketData);
            Assert.IsType<SyntheticOrderExecutor>(bundle.OrderExecutor);
        }

        [Fact]
        public void CreateUsesEnvironmentOverridesForSyntheticMetadata()
        {
            var originalMode = Environment.GetEnvironmentVariable("DATASOURCE__MODE");
            var originalBroker = Environment.GetEnvironmentVariable("BROKER__NAME");
            var originalServer = Environment.GetEnvironmentVariable("BROKER__SERVER");
            var originalAccount = Environment.GetEnvironmentVariable("BROKER__ACCOUNT_ID");

            try
            {
                Environment.SetEnvironmentVariable("DATASOURCE__MODE", "synthetic");
                Environment.SetEnvironmentVariable("BROKER__NAME", "EnvBroker");
                Environment.SetEnvironmentVariable("BROKER__SERVER", "EnvServer");
                Environment.SetEnvironmentVariable("BROKER__ACCOUNT_ID", "EnvAccount");

                var bundle = ConnectorBundle.Create(explicitMode: null);

                Assert.Equal("synthetic", bundle.Mode);
                Assert.Equal("EnvBroker", bundle.OrderExecutor.BrokerName);
                Assert.Equal("EnvServer", bundle.OrderExecutor.Server);
                Assert.Equal("EnvAccount", bundle.OrderExecutor.AccountId);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DATASOURCE__MODE", originalMode);
                Environment.SetEnvironmentVariable("BROKER__NAME", originalBroker);
                Environment.SetEnvironmentVariable("BROKER__SERVER", originalServer);
                Environment.SetEnvironmentVariable("BROKER__ACCOUNT_ID", originalAccount);
            }
        }
    }
}
