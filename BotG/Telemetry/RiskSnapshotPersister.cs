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
                File.AppendAllText(_filePath, "timestamp_iso,balance,equity,usedMargin,freeMargin,marginUtilPercent" + Environment.NewLine);
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
                double marginUtil = equity > 0 ? (usedMargin / equity) * 100.0 : 0.0;
                var line = string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    balance.ToString(CultureInfo.InvariantCulture),
                    equity.ToString(CultureInfo.InvariantCulture),
                    usedMargin.ToString(CultureInfo.InvariantCulture),
                    freeMargin.ToString(CultureInfo.InvariantCulture),
                    marginUtil.ToString(CultureInfo.InvariantCulture)
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
