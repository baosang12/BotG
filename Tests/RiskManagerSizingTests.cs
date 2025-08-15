using Xunit;
using RiskManager;

namespace BotG.Tests
{
    public class RiskManagerSizingTests
    {
        [Fact]
        public void CalculateOrderSize_UsesRiskPercentAndMinRisk_WithStopDistAndPointValue()
        {
            var rm = new RiskManager.RiskManager
            {
                RiskPercentPerTrade = 0.01, // 1%
                MinRiskUsdPerTrade = 3.0
            };
            rm.Initialize(new RiskSettings());
            rm.SetEquityOverrideForTesting(10000); // equity = $10,000

            // stopDist*pointValue = 2*1 = 2 USD per unit risk -> riskUsd = max(100,3)=100 -> floor(100/2)=50 units
            var units = rm.CalculateOrderSize(2.0, 1.0);
            Assert.Equal(50, units);
        }

        [Fact]
        public void CalculateOrderSize_MinUnitAndCap()
        {
            var rm = new RiskManager.RiskManager
            {
                RiskPercentPerTrade = 0.0001, // small
                MinRiskUsdPerTrade = 1.0
            };
            rm.Initialize(new RiskSettings());
            rm.SetEquityOverrideForTesting(1000);

            // riskUsd = max(0.1,1)=1 -> riskPerUnit=10 -> floor(1/10)=0 -> min 1
            var unitsMin = rm.CalculateOrderSize(10.0, 1.0);
            Assert.Equal(1, unitsMin);

            // big equity, tiny stop/risk per unit -> hits cap 1,000,000
            rm.SetEquityOverrideForTesting(1_000_000_000);
            rm.RiskPercentPerTrade = 1.0; // 100% risk for test cap only
            var unitsCap = rm.CalculateOrderSize(0.000001, 0.000001);
            Assert.Equal(1_000_000, unitsCap);
        }

        [Fact]
        public void CalculateOrderSize_UsesSettingsPointValue_WhenParamZero()
        {
            var rm = new RiskManager.RiskManager
            {
                RiskPercentPerTrade = 0.01,
                MinRiskUsdPerTrade = 3.0
            };
            var settings = new RiskSettings
            {
                PointValuePerUnit = 2.5
            };
            rm.Initialize(settings);
            rm.SetEquityOverrideForTesting(10000); // equity = $10,000 -> riskUsd = 100

            // stopDist=2, effectivePointValue=2.5 -> riskPerUnit=5 -> floor(100/5)=20
            var units = rm.CalculateOrderSize(2.0, 0.0);
            Assert.Equal(20, units);
        }
    }
}
