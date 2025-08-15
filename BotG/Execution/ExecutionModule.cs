using System;
using System.Collections.Generic;
using Bot3.Core;
using cAlgo.API;
using Strategies;
using Telemetry; // added
using RiskManager; // allow optional RiskManager injection

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
        // optional risk manager for order sizing
        private readonly RiskManager.RiskManager? _riskManager;

        // backward-compatible constructor
        public ExecutionModule(List<IStrategy<TradeSignal>> strategies, Robot bot)
            : this(strategies, bot, null) {}

        // new constructor with optional RiskManager for sizing
        public ExecutionModule(List<IStrategy<TradeSignal>> strategies, Robot bot, RiskManager.RiskManager? riskManager)
        {
            _strategies = strategies;
            _bot = bot;
            // Initialize telemetry with defaults; overridden by startup wiring if available
            var cfg = TelemetryConfig.Load();
            _orderLogger = new OrderLifecycleLogger(cfg.LogPath, cfg.OrderLogFile);
            _telemetry = new TelemetryCollector(cfg.LogPath, cfg.TelemetryFile, cfg.FlushIntervalSeconds);
            _riskManager = riskManager;
        }

        public void Execute(TradeSignal signal, double price)
        {
            try
            {
                string orderId = Guid.NewGuid().ToString("N");
                // determine requested size; prefer RiskManager if provided
                double requestedSize;
                if (_riskManager != null)
                {
                    try
                    {
                        double stopDist = 1.0; // default conservative distance in price units
                        double pointValue = 1.0; // default point value per unit

                        // a) From signal if available (StopLoss is a price level; convert to distance from current price)
                        if (signal != null && signal.StopLoss.HasValue && signal.StopLoss.Value > 0)
                        {
                            stopDist = Math.Abs(signal.StopLoss.Value - price);
                        }
                        else
                        {
                            // b) Try ATR-based heuristic using RiskSettings.StopLossAtrMultiplier if accessible via reflection or default
                            try
                            {
                                // Look for a current ATR value via AnalysisModule if globally accessible; otherwise use a small placeholder
                                // TODO: Integrate with a real ATR provider/context if available
                                double atr = 0.0;
                                // conservative default if no ATR available
                                double stopMult = 1.0;
                                try
                                {
                                    // Attempt to read RiskSettings.StopLossAtrMultiplier via reflection on _riskManager
                                    var rsField = typeof(RiskManager.RiskManager).GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                    var rsVal = rsField?.GetValue(_riskManager);
                                    if (rsVal != null)
                                    {
                                        var prop = rsVal.GetType().GetProperty("StopLossAtrMultiplier");
                                        if (prop != null)
                                        {
                                            var val = prop.GetValue(rsVal);
                                            if (val is double d && d > 0) stopMult = d;
                                        }
                                    }
                                }
                                catch { }
                                if (atr > 0)
                                {
                                    stopDist = atr * stopMult;
                                }
                            }
                            catch { /* keep default */ }
                        }

                        // c) pointValuePerUnit: let RiskManager handle defaults via its settings; pass 0 to indicate 'use default'
                        pointValue = 0.0;

                        if (stopDist <= 0)
                        {
                            stopDist = 1.0; // TODO: replace fallback stopDist with actual SL calc
                            _bot.Print("[ExecutionModule] WARNING: Using fallback stopDist=1.0 price units");
                        }

                        requestedSize = Math.Max(1.0, _riskManager.CalculateOrderSize(stopDist, pointValue));
                    }
                    catch
                    {
                        requestedSize = 1000; // fallback to legacy default
                    }
                }
                else
                {
                    requestedSize = 1000; // legacy default
                }
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
                                result = _bot.ExecuteMarketOrder(TradeType.Buy, _bot.SymbolName, Convert.ToInt64(Math.Round(requestedSize)), "Bot3", null, null);
                                break;
                            case TradeAction.Sell:
                                result = _bot.ExecuteMarketOrder(TradeType.Sell, _bot.SymbolName, Convert.ToInt64(Math.Round(requestedSize)), "Bot3", null, null);
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
                        try
                        {
                            if (result?.Position != null)
                            {
                                execPrice = result.Position.EntryPrice;
                                // VolumeInUnits is long; cast to double for logging
                                filled = result.Position.VolumeInUnits;
                            }
                        }
                        catch { }
                        // Log ACK with any available executed price/fill info
                        _orderLogger.Log("ACK", orderId, price, execPrice, requestedSize, filled, brokerMsg);
                        if (result?.IsSuccessful == true && filled.HasValue && filled.Value > 0)
                        {
                            _orderLogger.Log("FILL", orderId, price, execPrice, requestedSize, filled, "filled");
                            _telemetry.IncOrderFilled();
                        }
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
