using System;
using System.Collections.Generic;
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
        
        // Time-aware closed P&L tracking (A/B-LEDGER-001)
        private readonly List<(DateTime closeTime, double pnl)> _pendingClosedPnl = new List<(DateTime, double)>();
        
        // Paper mode equity model fields
        private double? _initialBalance = null;
        private readonly bool _isPaperMode;
        private readonly Func<double> _getOpenPnlCallback;

        public RiskSnapshotPersister(string folder, string fileName, bool isPaperMode = false, Func<double> getOpenPnlCallback = null)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            _isPaperMode = isPaperMode;
            _getOpenPnlCallback = getOpenPnlCallback;
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
        /// Update cumulative closed P&L when a trade closes (time-aware)
        /// Applies P&L to snapshots with timestamp >= closeTimeUtc
        /// </summary>
        public void AddClosedPnl(double pnl, DateTime closeTimeUtc)
        {
            lock (_lock)
            {
                _pendingClosedPnl.Add((closeTimeUtc, pnl));
            }
        }
        
        /// <summary>
        /// Legacy method for backward compatibility (immediate application)
        /// Applies P&L directly without pending queue
        /// </summary>
        public void AddClosedPnl(double pnl)
        {
            lock (_lock)
            {
                _closedPnl += pnl; // Immediate application for backward compatibility
            }
        }

        public void Persist(AccountInfo info)
        {
            try
            {
                if (info == null) return;
                var ts = DateTime.UtcNow;
                
                // Initialize balance on first persist
                if (!_initialBalance.HasValue)
                {
                    _initialBalance = info.Balance;
                }

                // Apply pending closed P&L for trades that closed before this snapshot
                lock (_lock)
                {
                    var toApply = _pendingClosedPnl.Where(x => x.closeTime <= ts).ToList();
                    foreach (var (closeTime, pnl) in toApply)
                    {
                        _closedPnl += pnl;
                        _pendingClosedPnl.Remove((closeTime, pnl));
                    }
                }

                // Get core account metrics
                double balance;
                double equity;
                double openPnl;
                double closedPnlSnapshot; // Renamed to avoid conflict
                
                lock (_lock)
                {
                    closedPnlSnapshot = _closedPnl;
                }
                
                if (_isPaperMode)
                {
                    // Paper mode: compute balance_model and equity_model
                    balance = _initialBalance.Value + closedPnlSnapshot;  // balance_model
                    openPnl = _getOpenPnlCallback?.Invoke() ?? 0.0;  // from positions
                    equity = balance + openPnl;  // equity_model
                }
                else
                {
                    // Live mode: use AccountInfo directly (broker updates)
                    balance = info.Balance;
                    equity = info.Equity;
                    openPnl = equity - balance;
                }
                
                double usedMargin = info.Margin;
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
                    openPnl.ToString(CultureInfo.InvariantCulture),
                    closedPnlSnapshot.ToString(CultureInfo.InvariantCulture),
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