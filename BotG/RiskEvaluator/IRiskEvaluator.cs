using Strategies;

namespace RiskEvaluator
{
    public interface IRiskEvaluator
    {
        RiskScore Evaluate(Signal signal, MarketContext? context = null);
    }
}
