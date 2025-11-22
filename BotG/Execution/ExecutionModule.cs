using System;
using System.Collections.Generic;
using System.Globalization;
using Bot3.Core;
using cAlgo.API;
using Strategies;
using Telemetry; // added
using BotG.PositionManagement;
using RiskManager = BotG.RiskManager; // allow optional RiskManager injection (alias)

namespace Execution
{
    public class ExecutionModule
    {
    private readonly IReadOnlyList<IStrategy> _strategies;
    private readonly Robot? _bot;
    private readonly IOrderExecutor _executor;

        // telemetry
        private readonly OrderLifecycleLogger _orderLogger;
        private readonly TelemetryCollector _telemetry;
        private readonly int _retryLimit = 1;
        // optional risk manager for order sizing
    private readonly RiskManager.RiskManager? _riskManager;
    private bool _pointValuePrinted = false; // ensure single startup print
    private double _feePipsRoundtrip = 0.0;
    private double _symbolPipSize = 0.0;
    private const double MinEffectiveStopLossPips = 0.1;
    private const double MinEffectiveTakeProfitPips = 0.1;

        // backward-compatible constructor
    public ExecutionModule(IReadOnlyList<IStrategy> strategies, Robot? bot)
            : this(strategies, bot, null) {}

        // new constructor with optional RiskManager for sizing
    public ExecutionModule(IReadOnlyList<IStrategy> strategies, Robot? bot, RiskManager.RiskManager? riskManager)
        {
            _strategies = strategies ?? Array.Empty<IStrategy>();
            _bot = bot;
            _executor = new RobotOrderExecutor(bot ?? new cAlgo.API.Robot());
            var telemetryConfig = TelemetryContext.Config ?? TelemetryConfig.Load();
            _orderLogger = TelemetryContext.OrderLogger ?? new OrderLifecycleLogger(telemetryConfig.LogPath, telemetryConfig.OrderLogFile, null, null, null, telemetryConfig);
            _telemetry = TelemetryContext.Collector ?? new TelemetryCollector(telemetryConfig.LogPath, telemetryConfig.TelemetryFile, telemetryConfig.FlushIntervalSeconds);
            _riskManager = riskManager;
            ApplySymbolDerivedExecutionConfig(telemetryConfig);
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
    public ExecutionModule(IReadOnlyList<IStrategy> strategies, IOrderExecutor executor, Robot? bot, RiskManager.RiskManager? riskManager = null)
        {
            _strategies = strategies ?? Array.Empty<IStrategy>();
            _bot = bot;
            _executor = executor;
            var cfg = TelemetryConfig.Load();
            _orderLogger = new OrderLifecycleLogger(cfg.LogPath, cfg.OrderLogFile, null, null, null, cfg);
            _telemetry = new TelemetryCollector(cfg.LogPath, cfg.TelemetryFile, cfg.FlushIntervalSeconds);
            _riskManager = riskManager;
            ApplySymbolDerivedExecutionConfig(cfg);
        }

        public void Execute(Signal signal, double price)
        {
            try
            {
                AdjustSignalForFees(signal, price);
                string orderId = Guid.NewGuid().ToString("N");
                string orderLabel = PositionLabelHelper.BuildStrategyLabel(signal?.StrategyName);
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
                        requestedUnits = _riskManager.CalculateOrderSize(stopDist, pointValue);
                        if (_bot != null)
                        {
                            try
                            {
                                requestedUnits = _riskManager.NormalizeUnitsForSymbol(_bot.Symbol, requestedUnits);
                            }
                            catch { }
                        }
                        else
                        {
                            requestedUnits = Math.Round(requestedUnits, 4);
                        }

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
                        try
                        {
                            var diag = new System.Collections.Generic.Dictionary<string, object?>
                            {
                                ["requestedUnits"] = requestedUnits,
                                ["theoreticalLots"] = theoreticalLots,
                                ["symbol"] = GetSymbolName(),
                                ["action"] = act.ToString()
                            };
                            BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "DISPATCH", "pre_exec", diag, null);
                        }
                        catch { }
                        switch (act)
                        {
                            case TradeAction.Buy:
                                result = _executor.ExecuteMarketOrder(TradeType.Buy, GetSymbolName(), requestedUnits, orderLabel, null, null);
                                break;
                            case TradeAction.Sell:
                                result = _executor.ExecuteMarketOrder(TradeType.Sell, GetSymbolName(), requestedUnits, orderLabel, null, null);
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
                            BotG.Runtime.Logging.PipelineLogger.Log(
                                "TRADE",
                                "Fill",
                                $"Order {orderId} filled",
                                new
                                {
                                    order_id = orderId,
                                    strategy = signal?.StrategyName,
                                    action = act.ToString(),
                                    symbol = GetSymbolName(),
                                    price = execPrice,
                                    filled_units = filled
                                },
                                null);
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

        private void ApplySymbolDerivedExecutionConfig(TelemetryConfig? telemetryConfig)
        {
            if (telemetryConfig == null) return;
            if (_bot == null) return;
            var symbol = _bot.Symbol;
            if (symbol == null) return;

            telemetryConfig.Execution ??= new ExecutionConfig();
            var exec = telemetryConfig.Execution;
            var updates = new List<string>();

            _symbolPipSize = TryReadSymbolDouble(symbol, 0.0, "PipSize", "TickSize");
            if (_symbolPipSize <= 0)
            {
                _symbolPipSize = symbol?.PipSize > 0 ? symbol.PipSize : 0.0001;
            }

            double spreadPips = TryGetSpreadPips(symbol);
            if (spreadPips > 0 && Math.Abs(exec.SpreadPips - spreadPips) > 1e-9)
            {
                exec.SpreadPips = spreadPips;
                exec.SpreadPipsMin = spreadPips;
                exec.SpreadPipsFallback = spreadPips;
                updates.Add($"spread_pips={spreadPips:G4}");
            }

            double commission = TryComputeCommissionRoundtripUsdPerLot(symbol);
            if (commission > 0 && Math.Abs(exec.CommissionRoundtripUsdPerLot - commission) > 1e-9)
            {
                exec.CommissionRoundtripUsdPerLot = commission;
                exec.CommissionRoundturnUsdPerLot = commission;
                exec.FeeRoundturnUsdPerLot = commission;
                updates.Add($"commission_roundtrip_usd_per_lot={commission:G4}");
            }

            double lotSize = TryReadSymbolDouble(symbol, 0.0, "LotSize", "ContractSize");
            if (lotSize <= 0)
            {
                lotSize = ReadSymbolLotSizeOrDefault();
            }

            double minVolume = TryReadSymbolDouble(symbol, 0.0, "VolumeInUnitsMin", "MinTradeVolume", "VolumeMin");
            if (lotSize > 0 && minVolume > 0)
            {
                var minLot = minVolume / lotSize;
                if (minLot > 0 && (exec.MinLot <= 0 || Math.Abs(exec.MinLot - minLot) > 1e-9))
                {
                    exec.MinLot = minLot;
                    updates.Add($"min_lot={minLot:G4}");
                }
            }

            double stepVolume = TryReadSymbolDouble(symbol, 0.0, "VolumeInUnitsStep", "VolumeStep");
            if (lotSize > 0 && stepVolume > 0)
            {
                var lotStep = stepVolume / lotSize;
                if (lotStep > 0 && (exec.LotStep <= 0 || Math.Abs(exec.LotStep - lotStep) > 1e-9))
                {
                    exec.LotStep = lotStep;
                    updates.Add($"lot_step={lotStep:G4}");
                }
            }

            if (updates.Count > 0)
            {
                try { _bot.Print("[Startup] Auto-ingested execution config: " + string.Join(", ", updates)); } catch { }
            }

            // Compute pip-equivalent fee snapshot (commission + spread)
            double pipValuePerLot = TryReadSymbolDouble(symbol, 0.0, "PipValue");
            pipValuePerLot = NormalizePipValuePerLot(symbol, pipValuePerLot);

            double spreadForFees = spreadPips;
            if (spreadForFees <= 0)
            {
                spreadForFees = exec.SpreadPipsMin > 0 ? exec.SpreadPipsMin : exec.SpreadPipsFallback;
            }

            double commissionPips = 0.0;
            if (commission > 0 && pipValuePerLot > 0)
            {
                commissionPips = commission / pipValuePerLot;
            }

            double swapLongPips = TryReadSymbolDouble(symbol, double.NaN, "SwapLongInPips", "SwapLongPoints", "SwapLong");
            if (!double.IsNaN(swapLongPips) && Math.Abs(exec.SwapLongPipsPerDay - swapLongPips) > 1e-9)
            {
                exec.SwapLongPipsPerDay = swapLongPips;
                updates.Add($"swap_long_pips_per_day={swapLongPips:G4}");
            }
            else if (double.IsNaN(swapLongPips))
            {
                swapLongPips = exec.SwapLongPipsPerDay;
            }

            double swapShortPips = TryReadSymbolDouble(symbol, double.NaN, "SwapShortInPips", "SwapShortPoints", "SwapShort");
            if (!double.IsNaN(swapShortPips) && Math.Abs(exec.SwapShortPipsPerDay - swapShortPips) > 1e-9)
            {
                exec.SwapShortPipsPerDay = swapShortPips;
                updates.Add($"swap_short_pips_per_day={swapShortPips:G4}");
            }
            else if (double.IsNaN(swapShortPips))
            {
                swapShortPips = exec.SwapShortPipsPerDay;
            }

            var swapType = TryReadSymbolEnum(symbol, "SwapType");
            if (!string.IsNullOrWhiteSpace(swapType) && !string.Equals(exec.SwapType, swapType, StringComparison.OrdinalIgnoreCase))
            {
                exec.SwapType = swapType;
                updates.Add($"swap_type={swapType}");
            }

            var swapTripleDay = TryReadSymbolEnum(symbol, "SwapTripleDay");
            if (!string.IsNullOrWhiteSpace(swapTripleDay) && !string.Equals(exec.SwapTripleDay, swapTripleDay, StringComparison.OrdinalIgnoreCase))
            {
                exec.SwapTripleDay = swapTripleDay;
                updates.Add($"swap_triple_day={swapTripleDay}");
            }

            double totalFeePips = 0.0;
            if (spreadForFees > 0) totalFeePips += spreadForFees;
            if (commissionPips > 0) totalFeePips += commissionPips;

            if (totalFeePips > 0)
            {
                exec.FeePipsPerRoundtrip = totalFeePips;
                exec.FeePipsPerSide = totalFeePips / 2.0;
                _feePipsRoundtrip = totalFeePips;
                try { _bot.Print($"[Startup] Fee profile: {totalFeePips:G4} pips/roundtrip (spread={spreadForFees:G4}, commissionInPips={commissionPips:G4})"); } catch { }
            }
            else
            {
                _feePipsRoundtrip = 0.0;
                exec.FeePipsPerRoundtrip = 0.0;
                exec.FeePipsPerSide = 0.0;
            }

            PersistSymbolFeeSnapshot(telemetryConfig, exec, GetSymbolName(), spreadForFees, commission, totalFeePips, swapLongPips, swapShortPips);
        }

        private static void PersistSymbolFeeSnapshot(TelemetryConfig? telemetryConfig, ExecutionConfig exec, string symbolName, double spreadPips, double commissionUsdPerLot, double feePipsPerRoundtrip, double swapLongPips, double swapShortPips)
        {
            if (telemetryConfig == null)
            {
                return;
            }

            try
            {
                string? folder = TelemetryContext.RunFolder;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    folder = telemetryConfig.RunFolder;
                }
                if (string.IsNullOrWhiteSpace(folder))
                {
                    folder = telemetryConfig.LogPath;
                }
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return;
                }

                var writer = new RunMetadataWriter(folder);
                var snapshot = new SymbolFeeProfileSnapshot
                {
                    Symbol = symbolName,
                    SpreadPips = spreadPips,
                    CommissionRoundtripUsdPerLot = commissionUsdPerLot,
                    FeePipsPerRoundtrip = feePipsPerRoundtrip,
                    FeePipsPerSide = feePipsPerRoundtrip > 0 ? feePipsPerRoundtrip / 2.0 : 0.0,
                    SwapLongPipsPerDay = double.IsNaN(swapLongPips) ? 0.0 : swapLongPips,
                    SwapShortPipsPerDay = double.IsNaN(swapShortPips) ? 0.0 : swapShortPips,
                    SwapType = exec.SwapType,
                    SwapTripleDay = exec.SwapTripleDay,
                    CapturedAtIso = DateTime.UtcNow.ToString("o")
                };
                writer.UpsertSymbolFeeProfile(symbolName, snapshot);
            }
            catch
            {
                // telemetry/logging only
            }
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

        private void AdjustSignalForFees(Signal signal, double entryPrice)
        {
            if (signal == null) return;
            if (_feePipsRoundtrip <= 0 || _symbolPipSize <= 0) return;
            if (signal.Action != TradeAction.Buy && signal.Action != TradeAction.Sell) return;

            double direction = signal.Action == TradeAction.Buy ? 1.0 : -1.0;
            double feePips = _feePipsRoundtrip;

            if (signal.StopLoss.HasValue)
            {
                double original = signal.StopLoss.Value;
                var adjusted = AdjustStopLossPrice(entryPrice, original, direction, feePips);
                if (adjusted.HasValue && Math.Abs(adjusted.Value - original) > 1e-10)
                {
                    signal.StopLoss = adjusted.Value;
                    try
                    {
                        _bot?.Print($"[Fees] Adjusted SL from {original:F5} to {adjusted.Value:F5} (feePips={feePips:G4})");
                    }
                    catch { }
                }
            }

            if (signal.TakeProfit.HasValue)
            {
                double original = signal.TakeProfit.Value;
                var adjusted = AdjustTakeProfitPrice(entryPrice, original, direction, feePips);
                if (adjusted.HasValue && Math.Abs(adjusted.Value - original) > 1e-10)
                {
                    signal.TakeProfit = adjusted.Value;
                    try
                    {
                        _bot?.Print($"[Fees] Adjusted TP from {original:F5} to {adjusted.Value:F5} (feePips={feePips:G4})");
                    }
                    catch { }
                }
            }
        }

        private double? AdjustStopLossPrice(double entryPrice, double stopPrice, double direction, double feePips)
        {
            double distance = direction > 0 ? entryPrice - stopPrice : stopPrice - entryPrice;
            if (distance <= 0) return stopPrice;
            double distancePips = distance / _symbolPipSize;
            if (distancePips <= 0) return stopPrice;

            double adjustedPips = distancePips - feePips;
            bool clamped = false;
            if (adjustedPips < MinEffectiveStopLossPips)
            {
                adjustedPips = MinEffectiveStopLossPips;
                clamped = true;
            }
            if (Math.Abs(adjustedPips - distancePips) < 1e-9) return stopPrice;

            double adjustedDistance = adjustedPips * _symbolPipSize;
            if (clamped)
            {
                try { _bot?.Print($"[Fees] StopLoss clamped to {adjustedPips:G4} pips (fees >= configured distance)"); } catch { }
            }
            return direction > 0 ? entryPrice - adjustedDistance : entryPrice + adjustedDistance;
        }

        private double? AdjustTakeProfitPrice(double entryPrice, double takeProfitPrice, double direction, double feePips)
        {
            double distance = direction > 0 ? takeProfitPrice - entryPrice : entryPrice - takeProfitPrice;
            if (distance <= 0) return takeProfitPrice;
            double distancePips = distance / _symbolPipSize;
            if (distancePips <= 0) return takeProfitPrice;

            double adjustedPips = distancePips + feePips;
            if (adjustedPips < MinEffectiveTakeProfitPips)
            {
                adjustedPips = MinEffectiveTakeProfitPips;
            }
            if (Math.Abs(adjustedPips - distancePips) < 1e-9) return takeProfitPrice;

            double adjustedDistance = adjustedPips * _symbolPipSize;
            return direction > 0 ? entryPrice + adjustedDistance : entryPrice - adjustedDistance;
        }

        private static double TryGetSpreadPips(object symbol)
        {
            double pipSize = TryReadSymbolDouble(symbol, 0.0, "PipSize", "TickSize");
            if (pipSize <= 0)
            {
                pipSize = 0.0001;
            }

            double spread = TryReadSymbolDouble(symbol, 0.0, "Spread");
            if (spread > 0)
            {
                if (spread < 1.0 && pipSize > 0)
                {
                    return spread / pipSize;
                }
                return spread;
            }

            double spreadPips = TryReadSymbolDouble(symbol, 0.0, "SpreadPips", "SpreadInPips");
            if (spreadPips > 0)
            {
                return spreadPips;
            }

            double ask = TryReadSymbolDouble(symbol, 0.0, "Ask", "BestAsk");
            double bid = TryReadSymbolDouble(symbol, 0.0, "Bid", "BestBid");
            if (pipSize > 0 && ask > 0 && bid > 0)
            {
                var diff = Math.Abs(ask - bid);
                if (diff > 0)
                {
                    return diff / pipSize;
                }
            }

            double spreadRaw = TryReadSymbolDouble(symbol, 0.0, "SpreadRaw");
            if (spreadRaw > 0 && pipSize > 0)
            {
                return spreadRaw / pipSize;
            }

            return 0.0;
        }

        private double NormalizePipValuePerLot(object symbol, double pipValuePerLot)
        {
            const double minReasonable = 0.01; // pip value per lot should not be tiny
            if (!double.IsNaN(pipValuePerLot) && pipValuePerLot >= minReasonable)
            {
                return pipValuePerLot;
            }

            double tickValue = TryReadSymbolDouble(symbol, 0.0, "TickValue");
            double tickSize = TryReadSymbolDouble(symbol, 0.0, "TickSize");
            if (tickValue > 0 && tickSize > 0 && _symbolPipSize > 0)
            {
                var computed = tickValue * (_symbolPipSize / tickSize);
                if (computed >= minReasonable)
                {
                    return computed;
                }
            }

            double lotSize = ReadSymbolLotSizeOrDefault();
            if (_riskManager != null)
            {
                try
                {
                    var settings = _riskManager.GetSettings();
                    if (settings != null && settings.PointValuePerUnit > 0 && lotSize > 0)
                    {
                        var computed = settings.PointValuePerUnit * lotSize;
                        if (computed >= minReasonable)
                        {
                            return computed;
                        }
                    }
                }
                catch { }
            }

            return Math.Max(pipValuePerLot, minReasonable);
        }

        private static double TryComputeCommissionRoundtripUsdPerLot(object symbol)
        {
            double commission = TryReadSymbolDouble(symbol, 0.0, "Commission");
            if (commission <= 0)
            {
                return 0.0;
            }

            string commissionType = TryReadSymbolEnum(symbol, "CommissionType");
            string handSide = TryReadSymbolEnum(symbol, "CommissionHandSide");
            double perSidePerLot = commission;

            if (!string.IsNullOrEmpty(commissionType) && commissionType.IndexOf("PerMillion", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                double contractSize = TryReadSymbolDouble(symbol, 0.0, "ContractSize", "LotSize");
                if (contractSize <= 0)
                {
                    contractSize = TryReadSymbolDouble(symbol, 0.0, "VolumeInUnitsStep", "VolumeInUnitsMin");
                }

                if (contractSize <= 0)
                {
                    return 0.0;
                }

                perSidePerLot = commission * (contractSize / 1_000_000.0);
            }

            if (!string.IsNullOrEmpty(handSide) && handSide.IndexOf("Both", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return perSidePerLot;
            }

            // Default assumption: commission reported per side, so multiply by 2 for roundtrip
            return perSidePerLot * 2.0;
        }

        private static double TryReadSymbolDouble(object symbol, double fallback, params string[] propertyNames)
        {
            if (symbol == null || propertyNames == null || propertyNames.Length == 0)
            {
                return fallback;
            }

            foreach (var name in propertyNames)
            {
                try
                {
                    var prop = symbol.GetType().GetProperty(name);
                    if (prop == null) continue;
                    var raw = prop.GetValue(symbol);
                    if (raw == null) continue;

                    switch (raw)
                    {
                        case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                            return d;
                        case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                            return f;
                        case int i:
                            return i;
                        case long l:
                            return l;
                        case decimal dec:
                            return (double)dec;
                        case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                            return parsed;
                        default:
                            if (double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var conv))
                            {
                                return conv;
                            }
                            break;
                    }
                }
                catch
                {
                    // continue trying other property names
                }
            }

            return fallback;
        }

        private static string TryReadSymbolEnum(object symbol, string propertyName)
        {
            if (symbol == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return string.Empty;
            }

            try
            {
                var prop = symbol.GetType().GetProperty(propertyName);
                var value = prop?.GetValue(symbol);
                return value?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    // Default executor that delegates to cAlgo Robot for live runs
    internal class RobotOrderExecutor : IOrderExecutor
    {
        private readonly Robot _bot;
        public RobotOrderExecutor(Robot bot) { _bot = bot; }
        public ExecuteResult ExecuteMarketOrder(TradeType type, string symbolName, double volume, string label, double? stopLoss, double? takeProfit)
        {
            TradeResult? tr = null;
            try
            {
                var symbol = _bot?.Symbols?.GetSymbol(symbolName);
                if (symbol != null)
                {
                    // ExecuteMarketOrder expects volumeInUnits (long), not lots
                    // volume parameter is already in units from NormalizeUnitsForSymbol
                    long volumeInUnits = Convert.ToInt64(Math.Round(volume));
                    if (volumeInUnits <= 0)
                    {
                        volumeInUnits = 1;
                    }
                    try { _bot?.Print($"[OrderVolumeDebug] {symbolName} -> units={volumeInUnits}, requested={volume}"); } catch { }
                    tr = _bot.ExecuteMarketOrder(type, symbol, volumeInUnits, label, stopLoss, takeProfit);
                }
                else
                {
                    long fallbackUnits = Convert.ToInt64(Math.Round(volume));
                    if (fallbackUnits <= 0)
                    {
                        fallbackUnits = 1;
                    }
                    try { _bot?.Print($"[OrderVolumeDebug:Fallback] {symbolName} -> units={fallbackUnits}, requested={volume}"); } catch { }
                    tr = _bot.ExecuteMarketOrder(type, symbolName, fallbackUnits, label, stopLoss, takeProfit);
                }
            }
            catch
            {
                long fallbackUnits = Convert.ToInt64(Math.Round(Math.Max(1.0, volume)));
                if (fallbackUnits <= 0)
                {
                    fallbackUnits = 1;
                }
                try { _bot?.Print($"[OrderVolumeDebug:Catch] {symbolName} -> units={fallbackUnits}, requested={volume}"); } catch { }
                tr = _bot.ExecuteMarketOrder(type, symbolName, fallbackUnits, label, stopLoss, takeProfit);
            }
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
