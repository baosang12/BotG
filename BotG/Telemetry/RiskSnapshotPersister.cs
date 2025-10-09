using System;
using System.Globalization;
using System.IO;
using System.Linq;
using DataFetcher.Models;

namespace Telemetry
{
    public class RiskSnapshotPersister
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private double _equityPeak = 0.0;
        private double _closedPnl = 0.0;

        public RiskSnapshotPersister(string folder, string fileName)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            EnsureHeader();
        }

        private void EnsureHeader()
        {
            if (!File.Exists(_filePath))
            {
                File.AppendAllText(_filePath, "timestamp_utc,equity,balance,open_pnl,closed_pnl,margin,free_margin,drawdown,R_used,exposure" + Environment.NewLine);
            }
        }

        /// <summary>
        /// Update cumulative closed P&L when a trade closes
        /// </summary>
        public void AddClosedPnl(double pnl)
        {
            lock (_lock)
            {
                _closedPnl += pnl;
            }
        }

        public void Persist(AccountInfo info)
        {
            try
            {
                if (info == null) return;
                var ts = DateTime.UtcNow;
                
                // Get core account metrics
                double balance = info.Balance;
                double equity = info.Equity;
                double usedMargin = info.Margin;
                double freeMargin = equity - usedMargin;

                // Calculate open_pnl: equity - balance (unrealized P&L from open positions)
                double openPnl = equity - balance;
                
                // Get closed_pnl from tracking
                double closedPnl;
                lock (_lock)
                {
                    closedPnl = _closedPnl;
                }

                // Track equity peak for drawdown calculation
                if (equity > _equityPeak)
                {
                    _equityPeak = equity;
                }
                double drawdown = _equityPeak - equity;

                // Placeholder for R_used and exposure (0.0 until RiskManager integration)
                double rUsed = 0.0;
                double exposure = 0.0;

                var line = string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    equity.ToString(CultureInfo.InvariantCulture),
                    balance.ToString(CultureInfo.InvariantCulture),
                    openPnl.ToString(CultureInfo.InvariantCulture),
                    closedPnl.ToString(CultureInfo.InvariantCulture),
                    usedMargin.ToString(CultureInfo.InvariantCulture),
                    freeMargin.ToString(CultureInfo.InvariantCulture),
                    drawdown.ToString(CultureInfo.InvariantCulture),
                    rUsed.ToString(CultureInfo.InvariantCulture),
                    exposure.ToString(CultureInfo.InvariantCulture)
                );
                
                lock (_lock)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
            }
            catch { /* swallow for safety */ }
        }
    }
}