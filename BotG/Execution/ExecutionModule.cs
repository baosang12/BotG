using System;
using System.Collections.Generic;
using Bot3.Core;
using cAlgo.API;
using Strategies;
using Telemetry; // added

namespace Execution
{
    public class ExecutionModule
    {
        private readonly List<IStrategy<TradeSignal>> _strategies;
        private readonly Robot _bot;

        // telemetry
        private readonly OrderLifecycleLogger _orderLogger;
        private readonly TelemetryCollector _telemetry;
        private readonly int _retryLimit = 1;

        public ExecutionModule(List<IStrategy<TradeSignal>> strategies, Robot bot)
        {
            _strategies = strategies;
            _bot = bot;
            // Initialize telemetry with defaults; overridden by startup wiring if available
            var cfg = TelemetryConfig.Load();
            _orderLogger = new OrderLifecycleLogger(cfg.LogPath, cfg.OrderLogFile);
            _telemetry = new TelemetryCollector(cfg.LogPath, cfg.TelemetryFile, cfg.FlushIntervalSeconds);
        }

        public void Execute(TradeSignal signal, double price)
        {
            try
            {
                string orderId = Guid.NewGuid().ToString("N");
                double? requestedSize = 1000; // original hard-coded size; do not change behavior
                _orderLogger.Log("REQUEST", orderId, price, null, requestedSize, null, $"Action={signal.Action}");
                _telemetry.IncOrderRequested();

                int attempts = 0;
                while (true)
                {
                    try
                    {
                        TradeResult result;
                        switch (signal.Action)
                        {
                            case TradeAction.Buy:
                                result = _bot.ExecuteMarketOrder(TradeType.Buy, _bot.SymbolName, (long)requestedSize, "Bot3", null, null);
                                break;
                            case TradeAction.Sell:
                                result = _bot.ExecuteMarketOrder(TradeType.Sell, _bot.SymbolName, (long)requestedSize, "Bot3", null, null);
                                break;
                            case TradeAction.Exit:
                                foreach (var pos in _bot.Positions)
                                {
                                    if (pos.SymbolName == _bot.SymbolName)
                                    {
                                        _bot.ClosePosition(pos);
                                    }
                                }
                                _orderLogger.Log("ACK", orderId, price, null, requestedSize, null, "Exit processed");
                                return;
                            default:
                                return;
                        }
                        double? execPrice = null;
                        double? filled = null;
                        string brokerMsg = (result?.IsSuccessful == true) ? "OK" : result?.Error.ToString();
                        // cTrader API might provide position or fill info; if not available, keep nulls
                        _orderLogger.Log("ACK", orderId, price, execPrice, requestedSize, filled, brokerMsg);
                        if (result?.IsSuccessful == true) _telemetry.IncOrderFilled();
                        break;
                    }
                    catch (Exception ex) when (attempts < _retryLimit)
                    {
                        attempts++;
                        _orderLogger.Log("ERROR", orderId, price, null, requestedSize, null, ex.Message);
                        _telemetry.IncError();
                        // brief backoff
                        System.Threading.Thread.Sleep(200);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                _bot.Print($"Execution error: {ex.Message}");
                try { _telemetry.IncError(); } catch {}
            }
        }
    }
}
