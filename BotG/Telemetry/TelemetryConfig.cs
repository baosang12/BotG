using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Telemetry
{
    public class TelemetryConfig
    {
        public string Mode { get; set; } = "paper"; // paper|live|sim
        public string LogPath { get; set; } = DefaultBasePath;
        public int FlushIntervalSeconds { get; set; } = 60;
        public string OrderLogFile { get; set; } = "orders.csv";
        public string RiskSnapshotFile { get; set; } = "risk_snapshots.csv";
        public string TelemetryFile { get; set; } = "telemetry.csv";
    public SimulationConfig Simulation { get; set; } = new SimulationConfig();
    public ExecutionConfig Execution { get; set; } = new ExecutionConfig();

    // Runtime, not from JSON: a per-run artifact folder under LogPath/artifacts/telemetry_run_yyyyMMdd_HHmmss
    public string? RunFolder { get; set; }

        public static string DefaultBasePath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "D:\\botg\\logs"
            : "/var/log/botg";

    public static TelemetryConfig Load(string? rootHint = null)
        {
            try
            {
                // Allow override via environment variables
                var envPath = Environment.GetEnvironmentVariable("BOTG_LOG_PATH");
                var envMode = Environment.GetEnvironmentVariable("BOTG_MODE");
                var envFlush = Environment.GetEnvironmentVariable("BOTG_TELEMETRY_FLUSH_SEC");

                string baseDir = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
                string searchRoot = !string.IsNullOrWhiteSpace(rootHint) ? rootHint : baseDir;
                string? cfgPath = FindConfigPath(searchRoot);
                TelemetryConfig cfg = new TelemetryConfig();
                if (!string.IsNullOrEmpty(cfgPath) && File.Exists(cfgPath))
                {
                    var json = File.ReadAllText(cfgPath);
                    var loaded = JsonSerializer.Deserialize<TelemetryConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (loaded != null) cfg = loaded;
                }
                if (!string.IsNullOrWhiteSpace(envPath)) cfg.LogPath = envPath!;
                if (!string.IsNullOrWhiteSpace(envMode)) cfg.Mode = envMode!;
                if (int.TryParse(envFlush, out var sec) && sec > 0) cfg.FlushIntervalSeconds = sec;

                // Ensure directory exists; fallback if access denied
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
                // create a run folder if not already set
                try
                {
                    if (string.IsNullOrEmpty(cfg.RunFolder))
                    {
                        var runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                        var runDir = Path.Combine(cfg.LogPath, "artifacts", $"telemetry_run_{runId}");
                        Directory.CreateDirectory(runDir);
                        cfg.RunFolder = runDir;
                    }
                }
                catch { }
                return cfg;
            }
            catch
            {
                // Fallback defaults
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
                // assign run folder
                try
                {
                    if (string.IsNullOrEmpty(cfg.RunFolder))
                    {
                        var runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                        var runDir = Path.Combine(cfg.LogPath, "artifacts", $"telemetry_run_{runId}");
                        Directory.CreateDirectory(runDir);
                        cfg.RunFolder = runDir;
                    }
                }
                catch { }
                return cfg;
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

    public class ExecutionConfig
    {
        // Flat fee per closed trade, in account currency
        public double FeePerTrade { get; set; } = 0.0;
        // Proportional fee (commission) as a fraction of notional
        public double FeePercent { get; set; } = 0.0;
        // Spread in pips (will be converted using symbol TickSize if known)
        public double SpreadPips { get; set; } = 0.0;
    }
}
