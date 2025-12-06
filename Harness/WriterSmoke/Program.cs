using System;
using System.Threading.Tasks;
using BotG.Common.IO;
using BotG.Runtime.Logging;

namespace WriterSmoke;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("WriterSmoke: Testing SafeCsvWriter and domain writers");
        Console.WriteLine("=======================================================");

        // Resolve and display log root
        var logRoot = PathUtil.GetLogRoot();
        Console.WriteLine($"Log root: {logRoot}");
        Console.WriteLine();

        // Write 10 telemetry rows
        Console.WriteLine("Writing 10 telemetry rows (EURUSD)...");
        var telemetryWriter = new TelemetryWriter();
        for (int i = 0; i < 10; i++)
        {
            var bid = 1.0850m + (i * 0.0001m);
            var ask = bid + 0.0002m;
            await telemetryWriter.AppendAsync(DateTime.UtcNow, "EURUSD", bid, ask);
        }
        Console.WriteLine($"  ✓ Written to {PathUtil.GetFile("telemetry.csv")}");
        Console.WriteLine();

        // Write 3 orders rows (REQUEST/ACK/FILL)
        Console.WriteLine("Writing 3 orders rows (REQUEST/ACK/FILL)...");
        var ordersWriter = new OrdersWriter();

        var ts1 = DateTime.UtcNow;
        await ordersWriter.AppendAsync(
            ts1, "REQUEST", "EURUSD", 0.01m, 1.0850m,
            "PENDING", "", 0, 1.0850m, 0m);

        await Task.Delay(10);
        var ts2 = DateTime.UtcNow;
        await ordersWriter.AppendAsync(
            ts2, "ACK", "EURUSD", 0.01m, 1.0850m,
            "ACCEPTED", "Broker acknowledged", 15, 1.0850m, 0m);

        await Task.Delay(10);
        var ts3 = DateTime.UtcNow;
        await ordersWriter.AppendAsync(
            ts3, "FILL", "EURUSD", 0.01m, 1.0850m,
            "FILLED", "Execution complete", 28, 1.0850m, 1.0851m);

        Console.WriteLine($"  ✓ Written to {PathUtil.GetFile("orders.csv")}");
        Console.WriteLine();

        // Write 3 risk rows
        Console.WriteLine("Writing 3 risk rows...");
        var riskWriter = new RiskWriter();

        await riskWriter.AppendAsync(DateTime.UtcNow, 10000m, 10000m, 0m);
        await Task.Delay(10);
        await riskWriter.AppendAsync(DateTime.UtcNow, 10005m, 10000m, 5m);
        await Task.Delay(10);
        await riskWriter.AppendAsync(DateTime.UtcNow, 10012m, 10010m, 2m);

        Console.WriteLine($"  ✓ Written to {PathUtil.GetFile("risk_snapshots.csv")}");
        Console.WriteLine();

        Console.WriteLine("SUCCESS: All files written.");
        Console.WriteLine();
        Console.WriteLine("Verify with PowerShell:");
        Console.WriteLine($"  Get-Content '{PathUtil.GetFile("telemetry.csv")}' | Select-Object -First 5");
        Console.WriteLine($"  Get-Content '{PathUtil.GetFile("orders.csv")}'");
        Console.WriteLine($"  Get-Content '{PathUtil.GetFile("risk_snapshots.csv")}'");

        return 0;
    }
}
