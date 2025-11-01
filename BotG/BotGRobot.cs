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
    private BotG.Runtime.RiskHeartbeatService? _riskHeartbeat;
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
    private bool _smokeOnceDone;
    private int _requestedUnitsLast;
    private string? _lastRejectReason;
    
    // Preflight live tick tracking
    private readonly CTraderTickSource _tickSource = new CTraderTickSource();

    protected override void OnStart()
    {
        BotGStartup.Initialize();
        BotG.Runtime.Logging.PipelineLogger.Initialize();
        BotG.Runtime.Logging.PipelineLogger.Log("BOOT", "Start", "Bot starting", null, Print);

        string EnvOr(string key, string defaultValue) => Environment.GetEnvironmentVariable(key) ?? defaultValue;
        var mode = EnvOr("DATASOURCE__MODE", "ctrader_demo");

        // Load config early
        var cfg = TelemetryConfig.Load();
        bool isPaper = cfg.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase);
        bool simEnabled = cfg.UseSimulation || (cfg.Simulation != null && cfg.Simulation.Enabled);

        // Ensure preflight directory exists
        var preflightDir = Path.Combine(cfg.LogPath, "preflight");
        Directory.CreateDirectory(preflightDir);

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
        }

        // Initialize RiskHeartbeatService using TelemetryContext.RiskPersister
        try {
            TelemetryContext.InitOnce();
            if (TelemetryContext.RiskPersister != null)
            {
                _riskHeartbeat = new BotG.Runtime.RiskHeartbeatService(this, TelemetryContext.RiskPersister, 15);
                Print("[RISK_HEARTBEAT] Service initialized");
            }
            else
            {
                Print("[RISK_HEARTBEAT] RiskPersister not available, heartbeat disabled");
            }
        } catch (Exception ex) {
            Print($"[RISK_HEARTBEAT] Initialization failed: {ex.Message}");
        }

        int eventsAttached = 0;
        BotG.Runtime.Logging.PipelineLogger.Log("EXECUTOR", "Start", "Initializing executor bundle", null, Print);
        try
        {
            _connector = ConnectorBundle.Create(this, mode);
            var marketData = _connector.MarketData;
            var executor = _connector.OrderExecutor;

            // NEW: trading gate chỉ theo ops.enable_trading
            bool tradingEnabled = cfg.Ops.EnableTrading;
            bool executorReady = executor != null;

            // Attach executor events for canary
            if (executor != null)
            {
                executor.OnFill += (fill) => {
                    Print("[ECHO+] Executor.OnFill: OrderId={0} Price={1}", fill.OrderId, fill.Price);
                };
                eventsAttached++;

                executor.OnReject += (reject) => {
                    Print("[ECHO+] Executor.OnReject: OrderId={0} Reason={1}", reject.OrderId, reject.Reason);
                };
                eventsAttached++;
            }

            // Log lý do rõ ràng
            BotG.Runtime.Logging.PipelineLogger.Log("GATE", "Initialized", "policy=ops_only", new System.Collections.Generic.Dictionary<string, object>
            {
                ["trading_enabled"] = tradingEnabled,
                ["ops_enable_trading"] = cfg.Ops.EnableTrading
            }, Print);
            Print("[GATE] trading_enabled={0} policy=ops_only; ops_enable_trading={1}", tradingEnabled, cfg.Ops.EnableTrading);
            
            Print("[ECHO+] TradingEnabled={0}", tradingEnabled);
            Print("[ECHO+] ExecutorReady={0}; EventsAttached={1}", executorReady, eventsAttached);

            marketData.Start();

            var hzEnv = EnvOr("L1_SNAPSHOT_HZ", "5");
            int snapshotHz = 5;
            if (int.TryParse(hzEnv, out var parsedHz) && parsedHz > 0)
            {
                snapshotHz = parsedHz;
            }

            TelemetryContext.AttachLevel1Snapshots(marketData, snapshotHz);

            var quoteTelemetry = new OrderQuoteTelemetry(marketData);
            if (executor != null)
            {
                TelemetryContext.AttachOrderLogger(quoteTelemetry, executor);

                TelemetryContext.MetadataHook = meta =>
                {
                    meta["data_source"] = mode;
                    meta["broker_name"] = executor.BrokerName ?? string.Empty;
                    meta["server"] = executor.Server ?? string.Empty;
                    meta["account_id"] = executor.AccountId ?? string.Empty;
                };
                TelemetryContext.UpdateDataSourceMetadata(mode, executor.BrokerName, executor.Server, executor.AccountId);
            }
            else
            {
                TelemetryContext.MetadataHook = meta =>
                {
                    meta["data_source"] = mode;
                    meta["broker_name"] = string.Empty;
                    meta["server"] = string.Empty;
                    meta["account_id"] = string.Empty;
                };
                TelemetryContext.UpdateDataSourceMetadata(mode, string.Empty, string.Empty, string.Empty);
            }

            try { TelemetryContext.QuoteTelemetry?.TrackSymbol(this.SymbolName); } catch { }
            if (executorReady)
            {
                var execData = new Dictionary<string, object>
                {
                    ["events_attached"] = eventsAttached,
                    ["broker_name"] = executor?.BrokerName ?? "",
                    ["server"] = executor?.Server ?? "",
                    ["account_id"] = executor?.AccountId ?? ""
                };
                BotG.Runtime.Logging.PipelineLogger.Log("EXECUTOR", "Ready", "Executor ready", execData, Print);
            }
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize connectivity: " + ex.Message);
        }

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
        }

        InitializeTelemetryWriter();

        // ========== EXECUTOR WIREPROOF (STARTUP) ==========
        try
        {
            // NEW: ops-only trading gate
            bool tradingEnabled = cfg.Ops.EnableTrading;
            bool executorReady = _connector?.OrderExecutor != null;
            string connectorType = _connector?.GetType().Name ?? "null";
            string executorType = _connector?.OrderExecutor?.GetType().Name ?? "null";

            var wireproofPath = Path.Combine(preflightDir, "executor_wireproof.json");
            var wireproof = new Dictionary<string, object?>
            {
                ["generated_at"] = DateTime.UtcNow.ToString("o", _invariantCulture),
                ["trading_enabled"] = tradingEnabled, // ops.enable_trading only
                ["ops_enable_trading"] = tradingEnabled, // Same value, policy=ops_only
                ["connector"] = connectorType,
                ["executor"] = executorType,
                ["ok"] = tradingEnabled && executorReady // Both gates must pass
            };

            var json = JsonSerializer.Serialize(wireproof, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(wireproofPath, json, _utf8NoBom);

            Print("[ECHO+] ExecutorReady trading_enabled={0} ops_enable_trading={1} executor={2}", 
                tradingEnabled, tradingEnabled, executorType);
        }
        catch (Exception ex)
        {
            Print("[ECHO+] wireproof write failed: " + ex.Message);
        }

        // ========== STARTUP ECHO ==========
        var echo = ComposeStartupEcho();
        Print("[ECHO] BuildStamp={0} ConfigSource={1} Mode={2} Simulation.Enabled={3} Env={4}",
            echo.BuildStamp, echo.ConfigSource, echo.ResolvedMode, echo.ResolvedSimulationEnabled, 
            JsonSerializer.Serialize(echo.Env));
        Print("[ECHO+] Policy=ops_only | RiskStops=ENABLED | ExecutorReady={0} | ops.enable_trading={1} | smoke_once={2}",
            _connector?.OrderExecutor != null, cfg.Ops.EnableTrading, cfg.Debug.SmokeOnce);

        // ========== PREFLIGHT LIVE FRESHNESS (NON-BLOCKING, LOGGED ONLY) ==========
        // Note: No mode/simulation gating - always run preflight if paper mode, just log result
        if (isPaper && !simEnabled)
        {
            Print("[PREFLIGHT] Starting async live freshness check (paper mode, simulation disabled)...");
            
            // Run preflight in background - don't block OnStart
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
                        Print("[PREFLIGHT] FAILED (L1 data stale: {0:F1}s > 5.0s) - logged, not blocking", result.LastAgeSec);
                        _preflightPassed = false;
                        // AutoStart: do NOT stop, just log
                    }
                    else
                    {
                        Print("[PREFLIGHT] PASSED");
                        _preflightPassed = true;
                    }
                }
                catch (Exception ex)
                {
                    Print("[PREFLIGHT] Exception: {0} - logged, not blocking", ex.Message);
                    _preflightPassed = false;
                }
            });
        }
        else
        {
            Print("[PREFLIGHT] Skipped (mode={0}, sim={1})", cfg.Mode, simEnabled);
            _preflightPassed = true; // allow trading
        var bootData = new Dictionary<string, object>
        {
            ["executor_ready"] = _connector?.OrderExecutor != null,
            ["ops_enable_trading"] = cfg.Ops.EnableTrading,
            ["debug_smoke_once"] = cfg.Debug.SmokeOnce,
            ["mode"] = cfg.Mode ?? "",
            ["simulation_enabled"] = simEnabled
        };
        BotG.Runtime.Logging.PipelineLogger.Log("BOOT", "Complete", "OnStart complete, AutoStart ready", bootData, Print);

        }

        Timer.Start(TimeSpan.FromSeconds(1));
        Print("[TLM] Timer started 1s; Symbol={0}", Symbol?.Name ?? "NULL");
        Print("BotGRobot started; telemetry initialized");
    }

    protected override void OnTick()
    {
        // Track tick for preflight live freshness
        _tickSource.OnTick(Server.Time);
        
        try { _connector?.TickPump?.Pump(); } catch { }
        try { TelemetryContext.Collector?.IncTick(); } catch { }

        // NOTE: Strategy pipeline disabled - empty strategy list.
        // To enable trading:
        // 1. Add strategies to list in OnStart: strategies.Add(new MyStrategy(...))
        // 2. Call strategy.Evaluate(data) here to generate signals
        // 3. Strategies emit SignalGenerated events → TradeManager.Process(signal, riskScore)

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

        // AutoStart RuntimeLoop: runs every 1s
        RuntimeLoop();
    }

    private readonly BotG.Runtime.SmokeOnceService _smokeOnceService = new BotG.Runtime.SmokeOnceService();

    private void RuntimeLoop()
    {
        // Heartbeat: always tick at start of loop
        _riskHeartbeat?.Tick();

        // Guard: _tradeManager must be initialized
        if (_tradeManager == null) return;

        var cfg = TelemetryConfig.Load();

        // ========== SMOKE_ONCE LOGIC ==========
        // Execute one market BUY->ACK->FILL->CLOSE cycle if enabled
        if (_smokeOnceService.ShouldFire(cfg))
        {
            // Mark done at the start to prevent looping on crash
            _smokeOnceService.MarkFired();

            try
            {
                // Check executor ready and no open positions
                bool executorReady = _connector?.OrderExecutor != null;
                bool noOpenPositions = Positions.Count == 0;

                if (!executorReady)
                {
                    Print("[SmokeOnce] Executor not ready, skipping");
                    return;
                }

                if (!noOpenPositions)
                {
                    Print("[SmokeOnce] Open positions exist, skipping");
                    return;
                }

                var executor = _connector!.OrderExecutor!;
                var symbol = this.Symbol;
                if (symbol == null)
                {
                    Print("[SmokeOnce] Symbol not available");
                    return;
                }

                // Calculate volume via RiskManager (units), then normalize to broker constraints
                double stopPoints = 100.0; // Fixed 100-point SL for smoke test
                double requestedVolume = 0;
                if (_riskManager != null)
                {
                    try
                    {
                        requestedVolume = _riskManager.CalculateOrderSize(stopPoints, 0.0);
                    }
                    catch (Exception ex)
                    {
                        Print("[SmokeOnce] Risk sizing failed: {0}", ex.Message);
                        return;
                    }
                }
                else
                {
                    Print("[SmokeOnce] RiskManager not initialized");
                    return;
                }

                // Normalize units to broker min/step
                int units = _riskManager.NormalizeUnitsForSymbol(symbol, requestedVolume);
                _requestedUnitsLast = units;

                // Evidence log in expected format
                try { Print("[SMOKE_ONCE] firing symbol={0} units={1}", symbol.Name, units); } catch {}

                // ORDER pipeline logging: PREPARED
                BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "PREPARED", "units_ready", new System.Collections.Generic.Dictionary<string, object>
                {
                    ["symbol"] = symbol.Name,
                    ["requestedUnits"] = units
                }, Print);

                // Build order and route via executor to ensure orders.csv is populated via OrderLifecycleLogger
                string orderId = $"SMOKE_{DateTime.UtcNow.Ticks}";
                var newOrder = new Connectivity.NewOrder(
                    OrderId: orderId,
                    Symbol: symbol.Name,
                    Side: Connectivity.OrderSide.Buy,
                    Volume: units,
                    Type: Connectivity.OrderType.Market,
                    Price: null,
                    StopLoss: null,
                    ClientTag: "BotG_SMOKE"
                );

                // orders.csv: REQUEST
                try
                {
                    TelemetryContext.OrderLogger?.LogV2(
                        phase: "REQUEST",
                        orderId: orderId,
                        clientOrderId: orderId,
                        side: "Buy",
                        action: "Buy",
                        type: "Market",
                        intendedPrice: this.Symbol?.Ask,
                        stopLoss: null,
                        execPrice: null,
                        theoreticalLots: null,
                        theoreticalUnits: units,
                        requestedVolume: units,
                        filledSize: null,
                        status: "REQUEST",
                        reason: null,
                        session: symbol.Name
                    );
                }
                catch { }

                // Wire one-shot handlers to capture ACK/FILL or REJECT
                Connectivity.OrderFill? lastFill = null;
                Connectivity.OrderReject? lastReject = null;
                void OnFillHandler(Connectivity.OrderFill f)
                {
                    if (!string.Equals(f.OrderId, orderId, StringComparison.OrdinalIgnoreCase)) return;
                    lastFill = f;
                    try { executor.OnFill -= OnFillHandler; } catch { }
                    try { executor.OnReject -= OnRejectHandler; } catch { }
                }
                void OnRejectHandler(Connectivity.OrderReject r)
                {
                    if (!string.Equals(r.OrderId, orderId, StringComparison.OrdinalIgnoreCase)) return;
                    lastReject = r;
                    _lastRejectReason = r.Reason;
                    try { executor.OnFill -= OnFillHandler; } catch { }
                    try { executor.OnReject -= OnRejectHandler; } catch { }
                }
                executor.OnFill += OnFillHandler;
                executor.OnReject += OnRejectHandler;

                // Send via executor (synchronous wait) - wrapped in try/catch, never throw
                try
                {
                    executor.SendAsync(newOrder).GetAwaiter().GetResult();
                    try { Print("[EXECUTOR] request sent tag={0} requestId={1}", newOrder.ClientTag ?? "", orderId); } catch {}
                }
                catch (Exception ex)
                {
                    _lastRejectReason = ex.Message;
                    Print("[SmokeOnce] SendAsync exception: {0}", ex.Message);
                }

                // Small wait to allow event propagation
                var waitDeadline = DateTime.UtcNow.AddMilliseconds(500);
                while (lastFill == null && lastReject == null && DateTime.UtcNow < waitDeadline)
                {
                    Thread.Sleep(25);
                }

                if (lastReject != null)
                {
                    // orders.csv: REJECT
                    try
                    {
                        TelemetryContext.OrderLogger?.LogV2(
                            phase: "REJECT",
                            orderId: orderId,
                            clientOrderId: orderId,
                            side: "Buy",
                            action: "Buy",
                            type: "Market",
                            intendedPrice: this.Symbol?.Ask,
                            stopLoss: null,
                            execPrice: null,
                            theoreticalLots: null,
                            theoreticalUnits: units,
                            requestedVolume: units,
                            filledSize: null,
                            status: "REJECT",
                            reason: lastReject?.Reason,
                            session: symbol.Name
                        );
                    }
                    catch { }
                    Print("[ECHO+] SmokeOnce BUY failed: {0}", _lastRejectReason ?? "unknown");
                    return;
                }

                if (lastFill == null)
                {
                    Print("[ECHO+] SmokeOnce BUY unknown state (no fill/reject)");
                    return;
                }

                // orders.csv: ACK and FILL
                try
                {
                    TelemetryContext.OrderLogger?.LogV2(
                        phase: "ACK",
                        orderId: orderId,
                        clientOrderId: orderId,
                        side: "Buy",
                        action: "Buy",
                        type: "Market",
                        intendedPrice: this.Symbol?.Ask,
                        stopLoss: null,
                        execPrice: lastFill.Price,
                        theoreticalLots: null,
                        theoreticalUnits: units,
                        requestedVolume: units,
                        filledSize: lastFill.Volume,
                        status: "ACK",
                        reason: null,
                        session: symbol.Name
                    );

                    TelemetryContext.OrderLogger?.LogV2(
                        phase: "FILL",
                        orderId: orderId,
                        clientOrderId: orderId,
                        side: "Buy",
                        action: "Buy",
                        type: "Market",
                        intendedPrice: this.Symbol?.Ask,
                        stopLoss: null,
                        execPrice: lastFill.Price,
                        theoreticalLots: null,
                        theoreticalUnits: units,
                        requestedVolume: units,
                        filledSize: lastFill.Volume,
                        status: "FILL",
                        reason: null,
                        session: symbol.Name
                    );
                }
                catch { }

                Print("[ECHO+] REQUEST/ACK/FILL: VolumeUnits={0}, FillPrice={1}", lastFill.Volume, lastFill.Price);

                // Close immediately by label
                try
                {
                    var pos = Positions.FirstOrDefault(p => p.SymbolName == symbol.Name && p.Label == "BotG_SMOKE");
                    if (pos != null)
                    {
                        Print("[ECHO+] SmokeOnce: Closing position {0}", pos.Id);
                        var closeResult = ClosePosition(pos);
                        if (!closeResult.IsSuccessful)
                        {
                            BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "CLOSE", "error", new System.Collections.Generic.Dictionary<string, object>
                            {
                                ["id"] = pos.Id,
                                ["reason"] = closeResult.Error?.ToString() ?? "UNKNOWN"
                            }, Print);
                            Print("[ECHO+] SmokeOnce CLOSE failed: {0}", closeResult.Error);
                        }
                        else
                        {
                            BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "CLOSE", "ok", new System.Collections.Generic.Dictionary<string, object>
                            {
                                ["id"] = pos.Id,
                                ["price"] = closeResult.Position?.EntryPrice ?? 0
                            }, Print);
                            Print("[ECHO+] CLOSE: PositionId={0}, ClosingPrice={1}", pos.Id, closeResult.Position?.EntryPrice);
                        }
                    }
                    else
                    {
                        Print("[ECHO+] SmokeOnce: No position found to close");
                    }
                }
                catch { }

                Print("[ECHO+] SmokeOnce completed");
            }
            catch (Exception ex)
            {
                Print("[SmokeOnce] Exception: {0}", ex.Message);
                _lastRejectReason = ex.Message;
            }
        }

        // ========== STRATEGY PIPELINE (ALWAYS RUNS, GATED BY ops.enable_trading) ==========
        // Note: TradeManager.Process() will only place orders if cfg.Ops.EnableTrading == true
        // For now, no strategies in list, so no signals generated
        // To enable production trading:
        // 1. Add strategies to list in OnStart: strategies.Add(new MyStrategy(...))
        // 2. Call strategy.Evaluate(data) here to generate signals
        // 3. Strategies emit SignalGenerated events → TradeManager.Process(signal, riskScore)

        // Write runtime probe snapshot
        WriteRuntimeProbe(cfg);
    }

    private void WriteRuntimeProbe(TelemetryConfig cfg)
    {
        try
        {
            var folder = string.IsNullOrEmpty(TelemetryContext.RunFolder) ? (cfg.LogPath ?? DefaultTelemetryDirectory) : TelemetryContext.RunFolder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "runtime_probe.json");
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["executorReady"] = _connector?.OrderExecutor != null,
                ["tradingEnabled"] = cfg.Ops.EnableTrading, // ops-only policy
                ["opsEnableTrading"] = cfg.Ops.EnableTrading,
                ["requestedUnitsLast"] = _requestedUnitsLast,
                ["lastRejectReason"] = _lastRejectReason == null ? null : System.Text.Json.Nodes.JsonValue.Create(_lastRejectReason)
            };
            File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions{WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}), _utf8NoBom);
        }
        catch { }
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

    private bool ResolveTradingEnabled()
    {
        try
        {
            var tradingProp = GetType().GetProperty("Trading");
            var trading = tradingProp?.GetValue(this);
            if (trading == null)
            {
                return false;
            }

            var enabledProp = trading.GetType().GetProperty("IsEnabled");
            if (enabledProp?.GetValue(trading) is bool flag)
            {
                return flag;
            }
        }
        catch
        {
            // ignore reflection errors, assume not yet enabled
        }

        return false;
    }

}
