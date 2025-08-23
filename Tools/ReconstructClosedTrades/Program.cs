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
        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: ReconstructClosedTrades.exe --orders <orders.csv> --out <closed_trades_fifo.csv>");
                return 2;
            }
            string orders = null!; string output = null!;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--orders") orders = args[i + 1];
                if (args[i] == "--out") output = args[i + 1];
            }
            if (string.IsNullOrEmpty(orders) || !File.Exists(orders)) { Console.Error.WriteLine("orders.csv not found"); return 3; }
            if (string.IsNullOrEmpty(output)) output = Path.Combine(Path.GetDirectoryName(orders)!, "closed_trades_fifo.csv");

            var header = "trade_id,entry_order_id,exit_order_id,open_time_iso,close_time_iso,side,size,open_price,close_price,pnl_in_account_currency,fee,notes";
            var buyQueue = new Queue<(DateTime ts, string id, double size, double price)>();
            var sellQueue = new Queue<(DateTime ts, string id, double size, double price)>();
            int tradeSeq = 0; int closed = 0;
            using (var reader = new StreamReader(orders))
            {
                string? line = reader.ReadLine(); // header
                while ((line = reader.ReadLine()) != null)
                {
                    var cols = SplitCsv(line);
                    if (cols.Length < 24) continue; // need v2 fields
                    var phase = cols[0];
                    var tsIso = cols[1];
                    var side = cols[14]; // appended side position based on our header construction
                    var status = cols[17];
                    var priceRequested = ParseD(cols[19]);
                    var priceFilled = ParseD(cols[20]);
                    var sizeRequested = ParseD(cols[21]);
                    var sizeFilled = ParseD(cols[22]);
                    var orderId = cols[3].Trim('"');
                    if (!string.Equals(status, "FILL", StringComparison.OrdinalIgnoreCase)) continue;
                    var ts = ParseTs(tsIso);
                    double sz = sizeFilled > 0 ? sizeFilled : sizeRequested;
                    double px = priceFilled > 0 ? priceFilled : priceRequested;
                    if (string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) || string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase))
                    {
                        buyQueue.Enqueue((ts, orderId, sz, px));
                    }
                    else if (string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) || string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase))
                    {
                        sellQueue.Enqueue((ts, orderId, sz, px));
                    }
                    // Try to match when both sides available
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
                        b.size -= size; s.size -= size;
                        buyQueue.Dequeue(); sellQueue.Dequeue();
                        if (b.size > 1e-9) buyQueue.Enqueue((b.ts, b.id, b.size, b.price));
                        if (s.size > 1e-9) sellQueue.Enqueue((s.ts, s.id, s.size, s.price));
                    }
                }
            }
            Console.WriteLine($"Reconstructed {closed} closed trades -> {output}");
            return 0;
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
