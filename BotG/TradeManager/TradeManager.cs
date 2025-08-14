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

        public TradeManager(Execution.ExecutionModule executionModule)
        {
            _executionModule = executionModule;
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
