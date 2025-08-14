using System;
using System.Collections.Generic;
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
