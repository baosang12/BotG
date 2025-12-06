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
        private const double RiskUnitUsd = 10.0;
        private readonly string _filePath;
        private readonly object _lock = new object();
        private double _closedPnl = 0.0;
        private double? _sessionStartEquity = null;
        private double _sessionPeakEquity = 0.0;

        // Time-aware closed P&L tracking (A/B-LEDGER-001)
        private readonly List<(DateTime closeTime, double pnl)> _pendingClosedPnl = new List<(DateTime, double)>();

        // Paper mode equity model fields
        private double? _initialBalance = null;
        private readonly bool _isPaperMode;
        private readonly Func<double>? _getOpenPnlCallback;

        public RiskSnapshotPersister(string folder, string fileName, bool isPaperMode = false, Func<double>? getOpenPnlCallback = null)
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
                // A9: Enhanced header with portfolio metrics
                var header = "timestamp_utc,equity,balance,open_pnl,closed_pnl,margin,free_margin,drawdown,R_used,exposure," +
                             "long_exposure,short_exposure,net_exposure,largest_pos_pnl,largest_pos_pct," +
                             "most_exposed_symbol,most_exposed_volume,total_positions,long_positions,short_positions" +
                             Environment.NewLine;
                File.AppendAllText(_filePath, header);
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

        public void Persist(AccountInfo info, IEnumerable<PositionSnapshot>? positions = null)
        {
            // A8 DEBUG: Log entry to Persist()
            try
            {
                var debugLog = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "a8_persist_debug.log");
                File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] Persist() CALLED, info={(info == null ? "NULL" : "NOT NULL")}, positions={(positions == null ? "NULL" : positions.Count().ToString())}\n");
            }
            catch { }

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

                if (!_sessionStartEquity.HasValue)
                {
                    _sessionStartEquity = equity;
                    _sessionPeakEquity = equity;
                }
                else if (equity > _sessionPeakEquity)
                {
                    _sessionPeakEquity = equity;
                }

                double drawdown = SanitizeNonNegative(_sessionPeakEquity - equity);
                double rUsed = SanitizeNonNegative(_sessionStartEquity.HasValue
                    ? (_sessionStartEquity.Value - equity) / RiskUnitUsd
                    : 0.0);

                double exposure = 0.0;

                // A9: Calculate portfolio metrics from positions
                PortfolioMetrics portfolioMetrics;
                if (positions != null && positions.Any())
                {
                    portfolioMetrics = PortfolioMetrics.Calculate(positions.ToArray(), equity);
                }
                else
                {
                    // No positions - use zero metrics
                    portfolioMetrics = new PortfolioMetrics();
                }

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
                    exposure.ToString(CultureInfo.InvariantCulture),
                    portfolioMetrics.ToCsvRow() // A9: Append portfolio metrics
                );

                // A8 DEBUG: Log before file write
                try
                {
                    var debugLog = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "a8_persist_debug.log");
                    File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] About to write CSV line: {line.Substring(0, Math.Min(50, line.Length))}...\n");
                }
                catch { }

                lock (_lock)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }

                // A8 DEBUG: Log after successful write
                try
                {
                    var debugLog = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "a8_persist_debug.log");
                    File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] CSV write SUCCESS\n");
                }
                catch { }
            }
            catch (Exception ex)
            {
                // A8 DEBUG: Log exception instead of swallowing silently
                try
                {
                    var logPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "risk_snapshot_errors.log");
                    var errorMsg = $"[{DateTime.UtcNow:o}] RiskSnapshotPersister.Persist() failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
                    File.AppendAllText(logPath, errorMsg);
                }
                catch { /* Ultimate fallback - don't crash if logging fails */ }
            }
        }

        private static double SanitizeNonNegative(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }

            return value < 0.0 ? 0.0 : value;
        }
    }
}
