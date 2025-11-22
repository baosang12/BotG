using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Telemetry
{
    public class TelemetryConfig
    {
        public string Mode { get; set; } = "paper"; // paper|live|sim
        public string LogPath { get; set; } = DefaultBasePath;
        public int FlushIntervalSeconds { get; set; } = 60;
    // Dev quick-run knobs
    public int Hours { get; set; } = 24; // Production default: 24h runs
    public int SecondsPerHour { get; set; } = 3600; // Real-time by default
    public int DrainSeconds { get; set; } = 30; // drain window at shutdown
    public int GracefulShutdownWaitSeconds { get; set; } = 5; // extra wait for OS buffers
    public bool UseSimulation { get; set; } = false; // Paper mode default (no simulation)
        public string OrderLogFile { get; set; } = "orders.csv";
        public string RiskSnapshotFile { get; set; } = "risk_snapshots.csv";
        public string TelemetryFile { get; set; } = "telemetry.csv";
    public SimulationConfig Simulation { get; set; } = new SimulationConfig();
    public PreflightConfig Preflight { get; set; } = new PreflightConfig();
    public ExecutionConfig Execution { get; set; } = new ExecutionConfig();
    public AccountConfig? Account { get; set; }
    public PaperConfig? Paper { get; set; }
    public TradingConfig? Trading { get; set; }
    public OpsConfig Ops { get; set; } = new OpsConfig();
    public DebugConfig Debug { get; set; } = new DebugConfig();
    public StrategyRegistrySettings StrategyRegistry { get; set; } = new StrategyRegistrySettings();

    // Runtime, not from JSON: a per-run artifact folder under LogPath/artifacts/telemetry_run_yyyyMMdd_HHmmss
    public string? RunFolder { get; set; }

    /// <summary>
    /// Get initial equity from config with fallback chain:
    /// account.initial_equity_usd → paper.initial_balance → trading.starting_balance_usd → 10000
    /// </summary>
    public double GetInitialEquity()
    {
        if (Account?.InitialEquityUsd != null && Account.InitialEquityUsd > 0)
            return Account.InitialEquityUsd.Value;
        if (Paper?.InitialBalance != null && Paper.InitialBalance > 0)
            return Paper.InitialBalance.Value;
        if (Trading?.StartingBalanceUsd != null && Trading.StartingBalanceUsd > 0)
            return Trading.StartingBalanceUsd.Value;
        return 10000.0; // fallback default
    }

        public static string DefaultBasePath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "D:\\botg\\logs"
            : "/var/log/botg";

        private static readonly object _cacheLock = new object();
        private static TelemetryConfig? _cachedConfig;
        private static DateTime _cachedAtUtc;
        private static TimeSpan _cacheTtl = TimeSpan.FromSeconds(60);
        private static string? _cachedRunFolder;
        private static readonly string _runFolderLog = Path.Combine(DefaultBasePath, "run_folder_debug.log");

    public static TelemetryConfig Load(string? rootHint = null)
    {
        return LoadCore(rootHint, ensureRunFolder: true, useCache: true);
    }

    public static TelemetryConfig Load(string? rootHint, bool ensureRunFolder, bool useCache)
    {
        return LoadCore(rootHint, ensureRunFolder, useCache);
    }

    public static TelemetryConfig LoadForGuard()
    {
        return LoadCore(null, ensureRunFolder: false, useCache: false);
    }

    private static TelemetryConfig LoadCore(string? rootHint, bool ensureRunFolder, bool useCache)
    {
        try
        {
            if (useCache)
            {
                TelemetryConfig? cached = null;
                lock (_cacheLock)
                {
                    if (_cachedConfig != null && (DateTime.UtcNow - _cachedAtUtc) < _cacheTtl)
                    {
                        cached = _cachedConfig;
                    }
                }

                if (cached != null)
                {
                    if (ensureRunFolder && string.IsNullOrWhiteSpace(cached.RunFolder))
                    {
                        try { EnsureRunFolderCached(cached); } catch { }
                    }

                    return cached;
                }
            }

            string baseDir = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();

            var loadedFiles = new System.Collections.Generic.List<string>();
            var cfg = new TelemetryConfig();

            var configPaths = new[]
            {
                Path.Combine(baseDir, "config.runtime.json"),
                "D:\\botg\\config\\config.runtime.json",
                "D:\\botg\\logs\\config.runtime.json"
            };

            foreach (var path in configPaths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<TelemetryConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(loaded.Mode)) cfg.Mode = loaded.Mode;
                    if (!string.IsNullOrWhiteSpace(loaded.LogPath)) cfg.LogPath = loaded.LogPath;
                    if (loaded.FlushIntervalSeconds > 0) cfg.FlushIntervalSeconds = loaded.FlushIntervalSeconds;
                    if (loaded.Hours > 0) cfg.Hours = loaded.Hours;
                    if (loaded.SecondsPerHour > 0) cfg.SecondsPerHour = loaded.SecondsPerHour;
                    cfg.UseSimulation = loaded.UseSimulation;
                    if (loaded.Simulation != null) cfg.Simulation = loaded.Simulation;
                    if (loaded.Execution != null) cfg.Execution = loaded.Execution;
                    if (loaded.Account != null) cfg.Account = loaded.Account;
                    if (loaded.Paper != null) cfg.Paper = loaded.Paper;
                    if (loaded.Trading != null) cfg.Trading = loaded.Trading;
                    if (loaded.Ops != null) cfg.Ops = loaded.Ops;
                    if (loaded.Debug != null) cfg.Debug = loaded.Debug;
                    if (loaded.StrategyRegistry != null)
                    {
                        cfg.StrategyRegistry ??= new StrategyRegistrySettings();
                        if (!string.IsNullOrWhiteSpace(loaded.StrategyRegistry.ConfigPath))
                        {
                            cfg.StrategyRegistry.ConfigPath = loaded.StrategyRegistry.ConfigPath;
                        }

                        cfg.StrategyRegistry.HotReloadEnabled = loaded.StrategyRegistry.HotReloadEnabled;

                        if (loaded.StrategyRegistry.WatchDebounceSeconds > 0)
                        {
                            cfg.StrategyRegistry.WatchDebounceSeconds = loaded.StrategyRegistry.WatchDebounceSeconds;
                        }
                    }
                    loadedFiles.Add(path);
                }
                catch { }
            }

            var envPath = Environment.GetEnvironmentVariable("BOTG_LOG_PATH");
            var envMode = Environment.GetEnvironmentVariable("BOTG_MODE")
                           ?? Environment.GetEnvironmentVariable("Mode");
            var envFlush = Environment.GetEnvironmentVariable("BOTG_TELEMETRY_FLUSH_SEC");
            var envSimEnabled = Environment.GetEnvironmentVariable("BOTG__Simulation__Enabled")
                                 ?? Environment.GetEnvironmentVariable("Simulation__Enabled");
            var envCanaryEnabled = Environment.GetEnvironmentVariable("PREFLIGHT__Canary__Enabled");

            var envOverrides = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                cfg.LogPath = envPath!;
                envOverrides.Add($"BOTG_LOG_PATH={envPath}");
            }
            if (!string.IsNullOrWhiteSpace(envMode))
            {
                cfg.Mode = envMode!;
                envOverrides.Add($"Mode={envMode}");
            }
            if (int.TryParse(envFlush, out var sec) && sec > 0)
            {
                cfg.FlushIntervalSeconds = sec;
                envOverrides.Add($"FlushIntervalSeconds={sec}");
            }

            bool simFromFile = cfg.Simulation?.Enabled ?? cfg.UseSimulation;
            bool? simFromEnv = null;
            if (!string.IsNullOrWhiteSpace(envSimEnabled) && bool.TryParse(envSimEnabled, out var envSim))
            {
                simFromEnv = envSim;
                envOverrides.Add($"Simulation__Enabled={envSim}");
            }

            bool finalSim;
            if (simFromEnv.HasValue)
            {
                finalSim = simFromEnv.Value;
            }
            else
            {
                string mode = cfg.Mode?.ToLowerInvariant() ?? "paper";
                finalSim = mode == "paper" ? false : simFromFile;
            }

            cfg.UseSimulation = finalSim;
            if (cfg.Simulation == null) cfg.Simulation = new SimulationConfig();
            cfg.Simulation.Enabled = finalSim;

            if (!string.IsNullOrWhiteSpace(envCanaryEnabled) && bool.TryParse(envCanaryEnabled, out var canaryEnabled))
            {
                if (cfg.Preflight == null) cfg.Preflight = new PreflightConfig();
                if (cfg.Preflight.Canary == null) cfg.Preflight.Canary = new CanaryConfig();
                cfg.Preflight.Canary.Enabled = canaryEnabled;
                envOverrides.Add($"Preflight__Canary__Enabled={canaryEnabled}");
            }

            var sourcesLog = loadedFiles.Count > 0
                ? $"Files=[{string.Join(", ", loadedFiles)}]"
                : "Files=[]";
            var envLog = envOverrides.Count > 0
                ? $"ENV=[{string.Join(", ", envOverrides)}]"
                : "ENV=[]";
            Console.WriteLine($"[ECHO+] {sourcesLog}; {envLog}; Mode={cfg.Mode}; Simulation.Enabled={finalSim}");

            try
            {
                Directory.CreateDirectory(cfg.LogPath);
            }
            catch (UnauthorizedAccessException)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotG", "logs");
                    Directory.CreateDirectory(local);
                    cfg.LogPath = local;
                }
                else
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "botg", "logs");
                    Directory.CreateDirectory(tmp);
                    cfg.LogPath = tmp;
                }
            }

            if (ensureRunFolder)
            {
                try { EnsureRunFolderCached(cfg); } catch { }
            }

            if (useCache)
            {
                lock (_cacheLock)
                {
                    _cachedConfig = cfg;
                    _cachedAtUtc = DateTime.UtcNow;
                }
            }

            return cfg;
        }
        catch
        {
            var cfg = new TelemetryConfig();
            try
            {
                Directory.CreateDirectory(cfg.LogPath);
            }
            catch
            {
                try
                {
                    var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotG", "logs");
                    Directory.CreateDirectory(local);
                    cfg.LogPath = local;
                }
                catch { }
            }

            if (ensureRunFolder)
            {
                try { EnsureRunFolderCached(cfg); } catch { }
            }

            if (useCache)
            {
                lock (_cacheLock)
                {
                    _cachedConfig = cfg;
                    _cachedAtUtc = DateTime.UtcNow;
                }
            }

            return cfg;
        }
    }

        private static void EnsureRunFolderCached(TelemetryConfig cfg)
        {
            lock (_cacheLock)
            {
                if (!string.IsNullOrWhiteSpace(cfg.RunFolder))
                {
                    Directory.CreateDirectory(cfg.RunFolder);
                    _cachedRunFolder = cfg.RunFolder;
                    LogRunFolderEvent(cfg, $"reuse-config-runfolder|{cfg.RunFolder}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_cachedRunFolder) && Directory.Exists(_cachedRunFolder))
                {
                    cfg.RunFolder = _cachedRunFolder;
                    LogRunFolderEvent(cfg, $"reuse-cached-runfolder|{_cachedRunFolder}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_cachedRunFolder) && !Directory.Exists(_cachedRunFolder))
                {
                    LogRunFolderEvent(cfg, $"cached-runfolder-missing|{_cachedRunFolder}");
                    _cachedRunFolder = null;
                }

                var runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var runDir = Path.Combine(cfg.LogPath, "artifacts", $"telemetry_run_{runId}");
                Directory.CreateDirectory(runDir);
                cfg.RunFolder = runDir;
                _cachedRunFolder = runDir;
                LogRunFolderEvent(cfg, $"create-runfolder|{runDir}");
            }
        }

        private static void LogRunFolderEvent(TelemetryConfig cfg, string message)
        {
            LogRunFolderEventInternal(message, cfg);
        }

        private static void LogRunFolderEvent(string message)
        {
            LogRunFolderEventInternal(message, null);
        }

        private static void LogRunFolderEventInternal(string message, TelemetryConfig? cfg)
        {
            try
            {
                var utcNow = DateTime.UtcNow.ToString("o");
                var line = $"{utcNow}|{message}{Environment.NewLine}";
                var dir = Path.GetDirectoryName(_runFolderLog);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir!);
                }
                File.AppendAllText(_runFolderLog, line);

                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.LogPath))
                {
                    try
                    {
                        var stack = Environment.StackTrace?.Replace(Environment.NewLine, " => ");
                        var debugPath = Path.Combine(cfg.LogPath!, "a8_debug.log");
                        var debugLine = $"{utcNow}|RUN_FOLDER|{message}|stack={stack}{Environment.NewLine}";
                        File.AppendAllText(debugPath, debugLine);
                    }
                    catch { }
                }
            }
            catch
            {
                // ignore logging errors
            }
        }

        private static string? FindConfigPath(string start)
        {
            try
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 5 && dir != null; i++)
                {
                    var candidate = Path.Combine(dir.FullName, "config.runtime.json");
                    if (File.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }
    }

    public class SimulationConfig
    {
        public bool Enabled { get; set; } = true;
        public double FillProbability { get; set; } = 1.0; // 1.0 for deterministic fills in smoke
        public bool SimulatePartialFills { get; set; } = false;
        // Optional sampling for telemetry to reduce size
        public int TelemetrySampleN { get; set; } = 1; // log every row by default
    }

    public class PreflightConfig
    {
        public CanaryConfig Canary { get; set; } = new CanaryConfig();

        [JsonPropertyName("TTLMinutes")]
        public int TtlMinutes { get; set; } = 10;
    }

    public class CanaryConfig
    {
        public bool Enabled { get; set; } = false; // Default: disabled
        public int WaitExecutorSec { get; set; } = 10; // Wait up to 10s for OrderExecutor readiness
    }

    public class ExecutionConfig
    {
        // Flat fee per closed trade, in account currency
        public double FeePerTrade { get; set; } = 0.0;
        // Proportional fee (commission) as a fraction of notional
        public double FeePercent { get; set; } = 0.0;
        // Spread in pips (will be converted using symbol TickSize if known)
        public double SpreadPips { get; set; } = 0.0;

        [JsonPropertyName("spread_pips_fallback")]
        public double SpreadPipsFallback { get; set; } = 0.0;

        [JsonPropertyName("spread_pips_min")]
        public double SpreadPipsMin { get; set; } = 0.0;

        [JsonPropertyName("min_lot")]
        public double MinLot { get; set; } = 0.0;

        [JsonPropertyName("lot_step")]
        public double LotStep { get; set; } = 0.0;

        [JsonPropertyName("commission_roundturn_usd_per_lot")]
        public double CommissionRoundturnUsdPerLot { get; set; } = 0.0;

        [JsonPropertyName("commission_roundtrip_usd_per_lot")]
        public double CommissionRoundtripUsdPerLot { get; set; } = 0.0;

        [JsonPropertyName("fee_roundturn_usd_per_lot")]
        public double FeeRoundturnUsdPerLot { get; set; } = 0.0;

        [JsonPropertyName("fee_pips_per_roundtrip")]
        public double FeePipsPerRoundtrip { get; set; } = 0.0;

        [JsonPropertyName("fee_pips_per_side")]
        public double FeePipsPerSide { get; set; } = 0.0;

        [JsonPropertyName("swap_long_pips_per_day")]
        public double SwapLongPipsPerDay { get; set; } = 0.0;

        [JsonPropertyName("swap_short_pips_per_day")]
        public double SwapShortPipsPerDay { get; set; } = 0.0;

        [JsonPropertyName("swap_type")]
        public string? SwapType { get; set; }

        [JsonPropertyName("swap_triple_day")]
        public string? SwapTripleDay { get; set; }
    }

    public class AccountConfig
    {
        [JsonPropertyName("initial_equity_usd")]
        public double? InitialEquityUsd { get; set; }
    }

    public class PaperConfig
    {
        [JsonPropertyName("initial_balance")]
        public double? InitialBalance { get; set; }
    }

    public class TradingConfig
    {
        [JsonPropertyName("starting_balance_usd")]
        public double? StartingBalanceUsd { get; set; }
    }

    public class OpsConfig
    {
        [JsonPropertyName("enable_trading")]
        public bool EnableTrading { get; set; } = true; // Default: trading enabled
    }

    public class DebugConfig
    {
        [JsonPropertyName("smoke_once")]
        public bool SmokeOnce { get; set; } = false; // Default: disabled
    }

    public class StrategyRegistrySettings
    {
        public string? ConfigPath { get; set; }
        public bool HotReloadEnabled { get; set; } = true;
        public double WatchDebounceSeconds { get; set; } = 2.0;
    }
}
