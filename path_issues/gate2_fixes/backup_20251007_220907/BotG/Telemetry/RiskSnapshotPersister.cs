using System;
using System.Globalization;
using System.IO;
using DataFetcher.Models;

namespace Telemetry
{
    public class RiskSnapshotPersister
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private double _equityPeak = 0.0;

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
                File.AppendAllText(_filePath, "timestamp,equity,balance,margin,free_margin,drawdown,R_used,exposure" + Environment.NewLine);
            }
        }

        public void Persist(AccountInfo info)
        {
            try
            {
                if (info == null) return;
                var ts = DateTime.UtcNow;
                double equity = info.Equity;
                double usedMargin = info.Margin;
                double balance = info.Balance;
                double freeMargin = equity - usedMargin;

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
