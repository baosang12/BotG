using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using cAlgo.API;
using Position = cAlgo.API.Position;
using TradeResult = cAlgo.API.TradeResult;
using Bar = DataFetcher.Models.Bar;            // Data bar type for IRiskManager
using Bot3.Core;
using DataFetcher.Models; // added
using Telemetry; // added

namespace BotG.RiskManager
{
    /// <summary>
    /// Tri?n khai IRiskManager d? gi�m s�t v� ki?m so�t r?i ro cho bot.
    /// </summary>
    public class RiskManager : IRiskManager, IModule
    {
        // Removed ctx dependency for volume calculation
        public double RiskPercentPerTrade { get; set; } = 0.01;    // e.g. 1% per trade
        public double MinRiskUsdPerTrade { get; set; } = 3.0;      // minimum USD risk per trade
        private readonly Dictionary<int, double> _minRiskByLevel = new()
        {
            /* level -> minRisk USD mapping */
            { -2, 3.0 },
            { -1, 5.0 },
            {  0, 10.0 }
        };

        private RiskSettings _settings;
        private List<Position> _openPositions = new List<Position>();
        private DateTime _lastReportTime;
        private readonly object _sync = new object();

        private int _snapshotIntervalSeconds = 60;
        private AccountInfo _lastAccountInfo;
    // testing-only equity override (used by unit tests where cAlgo AccountInfo isn't available)
    private double? _equityOverride;
    // optional runtime symbol reference for auto-compute of PointValuePerUnit
    private object _symbolRef;

        // IModule implementation from Bot3.Core
        void IModule.Initialize(BotContext ctx)
        {
            Initialize(new RiskSettings());
            TelemetryContext.InitOnce();
            
            // KICKOFF: Write immediate snapshot at startup
            PersistSnapshotIfAvailable();
            
            // Read risk-specific flush interval from environment or use default 60s
            int riskFlushSec = 60;
            var envRiskFlush = Environment.GetEnvironmentVariable("BOTG_RISK_FLUSH_SEC");
            if (int.TryParse(envRiskFlush, out var sec) && sec > 0)
            {
                riskFlushSec = sec;
            }
            
            _snapshotIntervalSeconds = riskFlushSec;
        }
        void IModule.OnBar(IReadOnlyList<cAlgo.API.Bar> bars)
        {
            // No-op on bar event for risk manager
        }
        void IModule.OnTick(cAlgo.API.Tick tick)
        {
            // No-op on tick event for risk manager
            try { TelemetryContext.Collector?.IncTick(); } catch {}
        }

        public RiskManager()
        {
            // EMERGENCY A47: timer sẽ được quản lý bởi BotGRobot thông qua MainThreadTimer
        }

        public void Initialize(RiskSettings settings)
        {
            _settings = settings;
            _lastReportTime = DateTime.UtcNow;
            // Minimal runtime config reader for Risk.PointValuePerUnit (non-throwing)
            try
            {
                var cfgPath = Path.Combine(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory(), "config.runtime.json");
                if (File.Exists(cfgPath))
                {
                    var json = File.ReadAllText(cfgPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Risk", out var r))
                    {
                        if (r.TryGetProperty("PointValuePerUnit", out var pvp) && pvp.ValueKind == JsonValueKind.Number)
                        {
                            var v = pvp.GetDouble();
                            if (v > 0) _settings.PointValuePerUnit = v;
                        }
                        // Optionally hydrate instance risk knobs if present (backward compatible)
                        if (r.TryGetProperty("RiskPercentPerTrade", out var rpt) && rpt.ValueKind == JsonValueKind.Number)
                        {
                            var v = rpt.GetDouble();
                            if (v > 0 && v < 1) this.RiskPercentPerTrade = v;
                        }
                        if (r.TryGetProperty("MinRiskUsdPerTrade", out var mr) && mr.ValueKind == JsonValueKind.Number)
                        {
                            var v = mr.GetDouble();
                            if (v > 0) this.MinRiskUsdPerTrade = v;
                        }
                    }
                }
            }
            catch { /* ignore config errors; use defaults */ }

            // Attempt auto-compute from symbol if settings did not provide a value
            TryAutoComputePointValueFromSymbol();

            // Initialize telemetry context (ensure run folder exists)
            TelemetryContext.InitOnce();

            // KICKOFF: Write immediate snapshot at startup to ensure >= 1 row even for short runs
            PersistSnapshotIfAvailable();

            // Read risk-specific flush interval from environment or use default 60s
            int riskFlushSec = 60;
            var envRiskFlush = Environment.GetEnvironmentVariable("BOTG_RISK_FLUSH_SEC");
            if (int.TryParse(envRiskFlush, out var sec) && sec > 0)
            {
                riskFlushSec = sec;
            }
            
            _snapshotIntervalSeconds = riskFlushSec;
        }

        /// <summary>
        /// Compute and set PointValuePerUnit using broker symbol metadata.
        /// Uses either PipValue/PipSize or TickValue/TickSize per 1 lot and converts to per price unit per 1 volume unit.
        /// If computation fails or yields <= 0, settings are not overwritten.
        /// </summary>
        public void SetPointValueFromSymbol(cAlgo.API.Symbol sym)
        {
            TryComputePointValueFromSymbol(sym);
        }

        /// <summary>
        /// Overload for environments where Robot.Symbol is cAlgo.API.Internals.Symbol
        /// </summary>
        public void SetPointValueFromSymbol(cAlgo.API.Internals.Symbol sym)
        {
            TryComputePointValueFromSymbol(sym);
        }

        private void TryComputePointValueFromSymbol(object sym)
        {
            try
            {
                if (sym == null)
                {
                    Console.WriteLine("[RiskManager] Symbol is null; skip PointValuePerUnit compute");
                    return;
                }

                // reflectively read sizes/values to accommodate API differences
                double incSize = 0.0; // price increment size
                double incValuePerLot = 0.0; // monetary value per increment for 1 lot
                try
                {
                    // Prefer PipValue/PipSize if available
                    var pipSizeProp = sym.GetType().GetProperty("PipSize");
                    var pipValueProp = sym.GetType().GetProperty("PipValue");
                    if (pipSizeProp != null && pipValueProp != null)
                    {
                        incSize = Convert.ToDouble(pipSizeProp.GetValue(sym));
                        incValuePerLot = Convert.ToDouble(pipValueProp.GetValue(sym));
                    }
                }
                catch { }

                if (incSize <= 0 || incValuePerLot <= 0)
                {
                    try
                    {
                        var tickSizeProp = sym.GetType().GetProperty("TickSize");
                        var tickValueProp = sym.GetType().GetProperty("TickValue");
                        if (tickSizeProp != null && tickValueProp != null)
                        {
                            incSize = Convert.ToDouble(tickSizeProp.GetValue(sym));
                            incValuePerLot = Convert.ToDouble(tickValueProp.GetValue(sym));
                        }
                    }
                    catch { }
                }

                if (incSize <= 0 || incValuePerLot <= 0)
                {
                    Console.WriteLine("[RiskManager] Could not determine increment size/value from Symbol");
                    return;
                }

                // Determine volume units per 1 lot
                double unitsPerLot = 0.0;
                try
                {
                    var lotSizeProp = sym.GetType().GetProperty("LotSize");
                    if (lotSizeProp != null)
                    {
                        unitsPerLot = Convert.ToDouble(lotSizeProp.GetValue(sym));
                    }
                }
                catch { }

                if (unitsPerLot <= 0.0)
                {
                    try
                    {
                        var method = sym.GetType().GetMethod("LotsToVolumeInUnits", new Type[] { typeof(double) });
                        if (method != null)
                        {
                            var result = method.Invoke(sym, new object[] { 1.0 });
                            unitsPerLot = Convert.ToDouble(result);
                        }
                    }
                    catch { }
                }

                if (unitsPerLot <= 0.0)
                {
                    Console.WriteLine("[RiskManager] Could not determine units per lot");
                    return;
                }

                // Convert: (incValue per lot) / incSize => value per 1.0 price unit per 1 lot
                // Then divide by unitsPerLot to get value per 1.0 price unit per 1 volume unit
                double valuePerPriceUnitPerLot = incValuePerLot / incSize;
                double computed = valuePerPriceUnitPerLot / unitsPerLot;
                if (computed > 0)
                {
                    lock (_sync)
                    {
                        _settings.PointValuePerUnit = computed;
                    }
                    Console.WriteLine($"[RiskManager] Auto pointValuePerUnit={computed:G}");
                }
                else
                {
                    Console.WriteLine("[RiskManager] Computed PointValuePerUnit <= 0; skip overwrite");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RiskManager] Error computing PointValuePerUnit: {ex.Message}");
            }
        }

        /// <summary>
        /// Provide a runtime symbol reference for auto-computation of PointValuePerUnit.
        /// Accepts either cAlgo.API.Symbol or cAlgo.API.Internals.Symbol or a compatible shim.
        /// </summary>
        public void SetSymbolReference(object symbol)
        {
            _symbolRef = symbol;
            // compute immediately if needed
            TryAutoComputePointValueFromSymbol();
        }

        private void TryAutoComputePointValueFromSymbol()
        {
            try
            {
                double current;
                lock (_sync) { current = _settings?.PointValuePerUnit ?? 0.0; }
                if (current > 0) return;
                if (_symbolRef != null)
                {
                    TryComputePointValueFromSymbol(_symbolRef);
                }
                else
                {
                    Console.WriteLine("[RiskManager] No symbol reference; using fallback PointValuePerUnit");
                }
            }
            catch { }
        }

        /// <summary>
        /// Test-friendly setter: compute PointValuePerUnit from raw parameters.
        /// pointValuePerUnit = (tickValue / tickSize) / lotSize
        /// Thread-safe and non-throwing; logs outcome.
        /// </summary>
        public void SetPointValueFromParams(double tickSize, double tickValue, double lotSize)
        {
            try
            {
                if (tickSize <= 0 || tickValue <= 0 || lotSize <= 0)
                {
                    Console.WriteLine("[RiskManager] Invalid params for PointValuePerUnit; skip overwrite");
                    return;
                }
                double valuePerPriceUnitPerLot = tickValue / tickSize;
                double computed = valuePerPriceUnitPerLot / lotSize;
                if (computed > 0)
                {
                    lock (_sync)
                    {
                        _settings.PointValuePerUnit = computed;
                    }
                    Console.WriteLine($"[RiskManager] Set PointValuePerUnit from params: {computed:G}");
                }
                else
                {
                    Console.WriteLine("[RiskManager] Computed PointValuePerUnit <= 0 from params; skip overwrite");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RiskManager] Error in SetPointValueFromParams: {ex.Message}");
            }
        }

        /// <summary>
        /// Expose current immutable settings snapshot for read-only usage (logging).
        /// </summary>
        public RiskSettings GetSettings() => _settings ?? new RiskSettings();

        /// <summary>
        /// Testing-only: override equity used for sizing when AccountInfo is not available in unit tests.
        /// </summary>
        public void SetEquityOverrideForTesting(double equity)
        {
            _equityOverride = equity;
        }

        /// <summary>
        /// Calculate order size (units) based on risk in USD divided by (stop distance * point value per unit).
        /// units = floor(riskUsd / max(eps, stopDistancePriceUnits * pointValuePerUnit))
        /// Enforces minimum 1 unit and a reasonable cap.
        /// </summary>
        public double CalculateOrderSize(double stopDistancePriceUnits, double pointValuePerUnit)
        {
            try
            {
                double equity = 0.0;
                if (_equityOverride.HasValue) equity = _equityOverride.Value;
                else if (_lastAccountInfo != null) equity = (double)_lastAccountInfo.Equity;
                if (equity <= 0) return ApplyVolumeConstraints(1.0);

                // Prefer instance properties; keep existing semantics (RiskPercentPerTrade is a fraction, e.g., 0.01 for 1%)
                double riskPercent = this.RiskPercentPerTrade;
                if (riskPercent <= 0) riskPercent = 0.01;
                double minRiskUsd = this.MinRiskUsdPerTrade;
                if (minRiskUsd <= 0) minRiskUsd = 1.0;
                double riskUsd = Math.Max(minRiskUsd, equity * riskPercent);

                // Try lot-based sizing first
                double tickValuePerLotPerPriceUnit = 0.0;
                // 1) From settings if provided
                if (_settings != null && _settings.PointValuePerLot > 0)
                {
                    tickValuePerLotPerPriceUnit = _settings.PointValuePerLot;
                }
                else
                {
                    // 2) From symbol: assume TickValue is monetary value per 1 tick for 1 lot; TickSize is price units per tick
                    // Therefore value per 1.0 price unit for 1 lot is TickValue / TickSize
                    (double lotSizeSym, double tickSizeSym, double tickValueSym) = GetSymbolLotTick();
                    if (tickSizeSym > 0 && tickValueSym > 0)
                    {
                        tickValuePerLotPerPriceUnit = tickValueSym / tickSizeSym;
                    }
                }

                double units;
                if (tickValuePerLotPerPriceUnit > 0)
                {
                    double riskPerLot = stopDistancePriceUnits * tickValuePerLotPerPriceUnit;
                    if (riskPerLot > 0)
                    {
                        double lots = Math.Floor(riskUsd / riskPerLot);
                        // clamp by settings
                        double maxLots = (_settings != null && _settings.MaxLotsPerTrade > 0) ? _settings.MaxLotsPerTrade : 10.0;
                        if (lots > maxLots) lots = maxLots;

                        double minLots = GetSymbolMinLots();
                        if (lots < minLots) lots = minLots;

                        // convert to units using symbol LotSize or default
                        double lotSize = GetSymbolLotSizeOrDefault();
                        units = lots * lotSize;
                        try { Console.WriteLine($"[RiskManager] Sizing: equity={equity:G} riskUsd={riskUsd:G} stopDist={stopDistancePriceUnits:G} tickValuePerLot={tickValuePerLotPerPriceUnit:G} lotSize={lotSize:G} lots={lots:G} units={units:G}"); } catch {}
                        return ApplyVolumeConstraints(units);
                    }
                }

                // Fallback: unit-based sizing using pointValuePerUnit
                double effectivePointValue = pointValuePerUnit;
                if (effectivePointValue <= 0)
                {
                    // Attempt auto-compute if we don't have a valid setting yet
                    TryAutoComputePointValueFromSymbol();
                    if (_settings != null && _settings.PointValuePerUnit > 0)
                    {
                        effectivePointValue = _settings.PointValuePerUnit;
                        try { Console.WriteLine("[RiskManager] Using PointValuePerUnit from settings: " + effectivePointValue.ToString("G")); } catch {}
                    }
                    else
                    {
                        effectivePointValue = 1.0;
                        try { Console.WriteLine("[RiskManager] Using fallback PointValuePerUnit=1.0; configure Risk.PointValuePerUnit to correct broker value"); } catch {}
                    }
                }
                double riskPerUnit = Math.Max(1e-8, stopDistancePriceUnits * effectivePointValue);
                units = Math.Floor(riskUsd / riskPerUnit);
                double capUnits = 1_000_000; // safe cap for units in fallback mode
                if (units > capUnits) units = capUnits;
                units = ApplyVolumeConstraints(units);
                try { Console.WriteLine($"[RiskManager] Sizing: equity={equity:G} riskUsd={riskUsd:G} stopDist={stopDistancePriceUnits:G} tickValuePerLot=0 lots=0 units={units:G}"); } catch {}
                return units;
            }
            catch
            {
                return ApplyVolumeConstraints(1.0);
            }
        }

        private (double lotSize, double tickSize, double tickValue) GetSymbolLotTick()
        {
            double lotSize = GetSymbolLotSizeOrDefault();
            double tickSize = 0.0;
            double tickValue = 0.0;
            try
            {
                if (_symbolRef != null)
                {
                    var t = _symbolRef.GetType();
                    var ts = t.GetProperty("TickSize");
                    var tv = t.GetProperty("TickValue");
                    if (ts != null) tickSize = Convert.ToDouble(ts.GetValue(_symbolRef));
                    if (tv != null) tickValue = Convert.ToDouble(tv.GetValue(_symbolRef));
                }
            }
            catch { }
            return (lotSize, tickSize, tickValue);
        }

        private double GetSymbolLotSizeOrDefault()
        {
            double lotSize = 0.0;
            try
            {
                if (_symbolRef != null)
                {
                    var prop = _symbolRef.GetType().GetProperty("LotSize");
                    if (prop != null)
                    {
                        lotSize = Convert.ToDouble(prop.GetValue(_symbolRef));
                    }
                    if (lotSize <= 0)
                    {
                        var method = _symbolRef.GetType().GetMethod("LotsToVolumeInUnits", new Type[] { typeof(double) });
                        if (method != null)
                        {
                            var result = method.Invoke(_symbolRef, new object[] { 1.0 });
                            lotSize = Convert.ToDouble(result);
                        }
                    }
                }
            }
            catch { }
            if (lotSize <= 0)
            {
                lotSize = (_settings != null && _settings.LotSizeDefault > 0) ? _settings.LotSizeDefault : 100.0;
            }
            return lotSize;
        }

        private static double ExtractDouble(object source, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                try
                {
                    var prop = source.GetType().GetProperty(name);
                    if (prop == null) continue;
                    var raw = prop.GetValue(source);
                    if (raw == null) continue;
                    switch (raw)
                    {
                        case double d: return d;
                        case float f: return f;
                        case decimal dec: return (double)dec;
                        case int i: return i;
                        case long l: return l;
                        case short s: return s;
                    }
                    if (double.TryParse(Convert.ToString(raw), out var parsed))
                    {
                        return parsed;
                    }
                }
                catch { }
            }
            return 0.0;
        }

        private double GetSymbolVolumeMinOrDefault(object? symbolOverride = null)
        {
            var symbol = symbolOverride ?? _symbolRef;
            double min = 0.0;
            if (symbol != null)
            {
                // IMPORTANT: Only trust units-based properties. Do NOT fall back to lots-based VolumeMin.
                // Some instruments (FX) expose VolumeMin in lots (e.g., 0.01) which caused us to treat
                // 0.01 as units and send invalid tiny volumes. We now read only VolumeInUnitsMin.
                min = ExtractDouble(symbol, "VolumeInUnitsMin");
                if (min <= 0)
                {
                    // Derive from lot size if units min isn't exposed. Assume 0.01 lot minimum.
                    // Use symbol lot size when available; otherwise, fall back to settings.
                    double lotSize = GetSymbolLotSizeOrDefault();
                    if (lotSize > 0)
                    {
                        min = Math.Max(1.0, lotSize * 0.01);
                    }
                }
            }

            if (min <= 0 && _settings != null && _settings.LotSizeDefault > 0)
            {
                // Final fallback based on configured lot size
                min = Math.Max(1.0, _settings.LotSizeDefault * 0.01);
            }

            if (min <= 0) min = 1.0;
            return min;
        }

        private double GetSymbolVolumeStepOrDefault(object? symbolOverride = null)
        {
            var symbol = symbolOverride ?? _symbolRef;
            double step = 0.0;
            if (symbol != null)
            {
                // Same rationale as min: do not read lots-based VolumeStep.
                step = ExtractDouble(symbol, "VolumeInUnitsStep");
            }

            // If step is not available but we have a min, use min as a safe step (common on FX: 1000/1000)
            if (step <= 0)
            {
                var min = GetSymbolVolumeMinOrDefault(symbol);
                if (min > 0) step = min;
            }

            if (step < 0) step = 0;
            return step;
        }

        private double GetSymbolMinLots()
        {
            double lotSize = GetSymbolLotSizeOrDefault();
            if (lotSize <= 0) lotSize = 1.0;
            double minUnits = GetSymbolVolumeMinOrDefault();
            double minLots = minUnits / lotSize;
            if (minLots <= 0) minLots = 0.01;
            return minLots;
        }

        private static int GetStepDecimals(double step)
        {
            if (step <= 0) return 4;
            double log = Math.Log10(step);
            int decimals = (int)Math.Ceiling(-log);
            if (decimals < 0) decimals = 0;
            if (decimals > 8) decimals = 8;
            return decimals;
        }

        private double ApplyVolumeConstraints(double requestedUnits, object? symbolOverride = null)
        {
            double units = requestedUnits;
            if (_settings != null)
            {
                var multiplier = _settings.PositionSizeMultiplier;
                if (multiplier > 0 && Math.Abs(multiplier - 1.0) > double.Epsilon)
                {
                    multiplier = Math.Clamp(multiplier, 0.01, 10.0);
                    units *= multiplier;
                }
            }
            var symbol = symbolOverride ?? _symbolRef;

            double minUnits = GetSymbolVolumeMinOrDefault(symbol);
            double stepUnits = GetSymbolVolumeStepOrDefault(symbol);

            if (units < minUnits)
            {
                units = minUnits;
            }

            if (stepUnits > 0)
            {
                units = Math.Round(units / stepUnits) * stepUnits;
            }

            int decimals = stepUnits > 0 ? GetStepDecimals(stepUnits) : 4;
            units = Math.Round(units, decimals, MidpointRounding.AwayFromZero);

            if (units < minUnits)
            {
                units = minUnits;
            }

            if (units <= 0)
            {
                units = 1.0;
            }

            return units;
        }

        // Hook for external components to provide latest account info
        public void UpdateAccountInfo(AccountInfo info)
        {
            _lastAccountInfo = info;
        }

        internal TimeSpan SnapshotInterval => TimeSpan.FromSeconds(_snapshotIntervalSeconds <= 0 ? 60 : _snapshotIntervalSeconds);

        internal void CaptureSnapshotForScheduler()
        {
            PersistSnapshotIfAvailable();
        }

        private void PersistSnapshotIfAvailable()
        {
            try
            {
                var info = _lastAccountInfo;
                if (info == null)
                {
                    // Stub AccountInfo to ensure 60s heartbeat even without updates
                    info = new AccountInfo
                    {
                        Equity = _equityOverride ?? 10000.0,
                        Balance = 10000.0,
                        Margin = 0.0
                    };
                }
                TelemetryContext.RiskPersister?.Persist(info);
            }
            catch { }
        }

        public void EvaluateTradeRisk(Bar bar, double atr, double proposedSize, out double adjustedSize, out double stopLoss, out double takeProfit)
        {
            stopLoss = atr * _settings.StopLossAtrMultiplier;
            takeProfit = atr * _settings.TakeProfitAtrMultiplier;
            adjustedSize = Math.Max(1, (int)Math.Round(proposedSize));
        }

        public void EvaluatePortfolioRisk()
        {
            // TODO: implement portfolio risk checks
        }

        public void MonitorMargin()
        {
            // TODO: implement margin usage monitoring
        }

        public void ManageStops(Position position)
        {
            // TODO: implement stop management
        }

        public void MonitorSlippage(TradeResult result)
        {
            // TODO: implement slippage monitoring
        }

        public void RunStressTests()
        {
            // TODO: th?c thi backtest/forward simulation offline
        }

        public void CheckAlerts()
        {
            // TODO: email/SMS/Slack th�ng qua webhook
        }

        public void GenerateReports()
        {
            if ((DateTime.UtcNow - _lastReportTime).TotalHours >= 24)
            {
                _lastReportTime = DateTime.UtcNow;
            }
        }

        private int DetermineLevel(double equity)
        {
            if (equity < 50) return -3;
            if (equity < 100) return -2;
            if (equity < 200) return -1;
            return 0;
        }
        private double LookupMinRiskByLevel(int level)
        {
            return _minRiskByLevel.TryGetValue(level, out var m) ? m : MinRiskUsdPerTrade;
        }
        public double CalculateLotSize(double accountBalance, double stopLossPips, double pipValue, double minVolume)
        {
            int level = DetermineLevel(accountBalance);
            double minRisk = LookupMinRiskByLevel(level);
            double riskUsd = Math.Max(accountBalance * RiskPercentPerTrade, minRisk);
            if (stopLossPips <= 0 || pipValue <= 0)
                return minVolume;
            double units = riskUsd / (stopLossPips * pipValue);
            return Math.Max(units, minVolume);
        }

        /// <summary>
        /// Normalize requested volume in units to satisfy broker symbol constraints (min and step).
        /// Accepts a cAlgo symbol (either cAlgo.API.Symbol or cAlgo.API.Internals.Symbol).
        /// Returns an integer number of units, rounded down to step and never below min.
        /// </summary>
        public double NormalizeUnitsForSymbol(object symbol, double requestedUnits)
        {
            return ApplyVolumeConstraints(requestedUnits, symbol);
        }
    }

}
