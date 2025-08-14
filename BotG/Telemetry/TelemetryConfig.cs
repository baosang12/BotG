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

        public static string DefaultBasePath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "C:\\botg\\logs"
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
                if (cfgPath != null && File.Exists(cfgPath))
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

                // Ensure directory exists
                Directory.CreateDirectory(cfg.LogPath);
                return cfg;
            }
            catch
            {
                // Fallback defaults
                var cfg = new TelemetryConfig();
                Directory.CreateDirectory(cfg.LogPath);
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
}
