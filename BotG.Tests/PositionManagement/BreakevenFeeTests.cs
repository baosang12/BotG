using BotG.PositionManagement;
using cAlgo.API;
using Xunit;

namespace BotG.Tests.PositionManagement
{
    public class BreakevenFeeTests
    {
        [Fact]
        public void ApplyBrokerFeeBuffer_ComputesCommissionAndSwap()
        {
            var exitParams = new ExitParameters();

            exitParams.ApplyBrokerFeeBuffer(
                symbolName: "EURUSD",
                pipSize: 0.0001,
                lotSize: 100000,
                pipValuePerLot: 10.0,
                tickSize: 0.00001,
                tickValue: 1.0,
                volumeInUnits: 1000,
                direction: TradeType.Buy);

            Assert.InRange(exitParams.BreakevenFeePips, 1.5, 1.6);
        }
    }
}
