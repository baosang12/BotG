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
            // ========== SINGLE-SWITCH GATE: ops.enable_trading ==========
            var cfg = TelemetryConfig.Load();
            if (!cfg.Ops.EnableTrading)
            {
                // Log to pipeline when blocked by ops gate
                BotG.Runtime.Logging.PipelineLogger.Log("TRADE", "CanTrade", "CanTrade=false (ops gate)", 
                    new { ops_enable_trading = false }, null);
                return false;
            }

            // TODO: Add hard-stop risk gates (-3R daily, -6R weekly) in future PR
            // if (daily_pnl <= -3R) return false;
            // if (weekly_pnl <= -6R) return false;

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
