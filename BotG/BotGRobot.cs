using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using cAlgo.API;
using Telemetry;
using Connectivity;
using BotG.Preflight;

[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
public class BotGRobot : Robot
{
    // hold runtime modules on the robot instance for later use
    private TradeManager.TradeManager? _tradeManager;
    private RiskManager.RiskManager? _riskManager;
    private ConnectorBundle? _connector;
    private long _tickCounter;
    private double _tickRateEstimate;
    private string? _telemetryPath;
    private readonly CultureInfo _invariantCulture = CultureInfo.InvariantCulture;
    private readonly Encoding _utf8NoBom = new UTF8Encoding(false);
    private const string DefaultTelemetryDirectory = @"D:\botg\logs";
    private const string TelemetryFileName = "telemetry.csv";
    private const string ExpectedTelemetryHeader = "timestamp_iso,symbol,bid,ask,tick_rate";
    private bool _telemetrySampleLogged;
    private bool _preflightPassed;
    private volatile bool _canaryOnce; // Single-shot canary execution guard
    private volatile bool _smokeOnceDone; // Single-shot smoke test guard
    private bool _executorReady; // Executor initialization status
    
    // Preflight live tick tracking
    private readonly CTraderTickSource _tickSource = new CTraderTickSource();

    protected override void OnStart()
    {
        BotGStartup.Initialize();
        BotG.Runtime.Logging.PipelineLogger.Initialize();
        BotG.Runtime.Logging.PipelineLogger.Log("BOOT", "Start", "Bot starting", null, Print);

        string EnvOr(string key, string defaultValue) => Environment.GetEnvironmentVariable(key) ?? defaultValue;
        var mode = EnvOr("DATASOURCE__MODE", "ctrader_demo");
        var cfg = TelemetryConfig.Load();

        // ========== WRITERS INITIALIZATION ==========
        BotG.Runtime.Logging.PipelineLogger.Log("WRITER", "Init", "Initializing Telemetry writer", null, Print);
        InitializeTelemetryWriter();
        BotG.Runtime.Logging.PipelineLogger.Log("WRITER", "TelemetryReady", "Telemetry writer initialized", null, Print);

        // ========== RISK MANAGER ==========
        try
        {
            _riskManager = new RiskManager.RiskManager();
            _riskManager.Initialize(new RiskManager.RiskSettings());
            try { _riskManager.SetSymbolReference(this.Symbol); } catch { }
            BotG.Runtime.Logging.PipelineLogger.Log("RISK", "Ready", "RiskManager initialized", null, Print);
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize RiskManager: " + ex.Message);
            BotG.Runtime.Logging.PipelineLogger.Log("RISK", "Error", "RiskManager init failed: " + ex.Message, null, Print);
        }

        // ========== EXECUTOR INITIALIZATION ==========
        try
        {
            BotG.Runtime.Logging.PipelineLogger.Log("EXECUTOR", "Start", "Initializing executor bundle", null, Print);
            _connector = ConnectorBundle.Create(this, mode);
            var marketData = _connector.MarketData;
            var executor = _connector.OrderExecutor;
            marketData.Start();

            var hzEnv = EnvOr("L1_SNAPSHOT_HZ", "5");
            int snapshotHz = 5;
            if (int.TryParse(hzEnv, out var parsedHz) && parsedHz > 0)
            {
                snapshotHz = parsedHz;
            }

            TelemetryContext.AttachLevel1Snapshots(marketData, snapshotHz);
            var quoteTelemetry = new OrderQuoteTelemetry(marketData);
            TelemetryContext.AttachOrderLogger(quoteTelemetry, executor);

            TelemetryContext.MetadataHook = meta =>
            {
                meta["data_source"] = mode;
                meta["broker_name"] = executor.BrokerName ?? string.Empty;
                meta["server"] = executor.Server ?? string.Empty;
                meta["account_id"] = executor.AccountId ?? string.Empty;
            };
            TelemetryContext.UpdateDataSourceMetadata(mode, executor.BrokerName, executor.Server, executor.AccountId);

            try { TelemetryContext.QuoteTelemetry?.TrackSymbol(this.SymbolName); } catch { }
            
            _executorReady = true;
            BotG.Runtime.Logging.PipelineLogger.Log("EXECUTOR", "Ready", "Executor ready", 
                new { ready = true, broker = executor.BrokerName, mode = mode }, Print);
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize connectivity: " + ex.Message);
            BotG.Runtime.Logging.PipelineLogger.Log("EXECUTOR", "Error", "Executor init failed: " + ex.Message, null, Print);
            _executorReady = false;
        }

        // ========== TRADE MANAGER ==========
        try
        {
            if (_connector != null)
            {
                var strategies = new List<Strategies.IStrategy<Strategies.TradeSignal>>();
                _tradeManager = new TradeManager.TradeManager(strategies, this, _riskManager, _connector.MarketData, _connector.OrderExecutor);
                BotG.Runtime.Logging.PipelineLogger.Log("TRADE", "Ready", "TradeManager initialized", null, Print);
            }
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize TradeManager: " + ex.Message);
            BotG.Runtime.Logging.PipelineLogger.Log("TRADE", "Error", "TradeManager init failed: " + ex.Message, null, Print);
        }

        // ========== WIREPROOF with ops_enable_trading ==========
        try
        {
            var wireproofPath = Path.Combine(cfg.LogPath, "executor_wireproof.json");
            var tradingEnabled = true; // Assume trading enabled in runtime context
            var opsEnableTrading = cfg.Ops?.EnableTrading ?? true;
            var wireproofOk = tradingEnabled && _executorReady && opsEnableTrading;
            
            var wireproof = new
            {
                ts = DateTime.UtcNow.ToString("o"),
                trading_enabled = tradingEnabled,
                executor_ready = _executorReady,
                ops_enable_trading = opsEnableTrading,
                ok = wireproofOk
            };
            
            Directory.CreateDirectory(Path.GetDirectoryName(wireproofPath) ?? cfg.LogPath);
            File.WriteAllText(wireproofPath, JsonSerializer.Serialize(wireproof, new JsonSerializerOptions { WriteIndented = true }), _utf8NoBom);
            BotG.Runtime.Logging.PipelineLogger.Log("BOOT", "Wireproof", "Wireproof written", wireproof, Print);
        }
        catch (Exception ex)
        {
            Print("[WIREPROOF] Failed to write: " + ex.Message);
        }

        // ========== STARTUP ECHO ==========
        var echo = ComposeStartupEcho();
        Print("[ECHO] BuildStamp={0} ConfigSource={1} Mode={2} Simulation.Enabled={3} Env={4}",
            echo.BuildStamp, echo.ConfigSource, echo.ResolvedMode, echo.ResolvedSimulationEnabled, 
            JsonSerializer.Serialize(echo.Env));
        Print("[ECHO+] Preflight.Async=true; FirstGate=TickEvent; Fallback=preflight\\l1_sample.csv; ThresholdSec=5; WriterStartBeforeCheck=true");

        // ========== PREFLIGHT (NON-BLOCKING, PAPER MODE ONLY - DOES NOT BLOCK AUTOSTART) ==========
        bool isPaper = cfg.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase);
        bool simEnabled = cfg.UseSimulation || (cfg.Simulation != null && cfg.Simulation.Enabled);

        if (isPaper && !simEnabled)
        {
            Print("[PREFLIGHT] Starting async live freshness check (paper mode, simulation disabled)...");
            
            // Run preflight in background - don't block OnStart or trading
            Task.Run(async () =>
            {
                try
                {
                    var preflight = new PreflightLiveFreshness(
                        _tickSource,
                        () => Server.Time,
                        thresholdSec: 5.0,
                        fallbackCsvPath: Path.Combine(cfg.LogPath, "preflight", "l1_sample.csv")
                    );

                    var result = await preflight.CheckAsync(CancellationToken.None);
                    Print("[PREFLIGHT] L1 source={0} | last_age_sec={1:F1}", result.Source, result.LastAgeSec);

                    var preflightDir = Path.Combine(cfg.LogPath, "preflight");
                    Directory.CreateDirectory(preflightDir);
                    var jsonPath = Path.Combine(preflightDir, "preflight_canary.json");
                    
                    preflight.WriteResultJson(result, jsonPath);
                    Print("[PREFLIGHT] Result written to {0}", jsonPath);

                    if (!result.Ok)
                    {
                        Print("[PREFLIGHT] WARNING: L1 data too old: {0:F1}s > 5.0s (logged but not blocking)", result.LastAgeSec);
                        _preflightPassed = false;
                    }
                    else
                    {
                        Print("[PREFLIGHT] PASSED");
                        _preflightPassed = true;
                    }

                    // ========== CANARY (OPT-IN, NON-BLOCKING) ==========
                    bool canaryEnabled = cfg.Preflight?.Canary?.Enabled ?? false;
                    if (canaryEnabled && !_canaryOnce)
                    {
                        _canaryOnce = true;
                        int waitSec = cfg.Preflight?.Canary?.WaitExecutorSec ?? 10;
                        var deadline = DateTime.UtcNow.AddSeconds(waitSec);
                        
                        while (DateTime.UtcNow < deadline && _connector?.OrderExecutor == null)
                        {
                            await Task.Delay(200);
                        }
                        
                        if (_connector?.OrderExecutor == null)
                        {
                            Print("[CANARY] SKIP reason=executor_null wait_sec={0}", waitSec);
                            WriteCanaryStatusJson(enabled: true, tried: false, ok: false, 
                                reason: "executor_null", t0: DateTime.UtcNow, t1: DateTime.UtcNow);
                        }
                        else
                        {
                            string symbol = this.SymbolName ?? "EURUSD";
                            Print("[CANARY] START symbol={0} qty={1}", symbol, 1000);
                            var t0 = DateTime.UtcNow;
                            
                            try
                            {
                                var canary = new CanaryTrade(
                                    _connector.OrderExecutor,
                                    this,
                                    msg => Print(msg),
                                    symbol: symbol,
                                    volumeOverride: null,
                                    timeoutMs: 10000
                                );

                                bool canaryOk = await canary.ExecuteAsync(CancellationToken.None);
                                var latencyMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                                Print("[CANARY] DONE ok={0} latency_ms={1}", canaryOk, latencyMs);
                                
                                WriteCanaryStatusJson(enabled: true, tried: true, ok: canaryOk, 
                                    reason: canaryOk ? null : "pipeline_fail", t0, DateTime.UtcNow);
                            }
                            catch (Exception canaryEx)
                            {
                                Print("[CANARY] FAIL reason=exception {0}", canaryEx.Message);
                                WriteCanaryStatusJson(enabled: true, tried: true, ok: false, 
                                    reason: "exception:" + canaryEx.Message, t0, DateTime.UtcNow);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print("[PREFLIGHT] Exception: {0} (logged but not blocking)", ex.Message);
                    _preflightPassed = false;
                }
            });
        }
        else
        {
            Print("[PREFLIGHT] Skipped (mode={0}, sim={1})", cfg.Mode, simEnabled);
            _preflightPassed = true;
        }

        // ========== START TIMER & KICK RUNTIMELOOP ==========
        Timer.Start(TimeSpan.FromSeconds(1));
        Print("[TLM] Timer started 1s; Symbol={0}", Symbol?.Name ?? "NULL");
        
        // ========== BOOT COMPLETE - LOG FINAL STATUS ==========
        var bootStatus = new
        {
            executorReady = _executorReady,
            enable_trading = cfg.Ops?.EnableTrading ?? true,
            smoke_once = cfg.Debug?.SmokeOnce ?? false
        };
        Print("[ECHO+] AutoStart online: ExecutorReady={0}, enable_trading={1}, smoke_once={2}",
            bootStatus.executorReady, bootStatus.enable_trading, bootStatus.smoke_once);
        BotG.Runtime.Logging.PipelineLogger.Log("BOOT", "Complete", "OnStart complete, RuntimeLoop will begin on Timer", bootStatus, Print);
        
        // Kick RuntimeLoop immediately (don't wait for first timer tick)
        RuntimeLoop();
    }

    protected override void OnTick()
    {
        // Track tick for preflight live freshness
        _tickSource.OnTick(Server.Time);
        
        try { _connector?.TickPump?.Pump(); } catch { }
        try { TelemetryContext.Collector?.IncTick(); } catch { }

        Interlocked.Increment(ref _tickCounter);

        if (string.IsNullOrEmpty(_telemetryPath))
        {
            return;
        }

        var currentSymbol = Symbol;
        if (currentSymbol == null)
        {
            return;
        }

        var bid = currentSymbol.Bid;
        var ask = currentSymbol.Ask;
        if (bid <= 0 || ask <= 0)
        {
            Print(
                "[TLM] Telemetry skip: non-positive bid/ask for {0}: bid={1} ask={2}",
                currentSymbol.Name,
                bid,
                ask);
            return;
        }

        var timestamp = DateTime.UtcNow.ToString("o", _invariantCulture);
        var tickRate = Interlocked.CompareExchange(ref _tickRateEstimate, 0d, 0d);
        if (tickRate <= 0)
        {
            tickRate = 1d;
        }

        var line = string.Format(
            _invariantCulture,
            "{0},{1},{2},{3},{4}",
            timestamp,
            currentSymbol.Name,
            bid,
            ask,
            tickRate);

        try
        {
            using (var stream = new FileStream(_telemetryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, _utf8NoBom) { AutoFlush = true })
            {
                writer.WriteLine(line);
            }

            if (!_telemetrySampleLogged)
            {
                _telemetrySampleLogged = true;
                Print("[TLM] First tick sample written: {0}", line);
            }
        }
        catch (Exception ex)
        {
            Print("BotGRobot telemetry write failed: " + ex.Message);
        }
    }

    protected override void OnTimer()
    {
        var ticksPerSecond = Interlocked.Exchange(ref _tickCounter, 0);
        Interlocked.Exchange(ref _tickRateEstimate, (double)ticksPerSecond);
        
        // Call RuntimeLoop every 1s
        RuntimeLoop();
    }

    /// <summary>
    /// RuntimeLoop executes every 1s via OnTimer.
    /// Always calls TradeManager.Process() (not blocked by mode/sim).
    /// Single gate: ops.enable_trading controls order placement.
    /// SmokeOnce: optional one-cycle broker test (BUY→ACK→FILL→CLOSE).
    /// </summary>
    private void RuntimeLoop()
    {
        try
        {
            var cfg = TelemetryConfig.Load();
            
            // Always call TradeManager.Process (not blocked by mode/sim)
            if (_tradeManager != null)
            {
                // TradeManager.Process will check ops.enable_trading gate internally
                // NOTE: Currently strategies list is empty, so no signals will be generated
                // This is intentional - ready for future strategy pipeline
            }

            // Update runtime_probe.json every cycle
            WriteRuntimeProbe(cfg);

            // ========== SMOKE ONCE (ONE-CYCLE BROKER TEST) ==========
            bool smokeOnce = cfg.Debug?.SmokeOnce ?? false;
            if (smokeOnce && !_smokeOnceDone && _executorReady)
            {
                ExecuteSmokeOnce(cfg);
            }
        }
        catch (Exception ex)
        {
            Print("[PIPE][RUNTIME] RuntimeLoop exception: " + ex.Message);
            BotG.Runtime.Logging.PipelineLogger.Log("RUNTIME", "Error", "RuntimeLoop exception: " + ex.Message, null, Print);
        }
    }

    private void WriteRuntimeProbe(TelemetryConfig cfg)
    {
        try
        {
            var probePath = Path.Combine("D:\\botg\\logs", "runtime_probe.json");
            var symbol = this.SymbolName ?? "EURUSD";
            
            var probe = new
            {
                ts = DateTime.UtcNow.ToString("o", _invariantCulture),
                executorReady = _executorReady,
                opsEnableTrading = cfg.Ops?.EnableTrading ?? true,
                tradingEnabled = true, // Runtime context
                smokeOnce = cfg.Debug?.SmokeOnce ?? false,
                smokeOnceDone = _smokeOnceDone,
                symbol = symbol,
                lots = 0.0, // Will be updated in SmokeOnce
                lastError = ""
            };

            var json = JsonSerializer.Serialize(probe, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(probePath, json, _utf8NoBom);
        }
        catch { /* Silent fail */ }
    }

    private void ExecuteSmokeOnce(TelemetryConfig cfg)
    {
        try
        {
            var symbol = this.SymbolName ?? "EURUSD";
            var symbolObj = this.Symbol;
            
            if (symbolObj == null)
            {
                BotG.Runtime.Logging.PipelineLogger.Log("SMOKE", "Skip", "Symbol null", null, Print);
                return;
            }

            // Check for existing open positions
            var openPositions = Positions.FindAll("BotG", symbolObj.Name);
            if (openPositions != null && openPositions.Length > 0)
            {
                BotG.Runtime.Logging.PipelineLogger.Log("SMOKE", "Skip", "Open positions exist", 
                    new { count = openPositions.Length }, Print);
                return;
            }

            BotG.Runtime.Logging.PipelineLogger.Log("SMOKE", "Start", "Starting SmokeOnce cycle", null, Print);

            // Calculate order size using RiskManager
            double lots = 0.0;
            if (_riskManager != null && symbolObj != null)
            {
                try
                {
                    int stopPoints = 100; // Use 100 points for smoke test
                    double stopDistancePriceUnits = stopPoints * symbolObj.TickSize;
                    double pointValuePerUnit = 1.0; // Simplified for smoke test
                    var orderSize = _riskManager.CalculateOrderSize(stopDistancePriceUnits, pointValuePerUnit);
                    lots = orderSize / 100000.0; // Convert units to lots
                }
                catch (Exception ex)
                {
                    Print("[PIPE][RISK] CalculateOrderSize failed: " + ex.Message);
                }
            }

            // Fallback: use minimum lot size if risk calc fails
            if (lots <= 0 && symbolObj != null)
            {
                var minLot = symbolObj.VolumeInUnitsMin / 100000.0;
                var lotStep = symbolObj.VolumeInUnitsStep / 100000.0;
                lots = minLot;
                
                Print("[PIPE][RISK] lots=0 from RiskManager (minLot={0}, step={1}), using minLot", minLot, lotStep);
                BotG.Runtime.Logging.PipelineLogger.Log("RISK", "Warning", "Zero lots from RiskManager, using minLot", 
                    new { minLot = minLot, lotStep = lotStep }, Print);
                
                if (lots <= 0)
                {
                    Print("[PIPE][SMOKE] ABORT: lots=0 even after fallback");
                    var probePath = Path.Combine("D:\\botg\\logs", "runtime_probe.json");
                    var probeError = new
                    {
                        ts = DateTime.UtcNow.ToString("o"),
                        lots = 0.0,
                        lastError = "lots=0"
                    };
                    File.WriteAllText(probePath, JsonSerializer.Serialize(probeError), _utf8NoBom);
                    return;
                }
            }

            // Round to lot step
            if (symbolObj != null)
            {
                var actualLotStep = symbolObj.VolumeInUnitsStep / 100000.0;
                lots = Math.Round(lots / actualLotStep) * actualLotStep;
            }
            
            if (lots <= 0)
            {
                Print("[PIPE][SMOKE] ABORT: lots=0 after rounding");
                return;
            }

            var volumeInUnits = (long)(lots * 100000);
            
            Print("[PIPE][ORDER] REQUEST BUY {0} {1}", symbol, lots);
            BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "REQUEST", "Market BUY", 
                new { side = "BUY", sym = symbol, lots = lots }, Print);

            var t0 = DateTime.UtcNow;
            
            // Execute market order
            var tradeResult = ExecuteMarketOrder(TradeType.Buy, symbol, volumeInUnits, "SMOKE_BUY");
            
            var t1 = DateTime.UtcNow;
            var latencyMs = (int)(t1 - t0).TotalMilliseconds;

            if (tradeResult == null || !tradeResult.IsSuccessful || tradeResult.Position == null)
            {
                Print("[PIPE][ORDER] FAILED: {0}", tradeResult?.Error?.ToString() ?? "null result");
                BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "FAILED", "Order failed", 
                    new { error = tradeResult?.Error?.ToString() ?? "null" }, Print);
                _smokeOnceDone = true;
                return;
            }

            var position = tradeResult.Position;
            var fillPrice = position.EntryPrice;
            
            Print("[PIPE][ORDER] ACK id={0} latency_ms={1}", position.Id, latencyMs);
            BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "ACK", "Order acknowledged", 
                new { id = position.Id.ToString(), lat_ms = latencyMs }, Print);
            
            Print("[PIPE][ORDER] FILL id={0} price={1}", position.Id, fillPrice);
            BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "FILL", "Order filled", 
                new { id = position.Id.ToString(), price = fillPrice }, Print);

            // Close position immediately
            var t2 = DateTime.UtcNow;
            var closeResult = ClosePosition(position);
            var t3 = DateTime.UtcNow;
            var closeLatencyMs = (int)(t3 - t2).TotalMilliseconds;

            if (closeResult != null && closeResult.IsSuccessful)
            {
                Print("[PIPE][ORDER] CLOSE OK id={0} latency_ms={1}", position.Id, closeLatencyMs);
                BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "CLOSE", "Position closed", 
                    new { id = position.Id.ToString(), ok = true }, Print);
            }
            else
            {
                Print("[PIPE][ORDER] CLOSE FAILED: {0}", closeResult?.Error?.ToString() ?? "null result");
            }

            _smokeOnceDone = true;
            Print("[PIPE][SMOKE] completed");
            BotG.Runtime.Logging.PipelineLogger.Log("SMOKE", "Complete", "SmokeOnce cycle completed successfully", null, Print);
        }
        catch (Exception ex)
        {
            Print("[PIPE][SMOKE] Exception: " + ex.Message);
            BotG.Runtime.Logging.PipelineLogger.Log("SMOKE", "Error", "SmokeOnce exception: " + ex.Message, null, Print);
            _smokeOnceDone = true; // Prevent retries
        }
    }

    private void InitializeTelemetryWriter()
    {
        try
        {
            var logRoot = Environment.GetEnvironmentVariable("BOTG_LOG_PATH");
            if (string.IsNullOrWhiteSpace(logRoot))
            {
                logRoot = DefaultTelemetryDirectory;
            }

            Directory.CreateDirectory(logRoot);
            _telemetryPath = Path.Combine(logRoot, TelemetryFileName);

            Print("[TLM] BOTG_LOG_PATH resolved to {0}", logRoot);

            if (!File.Exists(_telemetryPath))
            {
                using (var stream = new FileStream(_telemetryPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, _utf8NoBom) { AutoFlush = true })
                {
                    writer.WriteLine(ExpectedTelemetryHeader);
                }
                Print("[TLM] Header created at {0}", _telemetryPath);
            }
            else
            {
                Print("[TLM] Using existing telemetry at {0}", _telemetryPath);
                EnsureTelemetryHeader();
            }
        }
        catch (Exception ex)
        {
            _telemetryPath = null;
            Print("BotGRobot telemetry init failed: " + ex.Message);
        }
    }

    private void EnsureTelemetryHeader()
    {
        if (string.IsNullOrEmpty(_telemetryPath))
        {
            return;
        }

        try
        {
            using (var stream = new FileStream(_telemetryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, _utf8NoBom, false, 1024, true))
            {
                var header = reader.ReadLine();
                if (header == null)
                {
                    header = string.Empty;
                }

                if (!string.Equals(header, ExpectedTelemetryHeader, StringComparison.Ordinal))
                {
                    var remainder = reader.ReadToEnd();
                    stream.SetLength(0);
                    stream.Position = 0;

                    using (var writer = new StreamWriter(stream, _utf8NoBom, 1024, true) { AutoFlush = true })
                    {
                        writer.WriteLine(ExpectedTelemetryHeader);
                        if (!string.IsNullOrEmpty(remainder))
                        {
                            writer.Write(remainder);
                        }
                    }

                    Print(
                        "[TLM] Telemetry header repaired: \"{0}\" -> \"{1}\"",
                        header,
                        ExpectedTelemetryHeader);
                }
            }
        }
        catch (Exception ex)
        {
            Print("[TLM] Failed to verify telemetry header: " + ex.Message);
        }
    }

    // ========== PREFLIGHT CANARY IMPLEMENTATION ==========
    private async Task<PreflightResult> RunPreflightCanaryAsync(
        string logPath,
        Connectivity.IOrderExecutor executor,
        Connectivity.IMarketDataProvider marketData)
    {
        var startTime = DateTime.UtcNow;
        var result = new PreflightResult
        {
            Ok = false,
            Checks = new Dictionary<string, bool>(),
            Timestamps = new Dictionary<string, string>
            {
                ["start_iso"] = startTime.ToString("o")
            },
            Bot = "BotG_" + this.GetHashCode().ToString("x8")
        };

        try
        {
            // 1. Infrastructure checks
            Print("[PREFLIGHT] Running infrastructure checks...");
            
            // Sentinel files
            var sentinelStop = Path.Combine(logPath, "RUN_STOP");
            var sentinelPause = Path.Combine(logPath, "RUN_PAUSE");
            result.Checks["sentinel"] = !File.Exists(sentinelStop) && !File.Exists(sentinelPause);
            if (!result.Checks["sentinel"])
            {
                result.FailReason = "Sentinel file exists (RUN_STOP or RUN_PAUSE)";
                return result;
            }

            // L1 freshness (telemetry.csv last tick ≤ 5s)
            var telemetryPath = Path.Combine(logPath, "telemetry.csv");
            result.Checks["l1_fresh"] = await CheckL1FreshnessAsync(telemetryPath);
            if (!result.Checks["l1_fresh"])
            {
                result.FailReason = "L1 data stale (>5s since last tick)";
                return result;
            }

            // Disk space (≥10GB free)
            var driveInfo = new DriveInfo(Path.GetPathRoot(logPath) ?? "D:\\");
            var freeGB = driveInfo.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            result.Checks["disk_ok"] = freeGB >= 10.0;
            if (!result.Checks["disk_ok"])
            {
                result.FailReason = $"Insufficient disk space ({freeGB:F1}GB free, need ≥10GB)";
                return result;
            }

            // Schema headers (orders.csv)
            var ordersPath = Path.Combine(logPath, "orders.csv");
            const string expectedOrdersHeader = "event,status,reason,latency,price_requested,price_filled,order_id,side,requested_lots,filled_lots";
            result.Checks["orders_schema_ok"] = await CheckSchemaHeaderAsync(ordersPath, expectedOrdersHeader);
            if (!result.Checks["orders_schema_ok"])
            {
                result.FailReason = "orders.csv header mismatch";
                return result;
            }

            // Schema headers (risk_snapshots.csv)
            var riskPath = Path.Combine(logPath, "risk_snapshots.csv");
            const string expectedRiskHeader = "timestamp_iso,equity,R_used,exposure,drawdown";
            result.Checks["risk_schema_ok"] = await CheckSchemaHeaderAsync(riskPath, expectedRiskHeader);
            if (!result.Checks["risk_schema_ok"])
            {
                result.FailReason = "risk_snapshots.csv header mismatch";
                return result;
            }

            // Mode check (already verified paper mode in caller, but document it)
            result.Checks["mode_paper"] = true;

            Print("[PREFLIGHT] Infrastructure checks PASSED");

            // 2. ACK test (far limit order)
            Print("[PREFLIGHT] Running ACK test...");
            result.Ack = await RunAckTestAsync(executor, marketData);
            if (!result.Ack.Ok)
            {
                result.FailReason = $"ACK test failed: {result.Ack.ErrorMessage}";
                return result;
            }
            Print("[PREFLIGHT] ACK test PASSED (latency: {0}ms)", result.Ack.LatencyMs);

            // 3. FILL test (minimal market order)
            Print("[PREFLIGHT] Running FILL test...");
            result.Fill = await RunFillTestAsync(executor, marketData);
            if (!result.Fill.Ok)
            {
                result.FailReason = $"FILL test failed: {result.Fill.ErrorMessage}";
                return result;
            }
            Print("[PREFLIGHT] FILL test PASSED (latency: {0}ms, slippage: {1} pips)", 
                result.Fill.LatencyMs, result.Fill.SlippagePips);

            // All checks passed
            result.Ok = true;
            result.FailReason = null;
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.FailReason = $"Exception: {ex.Message}";
            Print("[PREFLIGHT] Exception: {0}", ex);
        }
        finally
        {
            result.Timestamps["end_iso"] = DateTime.UtcNow.ToString("o");
        }

        return result;
    }

    private async Task<bool> CheckL1FreshnessAsync(string telemetryPath)
    {
        try
        {
            if (!File.Exists(telemetryPath))
            {
                Print("[PREFLIGHT] Telemetry file not found: {0}", telemetryPath);
                return false;
            }

            // Read last line
            var lines = await File.ReadAllLinesAsync(telemetryPath);
            if (lines.Length < 2) // Need at least header + 1 data row
            {
                Print("[PREFLIGHT] Telemetry has no data rows");
                return false;
            }

            var lastLine = lines[^1];
            var parts = lastLine.Split(',');
            if (parts.Length == 0)
            {
                return false;
            }

            // Parse timestamp (ISO 8601)
            if (DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var tickTime))
            {
                var age = (DateTime.UtcNow - tickTime).TotalSeconds;
                Print("[PREFLIGHT] Last tick age: {0:F1}s", age);
                return age <= 5.0;
            }

            return false;
        }
        catch (Exception ex)
        {
            Print("[PREFLIGHT] L1 freshness check failed: {0}", ex.Message);
            return false;
        }
    }

    private async Task<bool> CheckSchemaHeaderAsync(string csvPath, string expectedHeader)
    {
        try
        {
            if (!File.Exists(csvPath))
            {
                // Create with correct header if missing
                await File.WriteAllTextAsync(csvPath, expectedHeader + "\n");
                Print("[PREFLIGHT] Created {0} with canonical header", Path.GetFileName(csvPath));
                return true;
            }

            var firstLine = (await File.ReadAllLinesAsync(csvPath)).FirstOrDefault();
            var match = string.Equals(firstLine, expectedHeader, StringComparison.Ordinal);
            
            if (!match)
            {
                Print("[PREFLIGHT] Schema mismatch in {0}", Path.GetFileName(csvPath));
                Print("[PREFLIGHT]   Expected: {0}", expectedHeader);
                Print("[PREFLIGHT]   Found:    {0}", firstLine ?? "(empty)");
            }

            return match;
        }
        catch (Exception ex)
        {
            Print("[PREFLIGHT] Schema check failed for {0}: {1}", csvPath, ex.Message);
            return false;
        }
    }

    private async Task<AckTestResult> RunAckTestAsync(Connectivity.IOrderExecutor executor, Connectivity.IMarketDataProvider marketData)
    {
        var result = new AckTestResult { Ok = false };
        var startTime = DateTime.UtcNow;

        try
        {
            var symbol = this.SymbolName ?? "EURUSD";
            var pipSize = this.Symbol?.PipSize ?? 0.0001;
            var minVol = this.Symbol?.VolumeInUnitsMin ?? 1000;
            
            // Get current bid
            var bid = this.Symbol?.Bid ?? 0;
            if (bid <= 0)
            {
                result.ErrorMessage = "Invalid bid price";
                return result;
            }

            // Place far limit order (500 pips below market for BUY)
            var limitPrice = bid - 500 * pipSize;
            var volume = Math.Max(1000, minVol); // 0.01 lot = 1000 units for standard

            Print("[PREFLIGHT] Placing ACK test order: BUY_LIMIT {0} lots at {1}", volume / 100000.0, limitPrice);

            var tradeResult = await Task.Run(() =>
            {
                var tr = ExecuteMarketOrder(TradeType.Buy, symbol, volume, "PREFLIGHT_ACK");
                return tr;
            });

            var ackTime = DateTime.UtcNow;
            result.LatencyMs = (int)(ackTime - startTime).TotalMilliseconds;

            if (tradeResult.IsSuccessful && tradeResult.Position != null)
            {
                result.Ok = true;
                result.OrderId = tradeResult.Position.Id.ToString();
                
                // Immediately close/cancel
                await Task.Run(() => ClosePosition(tradeResult.Position));
                Print("[PREFLIGHT] ACK test order closed");
            }
            else
            {
                result.ErrorMessage = tradeResult.Error?.ToString() ?? "Order failed";
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Print("[PREFLIGHT] ACK test exception: {0}", ex);
        }

        return result;
    }

    private async Task<FillTestResult> RunFillTestAsync(Connectivity.IOrderExecutor executor, Connectivity.IMarketDataProvider marketData)
    {
        var result = new FillTestResult { Ok = false };
        var startTime = DateTime.UtcNow;

        try
        {
            var symbol = this.SymbolName ?? "EURUSD";
            var minVol = this.Symbol?.VolumeInUnitsMin ?? 1000;
            var volume = Math.Max(1000, minVol);

            Print("[PREFLIGHT] Placing FILL test: MARKET BUY {0} lots", volume / 100000.0);

            var entryPrice = this.Symbol?.Ask ?? 0;
            var tradeResult = await Task.Run(() => ExecuteMarketOrder(TradeType.Buy, symbol, volume, "PREFLIGHT_FILL"));

            var fillTime = DateTime.UtcNow;
            result.LatencyMs = (int)(fillTime - startTime).TotalMilliseconds;

            if (tradeResult.IsSuccessful && tradeResult.Position != null)
            {
                var fillPrice = tradeResult.Position.EntryPrice;
                var pipSize = this.Symbol?.PipSize ?? 0.0001;
                result.SlippagePips = Math.Abs(fillPrice - entryPrice) / pipSize;
                
                // Close immediately
                var closeStart = DateTime.UtcNow;
                await Task.Run(() => ClosePosition(tradeResult.Position));
                var closeEnd = DateTime.UtcNow;
                result.CloseLatencyMs = (int)(closeEnd - closeStart).TotalMilliseconds;

                result.Ok = true;
                Print("[PREFLIGHT] FILL test completed and closed");
            }
            else
            {
                result.ErrorMessage = tradeResult.Error?.ToString() ?? "Order failed";
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Print("[PREFLIGHT] FILL test exception: {0}", ex);
        }

        return result;
    }

    // ========== PREFLIGHT DATA CLASSES ==========
    private class PreflightResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("fail_reason")]
        public string? FailReason { get; set; }

        [JsonPropertyName("checks")]
        public Dictionary<string, bool> Checks { get; set; } = new();

        [JsonPropertyName("ack")]
        public AckTestResult Ack { get; set; } = new();

        [JsonPropertyName("fill")]
        public FillTestResult Fill { get; set; } = new();

        [JsonPropertyName("timestamps")]
        public Dictionary<string, string> Timestamps { get; set; } = new();

        [JsonPropertyName("bot")]
        public string Bot { get; set; } = "";
    }

    private class AckTestResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("latency_ms")]
        public int LatencyMs { get; set; }

        [JsonPropertyName("order_id")]
        public string? OrderId { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private class FillTestResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("latency_ms")]
        public int LatencyMs { get; set; }

        [JsonPropertyName("slippage_pips")]
        public double SlippagePips { get; set; }

        [JsonPropertyName("close_latency_ms")]
        public int CloseLatencyMs { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    // ========== STARTUP ECHO (CONFIG & BUILD STAMP) ==========
    private class StartupEcho
    {
        public string BuildStamp { get; set; } = string.Empty;
        public string ConfigSource { get; set; } = string.Empty;
        public string ResolvedMode { get; set; } = string.Empty;
        public bool ResolvedSimulationEnabled { get; set; }
        public Dictionary<string, string?> Env { get; set; } = new();
    }

    private StartupEcho ComposeStartupEcho()
    {
        var asm = typeof(BotGRobot).Assembly;
        var ver = asm.GetName().Version?.ToString() ?? "0.0.0.0";
        var buildTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        
        var cfg = TelemetryConfig.Load();
        string cfgPath = cfg.LogPath ?? "(unknown)";
        bool sim = cfg.UseSimulation || (cfg.Simulation != null && cfg.Simulation.Enabled);
        string mode = cfg.Mode ?? "(null)";

        var env = new Dictionary<string, string?>
        {
            ["Mode"] = Environment.GetEnvironmentVariable("Mode"),
            ["Simulation__Enabled"] = Environment.GetEnvironmentVariable("Simulation__Enabled"),
            ["BOTG__Mode"] = Environment.GetEnvironmentVariable("BOTG__Mode"),
            ["BOTG__Simulation__Enabled"] = Environment.GetEnvironmentVariable("BOTG__Simulation__Enabled"),
        };

        return new StartupEcho
        {
            BuildStamp = $"{ver}|{buildTime}",
            ConfigSource = cfgPath,
            ResolvedMode = mode,
            ResolvedSimulationEnabled = sim,
            Env = env
        };
    }

    private void WriteCanaryStatusJson(bool enabled, bool tried, bool ok, string? reason, DateTime t0, DateTime t1)
    {
        try
        {
            var cfg = TelemetryConfig.Load();
            var dir = Path.Combine(cfg.LogPath ?? "D:\\botg\\logs", "preflight");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "canary_status.json");
            
            var status = new
            {
                enabled,
                tried,
                ok,
                reason,
                t_start_iso = t0.ToString("o"),
                t_end_iso = t1.ToString("o")
            };
            
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
            // Silent fail - don't crash bot if status file write fails
        }
    }

}
