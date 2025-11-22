using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using BotG.Strategies.Coordination;
using BotG.Threading;
using Strategies;
using Telemetry;
using TradeManager;

var coordinator = new PerformanceCoordinator();
await coordinator.RunAsync(args);

/// <summary>
/// Coordinates generation of telemetry sample files and runs baseline vs optimized measurements.
/// </summary>
internal sealed class PerformanceCoordinator
{
    private static readonly int[] DefaultSizesMb = new[] { 10, 50, 100 };
    private const int LargeSizeMb = 500;
    private const int HugeSizeMb = 1000;
    private readonly string _dataRoot = Path.Combine(Environment.CurrentDirectory, "tmp", "perfdata");
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public async Task RunAsync(string[] args)
    {
        Directory.CreateDirectory(_dataRoot);

        bool includeLarge = args.Any(a => string.Equals(a, "--all", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(a, "--include500", StringComparison.OrdinalIgnoreCase));
        bool includeHuge = args.Any(a => string.Equals(a, "--all", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(a, "--include1000", StringComparison.OrdinalIgnoreCase));
        bool baselineOnly = args.Any(a => string.Equals(a, "--baseline-only", StringComparison.OrdinalIgnoreCase));
        bool optimizedOnly = args.Any(a => string.Equals(a, "--optimized-only", StringComparison.OrdinalIgnoreCase));

        var sizes = new List<int>(DefaultSizesMb);
        if (includeLarge)
        {
            sizes.Add(LargeSizeMb);
        }
        if (includeHuge)
        {
            sizes.Add(HugeSizeMb);
        }

        Console.WriteLine("[Perf] Generating telemetry datasets...");
        var files = new List<string>();
        foreach (var sizeMb in sizes)
        {
            var path = await EnsureTelemetryFileAsync(sizeMb);
            files.Add(path);
        }

        Console.WriteLine();
        Console.WriteLine("[Perf] Running measurements...");

        var measurements = new List<MeasurementResult>();
        foreach (var file in files)
        {
            if (!optimizedOnly)
            {
                measurements.Add(await RunMeasurementAsync("baseline-readalllines", file, MeasureBaselineAsync));
            }

            if (!baselineOnly)
            {
                measurements.Add(await RunMeasurementAsync("optimized-csvtailreader", file, MeasureCsvTailReaderAsync));
            }
        }

        if (!baselineOnly)
        {
            measurements.Add(await MeasureStrategyPipelineAsync());
        }

        var summaryPath = Path.Combine(_dataRoot, $"perf_summary_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(measurements, _jsonOptions));
        Console.WriteLine();
        Console.WriteLine($"[Perf] Summary written to {summaryPath}");
        PrintTable(measurements);
    }

    private async Task<string> EnsureTelemetryFileAsync(int targetSizeMb)
    {
        string fileName = $"telemetry_{targetSizeMb}mb.csv";
        string path = Path.Combine(_dataRoot, fileName);
        long targetBytes = (long)targetSizeMb * 1024L * 1024L;

        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (info.Length >= targetBytes)
            {
                Console.WriteLine($"[Perf] Using existing file {fileName} ({info.Length / (1024 * 1024)} MB)");
                return path;
            }
        }

        Console.WriteLine($"[Perf] Generating {fileName} (~{targetSizeMb} MB)...");
        var header = "timestamp_iso,symbol,bid,ask,tick_rate";
        var random = new Random(42);
        var buffer = new StringBuilder(capacity: 256);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync(header);

        while (stream.Length < targetBytes)
        {
            buffer.Clear();
            var timestamp = DateTime.UtcNow.AddSeconds(-random.Next(0, 100000)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var symbol = random.Next(0, 2) switch
            {
                0 => "EURUSD",
                1 => "GBPUSD",
                _ => "USDJPY"
            };
            var bid = (1.05 + random.NextDouble() * 0.05).ToString("F5");
            var ask = (double.Parse(bid) + 0.0002 + random.NextDouble() * 0.0001).ToString("F5");
            var tickRate = random.Next(50, 300);
            buffer.Append(timestamp).Append(',').Append(symbol).Append(',').Append(bid).Append(',').Append(ask).Append(',').Append(tickRate);
            await writer.WriteLineAsync(buffer.ToString());
        }

        await writer.FlushAsync();
        Console.WriteLine($"[Perf] Generated {fileName} ({new FileInfo(path).Length / (1024 * 1024)} MB)");
        return path;
    }

    private static async Task<MeasurementResult> RunMeasurementAsync(string mode, string filePath, Func<string, Task<MeasurementResult>> measurementFunc)
    {
        Console.WriteLine($"[Perf] Measuring {mode} on {Path.GetFileName(filePath)}...");
        var result = await measurementFunc(filePath);
        Console.WriteLine($"        {mode}: {result.DurationMs:F1} ms, peak {result.PeakWorkingSetDeltaMb:F1} MB, GCΔ {result.GcAllocatedMb:F1} MB");
        return result;
    }

    private static async Task<MeasurementResult> MeasureBaselineAsync(string filePath)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        long peakBefore = process.PeakWorkingSet64;
        long privateBefore = process.PrivateMemorySize64;
        long gcBefore = GC.GetTotalMemory(forceFullCollection: true);

        string[] lines = Array.Empty<string>();
        string firstLine = string.Empty;
        string lastLine = string.Empty;

        var sw = Stopwatch.StartNew();
        try
        {
            lines = await File.ReadAllLinesAsync(filePath);
            if (lines.Length > 0)
            {
                firstLine = lines[0];
                lastLine = lines[^1];
            }
        }
        finally
        {
            sw.Stop();
        }

        process.Refresh();
        long peakAfter = process.PeakWorkingSet64;
        long privateAfter = process.PrivateMemorySize64;
        long gcAfter = GC.GetTotalMemory(forceFullCollection: true);

        int lineCount = lines.Length;
        lines = Array.Empty<string>();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return CreateResult("baseline-readalllines", filePath, sw.Elapsed, peakBefore, peakAfter, privateBefore, privateAfter, gcBefore, gcAfter, firstLine, lastLine, lineCount);
    }

    private static async Task<MeasurementResult> MeasureCsvTailReaderAsync(string filePath)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        long peakBefore = process.PeakWorkingSet64;
        long privateBefore = process.PrivateMemorySize64;
        long gcBefore = GC.GetTotalMemory(forceFullCollection: true);

        string? firstLine = null;
        string? lastLine = null;
        int lineCount = 0;

        var sw = Stopwatch.StartNew();
        try
        {
            using var reader = new CsvTailReader(filePath);
            firstLine = await reader.ReadFirstLineAsync();

            // Count lines via streaming while capturing tail
            await foreach (var line in reader.ReadNewLinesAsync(CancellationToken.None))
            {
                lineCount++;
                lastLine = line;
            }

            // Fallback: ensure last line populated if file not appended during enumeration
            lastLine ??= await reader.ReadLastLineAsync();
        }
        finally
        {
            sw.Stop();
        }

        process.Refresh();
        long peakAfter = process.PeakWorkingSet64;
        long privateAfter = process.PrivateMemorySize64;
        long gcAfter = GC.GetTotalMemory(forceFullCollection: true);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return CreateResult("optimized-csvtailreader", filePath, sw.Elapsed, peakBefore, peakAfter, privateBefore, privateAfter, gcBefore, gcAfter, firstLine, lastLine, lineCount);
    }

    private async Task<MeasurementResult> MeasureStrategyPipelineAsync(int tickCount = 100_000)
    {
        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        long peakBefore = process.PeakWorkingSet64;
        long privateBefore = process.PrivateMemorySize64;
        long gcBefore = GC.GetTotalMemory(forceFullCollection: true);

        using var serializer = new ExecutionSerializer();
        var tradeManager = new BenchmarkTradeManager();
        var strategies = new List<IStrategy>
        {
            new SmaCrossoverStrategy("SMA_Benchmark", fastPeriod:12, slowPeriod:26),
            new RsiStrategy("RSI_Benchmark", period:14, oversold:30, overbought:70)
        };
        var config = new StrategyCoordinationConfig
        {
            MinimumConfidence = 0.2,
            MinimumTimeBetweenTrades = TimeSpan.Zero,
            CooldownPenalty = 0.0,
            MaxSignalsPerTick = 10,
            MaxSignalsPerSymbol = 2,
            ConfidenceFloor = 0.1
        };
        var strategyCoordinator = new StrategyCoordinator(config);
        var pipeline = new StrategyPipeline(strategies, tradeManager, serializer, strategyCoordinator);

        var rand = new Random(42);
        var sw = Stopwatch.StartNew();
        int signals = 0;

        for (int i = 0; i < tickCount; i++)
        {
            double basePrice = 1.10 + Math.Sin(i / 25.0) * 0.0012;
            double jitter = (rand.NextDouble() - 0.5) * 0.0005;
            double mid = basePrice + jitter;
            double spread = 0.00012 + rand.NextDouble() * 0.00008;
            var data = new MarketData("EURUSD", mid - spread / 2.0, mid + spread / 2.0, DateTime.UtcNow);
            var context = new MarketContext(
                data,
                100_000,
                tradeManager.GetExposureEstimate(),
                tradeManager.GetDrawdownEstimate());

            var result = await pipeline.ProcessAsync(data, context, CancellationToken.None);
            signals += result.Evaluations.Count(e => e.Signal != null);
            tradeManager.Decay();
        }

        sw.Stop();

        process.Refresh();
        long peakAfter = process.PeakWorkingSet64;
        long privateAfter = process.PrivateMemorySize64;
        long gcAfter = GC.GetTotalMemory(forceFullCollection: true);

        var measurement = new MeasurementResult
        {
            Mode = "strategy-pipeline",
            FilePath = string.Empty,
            FileSizeBytes = 0,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            PeakWorkingSetDeltaMb = (peakAfter - peakBefore) / 1024d / 1024d,
            PrivateBytesDeltaMb = (privateAfter - privateBefore) / 1024d / 1024d,
            GcAllocatedMb = (gcAfter - gcBefore) / 1024d / 1024d,
            LineCount = tickCount,
            TimestampUtc = DateTime.UtcNow
        };

            measurement.Meta["ticks"] = tickCount;
            measurement.Meta["signals"] = signals;
            measurement.Meta["ticks_per_sec"] = tickCount / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
            measurement.Meta["signals_per_sec"] = signals / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
            measurement.Meta["trade_manager_calls"] = tradeManager.ProcessCallCount;

            return measurement;
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static MeasurementResult CreateResult(
        string mode,
        string filePath,
        TimeSpan elapsed,
        long peakBefore,
        long peakAfter,
        long privateBefore,
        long privateAfter,
        long gcBefore,
        long gcAfter,
        string? firstLine,
        string? lastLine,
        int lineCount)
    {
        double mb(long bytes) => bytes / 1024d / 1024d;
        var fileInfo = new FileInfo(filePath);

        return new MeasurementResult
        {
            Mode = mode,
            FilePath = filePath,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            DurationMs = elapsed.TotalMilliseconds,
            PeakWorkingSetDeltaMb = mb(Math.Max(0, peakAfter - peakBefore)),
            PrivateBytesDeltaMb = mb(Math.Max(0, privateAfter - privateBefore)),
            GcAllocatedMb = mb(Math.Max(0, gcAfter - gcBefore)),
            LineCount = lineCount,
            FirstLinePreview = firstLine ?? string.Empty,
            LastLinePreview = lastLine ?? string.Empty,
            TimestampUtc = DateTime.UtcNow
        };
    }

    private sealed class BenchmarkTradeManager : ITradeManager
    {
        private double _exposure;
        private double _drawdown;

        public int ProcessCallCount { get; private set; }

        public bool CanTrade(Signal signal, RiskScore riskScore) => riskScore.IsAcceptable;

        public void Process(Signal signal, RiskScore riskScore)
        {
            ProcessCallCount++;
            switch (signal.Action)
            {
                case TradeAction.Buy:
                    _exposure += 10_000;
                    break;
                case TradeAction.Sell:
                    _exposure -= 10_000;
                    break;
                case TradeAction.Exit:
                    _exposure *= 0.5;
                    break;
            }

            _drawdown = Math.Min(_drawdown, -Math.Abs(_exposure) * 0.00001);
        }

        public double GetExposureEstimate() => Math.Abs(_exposure);

        public double GetDrawdownEstimate() => _drawdown;

        public void Decay()
        {
            _exposure *= 0.995;
            _drawdown *= 0.99;
        }
    }

    private static void PrintTable(IEnumerable<MeasurementResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("Mode                         File          SizeMB   DurationMs   PeakΔMB   GCΔMB");
        Console.WriteLine("--------------------------------------------------------------------------------");
        foreach (var group in results.GroupBy(r => r.FilePath))
        {
            foreach (var result in group.OrderBy(r => r.Mode))
            {
                Console.WriteLine($"{result.Mode,-28} {Path.GetFileName(result.FilePath),-12} {result.FileSizeBytes / 1024d / 1024d,7:F1}   {result.DurationMs,10:F1}   {result.PeakWorkingSetDeltaMb,7:F1}   {result.GcAllocatedMb,6:F1}");
                if (result.Meta.Count > 0)
                {
                    var meta = string.Join(", ", result.Meta.Select(kv => $"{kv.Key}={kv.Value}"));
                    Console.WriteLine($"        meta: {meta}");
                }
            }
            Console.WriteLine("--------------------------------------------------------------------------------");
        }
    }
}

internal sealed class MeasurementResult
{
    public string Mode { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public double DurationMs { get; set; }
    public double PeakWorkingSetDeltaMb { get; set; }
    public double PrivateBytesDeltaMb { get; set; }
    public double GcAllocatedMb { get; set; }
    public int LineCount { get; set; }
    public string FirstLinePreview { get; set; } = string.Empty;
    public string LastLinePreview { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public Dictionary<string, object?> Meta { get; set; } = new();
}
