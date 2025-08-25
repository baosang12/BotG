using System;
using System.Globalization;
using System.IO;

namespace Telemetry
{
    public class ClosedTradesWriter
    {
        private readonly string _closedTradesCsv;
        private readonly string _tradeClosesLog;
        private readonly object _lock = new object();
    // Keep existing columns; allow optional gross_pnl appended for newer consumers
    private const string Header = "trade_id,entry_order_id,exit_order_id,open_time_iso,close_time_iso,side,size,open_price,close_price,pnl_in_account_currency,fee,notes,gross_pnl";

        public ClosedTradesWriter(string folder)
        {
            Directory.CreateDirectory(folder);
            _closedTradesCsv = Path.Combine(folder, "closed_trades_fifo.csv");
            _tradeClosesLog = Path.Combine(folder, "trade_closes.log");
        }

    public void Append(string tradeId, string entryOrderId, string exitOrderId, DateTime openTimeUtc, DateTime closeTimeUtc,
        string side, double size, double openPrice, double closePrice, double pnlAccountCcy, double fee, string? notes = null, double? grossPnl = null)
        {
            try
            {
                var line = string.Join(",",
            Escape(tradeId), Escape(entryOrderId), Escape(exitOrderId),
                    openTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                    closeTimeUtc.ToString("o", CultureInfo.InvariantCulture),
            Escape(side), F(size), F(openPrice), F(closePrice), F(pnlAccountCcy), F(fee), Escape(notes ?? string.Empty), (grossPnl.HasValue ? F(grossPnl.Value) : "")
                );
                lock (_lock)
                {
                    CsvUtils.SafeAppendCsv(_closedTradesCsv, Header, line);
                    var human = $"{closeTimeUtc.ToString("o", CultureInfo.InvariantCulture)} CLOSED {tradeId} {side} size={size} pnl={pnlAccountCcy.ToString(CultureInfo.InvariantCulture)}";
                    CsvUtils.SafeAppendCsv(_tradeClosesLog, "line", human);
                }
            }
            catch { /* swallow */ }
        }

        private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(',') || s.Contains('"'))
            {
                return '"' + s.Replace("\"", "\"\"") + '"';
            }
            return s;
        }
    }
}
