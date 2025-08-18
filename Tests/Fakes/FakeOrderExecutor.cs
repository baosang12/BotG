using System;
using Execution;
using cAlgo.API;

namespace BotG.Tests.Fakes
{
    public class FakeOrderExecutor : IOrderExecutor
    {
        public TradeType LastType { get; private set; }
        public string LastSymbol { get; private set; } = string.Empty;
        public double LastVolume { get; private set; }
        public double? LastStopLoss { get; private set; }
        public double? LastTakeProfit { get; private set; }

        public ExecuteResult ExecuteMarketOrder(TradeType type, string symbolName, double volume, string label, double? stopLoss, double? takeProfit)
        {
            LastType = type;
            LastSymbol = symbolName;
            LastVolume = volume;
            LastStopLoss = stopLoss;
            LastTakeProfit = takeProfit;
            // Return lightweight success result matching the adapter contract
            return new ExecuteResult
            {
                IsSuccessful = true,
                ErrorText = string.Empty,
                EntryPrice = 0.0,
                FilledVolumeInUnits = Math.Round(volume)
            };
        }
    }
}
