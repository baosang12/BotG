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
    private readonly Robot? _bot;
    private readonly IOrderExecutor _executor;

        // telemetry
        private readonly OrderLifecycleLogger _orderLogger;
        private readonly TelemetryCollector _telemetry;
        private readonly int _retryLimit = 1;
        // optional risk manager for order sizing
    private readonly RiskManager.RiskManager? _riskManager;
    private bool _pointValuePrinted = false; // ensure single startup print

        // backward-compatible constructor
    public ExecutionModule(List<IStrategy<TradeSignal>> strategies, Robot? bot)
            : this(strategies, bot, null) {}

        // new constructor with optional RiskManager for sizing
    public ExecutionModule(List<IStrategy<TradeSignal>> strategies, Robot? bot, RiskManager.RiskManager? riskManager)
        {
            _strategies = strategies;
            _bot = bot;
            _executor = new RobotOrderExecutor(bot ?? new cAlgo.API.Robot());
            // Initialize telemetry with defaults; overridden by startup wiring if available
            var cfg = TelemetryConfig.Load();
            _orderLogger = new OrderLifecycleLogger(cfg.LogPath, cfg.OrderLogFile);
            _telemetry = new TelemetryCollector(cfg.LogPath, cfg.TelemetryFile, cfg.FlushIntervalSeconds);
        _riskManager = riskManager;
            // Attempt to compute broker-specific PointValuePerUnit from symbol
            try
            {
                if (_riskManager != null && !_pointValuePrinted && _bot != null)
                {
            try { _riskManager.SetSymbolReference(_bot.Symbol); } catch {}
                    // Prefer SetPointValueFromSymbol if available; otherwise fallback to SetPointValueFromParams using reflection
                    var rmType = _riskManager.GetType();
                    object sym = _bot != null ? (object)_bot.Symbol : new cAlgo.API.Symbol();
                    var setFromSym = rmType.GetMethod("SetPointValueFromSymbol", new Type[] { typeof(cAlgo.API.Internals.Symbol) })
                                   ?? rmType.GetMethod("SetPointValueFromSymbol", new Type[] { typeof(cAlgo.API.Symbol) });
                    if (setFromSym != null)
                    {
                        setFromSym.Invoke(_riskManager, new object[] { sym });
                    }
                    else
                    {
                        // Reflection fallback: read TickSize/TickValue/LotSize/ContractSize and call SetPointValueFromParams
                        double ReadDoubleProp(object o, params string[] names)
                        {
                            foreach (var n in names)
                            {
                                var pi = o.GetType().GetProperty(n);
                                if (pi != null)
                                {
                                    var v = pi.GetValue(o);
                                    if (v != null)
                                    {
                                        if (double.TryParse(Convert.ToString(v), out var d)) return d;
                                    }
                                }
                            }
                            return 0.0;
                        }
                        double tickSize = ReadDoubleProp(sym, "TickSize", "PipSize");
                        double tickValue = ReadDoubleProp(sym, "TickValue", "PipValue");
                        double lotSize = ReadDoubleProp(sym, "LotSize", "ContractSize");
                        var setFromParams = rmType.GetMethod("SetPointValueFromParams", new Type[] { typeof(double), typeof(double), typeof(double) });
                        if (setFromParams != null)
                        {
                            setFromParams.Invoke(_riskManager, new object[] { tickSize, tickValue, lotSize });
                        }
                    }

                    // Print the effective PointValuePerUnit once
                    try { _bot?.Print($"[Startup] PointValuePerUnit = {_riskManager.GetSettings().PointValuePerUnit}"); _pointValuePrinted = true; } catch {}
                }
                // Dump Symbol properties for diagnostics
                if (_bot != null)
                {
                    foreach (var p in _bot.Symbol.GetType().GetProperties()) { _bot.Print($"Symbol.{p.Name} = {p.GetValue(_bot.Symbol)}"); }
                }
            }
            catch (Exception ex)
            {
                try { _bot?.Print("[Startup] Could not compute PointValuePerUnit: " + ex.Message); } catch {}
            }
        }

        // New constructor overload for tests or DI: provide an IOrderExecutor directly.
    public ExecutionModule(List<IStrategy<TradeSignal>> strategies, IOrderExecutor executor, Robot? bot, RiskManager.RiskManager? riskManager = null)
        {
            _strategies = strategies;
            _bot = bot;
            _executor = executor;
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
                // determine requested volume via RiskManager (units), also compute lots for logging
                double requestedUnits;
                double theoreticalLots = 0.0;
                double lotSize = 0.0;
        if (_riskManager != null)
                {
                    try
                    {
            double stopDist = 0.0;
            double pointValue = 0.0; // let RM use defaults

                        // a) From signal if available (StopLoss is a price level; convert to distance from current price)
                        if (signal != null && signal.StopLoss.HasValue && signal.StopLoss.Value > 0)
                        {
                            stopDist = Math.Abs(signal.StopLoss.Value - price);
                        }
                        else
                        {
                            // b) Try ATR-based heuristic using RiskSettings.StopLossAtrMultiplier if accessible via reflection; else use DefaultStopDistance
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
                                        // DefaultStopDistance fallback
                                        var defProp = rsVal.GetType().GetProperty("DefaultStopDistance");
                                        if (defProp != null)
                                        {
                                            var def = defProp.GetValue(rsVal);
                                            if (def is double ds && ds > 0 && atr <= 0)
                                            {
                                                stopDist = ds;
                                            }
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
                            if (stopDist <= 0)
                            {
                                // final fallback to a small default
                                stopDist = 0.05;
                            }
                        }
                        requestedUnits = Math.Max(1.0, _riskManager.CalculateOrderSize(stopDist, pointValue));
                        // compute lots for logging
                        lotSize = ReadSymbolLotSizeOrDefault();
                        theoreticalLots = (lotSize > 0) ? (requestedUnits / lotSize) : requestedUnits;
                    }
                    catch (Exception ex)
                    {
                        // FAIL-FAST: Risk sizing is mandatory, no fallback
                        _bot.Print($"[ExecutionModule] FATAL: Risk sizing calculation failed: {ex.Message}");
                        throw new InvalidOperationException("Risk sizing calculation failed - cannot proceed with order", ex);
                    }
                }
                else
                {
                    // FAIL-FAST: RiskManager must be initialized
                    _bot.Print("[ExecutionModule] FATAL: RiskManager not initialized");
                    throw new InvalidOperationException("RiskManager not initialized - cannot proceed with order");
                }
                // derive stopLoss for logging: prefer signal.StopLoss; else try ATR*multiplier; else blank
                var act = signal != null ? signal.Action : TradeAction.None;
                double? stopLossLog = null;
                try
                {
                    if (signal != null && signal.StopLoss.HasValue && signal.StopLoss.Value > 0)
                    {
                        stopLossLog = signal.StopLoss.Value;
                    }
                    else
                    {
                        double atr = 0.0; double mult = 1.0;
                        // attempt to read StopLossAtrMultiplier from RiskSettings
                        try
                        {
                            if (_riskManager != null)
                            {
                                var rsField = typeof(RiskManager.RiskManager).GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                var rsVal = rsField?.GetValue(_riskManager);
                                if (rsVal != null)
                                {
                                    var prop = rsVal.GetType().GetProperty("StopLossAtrMultiplier");
                                    if (prop != null)
                                    {
                                        var val = prop.GetValue(rsVal);
                                        if (val is double d && d > 0) mult = d;
                                    }
                                }
                            }
                        }
                        catch { }
                        // TODO: wire real ATR source; for now, leave blank if not available
                        if (atr > 0)
                        {
                            stopLossLog = act == TradeAction.Buy ? (price - atr * mult) : (price + atr * mult);
                        }
                    }
                }
                catch { }
                // REQUEST log with theoretical lots/units and requested volume
                var sideStr = act == TradeAction.Buy ? "Buy" : (act == TradeAction.Sell ? "Sell" : "Exit");
                _orderLogger.LogV2(
                    phase: "REQUEST",
                    orderId: orderId,
                    clientOrderId: orderId,
                    side: sideStr,
                    action: act.ToString(),
                    type: "Market",
                    intendedPrice: price,
                    stopLoss: stopLossLog,
                    execPrice: null,
                    theoreticalLots: theoreticalLots,
                    theoreticalUnits: requestedUnits,
                    requestedVolume: requestedUnits,
                    filledSize: null,
                    status: "REQUEST",
                    reason: null,
                    session: GetSymbolName()
                );
                _telemetry.IncOrderRequested();

                int attempts = 0;
                while (true)
                {
                    try
                    {
            ExecuteResult result;
                        switch (act)
                        {
                            case TradeAction.Buy:
                result = _executor.ExecuteMarketOrder(TradeType.Buy, GetSymbolName(), Math.Round(requestedUnits), "Bot3", null, null);
                                break;
                            case TradeAction.Sell:
                result = _executor.ExecuteMarketOrder(TradeType.Sell, GetSymbolName(), Math.Round(requestedUnits), "Bot3", null, null);
                                break;
                            case TradeAction.Exit:
                                if (_bot != null)
                                {
                                foreach (var pos in _bot.Positions)
                                {
                                    if (pos.SymbolName == _bot.SymbolName)
                                    {
                                        _bot.ClosePosition(pos);
                                    }
                                }
                                }
                                _orderLogger.LogV2("ACK", orderId, orderId, sideStr, act.ToString(), "Exit",
                                    price, stopLossLog, null, theoreticalLots, requestedUnits, requestedUnits, null, "ACK", "Exit processed", GetSymbolName());
                                return;
                            default:
                                return;
                        }
                        double? execPrice = result?.EntryPrice;
                        double? filled = result?.FilledVolumeInUnits;
                        string brokerMsg = (result?.IsSuccessful == true) ? "OK" : (result?.ErrorText ?? "");
                        // Log ACK with any available executed price/fill info
                        _orderLogger.LogV2("ACK", orderId, orderId, sideStr, act.ToString(), "Market",
                            price, stopLossLog, execPrice, theoreticalLots, requestedUnits, requestedUnits, filled, "ACK", brokerMsg, GetSymbolName());
                        if (result?.IsSuccessful == true && filled.HasValue && filled.Value > 0)
                        {
                            _orderLogger.LogV2("FILL", orderId, orderId, sideStr, act.ToString(), "Market",
                                price, stopLossLog, execPrice, theoreticalLots, requestedUnits, requestedUnits, filled, "FILL", "filled", GetSymbolName());
                            _telemetry.IncOrderFilled();
                        }
                        break;
                    }
                    catch (Exception ex) when (attempts < _retryLimit)
                    {
                        attempts++;
                        _orderLogger.LogV2("ERROR", orderId, orderId, sideStr, act.ToString(), "Market",
                            price, stopLossLog, null, theoreticalLots, requestedUnits, requestedUnits, null, "ERROR", ex.Message, GetSymbolName());
                        _telemetry.IncError();
                        // brief backoff
                        System.Threading.Thread.Sleep(200);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                _bot?.Print($"Execution error: {ex.Message}");
                try { _telemetry.IncError(); } catch {}
            }
        }

        private string GetSymbolName()
        {
            try { return _bot?.SymbolName ?? "TEST"; } catch { return "TEST"; }
        }

        private double ReadSymbolLotSizeOrDefault()
        {
            try
            {
                if (_bot == null)
                {
                    return (_riskManager != null ? _riskManager.GetSettings().LotSizeDefault : 1000);
                }

                var symbolObj = _bot.GetType().GetProperty("Symbol")?.GetValue(_bot);
                if (symbolObj == null)
                {
                    return (_riskManager != null ? _riskManager.GetSettings().LotSizeDefault : 1000);
                }

                // Try direct known properties first
                var symbolType = symbolObj.GetType();
                var lotSizeProp = symbolType.GetProperty("LotSize")
                                 ?? symbolType.GetProperty("VolumeInUnits")
                                 ?? symbolType.GetProperty("VolumeMin");
                if (lotSizeProp != null)
                {
                    var val = lotSizeProp.GetValue(symbolObj);
                    if (val is double d) return d;
                    if (val is int i) return i;
                    if (val is long l) return l;
                    if (val is float f) return f;
                    if (double.TryParse(val?.ToString(), out var parsed)) return parsed;
                }
            }
            catch { }
            // ultimate fallback
            return (_riskManager != null ? _riskManager.GetSettings().LotSizeDefault : 1000);
        }
    }

    // Default executor that delegates to cAlgo Robot for live runs
    internal class RobotOrderExecutor : IOrderExecutor
    {
        private readonly Robot _bot;
        public RobotOrderExecutor(Robot bot) { _bot = bot; }
        public ExecuteResult ExecuteMarketOrder(TradeType type, string symbolName, double volume, string label, double? stopLoss, double? takeProfit)
        {
            // cAlgo Robot expects volume in units (long). We round here.
            var tr = _bot.ExecuteMarketOrder(type, symbolName, Convert.ToInt64(Math.Round(volume)), label, stopLoss, takeProfit);
            return new ExecuteResult
            {
                IsSuccessful = tr?.IsSuccessful == true,
                ErrorText = tr?.Error.ToString() ?? string.Empty,
                EntryPrice = tr?.Position?.EntryPrice,
                FilledVolumeInUnits = tr?.Position?.VolumeInUnits
            };
        }
    }
}
