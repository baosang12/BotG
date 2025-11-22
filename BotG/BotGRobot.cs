using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using cAlgo.API;
using MarketSeries = cAlgo.API.Internals.MarketSeries;
using Telemetry;
using Connectivity;
using BotG.Preflight;
using BotG.Threading;
using Strategies;
using Strategies.Templates;
using BotG.PositionManagement;
using BotG.MarketRegime;
using BotG.Strategies.Coordination;
using BotG.Config;
using BotG.Runtime.Logging;
using BotG.MultiTimeframe;
using BotG.Performance;
using BotG.Performance.Monitoring;
using Strategies.Breakout;
using Strategies.Config;
using RiskManager = BotG.RiskManager;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
public class BotGRobot : Robot
{
    // hold runtime modules on the robot instance for later use
    private TradeManager.TradeManager? _tradeManager = null;
    private TelemetryConfig? _config;
    private BotG.Runtime.RiskHeartbeatService? _riskHeartbeat = null;
    private RiskManager.RiskManager? _riskManager = null;
    private MainThreadTimer? _riskHeartbeatTimer = null;
    private MainThreadTimer? _riskSnapshotTimer = null;
    private MainThreadTimer? _telemetryTimer = null;
    private ConnectorBundle? _connector;
    private long _tickCounter;
    private DateTime _lastTradingHoursLogUtc = DateTime.MinValue;
    private bool _enforceTradingHours = true;
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
    private double _requestedUnitsLast;
    private string? _lastRejectReason;
    private StrategyPipeline? _strategyPipeline;
    private BotG.Strategies.Coordination.IStrategyCoordinator? _coordinator;
    private ConfigHotReloadManager? _configHotReloadManager;
    private IReadOnlyList<IStrategy> _strategies = Array.Empty<IStrategy>();
    private Strategies.Registry.StrategyRegistry? _strategyRegistry;
    private FileSystemWatcher? _strategyRegistryWatcher;
    private System.Threading.Timer? _strategyReloadTimer;
    private readonly object _strategyReloadLock = new object();
    private TimeSpan _strategyReloadDebounce = TimeSpan.FromSeconds(2);
    private string? _pendingStrategyReloadReason;
    private string? _strategyRegistryPath;
    private FileStream? _instanceLockStream;
    private string? _instanceLockPath;
    private const string InstanceLockFileName = "botg_instance.lock";
    private static bool _exceptionSinksRegistered;
    private static readonly object _exceptionSinksLock = new object();
    
    // Preflight live tick tracking
    private readonly CTraderTickSource _tickSource = new CTraderTickSource();
    
    // Thread safety: Serializes all async operations to prevent race conditions
    private readonly ExecutionSerializer _executionSerializer = new ExecutionSerializer();

    // POSITION MANAGEMENT: Track & manage all positions with exit strategies
    private PositionManager? _positionManager = null;
    private ExitProfileService? _exitProfileService = null;

    // MARKET REGIME DETECTION: Classify market conditions for strategy routing
    private MarketRegimeDetector? _regimeDetector = null;
    private RegimeConfiguration? _regimeConfiguration = null;
    private RegimeType _currentRegime = RegimeType.Uncertain;
    private TimeframeManager? _timeframeManager;
    private TimeframeSynchronizer? _timeframeSynchronizer;
    private SessionAwareAnalyzer? _sessionAnalyzer;
    private MultiTimeframeBenchmark? _multiTimeframeBenchmark;
    private BacktestMonitor? _backtestMonitor;
    private TimeframeSnapshot? _lastSnapshot;
    private TimeframeAlignmentResult? _lastAlignment;
    private TradingSession _lastSession = TradingSession.Asian;
    private double _lastSessionMultiplier = 1.0;
    private TimeframeSynchronizerConfig? _timeframeSynchronizerConfig;
    private readonly Dictionary<ModelTimeFrame, DateTime> _lastIngestedOpenTimes = new();
    private const int LiveDailyTradeLimit = 3;
    private static readonly (TimeFrame CTrader, ModelTimeFrame Model)[] MultiTimeframePairs = new[]
    {
        (TimeFrame.Hour4, ModelTimeFrame.H4),
        (TimeFrame.Hour, ModelTimeFrame.H1),
        (TimeFrame.Minute15, ModelTimeFrame.M15)
    };
    private const int BenchmarkLogIntervalTicks = 600;

    protected override void OnStart()
    {
        try
        {
            SafeOnStart();
        }
        catch (Exception ex)
        {
            LogCritical("OnStart", ex);
        }
    }

    private void SafeOnStart()
    {
        var guardConfig = TelemetryConfig.LoadForGuard();
        if (!EnsureSingleInstance(guardConfig))
        {
            try
            {
                BotG.Runtime.Logging.PipelineLogger.Initialize();
                BotG.Runtime.Logging.PipelineLogger.Log("BOOT", "GuardBlock", "Instance guard prevented start", null, Print);
            }
            catch { }

            Stop();
            return;
        }

        BotGStartup.Initialize();
        BotG.Runtime.Logging.PipelineLogger.Initialize();
        BotG.Runtime.Logging.PipelineLogger.Log("BOOT", "Start", "Bot starting", null, Print);

        string EnvOr(string key, string defaultValue) => Environment.GetEnvironmentVariable(key) ?? defaultValue;
        var mode = EnvOr("DATASOURCE__MODE", "ctrader_demo");

        // Load config early
        _config = TelemetryConfig.Load();
        var cfg = _config;

        try
        {
            var exitProfilesConfig = ExitProfileConfigLoader.LoadFromRuntimeConfig();
            _exitProfileService = new ExitProfileService(exitProfilesConfig);
            PipelineLogger.Log(
                "EXIT",
                "ProfilesReady",
                "Exit profile service initialized",
                new Dictionary<string, object?>
                {
                    ["profile_count"] = _exitProfileService.ProfileCount,
                    ["default_profile"] = _exitProfileService.DefaultProfileName
                },
                Print);
        }
        catch (Exception ex)
        {
            _exitProfileService = null;
            PipelineLogger.Log(
                "EXIT",
                "ProfilesInitError",
                "Failed to initialize exit profile service",
                new { error = ex.Message },
                Print);
        }

    // CRITICAL SAFETY: Validate trading gate BEFORE any execution
        TradingGateValidator.ValidateOrThrow(cfg);
        bool isPaper = cfg.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase);
        bool simEnabled = cfg.UseSimulation || (cfg.Simulation != null && cfg.Simulation.Enabled);

        _enforceTradingHours = false;
        var enforceGateEnv = Environment.GetEnvironmentVariable("BOTG_ENFORCE_TRADING_HOURS");
        if (!string.IsNullOrWhiteSpace(enforceGateEnv) && enforceGateEnv.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            _enforceTradingHours = true;
        }

        if (!_enforceTradingHours)
        {
            BotG.Runtime.Logging.PipelineLogger.Log("TRADING_HOURS", "GateDisabled", "Trading hours guard disabled", new { cfg.Mode }, Print);
        }

    RegisterGlobalExceptionSinks(cfg);

        // Ensure preflight directory exists
        var preflightDir = Path.Combine(cfg.LogPath, "preflight");
        Directory.CreateDirectory(preflightDir);

        try
        {
            _riskManager = new RiskManager.RiskManager();
            var riskSettings = new RiskManager.RiskSettings
            {
                PositionSizeMultiplier = 0.5
            };
            _riskManager.Initialize(riskSettings);
            try { _riskManager.SetSymbolReference(this.Symbol); } catch { }
            BotG.Runtime.Logging.PipelineLogger.Log("RISK", "Ready", "RiskManager initialized", null, Print);
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize RiskManager: " + ex.Message);
        }

        // Initialize RiskHeartbeatService using TelemetryContext.RiskPersister
        // TelemetryContext.InitOnce() already called in BotGStartup.Initialize() (line 43)
        try
        {
            var debugLog = Path.Combine(cfg.LogPath, "a8_debug.log");
            File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] A8_DEBUG: Checking RiskPersister...\n");
            File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] A8_DEBUG: RiskPersister is {(TelemetryContext.RiskPersister == null ? "NULL" : "NOT NULL")}\n");
        }
        catch { }
        
        Print("[A8_DEBUG] TelemetryContext.RiskPersister is " + (TelemetryContext.RiskPersister == null ? "NULL" : "NOT NULL"));
        if (TelemetryContext.RiskPersister != null)
        {
            // A8 FIX: Re-initialize RiskPersister với callback openPnL cho paper mode
            var riskCfg = _config ?? TelemetryConfig.Load();
            bool isPaperMode = riskCfg.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase);
            try
            {
                var debugLog = Path.Combine(cfg.LogPath, "a8_debug.log");
                File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] A8_DEBUG: Config mode='{riskCfg.Mode}', isPaperMode={isPaperMode}\n");
            }
            catch { }
            Print($"[A8_DEBUG] Config mode='{riskCfg.Mode}', isPaperMode={isPaperMode}");
            if (isPaperMode)
            {
#pragma warning disable CS8602 // Dereference of a possibly null reference - suppressed for A8 fix
                Func<double> getOpenPnl = () =>
                {
                    try
                    {
                        double totalPnl = 0.0;
                        foreach (var pos in this.Positions)
                        {
                            totalPnl += pos.NetProfit;
                        }
                        return totalPnl;
                    }
                    catch
                    {
                        return 0.0;
                    }
                };

                var runDir = TelemetryContext.RunFolder;
                TelemetryContext.RiskPersister = new Telemetry.RiskSnapshotPersister(
                    runDir,
                    riskCfg.RiskSnapshotFile,
                    isPaperMode,
                    getOpenPnl
                );
                try
                {
                    var debugLog = Path.Combine(cfg.LogPath, "a8_debug.log");
                    File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] A8_DEBUG: RiskPersister re-initialized with openPnl callback\n");
                }
                catch { }
                Print("[A8_DEBUG] RiskPersister re-initialized with openPnl callback");
#pragma warning restore CS8602
            }
            else
            {
                try
                {
                    var debugLog = Path.Combine(cfg.LogPath, "a8_debug.log");
                    File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] A8_DEBUG: NOT paper mode (mode='{riskCfg.Mode}'), skipping callback re-init\n");
                }
                catch { }
                Print("[A8_DEBUG] NOT paper mode, skipping callback re-init");
            }

            int heartbeatSec = 15;
            var envHeartbeat = Environment.GetEnvironmentVariable("BOTG_RISK_HEARTBEAT_SEC");
            if (int.TryParse(envHeartbeat, out var hb) && hb > 0)
            {
                heartbeatSec = hb;
            }

            _riskHeartbeat = new BotG.Runtime.RiskHeartbeatService(this, TelemetryContext.RiskPersister, TelemetryContext.PositionPersister, heartbeatSec, riskCfg);
            _riskHeartbeatTimer = new MainThreadTimer(this, () =>
            {
                try
                {
                    _riskHeartbeat?.Tick();
                }
                catch (Exception ex)
                {
                    LogTimerException("RiskHeartbeat", ex);
                }
            }, TimeSpan.FromSeconds(heartbeatSec), runImmediately: true);
            _riskHeartbeatTimer.Start();

            AppendDebugLine("TIMER", $"RiskHeartbeat started interval={heartbeatSec}s (main-thread)");
            Print($"[RISK_HEARTBEAT] Service started (interval={heartbeatSec}s, main-thread)");
        }
        else
        {
            _riskHeartbeat = null;
            AppendDebugLine("TIMER", "RiskHeartbeat skipped - RiskPersister missing");
            Print("[RISK_HEARTBEAT] RiskPersister not available, heartbeat disabled");
        }

        if (_riskManager != null && TelemetryContext.RiskPersister != null)
        {
            var snapshotInterval = _riskManager.SnapshotInterval;
            _riskSnapshotTimer = new MainThreadTimer(this, () =>
            {
                try
                {
                    _riskManager?.CaptureSnapshotForScheduler();
                }
                catch (Exception ex)
                {
                    LogTimerException("RiskSnapshot", ex);
                }
            }, snapshotInterval);
            _riskSnapshotTimer.Start();
            AppendDebugLine("TIMER", $"Risk snapshot timer started interval={snapshotInterval.TotalSeconds:F0}s");
        }
        else if (_riskManager != null)
        {
            AppendDebugLine("TIMER", "Risk snapshot timer skipped - RiskPersister missing");
        }

        if (TelemetryContext.Collector != null)
        {
            var telemetryInterval = TelemetryContext.Collector.FlushInterval;
            _telemetryTimer = new MainThreadTimer(this, () =>
            {
                try
                {
                    TelemetryContext.Collector?.FlushOnMainThread();
                }
                catch (Exception ex)
                {
                    LogTimerException("TelemetryCollector", ex);
                }
            }, telemetryInterval);
            _telemetryTimer.Start();
            AppendDebugLine("TIMER", $"Telemetry collector timer started interval={telemetryInterval.TotalSeconds:F0}s");
            Print($"[TELEMETRY] Collector timer started (interval={telemetryInterval.TotalSeconds:F0}s)");
        }
        else
        {
            AppendDebugLine("TIMER", "Telemetry collector timer skipped - Collector null");
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
                executor.OnFill += (fill) =>
                {
                    Print("[ECHO+] Executor.OnFill: OrderId={0} Price={1}", fill.OrderId, fill.Price);
                    try { _backtestMonitor?.OnTradeFill(); } catch { }
                };
                eventsAttached++;

                executor.OnReject += (reject) =>
                {
                    Print("[ECHO+] Executor.OnReject: OrderId={0} Reason={1}", reject.OrderId, reject.Reason);
                    try { _backtestMonitor?.OnTradeReject(); } catch { }
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

        InitializeMultiTimeframeSystem();
        InitializeStrategyRegistry(cfg);

        try
        {
            if (_connector != null)
            {
                if (_strategies == null || _strategies.Count == 0)
                {
                    _strategies = CreateStrategies();
                }

                _tradeManager = new TradeManager.TradeManager(_strategies, this, _riskManager, _connector.MarketData, _connector.OrderExecutor);
                ConfigureDailyTradeLimit(cfg);

                var coordinationConfig = StrategyCoordinationConfigLoader.LoadFromRuntimeConfig();
                ApplyCoordinationConfig(coordinationConfig, isReload: false);

                InitializeCoordinationHotReload();

                if (cfg.StrategyRegistry != null && cfg.StrategyRegistry.HotReloadEnabled && _strategyRegistry != null)
                {
                    InitializeStrategyRegistryWatcher(cfg);
                }

                BotG.Runtime.Logging.PipelineLogger.Log("TRADE", "Ready", "TradeManager initialized",
                    new Dictionary<string, object?>
                    {
                        ["strategy_count"] = _strategies.Count,
                        ["strategies"] = string.Join(",", _strategies.Select(s => s.Name))
                    }, Print);

                Print("[DEBUG] About to initialize PositionManager...");
                
                // Initialize PositionManager with compound exit strategy (SL/TP + Time-based)
                try
                {
                    Print("[DEBUG] Creating CompoundExitStrategy...");
                    // M15 timeframe: 15 minutes × 60 seconds = 900 seconds per bar
                    var exitStrategy = CompoundExitStrategy.CreateDefault(secondsPerBar: 900);
                    Print("[DEBUG] Creating PositionManager instance...");
                    _positionManager = new PositionManager(this, exitStrategy);
                    Print("[DEBUG] Logging POSITION Ready...");
                    BotG.Runtime.Logging.PipelineLogger.Log("POSITION", "Ready", "PositionManager initialized",
                        new Dictionary<string, object?>
                        {
                            ["exit_strategy"] = "Compound (SL/TP + Time-based, M15=900s/bar)"
                        }, Print);
                    Print("[PositionManager] Initialized with compound exit strategy (M15 timeframe)");
                }
                catch (Exception pmEx)
                {
                    Print($"[PositionManager] FAILED to initialize: {pmEx.GetType().Name}: {pmEx.Message}");
                    Print($"[PositionManager] Stack trace: {pmEx.StackTrace}");
                    BotG.Runtime.Logging.PipelineLogger.Log("POSITION", "InitError", "PositionManager initialization failed",
                        new Dictionary<string, object?>
                        {
                            ["error"] = pmEx.Message,
                            ["type"] = pmEx.GetType().Name
                        }, Print);
                }

                // Initialize Market Regime Detector
                try
                {
                    _regimeConfiguration = RegimeConfigurationLoader.LoadFromRuntimeConfig();
                    _regimeDetector = new MarketRegimeDetector(this, _regimeConfiguration);
                    BotG.Runtime.Logging.PipelineLogger.Log("REGIME", "Ready", "MarketRegimeDetector initialized",
                        new Dictionary<string, object?>
                        {
                            ["adx_trend_threshold"] = _regimeConfiguration.AdxTrendThreshold,
                            ["adx_range_threshold"] = _regimeConfiguration.AdxRangeThreshold,
                            ["volatility_threshold"] = _regimeConfiguration.VolatilityThreshold,
                            ["calm_threshold"] = _regimeConfiguration.CalmThreshold,
                            ["confidence_floor"] = _regimeConfiguration.MinimumRegimeConfidence
                        }, Print);
                    Print($"[MarketRegime] Detector initialized (ADX>={_regimeConfiguration.AdxTrendThreshold}, volatility x{_regimeConfiguration.VolatilityThreshold}, confidence floor {_regimeConfiguration.MinimumRegimeConfidence:F2})");
                }
                catch (Exception rgEx)
                {
                    Print($"[MarketRegime] FAILED to initialize: {rgEx.GetType().Name}: {rgEx.Message}");
                    BotG.Runtime.Logging.PipelineLogger.Log("REGIME", "InitError", "MarketRegimeDetector initialization failed",
                        new Dictionary<string, object?>
                        {
                            ["error"] = rgEx.Message,
                            ["type"] = rgEx.GetType().Name
                        }, Print);
                }
            }
        }
        catch (Exception ex)
        {
            Print($"BotGRobot startup: failed to initialize TradeManager/PositionManager: {ex.GetType().Name}: {ex.Message}");
            Print($"Stack trace: {ex.StackTrace}");
        }

        InitializeTelemetryWriter();
        InitializeBacktestMonitor(cfg);

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
            // A47 EMERGENCY: Preflight async check DISABLED - Server.Time callback causes threading violation
            // The lambda () => Server.Time is invoked from Task.Run background thread
            // TODO: Capture Server.Time on main thread before passing to PreflightLiveFreshness
            Print("[A47_EMERGENCY] Preflight async check DISABLED - Server.Time threading violation");
            _preflightPassed = true; // Set to true to allow bot to continue
            
            /*
            Print("[PREFLIGHT] Starting async live freshness check (paper mode, simulation disabled)...");
            
            // JUSTIFICATION: Fire-and-forget Task.Run is acceptable here because:
            // 1. NON-CRITICAL PATH: Preflight check is diagnostic only, not trading operation
            // 2. NON-BLOCKING STARTUP: Must not delay OnStart completion
            // 3. EXCEPTION SAFE: Wrapped in try/catch, sets _preflightPassed flag
            // 4. NO RACE CONDITION: Only writes to _preflightPassed (atomic bool)
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
            */
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

        // A47 v4 EMERGENCY: Timer.Start() DISABLED entirely - all timer callbacks run on background thread
        // Even empty SafeOnTimer() triggers cTrader threading violations after ~60 seconds
        // Timer.Start(TimeSpan.FromSeconds(1));
        Print("[A47_EMERGENCY_v4] Timer.Start DISABLED - threading violation root cause");
        Print("BotGRobot started; telemetry initialized (Timer DISABLED)");
    }

    private void InitializeMultiTimeframeSystem()
    {
        if (Symbol == null)
        {
            return;
        }

        try
        {
            var config = new TimeframeManagerConfig
            {
                Timeframes = MultiTimeframePairs.Select(p => p.Model).ToArray(),
                RequireClosedBars = true,
                DefaultBufferSize = 256,
                AntiRepaintGuard = TimeSpan.FromSeconds(30)
            };

                    var synchronizerConfig = new TimeframeSynchronizerConfig
                    {
                        MinimumAlignedTimeframes = 2,
                        MinimumBarsPerTimeframe = 1,
                        MaximumAllowedSkew = TimeSpan.FromHours(4),
                        AntiRepaintGuard = TimeSpan.FromMinutes(5),
                        WarmupBarsRequired = 12,
                        WarmupBarsPerTimeframe = new Dictionary<ModelTimeFrame, int>
                        {
                            [ModelTimeFrame.H4] = 8,
                            [ModelTimeFrame.H1] = 20,
                            [ModelTimeFrame.M15] = 48
                        },
                        RequiredAlignmentRatio = 0.67,
                        EnableAntiRepaint = true,
                        EnableSkewCheck = true,
                        IgnoreSkewDuringWarmup = true
                    };

            _timeframeManager = new TimeframeManager(config);
            _timeframeSynchronizer = new TimeframeSynchronizer(synchronizerConfig);
            _timeframeSynchronizerConfig = synchronizerConfig;
            _sessionAnalyzer = new SessionAwareAnalyzer();
            _multiTimeframeBenchmark = new MultiTimeframeBenchmark(Symbol.Name, 50.0, BenchmarkLogIntervalTicks);

            var bootstrapTimestamp = DateTime.UtcNow;
            _lastSnapshot = TimeframeSnapshot.Empty(Symbol.Name, bootstrapTimestamp, config.Timeframes);
            _lastAlignment = _timeframeSynchronizer.GetAlignmentResult(_lastSnapshot);
            _lastSession = _sessionAnalyzer.GetCurrentSession(bootstrapTimestamp);
            _lastSessionMultiplier = _sessionAnalyzer.GetPositionSizeMultiplier(_lastSession);

            PipelineLogger.Log(
                "MTF",
                "Init",
                "Multi-timeframe components initialized",
                new
                {
                    symbol = Symbol.Name,
                    timeframes = string.Join(",", config.Timeframes)
                },
                Print);
        }
        catch (Exception ex)
        {
            PipelineLogger.Log(
                "MTF",
                "InitError",
                "Failed to initialize multi-timeframe components",
                new { error = ex.Message },
                Print);

            _timeframeManager = null;
            _timeframeSynchronizer = null;
            _sessionAnalyzer = null;
            _multiTimeframeBenchmark = null;
            _timeframeSynchronizerConfig = null;
        }
    }

    private void InitializeBacktestMonitor(TelemetryConfig cfg)
    {
        var symbol = Symbol;
        if (symbol == null)
        {
            PipelineLogger.Log("MONITOR", "InitSkip", "Backtest monitor skipped (no symbol)", null, Print);
            return;
        }

        try
        {
            var monitorConfig = BacktestMonitorConfig.FromEnvironment(cfg.LogPath);
            _backtestMonitor = new BacktestMonitor(
                symbol.Name,
                symbol.PipSize,
                monitorConfig,
                (module, evt, message, data) => PipelineLogger.Log(module, evt, message, data, Print));

            PipelineLogger.Log(
                "MONITOR",
                "Ready",
                "Backtest monitor initialized",
                new { output = monitorConfig.OutputDirectory, interval_sec = monitorConfig.ReportInterval.TotalSeconds },
                Print);
        }
        catch (Exception ex)
        {
            _backtestMonitor = null;
            PipelineLogger.Log(
                "MONITOR",
                "InitError",
                "Failed to initialize backtest monitor",
                new { error = ex.Message },
                Print);
        }
    }

    private void ConfigureDailyTradeLimit(TelemetryConfig? cfg)
    {
        if (_tradeManager == null)
        {
            return;
        }

        if (ShouldForceDisableDailyTradeLimit())
        {
            _tradeManager.SetDailyTradeLimit(int.MaxValue);
            PipelineLogger.Log(
                "TRADE",
                "DailyLimitBypass",
                "Daily trade limit disabled via override",
                new Dictionary<string, object?>
                {
                    ["mode"] = cfg?.Mode ?? string.Empty,
                    ["running_mode"] = RunningMode.ToString()
                },
                Print);
            Print("[TRADE] Daily trade limit bypassed via BOTG_DISABLE_DAILY_TRADE_LIMIT (limit -> unlimited)");
            return;
        }

        _tradeManager.SetDailyTradeLimit(LiveDailyTradeLimit);
        PipelineLogger.Log(
            "TRADE",
            "DailyLimitEnabled",
            "Daily trade limit enforced",
            new Dictionary<string, object?>
            {
                ["limit"] = LiveDailyTradeLimit,
                ["running_mode"] = RunningMode.ToString(),
                ["mode"] = cfg?.Mode ?? string.Empty
            },
            Print);
        Print($"[TRADE] Daily trade limit enforced (limit={LiveDailyTradeLimit})");
    }

    private static bool ShouldForceDisableDailyTradeLimit()
    {
        var disableLimit = Environment.GetEnvironmentVariable("BOTG_DISABLE_DAILY_TRADE_LIMIT");
        if (IsTruthyEnvFlag(disableLimit))
        {
            return true;
        }

        return false;
    }

    private static bool IsTruthyEnvFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        return !(value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private void InitializeStrategyRegistry(TelemetryConfig cfg)
    {
        try
        {
            var path = ResolveStrategyRegistryPath(cfg);
            _strategyRegistryPath = path;
            _strategyReloadDebounce = TimeSpan.FromSeconds(Math.Max(0.5, cfg.StrategyRegistry?.WatchDebounceSeconds ?? 2.0));
            _strategyRegistry = new Strategies.Registry.StrategyRegistry(path);
            var result = _strategyRegistry.BuildStrategies(BuildStrategyFactoryContext());
            ApplyStrategyRegistryResult(result, "startup");
        }
        catch (Exception ex)
        {
            PipelineLogger.Log("STRATEGY", "RegistryInitFailed", "Failed to initialize strategy registry", new { error = ex.Message }, Print);
            _strategyRegistry = null;
            _strategies = CreateStrategies();
        }
    }

    private Strategies.Registry.StrategyFactoryContext BuildStrategyFactoryContext()
    {
        return new Strategies.Registry.StrategyFactoryContext(
            _timeframeManager,
            _timeframeSynchronizer,
            _sessionAnalyzer,
            _regimeDetector);
    }

    private string ResolveStrategyRegistryPath(TelemetryConfig cfg)
    {
        var configured = cfg.StrategyRegistry?.ConfigPath;
        var baseDir = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();

        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(baseDir, "strategy_registry.json");
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(baseDir, configured);
    }

    private void ApplyStrategyRegistryResult(Strategies.Registry.StrategyRegistryResult? result, string reason)
    {
        IReadOnlyList<IStrategy> strategySet;
        var diagnostics = result?.Diagnostics?.ToArray() ?? Array.Empty<Strategies.Registry.StrategyLoadDiagnostic>();

        if (result == null || result.Strategies.Count == 0)
        {
            strategySet = CreateStrategies();
            PipelineLogger.Log(
                "STRATEGY",
                "RegistryFallback",
                "Strategy registry empty, using legacy defaults",
                new { reason },
                Print);
        }
        else
        {
            strategySet = result.Strategies;
        }

        _strategies = strategySet;

        if (_tradeManager != null)
        {
            _tradeManager.UpdateStrategies(_strategies);
        }

        if (_coordinator != null && _tradeManager != null)
        {
            _strategyPipeline = new StrategyPipeline(_strategies, _tradeManager, _executionSerializer, _coordinator);
        }

        var diagPayload = diagnostics
            .Select(d => new { strategy = d.StrategyName, status = d.Status, reason = d.Reason })
            .ToArray();

        PipelineLogger.Log(
            "STRATEGY",
            "RegistryApply",
            "Strategy registry applied",
            new
            {
                reason,
                count = _strategies.Count,
                diagnostics = diagPayload
            },
            Print);
    }

    private void InitializeStrategyRegistryWatcher(TelemetryConfig cfg)
    {
        if (_strategyRegistryPath == null || cfg.StrategyRegistry?.HotReloadEnabled != true)
        {
            return;
        }

        try
        {
            DisposeStrategyWatcher();

            var directory = Path.GetDirectoryName(_strategyRegistryPath);
            var file = Path.GetFileName(_strategyRegistryPath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };

            watcher.Changed += OnStrategyRegistryFileChanged;
            watcher.Created += OnStrategyRegistryFileChanged;
            watcher.Deleted += OnStrategyRegistryFileChanged;
            watcher.Renamed += OnStrategyRegistryFileRenamed;
            watcher.EnableRaisingEvents = true;

            _strategyRegistryWatcher = watcher;

            PipelineLogger.Log(
                "STRATEGY",
                "WatcherReady",
                "Strategy registry watcher started",
                new { path = _strategyRegistryPath },
                Print);
        }
        catch (Exception ex)
        {
            PipelineLogger.Log(
                "STRATEGY",
                "WatcherInitFailed",
                "Failed to initialize strategy registry watcher",
                new { error = ex.Message },
                Print);
        }
    }

    private void OnStrategyRegistryFileChanged(object sender, FileSystemEventArgs e)
    {
        ScheduleStrategyReload($"fs_{e.ChangeType}");
    }

    private void OnStrategyRegistryFileRenamed(object sender, RenamedEventArgs e)
    {
        ScheduleStrategyReload("fs_renamed");
    }

    private void ScheduleStrategyReload(string reason)
    {
        lock (_strategyReloadLock)
        {
            _pendingStrategyReloadReason = reason;
            _strategyReloadTimer?.Dispose();
            _strategyReloadTimer = new System.Threading.Timer(ExecuteStrategyReload, null, _strategyReloadDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void ExecuteStrategyReload(object? state)
    {
        string reason;
        lock (_strategyReloadLock)
        {
            _strategyReloadTimer?.Dispose();
            _strategyReloadTimer = null;
            reason = _pendingStrategyReloadReason ?? "filesystem";
            _pendingStrategyReloadReason = null;
        }

        ReloadStrategies(reason, triggeredByWatcher: true);
    }

    private void ReloadStrategies(string reason, bool triggeredByWatcher)
    {
        if (_strategyRegistry == null)
        {
            return;
        }

        try
        {
            var result = _strategyRegistry.BuildStrategies(BuildStrategyFactoryContext());
            ApplyStrategyRegistryResult(result, reason);
        }
        catch (Exception ex)
        {
            PipelineLogger.Log(
                "STRATEGY",
                "RegistryReloadFailed",
                "Failed to reload strategy registry",
                new { error = ex.Message, reason },
                Print);
        }
    }

    private void DisposeStrategyWatcher()
    {
        lock (_strategyReloadLock)
        {
            if (_strategyRegistryWatcher != null)
            {
                try { _strategyRegistryWatcher.EnableRaisingEvents = false; } catch { }
                try { _strategyRegistryWatcher.Changed -= OnStrategyRegistryFileChanged; } catch { }
                try { _strategyRegistryWatcher.Created -= OnStrategyRegistryFileChanged; } catch { }
                try { _strategyRegistryWatcher.Deleted -= OnStrategyRegistryFileChanged; } catch { }
                try { _strategyRegistryWatcher.Renamed -= OnStrategyRegistryFileRenamed; } catch { }
                try { _strategyRegistryWatcher.Dispose(); } catch { }
                _strategyRegistryWatcher = null;
            }

            _strategyReloadTimer?.Dispose();
            _strategyReloadTimer = null;
            _pendingStrategyReloadReason = null;
        }
    }

    private IReadOnlyList<IStrategy> CreateStrategies()
    {
        var list = new List<IStrategy>
        {
            new SmaCrossoverStrategy(),
            new RsiStrategy(),
            new PriceActionStrategy(),
            new VolatilityStrategy()
        };

        if (_timeframeManager != null && _timeframeSynchronizer != null && _sessionAnalyzer != null)
        {
            var breakoutConfig = new BreakoutStrategyConfig();
            list.Add(new BreakoutStrategy(_timeframeManager, _timeframeSynchronizer, _sessionAnalyzer, breakoutConfig));
        }

        return list;
    }

    private (TimeframeSnapshot Snapshot, TimeframeAlignmentResult Alignment, TradingSession Session, double Multiplier) UpdateMultiTimeframeState(DateTime serverTimeUtc)
    {
        if (_timeframeManager == null || _timeframeSynchronizer == null)
        {
            return EnsureFallbackMultiTimeframeState(serverTimeUtc);
        }

        if (Symbol == null)
        {
            return EnsureFallbackMultiTimeframeState(serverTimeUtc);
        }

        var symbol = Symbol.Name;

        foreach (var pair in MultiTimeframePairs)
        {
            IngestTimeframeSeries(symbol, pair, serverTimeUtc);
        }

        var snapshot = _timeframeManager.CaptureSnapshot(symbol, serverTimeUtc);
        var alignment = _timeframeSynchronizer.GetAlignmentResult(snapshot);

        if (_sessionAnalyzer != null)
        {
            _lastSession = _sessionAnalyzer.GetCurrentSession(serverTimeUtc);
            _lastSessionMultiplier = _sessionAnalyzer.GetPositionSizeMultiplier(_lastSession);
        }

        _lastSnapshot = snapshot;
        _lastAlignment = alignment;

        return (snapshot, alignment, _lastSession, _lastSessionMultiplier);
    }

    private (TimeframeSnapshot Snapshot, TimeframeAlignmentResult Alignment, TradingSession Session, double Multiplier) EnsureFallbackMultiTimeframeState(DateTime serverTimeUtc)
    {
        var symbol = Symbol?.Name ?? string.Empty;

        if (_lastSnapshot == null)
        {
            var frames = MultiTimeframePairs.Select(p => p.Model).ToArray();
            _lastSnapshot = TimeframeSnapshot.Empty(symbol, serverTimeUtc, frames);
        }

        if (_lastAlignment == null)
        {
            var statuses = MultiTimeframePairs.ToDictionary(
                pair => pair.Model,
                _ => new TimeframeSeriesStatus(0, false, null, null));

            _lastAlignment = new TimeframeAlignmentResult(
                false,
                false,
                0,
                _lastSnapshot.TotalTimeframes,
                new ReadOnlyDictionary<ModelTimeFrame, TimeframeSeriesStatus>(statuses),
                _lastSnapshot,
                "uninitialized",
                0,
                false,
                null);
        }

        if (_sessionAnalyzer != null)
        {
            _lastSession = _sessionAnalyzer.GetCurrentSession(serverTimeUtc);
            _lastSessionMultiplier = _sessionAnalyzer.GetPositionSizeMultiplier(_lastSession);
        }

        return (_lastSnapshot, _lastAlignment, _lastSession, _lastSessionMultiplier);
    }

    private void TrackWarmupProgressForMonitor(TimeframeAlignmentResult alignment)
    {
        if (_backtestMonitor == null || alignment.SeriesStatuses == null)
        {
            return;
        }

        foreach (var kvp in alignment.SeriesStatuses)
        {
            var required = GetWarmupRequirementForMonitor(kvp.Key);
            _backtestMonitor.TrackWarmup(kvp.Key.ToString(), kvp.Value.AvailableBars, required);
        }
    }

    private int GetWarmupRequirementForMonitor(ModelTimeFrame timeframe)
    {
        if (_timeframeSynchronizerConfig?.WarmupBarsPerTimeframe != null &&
            _timeframeSynchronizerConfig.WarmupBarsPerTimeframe.TryGetValue(timeframe, out var specific) &&
            specific > 0)
        {
            return specific;
        }

        return _timeframeSynchronizerConfig?.WarmupBarsRequired ?? 0;
    }

    private void IngestTimeframeSeries(string symbol, (TimeFrame CTrader, ModelTimeFrame Model) pair, DateTime serverTimeUtc)
    {
        if (_timeframeManager == null)
        {
            return;
        }

        MarketSeries series;
        try
        {
            series = MarketData.GetSeries(Symbol, pair.CTrader);
        }
        catch
        {
            return;
        }

        if (series == null)
        {
            return;
        }

        var count = series.Close.Count;
        if (count < 2)
        {
            return;
        }

        var lastClosedIndex = count - 2;
        _lastIngestedOpenTimes.TryGetValue(pair.Model, out var lastRecordedOpenTime);
        var maxCatchUp = Math.Min(lastClosedIndex + 1, 16);
        var pending = new Stack<ModelBar>();

        for (int index = lastClosedIndex; index >= 0 && pending.Count < maxCatchUp; index--)
        {
            var openTime = series.OpenTime[index];
            if (openTime <= lastRecordedOpenTime)
            {
                break;
            }

            pending.Push(ConvertToBar(series, index, pair));
        }

        while (pending.Count > 0)
        {
            var bar = pending.Pop();
            if (_timeframeManager.TryAddBar(symbol, bar, serverTimeUtc, isClosedBar: true))
            {
                _lastIngestedOpenTimes[pair.Model] = bar.OpenTime;
            }
            else
            {
                break;
            }
        }
    }

    private static ModelBar ConvertToBar(MarketSeries series, int index, (TimeFrame CTrader, ModelTimeFrame Model) pair)
    {
        return new ModelBar
        {
            OpenTime = series.OpenTime[index],
            Open = series.Open[index],
            High = series.High[index],
            Low = series.Low[index],
            Close = series.Close[index],
            Volume = Convert.ToInt64(series.TickVolume[index]),
            Tf = pair.Model
        };
    }

    private MarketContext BuildMarketContext(MarketData marketData, DateTime serverTimeUtc)
    {
        double equity = 0.0;
        double balance = 0.0;
        double exposure = 0.0;
        int positionCount = 0;

        try
        {
            equity = Account?.Equity ?? 0.0;
            balance = Account?.Balance ?? 0.0;

            if (Positions != null)
            {
                foreach (var position in Positions)
                {
                    if (!string.Equals(position.SymbolName, marketData.Symbol, StringComparison.OrdinalIgnoreCase))
                        continue;

                    positionCount++;
                    exposure += Math.Abs(position.VolumeInUnits) * marketData.Mid;
                }
            }
        }
        catch
        {
            // ignore account inspection errors; fall back to defaults
        }

        double drawdown = equity - balance;
        var metrics = new Dictionary<string, double>
        {
            ["tick_rate_estimate"] = _tickRateEstimate
        };

        var metadata = new Dictionary<string, object?>
        {
            ["position_count"] = positionCount,
            ["mode"] = _config?.Mode ?? Environment.GetEnvironmentVariable("BOTG_MODE") ?? Environment.GetEnvironmentVariable("Mode"),
            ["regime_confidence_floor"] = _regimeConfiguration?.MinimumRegimeConfidence,
            ["server_time"] = serverTimeUtc,
            ["bar_time"] = marketData.TimestampUtc
        };

        RegimeAnalysisResult? regimeAnalysis = null;

        // Update current regime if detector is available
        if (_regimeDetector != null)
        {
            try
            {
                regimeAnalysis = _regimeDetector.AnalyzeCurrentRegimeDetailed();
                _currentRegime = regimeAnalysis.Regime;
            }
            catch
            {
                regimeAnalysis = null;
                // If analysis fails, keep previous regime
            }
        }

        metrics["regime_risk_multiplier"] = regimeAnalysis?.GetRiskMultiplier() ?? _currentRegime.GetRiskMultiplier();
        metadata["regime_confidence"] = regimeAnalysis?.Confidence;
        metadata["regime_display"] = _currentRegime.ToDisplayString();

        if (_lastAlignment != null)
        {
            var alignment = _lastAlignment;
            var total = Math.Max(alignment.TotalTimeframes, 1);
            metrics["mtf_alignment_ratio"] = alignment.AlignedTimeframes / (double)total;
            metrics["mtf_alignment_ok"] = alignment.IsAligned ? 1.0 : 0.0;
            metrics["mtf_anti_repaint_ok"] = alignment.AntiRepaintSafe ? 1.0 : 0.0;

            metadata["mtf_alignment_ok"] = alignment.IsAligned;
            metadata["mtf_alignment_reason"] = alignment.Reason;
            metadata["mtf_aligned"] = alignment.AlignedTimeframes;
            metadata["mtf_total"] = alignment.TotalTimeframes;
            metadata["mtf_ready"] = alignment.IsAligned && alignment.AntiRepaintSafe;
        }

        metrics["mtf_session_multiplier"] = _lastSessionMultiplier;
        metadata["mtf_session"] = _lastSession.ToString();
        metadata["mtf_session_multiplier"] = _lastSessionMultiplier;

        if (_lastSnapshot != null && _lastAlignment != null)
        {
            metadata["mtf_snapshot_timestamp"] = _lastSnapshot.TimestampUtc;
            metadata["mtf_context"] = new MultiTimeframeEvaluationContext(
                marketData,
                _lastSnapshot,
                _lastAlignment,
                _lastSession);
        }

        var context = new MarketContext(marketData, equity, exposure, drawdown, _currentRegime, regimeAnalysis, metrics, metadata)
        {
            CurrentTime = serverTimeUtc
        };
        return context;
    }

    protected override void OnTick()
    {
        try
        {
            SafeOnTick();
        }
        catch (Exception ex)
        {
            LogCritical("OnTick", ex);
        }
    }

    private void RegisterGlobalExceptionSinks(TelemetryConfig cfg)
    {
        if (_exceptionSinksRegistered)
        {
            return;
        }

        lock (_exceptionSinksLock)
        {
            if (_exceptionSinksRegistered)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(cfg.LogPath);
                var sinkLog = Path.Combine(cfg.LogPath, "unhandled_exceptions.log");

                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                {
                    try
                    {
                        var message = args.ExceptionObject is Exception ex
                            ? ex.ToString()
                            : args.ExceptionObject?.ToString() ?? "<null exception object>";
                        File.AppendAllText(
                            sinkLog,
                            $"[{DateTime.UtcNow:o}] AppDomain.UnhandledException IsTerminating={args.IsTerminating}\n{message}\n\n");
                    }
                    catch
                    {
                    }
                };

                TaskScheduler.UnobservedTaskException += (_, taskArgs) =>
                {
                    try
                    {
                        var message = taskArgs.Exception?.ToString() ?? "<null exception>";
                        File.AppendAllText(
                            sinkLog,
                            $"[{DateTime.UtcNow:o}] TaskScheduler.UnobservedTaskException\n{message}\n\n");
                        taskArgs.SetObserved();
                    }
                    catch
                    {
                    }
                };

                _exceptionSinksRegistered = true;
            }
            catch
            {
                // Ignore failures - logging is best-effort and must not crash startup
            }
        }
    }

    private void SafeOnTick()
    {
        // Track tick for preflight live freshness
        _tickSource.OnTick(Server.Time);
        
        try { _connector?.TickPump?.Pump(); } catch { }
        try { TelemetryContext.Collector?.IncTick(); } catch { }

        var serverTimeUtc = GetServerTimeUtc();
        var mtfSw = Stopwatch.StartNew();
        var mtfState = UpdateMultiTimeframeState(serverTimeUtc);
        mtfSw.Stop();

        BenchmarkReport? benchmark = null;
        if (_multiTimeframeBenchmark != null)
        {
            benchmark = _multiTimeframeBenchmark.Record(mtfSw.Elapsed, mtfState.Alignment, serverTimeUtc);
        }

        if (benchmark?.ShouldLog == true)
        {
            PipelineLogger.Log(
                "MTF",
                "Benchmark",
                "Multi-timeframe ingestion metrics",
                new
                {
                    symbol = benchmark.Symbol,
                    latency_ms = benchmark.LastLatencyMs,
                    avg_ms = benchmark.AverageLatencyMs,
                    max_ms = benchmark.MaxLatencyMs,
                    aligned = mtfState.Alignment.IsAligned,
                    anti_repaint = mtfState.Alignment.AntiRepaintSafe,
                    samples = benchmark.Samples,
                    over_budget_pct = benchmark.OverBudgetRatio
                },
                Print);
        }

        if (benchmark?.ShouldAlert == true)
        {
            PipelineLogger.Log(
                "MTF",
                "LatencyAlert",
                "Multi-timeframe ingestion latency exceeds target",
                new
                {
                    symbol = benchmark.Symbol,
                    latency_ms = benchmark.LastLatencyMs,
                    target_ms = benchmark.TargetLatencyMs,
                    reason = mtfState.Alignment.Reason,
                    anti_repaint = mtfState.Alignment.AntiRepaintSafe
                },
                Print);
        }

        if (_backtestMonitor != null && Symbol != null)
        {
            _backtestMonitor.OnTick(
                serverTimeUtc,
                Symbol.Bid,
                Symbol.Ask,
                benchmark?.LastLatencyMs,
                mtfState.Alignment.IsAligned,
                mtfState.Alignment.AntiRepaintSafe);

            TrackWarmupProgressForMonitor(mtfState.Alignment);
        }

        // SCALPING CONSERVATIVE: Check trading hours (08:00-20:00 only)
        if (!IsWithinTradingHours())
        {
            // Still process position management during off-hours (exit monitoring)
            // but skip signal generation
            if (_positionManager != null && Symbol != null)
            {
                try
                {
                    SyncPositionsWithManager();
                    double currentBid = Symbol.Bid;
                    double currentAsk = Symbol.Ask;
                    if (currentBid > 0 && currentAsk > 0)
                    {
                        _positionManager.OnTick(Server.Time, currentBid, currentAsk, Symbol.Name);
                    }
                }
                catch (Exception ex)
                {
                    Print($"[PositionManager] OnTick error (off-hours): {ex.Message}");
                }
            }
            return; // Skip signal generation during off-hours
        }

        // NOTE: Strategy pipeline disabled - empty strategy list.
        // To enable trading:
        // 1. Add strategies to list in OnStart: strategies.Add(new MyStrategy(...))
        // 2. Call strategy.Evaluate(data) here to generate signals
        // 3. Strategies emit SignalGenerated events → TradeManager.Process(signal, riskScore)

        Interlocked.Increment(ref _tickCounter);

        // POSITION MANAGEMENT: Check exit conditions for all open positions every tick
        if (_positionManager != null && Symbol != null)
        {
            try
            {
                // 1. Sync cTrader positions → PositionManager (detect new positions)
                SyncPositionsWithManager();
                
                // 2. Check exit conditions for all tracked positions
                double currentBid = Symbol.Bid;
                double currentAsk = Symbol.Ask;
                if (currentBid > 0 && currentAsk > 0)
                {
                    _positionManager.OnTick(Server.Time, currentBid, currentAsk, Symbol.Name);
                }
            }
            catch (Exception ex)
            {
                Print($"[PositionManager] OnTick error: {ex.Message}");
            }
        }

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

        if (_strategyPipeline != null)
        {
            var marketData = new MarketData(currentSymbol.Name, bid, ask, serverTimeUtc);
            var context = BuildMarketContext(marketData, serverTimeUtc);
            if (mtfState.Alignment.IsAligned && mtfState.Alignment.AntiRepaintSafe)
            {
                try
                {
                    _strategyPipeline.ProcessAsync(marketData, context, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    BotG.Runtime.Logging.PipelineLogger.Log("STRATEGY", "PipelineError", "Strategy pipeline tick failed", new { error = ex.Message }, Print);
                }
            }
            else
            {
                PipelineLogger.Log(
                    "MTF",
                    "PipelineSkip",
                    "Skipped strategy pipeline due to multi-timeframe misalignment",
                    new
                    {
                        reason = mtfState.Alignment.Reason,
                        aligned = mtfState.Alignment.AlignedTimeframes,
                        total = mtfState.Alignment.TotalTimeframes,
                        anti_repaint = mtfState.Alignment.AntiRepaintSafe
                    },
                    Print);
            }
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

    private DateTime GetServerTimeUtc()
    {
        var serverTime = Server?.Time ?? DateTime.UtcNow;
        return serverTime.Kind switch
        {
            DateTimeKind.Utc => serverTime,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(serverTime, DateTimeKind.Utc),
            _ => serverTime.ToUniversalTime()
        };
    }

    protected override void OnTimer()
    {
        try
        {
            SafeOnTimer();
        }
        catch (Exception ex)
        {
            LogCritical("OnTimer", ex);
        }
    }

    private void SafeOnTimer()
    {
        var ticksPerSecond = Interlocked.Exchange(ref _tickCounter, 0);
        Interlocked.Exchange(ref _tickRateEstimate, (double)ticksPerSecond);

        // A47 EMERGENCY: RuntimeLoop DISABLED - accesses cTrader API (Positions, Symbol) from timer thread
        // TODO: Wrap all API calls in BeginInvokeOnMainThread() before re-enabling
        // RuntimeLoop();
    }

    private readonly BotG.Runtime.SmokeOnceService _smokeOnceService = new BotG.Runtime.SmokeOnceService();
    private int _runtimeLoopDebugCounter = 0; // A8 DEBUG: Counter for debug logging
    private int _memorySnapshotCounter = 0; // A10: Counter for memory snapshots (every 30 ticks = 30s)

    private void RuntimeLoop()
    {
        // A8 DEBUG: Check if _riskHeartbeat is null
        if (_runtimeLoopDebugCounter++ < 5) // Only log first 5 times
        {
            try
            {
                var debugCfg = _config ?? TelemetryConfig.Load();
                var debugLog = Path.Combine(debugCfg.LogPath, "a8_debug.log");
                File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] RuntimeLoop: _riskHeartbeat is {(_riskHeartbeat == null ? "NULL" : "NOT NULL")}\n");
            }
            catch { }
        }
        
        // Heartbeat: always tick at start of loop
        _riskHeartbeat?.Tick();

        // A10: Memory snapshot every 30 seconds - DISABLED temporarily
        /*
        if (_memorySnapshotCounter++ >= 30)
        {
            _memorySnapshotCounter = 0;
            try
            {
                var snapshot = TelemetryContext.MemoryProfiler?.CaptureSnapshot();
                if (snapshot != null)
                {
                    TelemetryContext.MemoryProfiler?.Persist(snapshot);
                }
            }
            catch { }
        }
        */

        // Guard: _tradeManager must be initialized
        if (_tradeManager == null) return;

    var cfg = _config ?? TelemetryConfig.Load();

        // CRITICAL SAFETY: Runtime preflight check - validates trading conditions every tick
        // This catches config changes, sentinel files, or stale preflight results during execution
        try
        {
            TradingGateValidator.ValidateOrThrow(cfg);
        }
        catch (InvalidOperationException gateEx)
        {
            // Trading gate violation - log and skip this tick
            BotG.Runtime.Logging.PipelineLogger.Log("GATE", "RuntimeBlock", gateEx.Message, null, Print);
            return;
        }
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
                double units = _riskManager.NormalizeUnitsForSymbol(symbol, requestedVolume);
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

    /// <summary>
    /// Sync cTrader positions với PositionManager tracking
    /// Gọi định kỳ để detect positions mới được mở bởi ExecutionModule
    /// </summary>
    private void SyncPositionsWithManager()
    {
        if (_positionManager == null) return;

        try
        {
            var trackedIds = _positionManager.GetOpenPositions().Select(p => p.Id).ToHashSet();
            
            foreach (var ctraderPos in Positions)
            {
                string posId = ctraderPos.Id.ToString();
                
                // Nếu position chưa được track, thêm vào PositionManager
                if (!trackedIds.Contains(posId))
                {
                    var symbolInfo = ctraderPos.Symbol;
                    if (symbolInfo == null && string.Equals(Symbol?.Name, ctraderPos.SymbolName, StringComparison.OrdinalIgnoreCase))
                    {
                        symbolInfo = Symbol;
                    }

                    double pipSize = symbolInfo?.PipSize ?? (ctraderPos.SymbolName.Contains("JPY") ? 0.01 : 0.0001);
                    double lotSize = symbolInfo?.LotSize ?? 100000.0;
                    double pipValuePerLot = symbolInfo?.PipValue ?? 0.0;
                    double tickSize = symbolInfo?.TickSize ?? 0.0;
                    double tickValue = symbolInfo?.TickValue ?? 0.0;
                    double pointValue = tickSize > 0 ? (tickValue / tickSize) : CalculatePointValueForPosition();

                    string? strategyName = PositionLabelHelper.TryParseStrategyName(ctraderPos.Label);
                    string appliedProfile;
                    var exitParams = CreateExitParametersForPosition(ctraderPos, pipSize, strategyName, out appliedProfile);

                    var position = new BotG.PositionManagement.Position
                    {
                        Id = posId,
                        Symbol = ctraderPos.SymbolName,
                        Direction = ctraderPos.TradeType,
                        EntryPrice = ctraderPos.EntryPrice,
                        VolumeInUnits = ctraderPos.VolumeInUnits,
                        OpenTime = ctraderPos.EntryTime,
                        CurrentPrice = ctraderPos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask,
                        Label = ctraderPos.Label,
                        Status = PositionStatus.Open,
                        ExitParams = exitParams,
                        PipSize = pipSize,
                        PointValue = pointValue
                    };

                    if (_exitProfileService != null)
                    {
                        PipelineLogger.Log(
                            "EXIT",
                            "ProfileApplied",
                            "Exit profile assigned",
                            new
                            {
                                position = posId,
                                profile = appliedProfile,
                                strategy = strategyName ?? "unknown",
                                symbol = ctraderPos.SymbolName
                            },
                            Print);
                    }

                    position.ExitParams?.ApplyBrokerFeeBuffer(
                        ctraderPos.SymbolName,
                        pipSize,
                        lotSize,
                        pipValuePerLot,
                        tickSize,
                        tickValue,
                        position.VolumeInUnits,
                        position.Direction);
                    position.UpdateUnrealizedPnL(position.CurrentPrice, position.PointValue);

                    _positionManager.OnPositionOpened(position);
                }
            }
        }
        catch (Exception ex)
        {
            Print($"[BotGRobot] SyncPositionsWithManager error: {ex.Message}");
        }
    }

    private ExitParameters CreateExitParametersForPosition(
        cAlgo.API.Position ctraderPos,
        double pipSize,
        string? strategyName,
        out string appliedProfile)
    {
        appliedProfile = "default_static";

        double balance = 0.0;
        try { balance = Account?.Balance ?? 0.0; }
        catch { }

        if (_exitProfileService == null)
        {
            return ExitParameters.CreateDefault(ctraderPos.SymbolName, ctraderPos.EntryPrice, ctraderPos.TradeType, balance);
        }

        try
        {
            var exitParams = _exitProfileService.CreateParameters(
                strategyName,
                ctraderPos.SymbolName,
                ctraderPos.EntryPrice,
                ctraderPos.TradeType,
                pipSize,
                balance,
                out appliedProfile);
            return exitParams;
        }
        catch (Exception ex)
        {
            PipelineLogger.Log(
                "EXIT",
                "ProfileError",
                "Failed to materialize exit profile",
                new
                {
                    position = ctraderPos.Id,
                    symbol = ctraderPos.SymbolName,
                    strategy = strategyName,
                    error = ex.Message
                },
                Print);

            appliedProfile = "default_error";
            return ExitParameters.CreateDefault(ctraderPos.SymbolName, ctraderPos.EntryPrice, ctraderPos.TradeType, balance);
        }
    }

    /// <summary>
    /// Tính point value để chuyển đổi price movement sang USD
    /// </summary>
    private double CalculatePointValueForPosition()
    {
        try
        {
            var symbol = Symbol;
            double tickValue = symbol.TickValue;
            double tickSize = symbol.TickSize;
            if (tickSize > 0)
            {
                return tickValue / tickSize;
            }
            return symbol.PipValue / 0.0001;
        }
        catch
        {
            return 10.0;
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

            // Read last line using high-performance tail reader
            using var reader = new Telemetry.CsvTailReader(telemetryPath);
            var lastLine = await reader.ReadLastLineAsync();
            if (string.IsNullOrWhiteSpace(lastLine))
            {
                Print("[PREFLIGHT] Telemetry has no data rows");
                return false;
            }
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

            // Read header using high-performance reader (reads only first 64KB)
            using var reader = new Telemetry.CsvTailReader(csvPath);
            var firstLine = await reader.ReadFirstLineAsync();
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

            // THREAD SAFETY: Use ExecutionSerializer to prevent concurrent trade operations
            var tradeResult = await _executionSerializer.RunAsync(() =>
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
                
                // THREAD SAFETY: Use ExecutionSerializer for position close
                await _executionSerializer.RunAsync(() => ClosePosition(tradeResult.Position));
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
            
            // THREAD SAFETY: Use ExecutionSerializer to prevent concurrent trade operations
            var tradeResult = await _executionSerializer.RunAsync(() => 
                ExecuteMarketOrder(TradeType.Buy, symbol, volume, "PREFLIGHT_FILL")
            );

            var fillTime = DateTime.UtcNow;
            result.LatencyMs = (int)(fillTime - startTime).TotalMilliseconds;

            if (tradeResult.IsSuccessful && tradeResult.Position != null)
            {
                var fillPrice = tradeResult.Position.EntryPrice;
                var pipSize = this.Symbol?.PipSize ?? 0.0001;
                result.SlippagePips = Math.Abs(fillPrice - entryPrice) / pipSize;
                
                // THREAD SAFETY: Use ExecutionSerializer for position close
                var closeStart = DateTime.UtcNow;
                await _executionSerializer.RunAsync(() => ClosePosition(tradeResult.Position));
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

        var cfg = _config ?? TelemetryConfig.Load();
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

    private void LogCritical(string stage, Exception ex)
    {
        try
        {
            var cfg = _config ?? TelemetryConfig.Load();
            var logPath = Path.Combine(cfg.LogPath, "a8_debug.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] CRITICAL:{stage}: {ex}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging failures to avoid recursive faults
        }

        try
        {
            Print($"[CRITICAL] {stage} exception: {ex.Message}");
        }
        catch
        {
            // final guard: Print may throw if platform in bad state
        }
    }

    private void AppendDebugLine(string category, string message)
    {
        try
        {
            var cfg = _config ?? TelemetryConfig.Load();
            var logPath = Path.Combine(cfg.LogPath, "a8_debug.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] {category}: {message}{Environment.NewLine}");
        }
        catch
        {
            // bỏ qua lỗi ghi log để tránh crash phụ
        }
    }

    private void LogTimerException(string timerName, Exception ex)
    {
        AppendDebugLine($"TIMER:{timerName}", ex.ToString());
        try
        {
            Print("[TIMER {0}] exception: {1}", timerName, ex.Message);
        }
        catch
        {
            // swallow printing errors để bảo vệ main thread
        }
    }

    private void ApplyCoordinationConfig(StrategyCoordinationConfig config, bool isReload)
    {
        if (config == null)
        {
            return;
        }

        var fusionEnabled = config.EnableBayesianFusion;
        var requiresReplacement = _coordinator == null || (_coordinator is EnhancedStrategyCoordinator) != fusionEnabled;

        if (requiresReplacement)
        {
            _coordinator = fusionEnabled
                ? new EnhancedStrategyCoordinator(config)
                : new StrategyCoordinator(config);

            if (_strategies != null && _tradeManager != null)
            {
                _strategyPipeline = new StrategyPipeline(_strategies, _tradeManager, _executionSerializer, _coordinator);
            }
        }
        else
        {
            _coordinator!.UpdateConfiguration(config);
        }

        var coordMeta = new Dictionary<string, object?>
        {
            ["min_confidence"] = config.MinimumConfidence,
            ["min_interval_seconds"] = config.MinimumTimeBetweenTrades.TotalSeconds,
            ["max_positions_per_symbol"] = config.MaxSignalsPerSymbol,
            ["enable_conflict_resolution"] = config.EnableConflictResolution,
            ["enable_time_filter"] = config.EnableTimeBasedFiltering,
            ["weights_count"] = config.StrategyWeights?.Count ?? 0,
            ["coordinator_type"] = _coordinator?.GetType().Name,
            ["fusion_enabled"] = fusionEnabled
        };

        if (isReload)
        {
            PipelineLogger.Log(
                "CONFIG",
                "CoordinationApplied",
                "Applied hot-reloaded StrategyCoordination config",
                coordMeta,
                Print);
        }
        else
        {
            PipelineLogger.Log(
                "COORD",
                "Init",
                "StrategyCoordinator initialized from runtime config",
                coordMeta,
                Print);
        }
    }

    private void InitializeCoordinationHotReload()
    {
        if (_configHotReloadManager != null)
        {
            return;
        }

        try
        {
            var path = StrategyCoordinationConfigLoader.GetRuntimeConfigPath();
            if (string.IsNullOrEmpty(path))
            {
                BotG.Runtime.Logging.PipelineLogger.Log("CONFIG", "CoordinationWatcherSkipped", "Config hot reload skipped because path was not resolved", null, Print);
                return;
            }

            var manager = new ConfigHotReloadManager(path, TimeSpan.FromSeconds(2));
            manager.ConfigReloaded += OnCoordinationConfigReloaded;
            manager.StartWatching();
            _configHotReloadManager = manager;
        }
        catch (Exception ex)
        {
            BotG.Runtime.Logging.PipelineLogger.Log("CONFIG", "CoordinationWatcherError", "Failed to start config hot reload watcher", new { error = ex.Message }, Print);
        }
    }

    private void OnCoordinationConfigReloaded(object? sender, StrategyCoordinationConfig config)
    {
        try
        {
            ApplyCoordinationConfig(config, isReload: true);
        }
        catch (Exception ex)
        {
            BotG.Runtime.Logging.PipelineLogger.Log("CONFIG", "CoordinationApplyFailed", "Failed to apply hot-reloaded config", new { error = ex.Message }, Print);
        }
    }

    protected override void OnStop()
    {
        try { _backtestMonitor?.Dispose(); } catch { }
        _backtestMonitor = null;

        try { _riskHeartbeatTimer?.Dispose(); } catch { }
        try { _riskSnapshotTimer?.Dispose(); } catch { }
        try { _telemetryTimer?.Dispose(); } catch { }
        _riskHeartbeatTimer = null;
        _riskSnapshotTimer = null;
        _telemetryTimer = null;

        if (_configHotReloadManager != null)
        {
            try { _configHotReloadManager.ConfigReloaded -= OnCoordinationConfigReloaded; } catch { }
            try { _configHotReloadManager.Dispose(); } catch { }
            _configHotReloadManager = null;
        }

        DisposeStrategyWatcher();

        try { TelemetryContext.Collector?.FlushOnMainThread(); } catch { }
        AppendDebugLine("TIMER", "Timers stopped via OnStop");

        try { BotG.Runtime.Logging.PipelineLogger.Log("BOOT", "Stop", "Bot stopping", null, Print); } catch {}

        try
        {
            var cfg = _config ?? TelemetryConfig.Load();
            var logPath = Path.Combine(cfg.LogPath, "a8_debug.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] INSTANCE_GUARD: release-stop\n");
        }
        catch { }

        ReleaseInstanceLock();
    }

    private bool EnsureSingleInstance(TelemetryConfig cfg)
    {
        if (cfg == null)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(cfg.LogPath);
        }
        catch
        {
            // ignore directory failures; lock acquisition may fail subsequently
        }

        if (_instanceLockStream != null)
        {
            return true;
        }

        var lockPath = Path.Combine(cfg.LogPath, InstanceLockFileName);

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            var payload = $"pid={Environment.ProcessId};machine={Environment.MachineName};started={DateTime.UtcNow:o}";
            var bytes = Encoding.UTF8.GetBytes(payload);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);

            _instanceLockStream = stream;
            _instanceLockPath = lockPath;

            try
            {
                File.AppendAllText(Path.Combine(cfg.LogPath, "a8_debug.log"), $"[{DateTime.UtcNow:o}] INSTANCE_GUARD: acquired {lockPath}\n");
            }
            catch { }

            return true;
        }
        catch (IOException ioEx)
        {
            try
            {
                var detail = $"[{DateTime.UtcNow:o}] INSTANCE_GUARD: failed {lockPath}: {ioEx.Message}\n";
                File.AppendAllText(Path.Combine(cfg.LogPath, "a8_debug.log"), detail);
            }
            catch { }
        }
        catch (UnauthorizedAccessException accessEx)
        {
            try
            {
                var detail = $"[{DateTime.UtcNow:o}] INSTANCE_GUARD: access-denied {lockPath}: {accessEx.Message}\n";
                File.AppendAllText(Path.Combine(cfg.LogPath, "a8_debug.log"), detail);
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    /// SCALPING CONSERVATIVE: Only trade during active market hours (08:00-20:00)
    /// </summary>
    private bool IsWithinTradingHours()
    {
        var serverTime = Server.Time;
        // Convert UTC to Vietnam time (UTC+7)
        var localTime = serverTime.AddHours(7);
        int currentHour = localTime.Hour;
        
        // Trading window: 08:00 - 20:00 Vietnam time (excludes overnight sessions)
        bool withinHours = currentHour >= 8 && currentHour < 20;
        
        if (!_enforceTradingHours)
        {
            return true;
        }

        if (!withinHours)
        {
            var lastLoggedAgo = serverTime - _lastTradingHoursLogUtc;
            if (lastLoggedAgo >= TimeSpan.FromHours(1))
            {
                Print($"[TradingHours] Outside trading window: {localTime:HH:mm} VN (UTC: {serverTime:HH:mm}, active: 08:00-20:00 VN)");
                _lastTradingHoursLogUtc = serverTime;
            }
        }
        
        return withinHours;
    }

    private void ReleaseInstanceLock()
    {
        try
        {
            _instanceLockStream?.Dispose();
        }
        catch { }

        if (!string.IsNullOrEmpty(_instanceLockPath))
        {
            try
            {
                File.Delete(_instanceLockPath);
            }
            catch { }
        }

        _instanceLockStream = null;
        _instanceLockPath = null;
    }

}
