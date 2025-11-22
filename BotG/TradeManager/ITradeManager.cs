using Strategies;

namespace TradeManager
{
    public interface ITradeManager
    {
        bool CanTrade(Signal signal, RiskScore riskScore);
        void Process(Signal signal, RiskScore riskScore);
    }
}
