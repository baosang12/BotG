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

namespace RiskManager
{
    /// <summary>
    /// Triển khai IRiskManager để giám sát và kiểm soát rủi ro cho bot.
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

        // telemetry
        private readonly System.Threading.Timer _snapshotTimer;
        private AccountInfo? _lastAccountInfo;
    // testing-only equity override (used by unit tests where cAlgo AccountInfo isn't available)
    private double? _equityOverride;

        // IModule implementation from Bot3.Core
        void IModule.Initialize(BotContext ctx)
        {
            Initialize(new RiskSettings());
            TelemetryContext.InitOnce();
            // Snapshot every FlushIntervalSeconds
            _snapshotTimer?.Change(TimeSpan.FromSeconds(TelemetryContext.Config.FlushIntervalSeconds), TimeSpan.FromSeconds(TelemetryContext.Config.FlushIntervalSeconds));
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
            _snapshotTimer = new System.Threading.Timer(_ => PersistSnapshotIfAvailable(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
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
        }

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
                if (equity <= 0) return 1.0;

                // Prefer instance properties; keep existing semantics (RiskPercentPerTrade is a fraction, e.g., 0.01 for 1%)
                double riskPercent = this.RiskPercentPerTrade;
                if (riskPercent <= 0) riskPercent = 0.01;
                double minRiskUsd = this.MinRiskUsdPerTrade;
                if (minRiskUsd <= 0) minRiskUsd = 1.0;
                double riskUsd = Math.Max(minRiskUsd, equity * riskPercent);

                // determine effective point value per unit
                double effectivePointValue = pointValuePerUnit;
                if (effectivePointValue <= 0)
                {
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

                // ensure positive non-zero riskPerUnit
                double riskPerUnit = Math.Max(1e-8, stopDistancePriceUnits * effectivePointValue);
                double units = Math.Floor(riskUsd / riskPerUnit);

                if (units < 1) units = 1;
                // optional cap to avoid absurd sizes; use config if exists
                double cap = 1_000_000; // safe cap
                if (units > cap) units = cap;
                return units;
            }
            catch
            {
                return 1.0;
            }
        }

        // Hook for external components to provide latest account info
        public void UpdateAccountInfo(AccountInfo info)
        {
            _lastAccountInfo = info;
        }

        private void PersistSnapshotIfAvailable()
        {
            try
            {
                if (_lastAccountInfo != null)
                {
                    TelemetryContext.RiskPersister?.Persist(_lastAccountInfo);
                }
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
            // TODO: thực thi backtest/forward simulation offline
        }

        public void CheckAlerts()
        {
            // TODO: email/SMS/Slack thông qua webhook
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
    }

}
