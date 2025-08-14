using Strategies;

namespace TradeManager
{
    public interface ITradeManager
    {
        bool CanTrade(TradeSignal signal, double riskScore);
        void Process(TradeSignal signal, double riskScore);
    }
}
