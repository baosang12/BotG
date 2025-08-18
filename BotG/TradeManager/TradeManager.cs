using System;
using Strategies;
using Telemetry; // added
namespace TradeManager
{
    public class TradeManager : ITradeManager
    {
        private int _tradeCountToday = 0;
        private int _maxTradesPerDay = 5;
        private Execution.ExecutionModule _executionModule;

        // Existing constructor: accepts a pre-built ExecutionModule (backwards compatible)
        public TradeManager(Execution.ExecutionModule executionModule)
        {
            _executionModule = executionModule;
            TelemetryContext.InitOnce();
        }

        // Convenience constructor: accept dependencies and construct ExecutionModule internally.
        // This allows composition roots to pass an optional RiskManager instance so sizing will be used.
    public TradeManager(System.Collections.Generic.List<Strategies.IStrategy<Strategies.TradeSignal>> strategies, cAlgo.API.Robot bot, RiskManager.RiskManager riskManager = null)
        {
            _executionModule = new Execution.ExecutionModule(strategies, bot, riskManager);
            TelemetryContext.InitOnce();
        }

        public bool CanTrade(TradeSignal signal, double riskScore)
        {
            if (_tradeCountToday >= _maxTradesPerDay) return false;
            // Allow trades with risk score >= 5.0 (was 7.5)
            if (riskScore < 5.0) return false;
            // Có thể bổ sung kiểm tra winrate, thời gian giao dịch, v.v.
            return true;
        }

        public void Process(TradeSignal signal, double riskScore)
        {
            try { TelemetryContext.Collector?.IncSignal(); } catch {}
            if (!CanTrade(signal, riskScore)) return;
            _tradeCountToday++;
            _executionModule.Execute(signal, signal.Price);
        }
    }
}
