using System;
using Connectivity;
using Strategies;
using Telemetry; // added
namespace TradeManager
{
    public class TradeManager : ITradeManager
    {
        private int _tradeCountToday = 0;
        private int _maxTradesPerDay = 5;
    private Execution.ExecutionModule _executionModule;
    private readonly IMarketDataProvider? _marketData;
    private readonly IOrderExecutor? _orderExecutor;

        // Existing constructor: accepts a pre-built ExecutionModule (backwards compatible)
        public TradeManager(Execution.ExecutionModule executionModule)
        {
            _executionModule = executionModule;
            TelemetryContext.InitOnce();
        }

        // Convenience constructor: accept dependencies and construct ExecutionModule internally.
        // This allows composition roots to pass an optional RiskManager instance so sizing will be used.
        public TradeManager(System.Collections.Generic.List<Strategies.IStrategy<Strategies.TradeSignal>> strategies, cAlgo.API.Robot bot, RiskManager.RiskManager? riskManager = null, IMarketDataProvider? marketData = null, IOrderExecutor? orderExecutor = null)
        {
            _marketData = marketData;
            _orderExecutor = orderExecutor;
            _executionModule = new Execution.ExecutionModule(strategies, bot, riskManager);
            TelemetryContext.InitOnce();
        }

        public bool CanTrade(TradeSignal signal, double riskScore)
        {
            // Single-switch gate: ops.enable_trading
            var cfg = Telemetry.TelemetryConfig.Load();
            if (!cfg.Ops.EnableTrading)
            {
                // Log but don't place orders when disabled
                return false;
            }

            if (_tradeCountToday >= _maxTradesPerDay) return false;
            // Allow trades with risk score >= 5.0 (was 7.5)
            if (riskScore < 5.0) return false;

            // TODO: Add risk hard-stops here
            // - Daily loss <= -3R → block new orders
            // - Weekly loss <= -6R → block new orders
            // Implementation: track realized P&L per day/week, compare to account equity * risk-per-trade

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
