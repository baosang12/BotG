using System;
using System.Threading;
using Telemetry;
using DataFetcher.Models;

// Simple harness to exercise telemetry without cTrader runtime
class Program
{
    static void Main(string[] args)
    {
        // Load config and shorten flush interval for a quick demo
        var cfg = TelemetryConfig.Load();
        cfg.FlushIntervalSeconds = 2;
        TelemetryContext.InitOnce(cfg);

        // Simulate ticks/signals/orders
        var collector = TelemetryContext.Collector!;
        for (int i = 0; i < 120; i++)
        {
            collector.IncTick();
            if (i % 10 == 0) collector.IncSignal();
            if (i % 15 == 0)
            {
                collector.IncOrderRequested();
                TelemetryContext.OrderLogger?.Log("REQUEST", $"ORD-{i}", 100.0 + i * 0.01, null, 1, null, null);
                TelemetryContext.OrderLogger?.Log("ACK", $"ORD-{i}", 100.0 + i * 0.01, null, 1, null, null);
            }
            if (i % 30 == 0)
            {
                collector.IncOrderFilled();
                TelemetryContext.OrderLogger?.Log("FILL", $"ORD-{i}", 100.0 + i * 0.01, 100.0 + i * 0.01 + 0.0005, 1, 1, null);
            }
            if (i % 37 == 0) collector.IncError();
            Thread.Sleep(5);
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
    }
}
