using System;
using System.IO;
using System.Globalization;
using System.Threading;
using Telemetry;
using DataFetcher.Models;
using RiskManager = BotG.RiskManager;
using RiskSettings = BotG.RiskManager.RiskSettings;
using System.Collections.Generic;
using Connectivity;
using Connectivity.Synthetic;

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
        // Allow harness symbol override to mimic real instruments
        var symbol = Environment.GetEnvironmentVariable("BOTG_SYMBOL");
        if (string.IsNullOrWhiteSpace(symbol)) symbol = "XAUUSD";
        symbol = symbol!.Trim().ToUpperInvariant();

        // Init telemetry and write run metadata with extra context
        TelemetryContext.InitOnce(cfg);
        var runDir = RunInitializer.EnsureRunFolderAndMetadata(cfg, new {
            sim_seed = 42,
            seconds = (int?)null,
            seconds_per_hour = cfg.SecondsPerHour,
            drain_seconds = cfg.DrainSeconds,
            graceful_shutdown_wait_seconds = cfg.GracefulShutdownWaitSeconds,
            use_simulation = cfg.UseSimulation,
            fill_probability = cfg.Simulation?.FillProbability
        });

        // Wire synthetic connectivity so metadata + quote capture behave like production wiring
        var modeEnv = Environment.GetEnvironmentVariable("DATASOURCE__MODE");
        var connectorMode = string.IsNullOrWhiteSpace(modeEnv) ? "synthetic" : modeEnv;
        ConnectorBundle connector;
        try
        {
            connector = ConnectorBundle.Create(connectorMode);
        }
        catch (InvalidOperationException)
        {
            connector = ConnectorBundle.Create("synthetic");
        }
        connector.MarketData.Start();
        connector.MarketData.Subscribe(symbol);
        TelemetryContext.AttachConnectivity(connector.Mode, connector.MarketData, connector.OrderExecutor, 2);
        TelemetryContext.QuoteTelemetry?.TrackSymbol(symbol);
        var syntheticMd = connector.MarketData as SyntheticMarketDataProvider;

        static void PublishQuote(SyntheticMarketDataProvider? provider, string sym, double mid, double spread)
        {
            if (provider == null) return;
            var half = spread / 2.0;
            var bid = mid - half;
            var ask = mid + half;
            provider.PublishQuote(sym, bid, ask, DateTime.UtcNow);
        }

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
            // Get equity from config instead of hardcoding
            var initialEquity = cfg.GetInitialEquity(); rm.SetEquityOverrideForTesting(initialEquity);
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
    var buyStack = new List<(string orderId, DateTime ts, double size, double price)>();
    var sellStack = new List<(string orderId, DateTime ts, double size, double price)>();
        double midPrice = 100.0;
        while (DateTime.UtcNow - start < duration)
        {
            collector.IncTick();
            if (i % 10 == 0) collector.IncSignal();
            if (i % 15 == 0)
            {
                collector.IncOrderRequested();
                midPrice += Math.Sin(i / 25.0) * 0.02;
                var spreadPips = 0.0006;
                PublishQuote(syntheticMd, symbol, midPrice, spreadPips);
                double entry = midPrice + i * 0.0001;
                double? stop = (i % 30 == 0) ? entry - 0.05 : (double?)null; // add stop every 30 ticks as example
                var side = (i % 30 == 0) ? "Buy" : "Sell";
                var oid = $"ORD-{i}";
                TelemetryContext.OrderLogger?.LogV2("REQUEST", oid, oid, side, side.ToUpperInvariant(), "Market", entry, stop, null, null, null, 1, null, "REQUEST", null, symbol);
                TelemetryContext.OrderLogger?.LogV2("ACK", oid, oid, side, side.ToUpperInvariant(), "Market", entry, stop, null, null, null, 1, null, "ACK", null, symbol);
                // simulated fill
                bool willFill = rng.NextDouble() <= fillProb;
                if (willFill)
                {
                    collector.IncOrderFilled();
                    double exec = entry + ((side == "Buy") ? 0.0005 : -0.0005);
                    PublishQuote(syntheticMd, symbol, exec, spreadPips);
                    TelemetryContext.OrderLogger?.LogV2("FILL", oid, oid, side, side.ToUpperInvariant(), "Market", entry, stop, exec, null, null, 1, 1, "FILL", "filled", symbol);
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
                        TelemetryContext.ClosedTrades?.Append($"T-{oid}", b.orderId, s.orderId, b.ts, s.ts, "BUY-SELL", 1.0, b.price, s.price, net, fee, symbol, gross);
                    }
                }
            }
            if (i % 37 == 0) collector.IncError();
            Thread.Sleep(5);
            i++;
        }

    // Graceful drain at end: try to pair any remaining side within a short window
    int pendingBefore = buyStack.Count + sellStack.Count;
        var drainUntil = DateTime.UtcNow.AddSeconds(5);
        while ((buyStack.Count > 0 || sellStack.Count > 0) && DateTime.UtcNow < drainUntil)
        {
            // Attempt to create a synthetic counter-side fill to close the earliest open side
            if (buyStack.Count > 0 && sellStack.Count == 0)
            {
                var b = buyStack[0];
                buyStack.RemoveAt(0);
                var fakeSellPrice = b.price + 0.0005; // tiny move against
                var now = DateTime.UtcNow;
                double pipValue = 0.0001;
                var (entryAdj, exitAdj) = Execution.FeeCalculator.ComputeSpreadAdjustments(TelemetryContext.Config, pipValue);
                double adjEntry = b.price + entryAdj;
                double adjExit = fakeSellPrice + exitAdj;
                double gross = (adjExit - adjEntry) * 1.0;
                double pvu = 1.0;
                try { pvu = Math.Max(1e-9, new RiskManager.RiskManager().GetSettings().PointValuePerUnit); } catch {}
                double fee = Execution.FeeCalculator.ComputeFee((adjEntry + adjExit) / 2.0, 1.0, TelemetryContext.Config, pvu);
                double net = gross - fee;
        PublishQuote(syntheticMd, symbol, fakeSellPrice, 0.0006);
        TelemetryContext.ClosedTrades?.Append($"T-DRAIN-{b.orderId}", b.orderId, "DRAIN-SELL", b.ts, now, "BUY-SELL", 1.0, b.price, fakeSellPrice, net, fee, "drain_close", gross);
        // Also log the synthetic exit order lifecycle to ensure latency_ms is derivable
    TelemetryContext.OrderLogger?.LogV2("REQUEST", "DRAIN-SELL", "DRAIN-SELL", "Sell", "SELL", "Market", fakeSellPrice, null, null, null, null, 1, null, "REQUEST", "drain", symbol);
    TelemetryContext.OrderLogger?.LogV2("ACK", "DRAIN-SELL", "DRAIN-SELL", "Sell", "SELL", "Market", fakeSellPrice, null, null, null, null, 1, null, "ACK", "drain", symbol);
    TelemetryContext.OrderLogger?.LogV2("FILL", "DRAIN-SELL", "DRAIN-SELL", "Sell", "SELL", "Market", fakeSellPrice, null, fakeSellPrice, null, null, 1, 1, "FILL", "drain", symbol);
            }
            else if (sellStack.Count > 0 && buyStack.Count == 0)
            {
                var s = sellStack[0];
                sellStack.RemoveAt(0);
                var fakeBuyPrice = s.price - 0.0005; // tiny move against
                var now = DateTime.UtcNow;
                double pipValue = 0.0001;
                var (entryAdj, exitAdj) = Execution.FeeCalculator.ComputeSpreadAdjustments(TelemetryContext.Config, pipValue);
                double adjEntry = fakeBuyPrice + entryAdj;
                double adjExit = s.price + exitAdj;
                double gross = (adjExit - adjEntry) * 1.0;
                double pvu = 1.0;
                try { pvu = Math.Max(1e-9, new RiskManager.RiskManager().GetSettings().PointValuePerUnit); } catch {}
                double fee = Execution.FeeCalculator.ComputeFee((adjEntry + adjExit) / 2.0, 1.0, TelemetryContext.Config, pvu);
                double net = gross - fee;
                PublishQuote(syntheticMd, symbol, fakeBuyPrice, 0.0006);
                TelemetryContext.ClosedTrades?.Append($"T-DRAIN-{s.orderId}", "DRAIN-BUY", s.orderId, s.ts, now, "BUY-SELL", 1.0, fakeBuyPrice, s.price, net, fee, "drain_close", gross);
                TelemetryContext.OrderLogger?.LogV2("REQUEST", "DRAIN-BUY", "DRAIN-BUY", "Buy", "BUY", "Market", fakeBuyPrice, null, null, null, null, 1, null, "REQUEST", "drain", symbol);
                TelemetryContext.OrderLogger?.LogV2("ACK", "DRAIN-BUY", "DRAIN-BUY", "Buy", "BUY", "Market", fakeBuyPrice, null, null, null, null, 1, null, "ACK", "drain", symbol);
                TelemetryContext.OrderLogger?.LogV2("FILL", "DRAIN-BUY", "DRAIN-BUY", "Buy", "BUY", "Market", fakeBuyPrice, null, fakeBuyPrice, null, null, 1, 1, "FILL", "drain", symbol);
            }
            else
            {
                // Both sides present, close immediately FIFO
                var b = buyStack[0]; var s = sellStack[0];
                buyStack.RemoveAt(0); sellStack.RemoveAt(0);
                double pipValue = 0.0001;
                var (entryAdj, exitAdj) = Execution.FeeCalculator.ComputeSpreadAdjustments(TelemetryContext.Config, pipValue);
                double adjEntry = b.price + entryAdj;
                double adjExit = s.price + exitAdj;
                double gross = (adjExit - adjEntry) * 1.0;
                double pvu = 1.0;
                try { pvu = Math.Max(1e-9, new RiskManager.RiskManager().GetSettings().PointValuePerUnit); } catch {}
                double fee = Execution.FeeCalculator.ComputeFee((adjEntry + adjExit) / 2.0, 1.0, TelemetryContext.Config, pvu);
                double net = gross - fee;
                TelemetryContext.ClosedTrades?.Append($"T-DRAIN-{b.orderId}", b.orderId, s.orderId, b.ts, DateTime.UtcNow, "BUY-SELL", 1.0, b.price, s.price, net, fee, "drain_match", gross);
            }
        }

    // Drain: close any leftover positions by issuing synthetic opposing fills
        try
        {
            if (buyStack.Count > 0 || sellStack.Count > 0)
            {
                Console.WriteLine($"[Harness] Draining leftovers: buys={buyStack.Count} sells={sellStack.Count}");
            }
            int drainSeq = 0;
            while (buyStack.Count > 0)
            {
                var b = buyStack[0]; buyStack.RemoveAt(0);
                var oid = $"ORD-DRAIN-S-{i + (++drainSeq)}"; // synthetic sell to close the buy
                double exec = b.price + 0.0003; // small offset
                PublishQuote(syntheticMd, symbol, b.price, 0.0006);
                TelemetryContext.OrderLogger?.LogV2("REQUEST", oid, oid, "Sell", "SELL", "Market", b.price, null, null, null, null, 1, null, "REQUEST", null, symbol);
                TelemetryContext.OrderLogger?.LogV2("ACK", oid, oid, "Sell", "SELL", "Market", b.price, null, null, null, null, 1, null, "ACK", null, symbol);
                TelemetryContext.OrderLogger?.LogV2("FILL", oid, oid, "Sell", "SELL", "Market", b.price, null, exec, null, null, 1, 1, "FILL", "drain", symbol);
                var closeTs = DateTime.UtcNow;
                double pipValue = 0.0001; // simplistic default
                var (entryAdj, exitAdj) = Execution.FeeCalculator.ComputeSpreadAdjustments(cfg, pipValue);
                double adjEntry = b.price + entryAdj;
                double adjExit = exec + exitAdj;
                double gross = (adjExit - adjEntry) * 1.0;
                double pvu = 1.0;
                try { pvu = Math.Max(1e-9, new RiskManager.RiskManager().GetSettings().PointValuePerUnit); } catch {}
                double fee = Execution.FeeCalculator.ComputeFee((adjEntry + adjExit) / 2.0, 1.0, cfg, pvu);
                double net = gross - fee;
                TelemetryContext.ClosedTrades?.Append($"T-{oid}", b.orderId, oid, b.ts, closeTs, "BUY-SELL", 1.0, b.price, exec, net, fee, "drain", gross);
            }
            while (sellStack.Count > 0)
            {
                var s = sellStack[0]; sellStack.RemoveAt(0);
                var oid = $"ORD-DRAIN-B-{i + (++drainSeq)}"; // synthetic buy to close the sell
                double exec = s.price - 0.0003; // small offset
                PublishQuote(syntheticMd, symbol, s.price, 0.0006);
                TelemetryContext.OrderLogger?.LogV2("REQUEST", oid, oid, "Buy", "BUY", "Market", s.price, null, null, null, null, 1, null, "REQUEST", null, symbol);
                TelemetryContext.OrderLogger?.LogV2("ACK", oid, oid, "Buy", "BUY", "Market", s.price, null, null, null, null, 1, null, "ACK", null, symbol);
                TelemetryContext.OrderLogger?.LogV2("FILL", oid, oid, "Buy", "BUY", "Market", s.price, null, exec, null, null, 1, 1, "FILL", "drain", symbol);
                var closeTs = DateTime.UtcNow;
                double pipValue = 0.0001;
                var (entryAdj, exitAdj) = Execution.FeeCalculator.ComputeSpreadAdjustments(cfg, pipValue);
                double adjEntry = s.price - entryAdj; // sell entry worse => invert sign carefully
                double adjExit = exec - exitAdj;
                double gross = (adjExit - adjEntry) * 1.0;
                double pvu = 1.0;
                try { pvu = Math.Max(1e-9, new RiskManager.RiskManager().GetSettings().PointValuePerUnit); } catch {}
                double fee = Execution.FeeCalculator.ComputeFee((adjEntry + adjExit) / 2.0, 1.0, cfg, pvu);
                double net = gross - fee;
                TelemetryContext.ClosedTrades?.Append($"T-{oid}", s.orderId, oid, s.ts, closeTs, "SELL-BUY", 1.0, s.price, exec, net, fee, "drain", gross);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Harness] Drain failed: " + ex.Message);
        }

        int pendingAfter = buyStack.Count + sellStack.Count;
        // Append drain metrics into run_metadata.json for quick diagnostics
        try
        {
            var metaPath = System.IO.Path.Combine(runDir, "run_metadata.json");
            if (File.Exists(metaPath))
            {
                var json = File.ReadAllText(metaPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = new System.Text.Json.Nodes.JsonObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    root[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
                }
                var drain = new System.Text.Json.Nodes.JsonObject
                {
                    ["pending_requests_before_drain"] = pendingBefore,
                    ["pending_after_drain"] = pendingAfter
                };
                root["drain_metrics"] = drain;
                File.WriteAllText(metaPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions{ WriteIndented = true }));
            }
        }
        catch { }

        // Persist an example account snapshot
        TelemetryContext.RiskPersister?.Persist(new AccountInfo
        {
            Balance = 10000,
            Equity = 10020,
            Margin = 250,
            Positions = 1
        });

    // Allow time for a flush tick and ensure final writes have time to hit disk
    int waitMs = Math.Max(500, (int)(TelemetryContext.Config.GracefulShutdownWaitSeconds * 1000));
    Thread.Sleep(waitMs);
        // Final fsync to make sure core files are on disk before exit
        try
        {
            Telemetry.FileSyncUtils.TryFsyncMany(runDir,
                "orders.csv",
                "telemetry.csv",
                "closed_trades_fifo.csv",
                "reconcile_report.json",
                "analysis_summary.json",
                "run_metadata.json");
        }
        catch { }
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
                double equity = cfg.GetInitialEquity(); // from config
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
