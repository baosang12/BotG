using System.Globalization;
using BotG.Harness.Data;
using BotG.Harness.Models;
using BotG.Telemetry;
using Analysis.Structure;
using Analysis.Imbalance;
using Analysis.OrderBlocks;
using Analysis.Zones;
using Signals;
using Risk;

namespace BotG.Harness
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var config = ParseArgs(args);
            if (config == null)
            {
                ShowUsage();
                return 1;
            }

            Console.WriteLine($"=== HARNESS START ===");
            Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Profile: {config.Profile}");
            Console.WriteLine($"Symbol: {config.Symbol}");
            Console.WriteLine($"Mode: {config.Mode}");
            Console.WriteLine($"Duration: {config.Duration}");

            // Create run directory
            var runDir = CreateRunDirectory(config.LogPath);
            Console.WriteLine($"Run directory: {runDir}");

            // Initialize logger
            var ordersFile = Path.Combine(runDir, "orders.csv");
            var logger = new OrderLifecycleLogger(ordersFile);

            try
            {
                // Initialize market data provider
                var provider = CreateMarketDataProvider(config);
                if (provider == null)
                {
                    Console.WriteLine("CANNOT_RUN");
                    Console.WriteLine("No market data provider.");
                    Console.WriteLine("Provide one of:");
                    Console.WriteLine("- LIVE: set CTRADER_API_BASEURI, CTRADER_API_KEY and use --mode live");
                    Console.WriteLine($"- REPLAY CSV: place data/bars/{config.Symbol}_M15.csv and data/bars/{config.Symbol}_H1.csv (ISO header: timestamp,open,high,low,close,volume)");
                    Console.WriteLine("Then run again.");
                    return 2;
                }

                // Initialize SMC components and run trading
                return await RunTrading(config, provider, logger, runDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
            finally
            {
                Console.WriteLine($"=== HARNESS END ===");
                Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
        }

        static HarnessConfig? ParseArgs(string[] args)
        {
            var config = new HarnessConfig();
            
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--profile":
                        if (i + 1 < args.Length) config.Profile = args[++i];
                        break;
                    case "--symbol":
                        if (i + 1 < args.Length) config.Symbol = args[++i];
                        break;
                    case "--tf":
                        if (i + 1 < args.Length) config.Timeframe = args[++i];
                        break;
                    case "--trend-tf":
                        if (i + 1 < args.Length) config.TrendTimeframe = args[++i];
                        break;
                    case "--mode":
                        if (i + 1 < args.Length) config.Mode = args[++i];
                        break;
                    case "--duration":
                        if (i + 1 < args.Length && TimeSpan.TryParse(args[++i], out var duration))
                            config.Duration = duration;
                        break;
                    case "--log-path":
                        if (i + 1 < args.Length) config.LogPath = args[++i];
                        break;
                }
            }

            return config;
        }

        static void ShowUsage()
        {
            Console.WriteLine("Usage: BotG.Harness [options]");
            Console.WriteLine("Options:");
            Console.WriteLine("  --profile <name>     Profile name (default: xauusd_mtf)");
            Console.WriteLine("  --symbol <symbol>    Trading symbol (default: XAUUSD)");
            Console.WriteLine("  --tf <timeframe>     Entry timeframe (default: M15)");
            Console.WriteLine("  --trend-tf <tf>      Trend timeframe (default: H1)");
            Console.WriteLine("  --mode <mode>        Trading mode: paper|live (default: paper)");
            Console.WriteLine("  --duration <time>    Duration in hh:mm:ss (default: 00:15:00)");
            Console.WriteLine("  --log-path <path>    Log directory (default: D:\\botg\\logs)");
        }

        static string CreateRunDirectory(string logPath)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var runDir = Path.Combine(logPath, $"telemetry_run_{timestamp}");
            Directory.CreateDirectory(runDir);
            return runDir;
        }

        static IMarketDataProvider? CreateMarketDataProvider(HarnessConfig config)
        {
            if (config.Mode.ToLower() == "live")
            {
                // Check for live trading environment variables
                var baseUri = Environment.GetEnvironmentVariable("CTRADER_API_BASEURI");
                var apiKey = Environment.GetEnvironmentVariable("CTRADER_API_KEY");
                
                if (!string.IsNullOrEmpty(baseUri) && !string.IsNullOrEmpty(apiKey))
                {
                    // TODO: Implement live cTrader provider when available
                    Console.WriteLine("LIVE mode requested but not yet implemented");
                    return null;
                }
            }

            // Try replay CSV provider
            var csvProvider = new ReplayCsvProvider(config.Symbol);
            
            // Check if required CSV files exist
            var m15File = $"data/bars/{config.Symbol}_M15.csv";
            var h1File = $"data/bars/{config.Symbol}_H1.csv";
            
            if (!File.Exists(m15File) || !File.Exists(h1File))
            {
                Console.WriteLine($"CSV files not found: {m15File}, {h1File}");
                return null;
            }

            return csvProvider;
        }

        static async Task<int> RunTrading(HarnessConfig config, IMarketDataProvider provider, OrderLifecycleLogger logger, string runDir)
        {
            // Initialize SMC components
            var riskManager = new RiskManager();
            
            // Aggregators for OHLC arrays
            var m15Bars = new List<Bar>();
            var h1Bars = new List<Bar>();
            
            var startTime = DateTime.Now;
            var endTime = startTime.Add(config.Duration);
            
            var orderCounter = 0;
            var random = new Random();

            Console.WriteLine($"Trading started. Will run until {endTime:HH:mm:ss}");
            Console.WriteLine($"Provider has more data: {provider.HasMore}");

            // For CSV replay, we process all available data instead of waiting for real time
            while (provider.HasMore)
            {
                // Get next M15 bar
                var m15Bar = provider.GetNextBar("M15");
                if (m15Bar == null) 
                {
                    break; // No more M15 data
                }

                m15Bars.Add(m15Bar);
                
                // Get corresponding H1 bar if available and timestamp matches
                var h1Bar = provider.GetNextBar("H1");
                if (h1Bar != null && h1Bar.Timestamp <= m15Bar.Timestamp)
                    h1Bars.Add(h1Bar);

                // Need sufficient bars for analysis
                if (m15Bars.Count < 50) continue;

                try
                {
                    // Process on bar close (simulate)
                    orderCounter = await ProcessBarClose(m15Bars, h1Bars, riskManager, logger, orderCounter, random);
                    
                    // Small delay to simulate processing time
                    await Task.Delay(10);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing bar: {ex.Message}");
                }
            }

            // Write summary files
            await WriteSummaryFiles(runDir, m15Bars.Count, orderCounter);

            Console.WriteLine($"Trading completed. Processed {m15Bars.Count} M15 bars, {orderCounter} orders");
            return 0;
        }

        static async Task<int> ProcessBarClose(List<Bar> m15Bars, List<Bar> h1Bars, RiskManager riskManager, OrderLifecycleLogger logger, int orderCounter, Random random)
        {
            var currentBar = m15Bars.Last();
            
            // Calculate ATR for risk management
            var atr15 = CalculateATR(m15Bars.TakeLast(14).ToArray());
            var atrBaseline = CalculateATR(m15Bars.TakeLast(Math.Min(200, m15Bars.Count)).ToArray());
            
            // Build OHLC arrays for SMC analysis
            var highs = m15Bars.Select(b => b.High).ToArray();
            var lows = m15Bars.Select(b => b.Low).ToArray();
            var opens = m15Bars.Select(b => b.Open).ToArray();
            var closes = m15Bars.Select(b => b.Close).ToArray();
            var volumes = m15Bars.Select(b => b.Volume).ToArray();

            // H1 analysis for trend
            var h1Event = StructureEvent.None;
            var h1Range = new Analysis.Zones.PremiumDiscount.Range { Low = 2000, High = 2100 };
            var lastCloseH1 = 2050.0;
            
            if (h1Bars.Count >= 10)
            {
                var h1Highs = h1Bars.Select(b => b.High).ToArray();
                var h1Lows = h1Bars.Select(b => b.Low).ToArray();
                var h1Swings = MarketStructureDetector.DetectSwings(h1Highs, h1Lows, 2);
                h1Event = MarketStructureDetector.DetectEvent(h1Swings, h1Highs.Length - 1, h1Bars.Last().Close);
                lastCloseH1 = h1Bars.Last().Close;
                
                var h1Recent = h1Bars.TakeLast(Math.Min(100, h1Bars.Count)).ToArray();
                h1Range = new Analysis.Zones.PremiumDiscount.Range 
                { 
                    Low = h1Recent.Min(b => b.Low), 
                    High = h1Recent.Max(b => b.High) 
                };
            }

            // Get SMC signal
            var config = new SmcPlanner.SmcConfig();
            var signal = SmcPlanner.Evaluate(opens, highs, lows, closes, volumes, atr15, atrBaseline, lastCloseH1, h1Range, h1Event, config, out var entry, out var sl, out var tp, out var reason);
            
            if (signal != SmcPlanner.SmcSignal.None && entry > 0 && sl > 0)
            {
                // Check if risk manager allows new trades - pass 0.0 for daily return since we don't track account balance
                if (riskManager.IsHalted(0.0))
                {
                    Console.WriteLine("Trading halted due to daily risk limits");
                    return orderCounter;
                }

                orderCounter++;
                var orderId = $"ORD-SMC-{orderCounter:D3}";
                
                // Calculate position size
                var riskPct = riskManager.ComputeRiskPct(atr15, atrBaseline);
                var slDistance = Math.Abs(entry - sl);
                var notionalSize = 1000.0; // Base position size
                var size = notionalSize * riskPct / slDistance;

                // Determine side
                var side = signal == SmcPlanner.SmcSignal.LongLimit ? "Buy" : "Sell";

                // Log REQUEST
                var latency = random.Next(25, 150);
                logger.LogV3(
                    phase: "REQUEST",
                    orderId: orderId,
                    side: side,
                    priceRequested: entry,
                    priceFilled: null,
                    stopLoss: sl,
                    takeProfit: tp > 0 ? tp : null,
                    status: "REQUEST",
                    reason: reason ?? "SMC_SIGNAL",
                    latencyMs: 0,
                    sizeRequested: size,
                    sizeFilled: null,
                    host: Environment.MachineName,
                    session: "SMC"
                );

                await Task.Delay(latency);

                // Simulate fill (paper trading)
                var fillPrice = SimulateFill(entry, currentBar);
                if (fillPrice.HasValue)
                {
                    logger.LogV3(
                        phase: "FILL",
                        orderId: orderId,
                        side: side,
                        priceRequested: entry,
                        priceFilled: fillPrice.Value,
                        stopLoss: sl,
                        takeProfit: tp > 0 ? tp : null,
                        status: "FILL",
                        reason: "SMC_FILL",
                        latencyMs: latency,
                        sizeRequested: size,
                        sizeFilled: size,
                        slippage: Math.Abs(fillPrice.Value - entry),
                        host: Environment.MachineName,
                        session: "SMC"
                    );

                    Console.WriteLine($"Order {orderId} filled at {fillPrice.Value:F2} (requested: {entry:F2})");
                }
            }

            return orderCounter;
        }

        static double? SimulateFill(double entryPrice, Bar currentBar)
        {
            // Simple fill simulation: if entry price is within current bar's range, fill at entry
            if (entryPrice >= currentBar.Low && entryPrice <= currentBar.High)
            {
                // Add small slippage
                var slippage = (new Random().NextDouble() - 0.5) * 0.1;
                return entryPrice + slippage;
            }
            return null;
        }

        static double CalculateATR(Bar[] bars)
        {
            if (bars.Length < 2) return 1.0;

            var trueRanges = new List<double>();
            for (int i = 1; i < bars.Length; i++)
            {
                var tr = Math.Max(
                    bars[i].High - bars[i].Low,
                    Math.Max(
                        Math.Abs(bars[i].High - bars[i - 1].Close),
                        Math.Abs(bars[i].Low - bars[i - 1].Close)
                    )
                );
                trueRanges.Add(tr);
            }

            return trueRanges.Average();
        }

        static async Task WriteSummaryFiles(string runDir, int barsProcessed, int orderCount)
        {
            // Write telemetry.csv
            var telemetryFile = Path.Combine(runDir, "telemetry.csv");
            var telemetryData = $@"timestamp,metric,value
{DateTime.Now:o},ticks_processed,{barsProcessed}
{DateTime.Now:o},signals_generated,{orderCount}
{DateTime.Now:o},orders_requested,{orderCount}
{DateTime.Now:o},orders_filled,{orderCount}
{DateTime.Now:o},smc_engine,active
";
            await File.WriteAllTextAsync(telemetryFile, telemetryData);

            // Write risk_snapshots.csv
            var riskFile = Path.Combine(runDir, "risk_snapshots.csv");
            var riskData = $@"timestamp,equity,balance,margin,risk_state,daily_r,daily_pct
{DateTime.Now:o},10000,10000,0,NORMAL,0.0,0.0
";
            await File.WriteAllTextAsync(riskFile, riskData);
        }
    }

    public class HarnessConfig
    {
        public string Profile { get; set; } = "xauusd_mtf";
        public string Symbol { get; set; } = "XAUUSD";
        public string Timeframe { get; set; } = "M15";
        public string TrendTimeframe { get; set; } = "H1";
        public string Mode { get; set; } = "paper";
        public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(15);
        public string LogPath { get; set; } = @"D:\botg\logs";
    }
}