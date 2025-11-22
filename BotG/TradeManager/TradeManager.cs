using System;
using System.Collections.Generic;
using Connectivity;
using Strategies;
using Telemetry; // added
using BotG.Runtime.Logging;
using RiskManager = BotG.RiskManager;
namespace TradeManager
{
    public class TradeManager : ITradeManager
    {
        private int _tradeCountToday = 0;
        private DateTime _lastTradeDate;
        private int _maxTradesPerDay = 100;
    private Execution.ExecutionModule _executionModule;
    private readonly IMarketDataProvider? _marketData;
    private readonly IOrderExecutor? _orderExecutor;
        private readonly Func<DateTime> _getCurrentTime;
        private readonly object _executionModuleLock = new object();
        private readonly cAlgo.API.Robot? _bot;
        private readonly RiskManager.RiskManager? _riskManagerInstance;
        private IReadOnlyList<Strategies.IStrategy> _activeStrategies = Array.Empty<Strategies.IStrategy>();

        // Existing constructor: accepts a pre-built ExecutionModule (backwards compatible)
        public TradeManager(Execution.ExecutionModule executionModule)
        {
            _executionModule = executionModule;
            TelemetryContext.InitOnce();
            _getCurrentTime = static () => DateTime.UtcNow;
            _lastTradeDate = _getCurrentTime().Date;
        }

        // Convenience constructor: accept dependencies and construct ExecutionModule internally.
        // This allows composition roots to pass an optional RiskManager instance so sizing will be used.
        public TradeManager(System.Collections.Generic.IReadOnlyList<Strategies.IStrategy> strategies, cAlgo.API.Robot bot, RiskManager.RiskManager? riskManager = null, IMarketDataProvider? marketData = null, IOrderExecutor? orderExecutor = null)
        {
            _marketData = marketData;
            _orderExecutor = orderExecutor;
            _bot = bot;
            _riskManagerInstance = riskManager;
            _activeStrategies = strategies;
            _executionModule = new Execution.ExecutionModule(strategies, bot, riskManager);
            TelemetryContext.InitOnce();
            _getCurrentTime = () =>
            {
                try
                {
                    var server = bot?.Server;
                    if (server != null)
                    {
                        return server.Time;
                    }
                }
                catch { }

                return DateTime.UtcNow;
            };
            _lastTradeDate = _getCurrentTime().Date;
        }

        public void UpdateStrategies(System.Collections.Generic.IReadOnlyList<Strategies.IStrategy> strategies)
        {
            if (strategies == null)
            {
                throw new ArgumentNullException(nameof(strategies));
            }

            lock (_executionModuleLock)
            {
                _activeStrategies = strategies;
                if (_bot != null)
                {
                    _executionModule = new Execution.ExecutionModule(_activeStrategies, _bot, _riskManagerInstance);
                }
            }

            PipelineLogger.Log(
                "TRADE",
                "StrategiesUpdated",
                "TradeManager strategies updated",
                new { count = strategies.Count },
                null);
        }

        public void SetDailyTradeLimit(int maxTradesPerDay)
        {
            if (maxTradesPerDay > 0)
            {
                _maxTradesPerDay = maxTradesPerDay;
            }
        }

        public bool CanTrade(Signal signal, RiskScore riskScore)
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

            ResetDailyCounterIfNeeded();

            if (_tradeCountToday >= _maxTradesPerDay)
            {
                BotG.Runtime.Logging.PipelineLogger.Log(
                    "TRADE",
                    "CanTrade",
                    "CanTrade=false (daily trade limit)",
                    new { trades = _tradeCountToday, max = _maxTradesPerDay },
                    null);
                return false;
            }
            if (!riskScore.IsAcceptable)
            {
                BotG.Runtime.Logging.PipelineLogger.Log(
                    "TRADE",
                    "CanTrade",
                    "CanTrade=false (risk gate)",
                    new
                    {
                        risk_score = riskScore.Score,
                        risk_level = riskScore.Level.ToString(),
                        risk_reason = riskScore.Reason
                    },
                    null);
                return false;
            }

            // TODO: Add risk hard-stops here
            // - Daily loss <= -3R → block new orders
            // - Weekly loss <= -6R → block new orders
            // Implementation: track realized P&L per day/week, compare to account equity * risk-per-trade

            return true;
        }

        public void Process(Signal signal, RiskScore riskScore)
        {
            try { TelemetryContext.Collector?.IncSignal(); } catch {}
            if (!CanTrade(signal, riskScore)) return;
            _tradeCountToday++;
            PipelineLogger.Log(
                "TRADE",
                "StrategyOrder",
                $"Strategy {signal.StrategyName} requested {signal.Action}",
                new
                {
                    strategy = signal.StrategyName,
                    action = signal.Action.ToString(),
                    price = signal.Price,
                    timestamp = signal.TimestampUtc
                },
                null);
            _executionModule.Execute(signal, signal.Price);
        }

        private void ResetDailyCounterIfNeeded()
        {
            var today = _getCurrentTime().Date;
            if (today == _lastTradeDate)
            {
                return;
            }

            _tradeCountToday = 0;
            _lastTradeDate = today;
            BotG.Runtime.Logging.PipelineLogger.Log(
                "TRADE",
                "DailyReset",
                "Daily trade counter reset",
                new { date = today },
                null);
        }
    }
}
