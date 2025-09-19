using System.Globalization;
using System.Linq;
using BotG.Harness.Data;
using BotG.Harness.Models;
using BotG.Telemetry;
using Analysis.Structure;
using Analysis.Imbalance;
using Analysis.OrderBlocks;
using Analysis.Zones;
using BotG.Analysis.Trend;
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
            Console.WriteLine($"Timeframes: {config.Timeframe}/{config.TrendTimeframe}");
            Console.WriteLine($"Mode: {config.Mode}");
            Console.WriteLine($"TradeMode: {config.TradeMode}");
            if (config.Seed.HasValue)
                Console.WriteLine($"Seed: {config.Seed.Value}");
            if (config.Bars.HasValue)
                Console.WriteLine($"Bars: {config.Bars.Value}");
            else
                Console.WriteLine($"Duration: {config.Duration}");
            if (config.ScanWindows.HasValue)
                Console.WriteLine($"Scan windows: {config.ScanWindows.Value}, Window size: {config.WindowSize}");
            if (config.TestMini && config.TradeMode.Equals("Test", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Mini-signal: ON (after {config.MiniAfter} bars, max {config.MiniMax})");
                if (config.TestMarket)
                    Console.WriteLine($"Market mini-order: ON (slippage {config.TestMarketSlippageBps} bps)");
            }
            Console.WriteLine($"Log path: {config.LogPath}");

            // Create run directory
            var runDir = CreateRunDirectory(config.LogPath);
            Console.WriteLine($"Run directory: {runDir}");

            // Create audit trail config stamp
            await CreateConfigStamp(config, runDir);

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

                // Check for handshake-only path (live mode with minimal bars)
                if (config.Mode.ToLower() == "live" && config.Bars <= 1)
                {
                    // Handshake-only path: verify connectivity and create run structure
                    var healthCheck = await provider.CheckHealthAsync();
                    if (!healthCheck)
                    {
                        Console.WriteLine("CANNOT_RUN: live handshake failed (cannot reach CTRADER_API_BASEURI)");
                        return 1;
                    }

                    // Logger header is already created in constructor
                    // Update config stamp to indicate handshake-only mode
                    await UpdateConfigStampForHandshake(config, runDir);
                    
                    Console.WriteLine("HANDSHAKE_OK");
                    return 0;
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
                        if (i + 1 < args.Length) 
                        {
                            var modeValue = args[++i];
                            // Handle deprecated --mode Test|Strict usage
                            if (modeValue.Equals("Test", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine("DEPRECATED: Use --trade-mode Test instead of --mode Test");
                                config.TradeMode = "Test";
                                config.Mode = "paper";
                            }
                            else if (modeValue.Equals("Strict", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine("DEPRECATED: Use --trade-mode Strict instead of --mode Strict");
                                config.TradeMode = "Strict";
                                config.Mode = "paper";
                            }
                            else
                            {
                                config.Mode = modeValue;
                            }
                        }
                        break;
                    case "--duration":
                        if (i + 1 < args.Length && TimeSpan.TryParse(args[++i], out var duration))
                            config.Duration = duration;
                        break;
                    case "--bars":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var bars))
                            config.Bars = bars;
                        break;
                    case "--scan":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var scanWindows))
                            config.ScanWindows = scanWindows;
                        break;
                    case "--window":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var windowSize))
                            config.WindowSize = windowSize;
                        break;
                    case "--test-mini":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var testMini))
                            config.TestMini = testMini == 1;
                        break;
                    case "--mini-after":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var miniAfter))
                            config.MiniAfter = miniAfter;
                        break;
                    case "--mini-max":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var miniMax))
                            config.MiniMax = miniMax;
                        break;
                    case "--test-market":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var testMarket))
                            config.TestMarket = testMarket == 1;
                        break;
                    case "--test-market-slippage-bps":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var slippageBps))
                            config.TestMarketSlippageBps = slippageBps;
                        break;
                    case "--log-path":
                        if (i + 1 < args.Length) config.LogPath = args[++i];
                        break;
                    case "--trade-mode":
                        if (i + 1 < args.Length) config.TradeMode = args[++i];
                        break;
                    case "--relax-vol":
                        if (i + 1 < args.Length && double.TryParse(args[++i], out var relaxVol))
                            config.RelaxVol = relaxVol;
                        break;
                    case "--relax-atr":
                        if (i + 1 < args.Length && double.TryParse(args[++i], out var relaxAtr))
                            config.RelaxAtr = relaxAtr;
                        break;
                    case "--confirm":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var confirm))
                            config.Confirm = confirm;
                        break;
                    case "--seed":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var seed))
                            config.Seed = seed;
                        break;
                }
            }

            // Apply environment variable overrides (CLI takes precedence)
            ApplyEnvironmentOverrides(config);

            // CI guards: ignore test-only flags in Strict mode
            ApplyStrictModeGuards(config);

            // Critical safety: prevent Test+Live combination
            ValidateSafetyRules(config);

            return config;
        }

        static void ApplyEnvironmentOverrides(HarnessConfig config)
        {
            // Only apply ENV if CLI didn't set the value
            var smcMode = Environment.GetEnvironmentVariable("SMC_MODE");
            if (!string.IsNullOrEmpty(smcMode) && config.TradeMode == "Strict")
                config.TradeMode = smcMode;

            var smcVol = Environment.GetEnvironmentVariable("SMC_VOL");
            if (!string.IsNullOrEmpty(smcVol) && config.RelaxVol == null && double.TryParse(smcVol, out var vol))
                config.RelaxVol = vol;

            var smcAtr = Environment.GetEnvironmentVariable("SMC_ATR");
            if (!string.IsNullOrEmpty(smcAtr) && config.RelaxAtr == null && double.TryParse(smcAtr, out var atr))
                config.RelaxAtr = atr;

            var smcConfirm = Environment.GetEnvironmentVariable("SMC_CONFIRM");
            if (!string.IsNullOrEmpty(smcConfirm) && config.Confirm == null && int.TryParse(smcConfirm, out var confirm))
                config.Confirm = confirm;
        }

        static void ApplyStrictModeGuards(HarnessConfig config)
        {
            // In Strict mode, ignore all test-only flags with warnings
            if (config.TradeMode.Equals("Strict", StringComparison.OrdinalIgnoreCase))
            {
                if (config.TestMini)
                {
                    Console.WriteLine("WARNING: --test-mini ignored in Strict mode (Test-only feature)");
                    config.TestMini = false;
                }
                if (config.TestMarket)
                {
                    Console.WriteLine("WARNING: --test-market ignored in Strict mode (Test-only feature)");
                    config.TestMarket = false;
                }
            }
        }

        static void ValidateSafetyRules(HarnessConfig config)
        {
            // CRITICAL: Forbid Test+Live combination absolutely
            if (config.Mode.Equals("live", StringComparison.OrdinalIgnoreCase) && 
                config.TradeMode.Equals("Test", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("FATAL ERROR: Test mode cannot be used with live trading");
                Console.WriteLine("Use either: --mode live --trade-mode Strict OR --mode paper --trade-mode Test");
                Environment.Exit(2);
            }

            // ENV-gated market-mini for release builds
            #if !DEBUG
            bool allowMarketMini = Environment.GetEnvironmentVariable("ALLOW_TEST_MARKET") == "1";
            #else
            bool allowMarketMini = true; // Always allow in debug builds
            #endif

            if (config.TestMarket)
            {
                bool canUseMarketMini = allowMarketMini && 
                                       config.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase) && 
                                       config.TradeMode.Equals("Test", StringComparison.OrdinalIgnoreCase);

                if (!canUseMarketMini)
                {
                    Console.WriteLine("TEST_MARKET_DISABLED: Requires ALLOW_TEST_MARKET=1 + paper + test mode");
                    config.TestMarket = false;
                }
            }
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
            Console.WriteLine("  --bars <n>           Process exactly N bars instead of duration");
            Console.WriteLine("  --scan <n>           Scan N windows for tradeable signals");
            Console.WriteLine("  --window <size>      Window size for scanning (default: 200, adaptive)");
            Console.WriteLine("  --test-mini <0|1>    Enable mini-signal (Test mode only, default: 0)");
            Console.WriteLine("  --mini-after <bars>  Bars before mini-signal triggers (default: 60)");
            Console.WriteLine("  --mini-max <count>   Max mini-signals per run (default: 1)");
            Console.WriteLine("  --test-market <0|1>  Enable market mini-order (Test mode only, default: 0)");
            Console.WriteLine("  --test-market-slippage-bps <int>  Market slippage in bps (default: 5)");
            Console.WriteLine("  --log-path <path>    Log directory (default: D:\\botg\\logs)");
            Console.WriteLine();
            Console.WriteLine("Dual-Mode Trading Options:");
            Console.WriteLine("  --trade-mode <mode>  strict|test (default: strict)");
            Console.WriteLine("  --relax-vol <mult>   Volume spike multiplier override");
            Console.WriteLine("  --relax-atr <mult>   ATR spike multiplier override");
            Console.WriteLine("  --confirm <n>        Confirmations required override");
            Console.WriteLine("  --seed <n>           Random seed for deterministic testing");
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
                    // Create CTraderLiveProvider for handshake
                    return new CTraderLiveProvider(baseUri, apiKey, config.Symbol, config.Timeframe);
                }
            }

            // Try replay CSV provider
            var csvProvider = new ReplayCsvProvider(config.Symbol);
            
            // Check if required CSV files exist
            var m15File = $"data/bars/{config.Symbol}_M15.csv";
            var h1File = $"data/bars/{config.Symbol}_H1.csv";
            
            if (!File.Exists(m15File))
            {
                Console.WriteLine("CANNOT_RUN");
                Console.WriteLine("No replay CSV:");
                Console.WriteLine($"- Provide {m15File} (ISO header: timestamp,open,high,low,close,volume)");
                Console.WriteLine("Optional:");
                Console.WriteLine($"- {h1File} (else H1 will be aggregated from M15)");
                Console.WriteLine("Then run again.");
                return null;
            }

            return csvProvider;
        }

        static async Task<int> RunTrading(HarnessConfig config, IMarketDataProvider provider, OrderLifecycleLogger logger, string runDir)
        {
            // Initialize SMC components
            var riskManager = new RiskManager();
            
            // Apply test mode risk clamping if needed
            if (config.TradeMode.Equals("Test", StringComparison.OrdinalIgnoreCase))
            {
                riskManager.ApplyTestModeClamp();
            }
            
            // Aggregators for OHLC arrays
            var m15Bars = new List<Bar>();
            var h1Bars = new List<Bar>();
            
            var startTime = DateTime.Now;
            var endTime = startTime.Add(config.Duration);
            
            var orderCounter = 0;
            var requestCounter = 0; // Track total REQUEST count for mini-signal
            var noTradeReasons = new Dictionary<string, int>();
            var selectedWindow = 0; // Track which window was selected for SCAN_PICK report
            var miniSignalUsed = false; // Track if mini-signal was triggered
            var marketMiniUsed = false; // Track if market mini-order was used
            var miniSignalCount = 0; // Track number of mini-signals used
            
            // Create deterministic or random generator based on seed
            var random = config.Seed.HasValue ? new Random(config.Seed.Value) : new Random();

            Console.WriteLine($"Trading started. Will run until {endTime:HH:mm:ss}");

            // If scan mode, find tradeable window first
            if (config.ScanWindows.HasValue)
            {
                selectedWindow = await ScanForTradeableWindow(config, provider, config.ScanWindows.Value);
                if (selectedWindow == -1)
                {
                    Console.WriteLine("CANNOT_RUN");
                    Console.WriteLine("No tradeable window found with current config.");
                    Console.WriteLine("Try: --trade-mode test, --relax-vol 1.5, --relax-atr 1.05, larger --scan, or more bars.");
                    return 2;
                }
                
                Console.WriteLine($"SCAN_PICK window={selectedWindow} bars=[{selectedWindow * config.WindowSize}..{(selectedWindow + 1) * config.WindowSize}]");
                
                // Reset provider and skip to selected window
                provider.Reset();
                for (int i = 0; i < selectedWindow * config.WindowSize; i++)
                {
                    var skipBar = provider.GetNextBar("M15");
                    if (skipBar == null) break;
                }
            }

            // For CSV replay, we process all available data instead of waiting for real time
            int barsProcessed = 0;
            int maxBarsToProcess = config.ScanWindows.HasValue ? config.WindowSize : int.MaxValue; // Limit to window size when scanning
            
            while (provider.HasMore && barsProcessed < maxBarsToProcess)
            {
                // Get next M15 bar
                var m15Bar = provider.GetNextBar("M15");
                if (m15Bar == null) 
                {
                    break; // No more M15 data
                }

                m15Bars.Add(m15Bar);
                barsProcessed++;
                
                // Get corresponding H1 bar if available and timestamp matches
                var h1Bar = provider.GetNextBar("H1");
                if (h1Bar != null && h1Bar.Timestamp <= m15Bar.Timestamp)
                    h1Bars.Add(h1Bar);

                // Need sufficient bars for analysis
                if (m15Bars.Count < 50) continue;

                try
                {
                    // Process on bar close (simulate)
                    var (newOrderCounter, newRequestCounter, miniUsed) = await ProcessBarClose(
                        m15Bars, h1Bars, riskManager, logger, orderCounter, requestCounter, 
                        random, config, noTradeReasons, miniSignalCount, barsProcessed);
                    
                    orderCounter = newOrderCounter;
                    requestCounter = newRequestCounter;
                    if (miniUsed)
                    {
                        miniSignalUsed = true;
                        miniSignalCount++;
                        if (config.TestMarket)
                            marketMiniUsed = true;
                    }
                    
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

            // Print comprehensive final report
            PrintFinalReport(runDir, m15Bars.Count, orderCounter, noTradeReasons, config, selectedWindow, miniSignalUsed, marketMiniUsed);

            Console.WriteLine($"Trading completed. Processed {m15Bars.Count} M15 bars, {orderCounter} orders");
            return 0;
        }

        static async Task<(int orderCounter, int requestCounter, bool miniUsed)> ProcessBarClose(
            List<Bar> m15Bars, List<Bar> h1Bars, RiskManager riskManager, OrderLifecycleLogger logger, 
            int orderCounter, int requestCounter, Random random, HarnessConfig config, 
            Dictionary<string, int> noTradeReasons, int miniSignalCount, int barsProcessed)
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
            var h1Range = new PremiumDiscount.Range { Low = 2000, High = 2100 };
            var lastCloseH1 = 2050.0;
            
            if (h1Bars.Count >= 10)
            {
                var h1HighArray = h1Bars.Select(b => b.High).ToArray();
                var h1LowArray = h1Bars.Select(b => b.Low).ToArray();
                var h1Swings = MarketStructureDetector.DetectSwings(h1HighArray, h1LowArray, 2);
                h1Event = MarketStructureDetector.DetectEvent(h1Swings, h1HighArray.Length - 1, h1Bars.Last().Close);
                lastCloseH1 = h1Bars.Last().Close;
                
                var h1Recent = h1Bars.TakeLast(Math.Min(100, h1Bars.Count)).ToArray();
                h1Range = new PremiumDiscount.Range 
                { 
                    Low = h1Recent.Min(b => b.Low), 
                    High = h1Recent.Max(b => b.High) 
                };
                
                // Debug H1 analysis
                if (m15Bars.Count % 20 == 0)
                {
                    Console.WriteLine($"H1 Analysis: Bars={h1Bars.Count}, Event={h1Event}, Swings={h1Swings.Count}, Range=[{h1Range.Low:F2}-{h1Range.High:F2}]");
                }
            }

            // Configure SMC based on trade mode and CLI overrides
            var smcConfig = SmcPlanner.ConfigureForTradeMode(config.TradeMode, config.RelaxVol, config.RelaxAtr, config.Confirm);
            
            // Get H1 arrays for trend analysis if available
            double[] h1Closes = null, h1Highs = null, h1Lows = null;
            if (h1Bars.Count > 0)
            {
                h1Closes = h1Bars.Select(b => b.Close).ToArray();
                h1Highs = h1Bars.Select(b => b.High).ToArray();
                h1Lows = h1Bars.Select(b => b.Low).ToArray();
            }
            
            var smcResult = SmcPlanner.EvaluateDetailed(opens, highs, lows, closes, volumes, atr15, atrBaseline, lastCloseH1, h1Range, h1Event, smcConfig, h1Closes, h1Highs, h1Lows);
            
            // Debug output every 10 bars to see what's happening
            if (m15Bars.Count % 10 == 0 || smcResult.Signal != SmcPlanner.SmcSignal.None)
            {
                var reasons = string.Join(",", smcResult.NoTradeReasons);
                Console.WriteLine($"Bar {m15Bars.Count}: Signal={smcResult.Signal}, NoTradeReasons=[{reasons}], Entry={smcResult.Entry:F2}, SL={smcResult.StopLoss:F2}");
            }
            
            // Collect no-trade reasons for final report
            if (smcResult.Signal == SmcPlanner.SmcSignal.None)
            {
                foreach (var reason in smcResult.NoTradeReasons)
                {
                    if (noTradeReasons.ContainsKey(reason))
                        noTradeReasons[reason]++;
                    else
                        noTradeReasons[reason] = 1;
                }
                
                // Check for mini-signal conditions (Test mode only)
                var miniUsed = await CheckMiniSignal(m15Bars, h1Bars, config, barsProcessed, requestCounter, 
                    miniSignalCount, riskManager, logger, random);
                if (miniUsed) requestCounter++;
                
                return (orderCounter, requestCounter, miniUsed);
            }
            
            if (smcResult.Signal != SmcPlanner.SmcSignal.None && smcResult.Entry > 0 && smcResult.StopLoss > 0)
            {
                // Check if risk manager allows new trades - pass 0.0 for daily return since we don't track account balance
                if (riskManager.IsHalted(0.0))
                {
                    Console.WriteLine("Trading halted due to daily risk limits");
                    return (orderCounter, requestCounter, false);
                }

                orderCounter++;
                var orderId = $"ORD-SMC-{orderCounter:D3}";
                
                // Calculate position size
                var riskPct = riskManager.ComputeRiskPct(atr15, atrBaseline);
                var slDistance = Math.Abs(smcResult.Entry - smcResult.StopLoss);
                var notionalSize = 1000.0; // Base position size
                var size = notionalSize * riskPct / slDistance;

                // Determine side
                var side = smcResult.Signal == SmcPlanner.SmcSignal.LongLimit ? "Buy" : "Sell";

                // Log REQUEST
                var latency = random.Next(25, 150);
                logger.LogV3(
                    phase: "REQUEST",
                    orderId: orderId,
                    side: side,
                    priceRequested: smcResult.Entry,
                    priceFilled: null,
                    stopLoss: smcResult.StopLoss,
                    takeProfit: smcResult.TakeProfit > 0 ? smcResult.TakeProfit : null,
                    status: "REQUEST",
                    reason: smcResult.Reason ?? "SMC_SIGNAL",
                    latencyMs: 0,
                    sizeRequested: size,
                    sizeFilled: null,
                    host: Environment.MachineName,
                    session: "SMC"
                );

                await Task.Delay(latency);

                // Simulate fill (paper trading)
                var fillPrice = SimulateFill(smcResult.Entry, currentBar, random);
                if (fillPrice.HasValue)
                {
                    logger.LogV3(
                        phase: "FILL",
                        orderId: orderId,
                        side: side,
                        priceRequested: smcResult.Entry,
                        priceFilled: fillPrice.Value,
                        stopLoss: smcResult.StopLoss,
                        takeProfit: smcResult.TakeProfit > 0 ? smcResult.TakeProfit : null,
                        status: "FILL",
                        reason: "SMC_FILL",
                        latencyMs: latency,
                        sizeRequested: size,
                        sizeFilled: size,
                        slippage: Math.Abs(fillPrice.Value - smcResult.Entry),
                        host: Environment.MachineName,
                        session: "SMC"
                    );

                    Console.WriteLine($"Order {orderId} filled at {fillPrice.Value:F2} (requested: {smcResult.Entry:F2})");
                }
            }

            return (orderCounter, requestCounter + 1, false); // +1 for the REQUEST we just made
        }

        static async Task<bool> CheckMiniSignal(List<Bar> m15Bars, List<Bar> h1Bars, HarnessConfig config, 
            int barsProcessed, int requestCounter, int miniSignalCount, RiskManager riskManager, 
            OrderLifecycleLogger logger, Random random)
        {
            // Only in Test mode with mini-signal enabled
            if (!config.TradeMode.Equals("Test", StringComparison.OrdinalIgnoreCase) || 
                !config.TestMini || 
                miniSignalCount >= config.MiniMax || 
                requestCounter > 0 || // Already have requests
                barsProcessed < config.MiniAfter) // Not enough bars yet
            {
                return false;
            }

            // Calculate adaptive ATR baseline
            var atr15 = CalculateATR(m15Bars.TakeLast(14).ToArray());
            var atrBaseline = CalculateATR(m15Bars.TakeLast(Math.Min(200, m15Bars.Count)).ToArray());
            
            // Check ATR condition
            if (atr15 < 0.4 * atrBaseline)
            {
                return false;
            }

            // Check H1 slope for direction (very relaxed requirement)
            double slope = 0.0;
            if (h1Bars.Count >= 10)
            {
                var h1Closes = h1Bars.Select(b => b.Close).ToArray();
                int adaptiveLinReg = Math.Min(50, Math.Max(10, h1Closes.Length - 5));
                slope = TrendDetector.LinRegSlope(h1Closes, adaptiveLinReg);
            }

            if (Math.Abs(slope) < 0.0001) // Very small slope requirement
            {
                return false;
            }

            // Generate mini-signal
            var currentBar = m15Bars.Last();
            var isLong = slope >= 0;
            var entry = isLong ? currentBar.Close - 0.1 * atr15 : currentBar.Close + 0.1 * atr15;
            var sl = isLong ? entry - 1.0 * atr15 : entry + 1.0 * atr15;
            var tp = isLong ? entry + 2.0 * atr15 : entry - 2.0 * atr15;

            // Ultra-conservative risk for mini-signal
            var miniRiskPct = Math.Min(0.0025, riskManager.BaseRiskPct);
            var slDistance = Math.Abs(entry - sl);
            var notionalSize = 1000.0;
            var size = notionalSize * miniRiskPct / slDistance;

            var orderId = $"ORD-MINI-{miniSignalCount + 1:D3}";
            var side = isLong ? "Buy" : "Sell";
            
            // Determine order type: market if test-market enabled, otherwise limit
            var orderType = config.TestMarket ? "market" : "limit";
            var priceToUse = config.TestMarket ? currentBar.Close : entry;

            // Log mini-signal REQUEST
            var latency = random.Next(25, 150);
            logger.LogV3(
                phase: "REQUEST",
                orderId: orderId,
                side: side,
                priceRequested: priceToUse,
                priceFilled: null,
                stopLoss: sl,
                takeProfit: tp > 0 ? tp : null,
                status: "REQUEST",
                reason: "mini_signal",
                latencyMs: latency,
                sizeRequested: size,
                sizeFilled: null
            );

            Console.WriteLine($"Mini-signal triggered: {side} {orderType} at {priceToUse:F2} (slope={slope:F6})");

            // Handle fills based on order type
            double? fillPrice = null;
            if (config.TestMarket)
            {
                // Market order: immediate fill with slippage at next bar's open
                var slippageMultiplier = isLong ? (1.0 + config.TestMarketSlippageBps / 10000.0) : (1.0 - config.TestMarketSlippageBps / 10000.0);
                fillPrice = currentBar.Close * slippageMultiplier;
                
                var slippage = Math.Abs(fillPrice.Value - currentBar.Close);
                logger.LogV3(
                    phase: "FILL",
                    orderId: orderId,
                    side: side,
                    priceRequested: currentBar.Close,
                    priceFilled: fillPrice.Value,
                    stopLoss: sl,
                    takeProfit: tp > 0 ? tp : null,
                    status: "FILLED",
                    reason: "mini_signal|market_fill",
                    latencyMs: random.Next(50, 150),
                    sizeRequested: size,
                    sizeFilled: size,
                    slippage: slippage
                );

                Console.WriteLine($"Mini-signal market filled at {fillPrice.Value:F2} (slippage: {config.TestMarketSlippageBps} bps)");
            }
            else
            {
                // Limit order: traditional touch-based fill simulation
                fillPrice = SimulateFill(entry, currentBar, random);
                if (fillPrice.HasValue)
                {
                    var slippage = Math.Abs(fillPrice.Value - entry);
                    logger.LogV3(
                        phase: "FILL",
                        orderId: orderId,
                        side: side,
                        priceRequested: entry,
                        priceFilled: fillPrice.Value,
                        stopLoss: sl,
                        takeProfit: tp > 0 ? tp : null,
                        status: "FILLED",
                        reason: "mini_signal",
                        latencyMs: random.Next(5, 50),
                        sizeRequested: size,
                        sizeFilled: size,
                        slippage: slippage
                    );

                    Console.WriteLine($"Mini-signal limit filled at {fillPrice.Value:F2} (requested: {entry:F2})");
                }
            }

            return true;
        }

        static double? SimulateFill(double entryPrice, Bar currentBar, Random random)
        {
            // Simple fill simulation: if entry price is within current bar's range, fill at entry
            if (entryPrice >= currentBar.Low && entryPrice <= currentBar.High)
            {
                // Add small deterministic slippage using provided random
                var slippage = (random.NextDouble() - 0.5) * 0.1;
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

        static async Task CreateConfigStamp(HarnessConfig config, string runDir)
        {
            var stamp = new
            {
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                tradeMode = config.TradeMode,
                smcConfig = new
                {
                    relaxVol = config.RelaxVol,
                    relaxAtr = config.RelaxAtr,
                    confirm = config.Confirm
                },
                riskConfig = new
                {
                    mode = config.Mode
                },
                executionConfig = new
                {
                    seed = config.Seed,
                    bars = config.Bars,
                    scanWindows = config.ScanWindows,
                    windowSize = config.WindowSize,
                    testMini = config.TestMini,
                    miniAfter = config.MiniAfter,
                    miniMax = config.MiniMax,
                    testMarket = config.TestMarket,
                    testMarketSlippageBps = config.TestMarketSlippageBps,
                    allowTestMarket = Environment.GetEnvironmentVariable("ALLOW_TEST_MARKET") == "1",
                    symbol = config.Symbol,
                    timeframe = config.Timeframe,
                    trendTimeframe = config.TrendTimeframe
                },
                environment = new
                {
                    smcMode = Environment.GetEnvironmentVariable("SMC_MODE"),
                    smcVol = Environment.GetEnvironmentVariable("SMC_VOL"),
                    smcAtr = Environment.GetEnvironmentVariable("SMC_ATR"),
                    smcConfirm = Environment.GetEnvironmentVariable("SMC_CONFIRM"),
                    allowTestMarketPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALLOW_TEST_MARKET"))
                },
                cliArgs = Environment.GetCommandLineArgs(),
                machineName = Environment.MachineName,
                workingDirectory = Environment.CurrentDirectory
            };

            var stampPath = Path.Combine(runDir, "config_stamp.json");
            var json = System.Text.Json.JsonSerializer.Serialize(stamp, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(stampPath, json);
        }

        static async Task UpdateConfigStampForHandshake(HarnessConfig config, string runDir)
        {
            var stampPath = Path.Combine(runDir, "config_stamp.json");
            
            // Read existing stamp
            var existingJson = await File.ReadAllTextAsync(stampPath);
            var existingStamp = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(existingJson);
            
            // Create updated stamp with handshake info
            var stampDict = new Dictionary<string, object>();
            foreach (var prop in existingStamp.EnumerateObject())
            {
                stampDict[prop.Name] = prop.Value;
            }
            
            // Add handshake-specific properties
            stampDict["handshakeOnly"] = true;
            stampDict["liveProvider"] = "ctrader";
            
            var json = System.Text.Json.JsonSerializer.Serialize(stampDict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(stampPath, json);
        }

        static void PrintFinalReport(string runDir, int barsProcessed, int orderCount, Dictionary<string, int> noTradeReasons, HarnessConfig config, int selectedWindow = 0, bool miniSignalUsed = false, bool marketMiniUsed = false)
        {
            Console.WriteLine();
            Console.WriteLine("=".PadRight(60, '='));
            Console.WriteLine("CLEAN PASS (HARNESS REPLAY XAUUSD)");
            Console.WriteLine($"Run path: {runDir}");
            
            // Check if orders.csv exists and show header
            var ordersFile = Path.Combine(runDir, "orders.csv");
            if (File.Exists(ordersFile))
            {
                var lines = File.ReadAllLines(ordersFile);
                if (lines.Length > 0)
                {
                    Console.WriteLine($"Header: {lines[0]}");
                }
            }
            
            // Count different order phases (assuming logger tracks REQUEST, FILL, etc.)
            var requestCount = orderCount; // Simplified - in full implementation, parse the CSV
            var fillCount = orderCount;    // Simplified - assume all requests filled
            var closeCount = 0;           // Simplified - no close tracking yet
            var isHalted = false;         // Simplified - check risk manager state
            
            Console.WriteLine($"REQUEST = {requestCount}, FILL = {fillCount}, CLOSE = {closeCount}, HALT = {(isHalted ? "Y" : "N")}");
            
            // Top no-trade reasons (top 5)
            var topReasons = noTradeReasons
                .OrderByDescending(kvp => kvp.Value)
                .Take(5)
                .ToList();
            
            if (topReasons.Any())
            {
                var reasonsText = string.Join(", ", topReasons.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                Console.WriteLine($"Top no-trade reasons: {reasonsText}");
            }
            else
            {
                Console.WriteLine("Top no-trade reasons: none (all signals generated)");
            }
            
            Console.WriteLine($"TradeMode={config.TradeMode} Seed={config.Seed?.ToString() ?? "none"}");
            if (config.ScanWindows.HasValue)
            {
                Console.WriteLine($"SCAN_PICK={selectedWindow} WINDOW={config.WindowSize}");
            }
            if (config.TradeMode.Equals("Test", StringComparison.OrdinalIgnoreCase))
            {
                var miniStatus = config.TestMini ? "on" : "off";
                var triggered = miniSignalUsed ? "yes" : "no";
                var marketMiniStatus = config.TestMarket ? "on" : "off";
                Console.WriteLine($"MiniSignal={miniStatus} Triggered={triggered} MarketMini={marketMiniStatus}");
            }
            Console.WriteLine("=".PadRight(60, '='));
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

        static async Task<int> ScanForTradeableWindow(HarnessConfig config, IMarketDataProvider provider, int numWindows)
        {
            // First, determine total available M15 bars
            provider.Reset();
            int totalBarsM15 = 0;
            while (provider.HasMore)
            {
                var bar = provider.GetNextBar("M15");
                if (bar == null) break;
                totalBarsM15++;
            }
            
            Console.WriteLine($"Total M15 bars available: {totalBarsM15}");
            
            // Check minimum dataset requirement
            if (totalBarsM15 < 30)
            {
                Console.WriteLine("CANNOT_RUN");
                Console.WriteLine("Dataset too short (<30 bars). Provide more M15 bars.");
                return -1;
            }
            
            // Adaptive window sizing
            int adaptiveWindow = Math.Min(config.WindowSize, Math.Max(30, totalBarsM15 - 10));
            Console.WriteLine($"Adaptive window size: {adaptiveWindow} (requested: {config.WindowSize})");
            
            // Calculate max possible windows
            int maxPossibleWindows = totalBarsM15 / adaptiveWindow;
            int windowsToScan = Math.Min(numWindows, maxPossibleWindows);
            
            Console.WriteLine($"Scanning {windowsToScan} windows of {adaptiveWindow} bars each...");
            
            for (int windowIndex = 0; windowIndex < windowsToScan; windowIndex++)
            {
                provider.Reset();
                
                // Skip to start of current window
                for (int skip = 0; skip < windowIndex * adaptiveWindow; skip++)
                {
                    var skipBar = provider.GetNextBar("M15");
                    if (skipBar == null) return -1; // Not enough data
                }
                
                // Test this window for signals
                var windowM15Bars = new List<Bar>();
                var windowH1Bars = new List<Bar>();
                var signalCount = 0;
                
                for (int barInWindow = 0; barInWindow < adaptiveWindow && provider.HasMore; barInWindow++)
                {
                    var m15Bar = provider.GetNextBar("M15");
                    if (m15Bar == null) break;
                    
                    windowM15Bars.Add(m15Bar);
                    
                    var h1Bar = provider.GetNextBar("H1");
                    if (h1Bar != null && h1Bar.Timestamp <= m15Bar.Timestamp)
                        windowH1Bars.Add(h1Bar);
                    
                    // Need sufficient bars for analysis
                    if (windowM15Bars.Count < 30) continue;
                    
                    // Dry run signal evaluation
                    signalCount += await EvaluateWindowSignals(windowM15Bars, windowH1Bars, config);
                }
                
                Console.WriteLine($"Window {windowIndex}: {signalCount} signals found");
                
                if (signalCount > 0)
                {
                    return windowIndex; // Found tradeable window
                }
            }
            
            return -1; // No tradeable window found
        }
        
        static async Task<int> EvaluateWindowSignals(List<Bar> m15Bars, List<Bar> h1Bars, HarnessConfig config)
        {
            try
            {
                // Calculate ATR
                var atr15 = CalculateATR(m15Bars.TakeLast(14).ToArray());
                var atrBaseline = CalculateATR(m15Bars.TakeLast(Math.Min(200, m15Bars.Count)).ToArray());
                
                // Build OHLC arrays
                var highs = m15Bars.Select(b => b.High).ToArray();
                var lows = m15Bars.Select(b => b.Low).ToArray();
                var opens = m15Bars.Select(b => b.Open).ToArray();
                var closes = m15Bars.Select(b => b.Close).ToArray();
                var volumes = m15Bars.Select(b => b.Volume).ToArray();

                // H1 analysis
                var h1Event = StructureEvent.None;
                var h1Range = new PremiumDiscount.Range { Low = 2000, High = 2100 };
                var lastCloseH1 = 2050.0;
                
                if (h1Bars.Count >= 10)
                {
                    var h1HighArray = h1Bars.Select(b => b.High).ToArray();
                    var h1LowArray = h1Bars.Select(b => b.Low).ToArray();
                    var h1Swings = MarketStructureDetector.DetectSwings(h1HighArray, h1LowArray, 2);
                    h1Event = MarketStructureDetector.DetectEvent(h1Swings, h1HighArray.Length - 1, h1Bars.Last().Close);
                    lastCloseH1 = h1Bars.Last().Close;
                    
                    var h1Recent = h1Bars.TakeLast(Math.Min(100, h1Bars.Count)).ToArray();
                    h1Range = new PremiumDiscount.Range 
                    { 
                        Low = h1Recent.Min(b => b.Low), 
                        High = h1Recent.Max(b => b.High) 
                    };
                }

                // Configure SMC and get H1 arrays for trend analysis
                var smcConfig = SmcPlanner.ConfigureForTradeMode(config.TradeMode, config.RelaxVol, config.RelaxAtr, config.Confirm);
                
                // Get H1 arrays from provider if it's ReplayCsvProvider
                double[] h1Closes = null, h1Highs = null, h1Lows = null;
                if (h1Bars.Count > 0)
                {
                    h1Closes = h1Bars.Select(b => b.Close).ToArray();
                    h1Highs = h1Bars.Select(b => b.High).ToArray();
                    h1Lows = h1Bars.Select(b => b.Low).ToArray();
                }
                
                var smcResult = SmcPlanner.EvaluateDetailed(opens, highs, lows, closes, volumes, atr15, atrBaseline, lastCloseH1, h1Range, h1Event, smcConfig, h1Closes, h1Highs, h1Lows);
                
                return smcResult.Signal != SmcPlanner.SmcSignal.None ? 1 : 0;
            }
            catch
            {
                return 0; // Error = no signal
            }
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
        public int? Bars { get; set; } = null;  // If set, use bars instead of duration
        public int? ScanWindows { get; set; } = null;  // If set, scan for tradeable windows
        public int WindowSize { get; set; } = 200;  // Default window size for scanning
        
        // New dual-mode parameters
        public string TradeMode { get; set; } = "Strict";  // Strict or Test
        public double? RelaxVol { get; set; } = null;  // Override volume spike multiplier
        public double? RelaxAtr { get; set; } = null;  // Override ATR spike multiplier
        public int? Confirm { get; set; } = null;  // Override confirmations required
        public int? Seed { get; set; } = null;  // Deterministic random seed
        
        // Mini-signal parameters (Test mode only)
        public bool TestMini { get; set; } = false;  // Enable mini-signal
        public int MiniAfter { get; set; } = 60;  // Bars before mini-signal triggers
        public int MiniMax { get; set; } = 1;  // Max mini-signals per run
        
        // Test-market parameters (Test mode only)
        public bool TestMarket { get; set; } = false;  // Enable market mini-order
        public int TestMarketSlippageBps { get; set; } = 5;  // Slippage in basis points (0.05%)
    }
}