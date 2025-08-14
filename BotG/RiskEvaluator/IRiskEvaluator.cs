
using Strategies;

namespace RiskEvaluator
{

    public interface IRiskEvaluator
    {
        double Evaluate(TradeSignal signal); // Trả về điểm số risk
        bool IsAcceptable(TradeSignal signal); // True nếu đủ điều kiện
    }
}
