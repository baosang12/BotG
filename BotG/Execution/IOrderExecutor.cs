using cAlgo.API;

namespace Execution
{
    // Lightweight result to avoid tight coupling to cAlgo TradeResult in tests
    public class ExecuteResult
    {
        public bool IsSuccessful { get; set; }
        public string ErrorText { get; set; } = string.Empty;
        public double? EntryPrice { get; set; }
        public double? FilledVolumeInUnits { get; set; }
    }

    // Abstraction to decouple ExecutionModule from Robot for unit testing
    public interface IOrderExecutor
    {
        ExecuteResult ExecuteMarketOrder(TradeType type, string symbolName, double volume, string label, double? stopLoss, double? takeProfit);
    }
}
