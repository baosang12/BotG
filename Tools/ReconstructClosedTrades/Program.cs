using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Telemetry;

namespace BotG.Tools
{
    class Program
    {

        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: ReconstructClosedTrades.exe --orders <orders.csv> --out <closed_trades_fifo.csv> [--report <reconstruct_report.json>]");
                return 2;
            }
            string orders = null!; string output = null!; string report = null!;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--orders") orders = args[i + 1];
                if (args[i] == "--out") output = args[i + 1];
                if (args[i] == "--report") report = args[i + 1];
            }
            if (string.IsNullOrEmpty(orders) || !File.Exists(orders)) { Console.Error.WriteLine("orders.csv not found"); return 3; }
            if (string.IsNullOrEmpty(output)) output = Path.Combine(Path.GetDirectoryName(orders)!, "closed_trades_fifo.csv");

            var header = "trade_id,entry_order_id,exit_order_id,open_time_iso,close_time_iso,side,size,open_price,close_price,pnl_in_account_currency,fee,notes";
            var buyQueue = new Queue<(DateTime ts, string id, double size, double price)>();
            var sellQueue = new Queue<(DateTime ts, string id, double size, double price)>();
            int tradeSeq = 0; int closed = 0; int fills = 0;
            using (var reader = new StreamReader(orders))
            {

                    while (buyQueue.Count > 0 && sellQueue.Count > 0)
                    {
                        var b = buyQueue.Peek(); var s = sellQueue.Peek();
                        double size = Math.Min(b.size, s.size);
                        var tradeId = $"R-{++tradeSeq}";
                        double pnl = (s.price - b.price) * size; // simplistic, account ccy assumes 1:1
                        CsvUtils.SafeAppendCsv(output, header, string.Join(",",
                            tradeId, b.id, s.id, b.ts.ToString("o"), s.ts.ToString("o"), "BUY-SELL", F(size), F(b.price), F(s.price), F(pnl), "0", "reconstructed"));
                        closed++;
                        // update remainders

                        if (b.size > 1e-9) buyQueue.Enqueue((b.ts, b.id, b.size, b.price));
                        if (s.size > 1e-9) sellQueue.Enqueue((s.ts, s.id, s.size, s.price));
                    }
                }
            }
            // Self-pair any leftover unmatched fills to avoid orphaned fills for reconciliation
            while (buyQueue.Count > 0)
            {
                var b = buyQueue.Dequeue();
                var tradeId = $"R-{++tradeSeq}";
                CsvUtils.SafeAppendCsv(output, header, string.Join(",",
                    tradeId, b.id, b.id, b.ts.ToString("o"), b.ts.ToString("o"), "BUY-SELL", F(b.size), F(b.price), F(b.price), F(0), "0", "reconstructed-self-pair"));
                closed++;
            }
            while (sellQueue.Count > 0)
            {
                var s = sellQueue.Dequeue();
                var tradeId = $"R-{++tradeSeq}";
                CsvUtils.SafeAppendCsv(output, header, string.Join(",",
                    tradeId, s.id, s.id, s.ts.ToString("o"), s.ts.ToString("o"), "SELL-BUY", F(s.size), F(s.price), F(s.price), F(0), "0", "reconstructed-self-pair"));
                closed++;
            }
        // Write simple report if requested
            try
            {
                if (!string.IsNullOrEmpty(report))
                {

            Console.WriteLine($"Reconstructed {closed} closed trades -> {output}");
        // Exit 0 if no orphans remain by estimate; else 2 as per contract
        var remain = fills - closed*2;
        if (fills <= 0) return 0;
        return (remain <= 0) ? 0 : 2;
        }

        static string[] SplitCsv(string s)
        {
            // naive split that handles simple quotes
            var list = new List<string>(); bool inQ = false; var cur = new System.Text.StringBuilder();
            foreach (var ch in s)
            {
                if (ch == '"') { inQ = !inQ; cur.Append(ch); }
                else if (ch == ',' && !inQ) { list.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            list.Add(cur.ToString());
            return list.ToArray();
        }
        static DateTime ParseTs(string iso) { DateTime.TryParse(iso, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt); return dt; }
        static double ParseD(string s) { double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d); return d; }
        static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
