using System;
using System.IO;
using System.Globalization;
using System.Threading;
using Telemetry;
using DataFetcher.Models;
using RiskManager;

// Simple harness to exercise telemetry without cTrader runtime
class Program
{
    static void Main(string[] args)
    {
        // Load config and shorten flush interval for a quick demo
        var cfg = TelemetryConfig.Load();
        cfg.FlushIntervalSeconds = 2;
        // parse optional args: --seconds N, --fill-prob p, --artifactPath path
        for (int a = 0; a < args.Length - 1; a++)
        {
            if (args[a].Equals("--fill-prob", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(args[a + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fp) && fp >= 0 && fp <= 1)
                    cfg.Simulation.FillProbability = fp;
            }
            if (args[a].Equals("--artifactPath", StringComparison.OrdinalIgnoreCase))
            {
                var p = args[a + 1]; if (!string.IsNullOrWhiteSpace(p)) { Directory.CreateDirectory(p); cfg.RunFolder = p; }
            }
        }
        TelemetryContext.InitOnce(cfg);
        var runDir = RunInitializer.EnsureRunFolderAndMetadata(cfg);

        // Provide a fake symbol so RiskManager can auto-compute PointValuePerUnit (e.g., XAUUSD-like)
        try
        {
            var rm = new RiskManager.RiskManager();
            rm.Initialize(new RiskSettings());
            // Fake symbol: TickSize=0.01, TickValue=0.01 per tick per lot -> tickValuePerLot = 0.01/0.01 = 1.0 USD per price-unit per lot
            // LotSize = 100 units per lot to match default
            var fakeSym = new cAlgo.API.Symbol { TickSize = 0.01, TickValue = 0.01, LotSize = 100.0 };
            // ensure config value doesn't block auto-compute in this harness
            rm.GetSettings().PointValuePerUnit = 0.0;
            rm.SetSymbolReference(fakeSym);
            // Force equity override for consistent sizing tests
            rm.SetEquityOverrideForTesting(10000);
            Console.WriteLine("[Harness] Auto PointValuePerUnit = " + rm.GetSettings().PointValuePerUnit.ToString("G"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Harness] Failed to set test PointValuePerUnit: " + ex.Message);
        }

        // Simulate ticks/signals/orders
        var collector = TelemetryContext.Collector!;
        var start = DateTime.UtcNow;
        var duration = TimeSpan.FromSeconds(60); // use 60s if environment is constrained
        if (args.Length > 0 && int.TryParse(args[0], out var sec0) && sec0 > 0) duration = TimeSpan.FromSeconds(sec0);
        for (int a = 0; a < args.Length - 1; a++)
        {
            if (args[a].Equals("--seconds", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(args[a + 1], out var sec) && sec > 0) duration = TimeSpan.FromSeconds(sec);
            }
        }
        int i = 0;
        var rng = new Random(42); // deterministic
        double fillProb = cfg.Simulation?.FillProbability ?? 1.0;
        // simple queues to pair buy/sell for closed trades
        var buyStack = new System.Collections.Generic.List<(string orderId, DateTime ts, double size, double price)>();
        var sellStack = new System.Collections.Generic.List<(string orderId, DateTime ts, double size, double price)>();
        while (DateTime.UtcNow - start < duration)
        {
            collector.IncTick();
            if (i % 10 == 0) collector.IncSignal();
            if (i % 15 == 0)
            {
                collector.IncOrderRequested();
                double entry = 100.0 + i * 0.01;
                double? stop = (i % 30 == 0) ? entry - 0.05 : (double?)null; // add stop every 30 ticks as example
                var side = (i % 30 == 0) ? "Buy" : "Sell";
                var oid = $"ORD-{i}";
                TelemetryContext.OrderLogger?.LogV2("REQUEST", oid, oid, side, side.ToUpperInvariant(), "Market", entry, stop, null, null, null, 1, null, "REQUEST", null, "HARNESS");
                TelemetryContext.OrderLogger?.LogV2("ACK", oid, oid, side, side.ToUpperInvariant(), "Market", entry, stop, null, null, null, 1, null, "ACK", null, "HARNESS");
                // simulated fill
                bool willFill = rng.NextDouble() <= fillProb;
                if (willFill)
                {
                    collector.IncOrderFilled();
                    double exec = entry + ((side == "Buy") ? 0.0005 : -0.0005);
                    TelemetryContext.OrderLogger?.LogV2("FILL", oid, oid, side, side.ToUpperInvariant(), "Market", entry, stop, exec, null, null, 1, 1, "FILL", "filled", "HARNESS");
                    var ts = DateTime.UtcNow;
                    if (side == "Buy") buyStack.Add((oid, ts, 1, exec)); else sellStack.Add((oid, ts, 1, exec));
                    // if we have both sides, close one trade FIFO-style
                    if (buyStack.Count > 0 && sellStack.Count > 0)
                    {
                        var b = buyStack[0]; var s = sellStack[0];
                        buyStack.RemoveAt(0); sellStack.RemoveAt(0);
                        // Optional spread adjustments: apply half-spread on entry and reverse on exit
                        double pipValue = 0.0001; // simplistic default
                        var (entryAdj, exitAdj) = Execution.FeeCalculator.ComputeSpreadAdjustments(cfg, pipValue);
                        double adjEntry = b.price + entryAdj; // buy worse
                        double adjExit  = s.price + exitAdj;  // sell worse
                        double gross = (adjExit - adjEntry) * 1.0;
                        // Fee based on exit notional (approx)
                        // Use PointValuePerUnit if set in RiskSettings; default to 1.0
                        double pvu = 1.0;
                        try { pvu = Math.Max(1e-9, new RiskManager.RiskManager().GetSettings().PointValuePerUnit); } catch {}
                        double fee = Execution.FeeCalculator.ComputeFee((adjEntry + adjExit) / 2.0, 1.0, cfg, pvu);
                        double net = gross - fee;
                        TelemetryContext.ClosedTrades?.Append($"T-{oid}", b.orderId, s.orderId, b.ts, s.ts, "BUY-SELL", 1.0, b.price, s.price, net, fee, "harness", gross);
                    }
                }
            }
            if (i % 37 == 0) collector.IncError();
            Thread.Sleep(5);
            i++;
        }

        // Persist an example account snapshot
        TelemetryContext.RiskPersister?.Persist(new AccountInfo
        {
            Balance = 10000,
            Equity = 10020,
            Margin = 250,
            Positions = 1
        });

        // Allow time for a flush tick
        Thread.Sleep(2500);
        Console.WriteLine("Harness run complete. Telemetry written to: " + TelemetryContext.Config.LogPath);

        // Optional: compute simple size comparison for the last 3 fills
        try
        {
            var logPath = System.IO.Path.Combine(runDir, "orders.csv");
            var riskPath = System.IO.Path.Combine(TelemetryContext.Config.LogPath, "risk_snapshots.csv");
            if (File.Exists(logPath))
            {
                var lines = File.ReadAllLines(logPath);
                // header: phase,timestamp_iso,epoch_ms,orderId,intendedPrice,stopLoss,execPrice,theoretical_lots,theoretical_units,requestedVolume,filledSize,slippage,brokerMsg
                var fills = new System.Collections.Generic.List<string[]>();
                for (int idx = lines.Length - 1; idx >= 1 && fills.Count < 3; idx--)
                {
                    var parts = lines[idx].Split(',');
                    if (parts.Length >= 13 && parts[0] == "FILL") fills.Add(parts);
                }
                fills.Reverse();
                double equity = 10000; // as overridden
                // Prepare an RM instance mirroring harness assumptions to compute theoretical lots/units with clamp
                var rm2 = new RiskManager.RiskManager();
                rm2.Initialize(new RiskSettings { MaxLotsPerTrade = 10.0, LotSizeDefault = 100 });
                var fake2 = new cAlgo.API.Symbol { TickSize = 0.01, TickValue = 0.01, LotSize = 100.0 };
                rm2.GetSettings().PointValuePerUnit = 0.0; // force auto-compute
                rm2.SetSymbolReference(fake2);
                rm2.SetEquityOverrideForTesting(equity);
                double lotSizeForOut = rm2.GetSettings().LotSizeDefault;
                try {
                    var lsProp = fake2.GetType().GetProperty("LotSize");
                    if (lsProp != null)
                    {
                        var v = lsProp.GetValue(fake2);
                        if (v != null) lotSizeForOut = Convert.ToDouble(v);
                    }
                } catch {}

                var results = new System.Collections.Generic.List<object>();
                foreach (var f in fills)
                {
                    string orderId = f[3].Trim('"');
                    double entry = double.TryParse(f[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var e) ? e : 0;
                    double stop = double.TryParse(f[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0;
                    double exec = double.TryParse(f[6], NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : 0;
                    double requested = double.TryParse(f[9], NumberStyles.Any, CultureInfo.InvariantCulture, out var rq) ? rq : 0;
                    double filled = double.TryParse(f[10], NumberStyles.Any, CultureInfo.InvariantCulture, out var fl) ? fl : 0;
                    double slippage = double.TryParse(f[11], NumberStyles.Any, CultureInfo.InvariantCulture, out var sl) ? sl : 0;
                    double stopDist = Math.Abs(entry - stop);
                    double theoreticalUnits = 0;
                    double theoreticalLots = 0;
                    if (stopDist > 0) {
                        theoreticalUnits = rm2.CalculateOrderSize(stopDist, 0.0); // uses lot-based path and clamps internally
                        theoreticalLots = lotSizeForOut > 0 ? theoreticalUnits / lotSizeForOut : 0;
                    }
                    results.Add(new {
                        orderId, entry, stopLoss = (double?) (stop > 0 ? stop : (double?)null), stopDistance = stopDist,
                        equity_used = equity, theoretical_lots = theoreticalLots, theoretical_units = theoreticalUnits, requestedSize = requested, filledSize = filled, slippage
                    });
                }
                var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions{ WriteIndented = true });
                Console.WriteLine(json);
                var outPath = System.IO.Path.Combine(runDir, "size_comparison.json");
                File.WriteAllText(outPath, json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Harness] size comparison failed: " + ex.Message);
        }
    }
}
