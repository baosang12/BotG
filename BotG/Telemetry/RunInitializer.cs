using System;
using System.IO;
using System.Text.Json;

namespace Telemetry
{
    public static class RunInitializer
    {
        public static string EnsureRunFolderAndMetadata(TelemetryConfig cfg, object? extra = null)
        {
            try
            {
                var runDir = cfg.RunFolder;
                if (string.IsNullOrEmpty(runDir))
                {
                    var runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                    runDir = Path.Combine(cfg.LogPath, "artifacts", $"telemetry_run_{runId}");
                    Directory.CreateDirectory(runDir);
                    cfg.RunFolder = runDir;
                }
                var metaPath = Path.Combine(runDir!, "run_metadata.json");
                if (!File.Exists(metaPath))
                {
                    string commit = Environment.GetEnvironmentVariable("GIT_COMMIT") ?? string.Empty;
                    try
                    {
                        var gitHead = System.Environment.GetEnvironmentVariable("GIT_COMMIT");
                        if (string.IsNullOrWhiteSpace(commit) && !string.IsNullOrWhiteSpace(gitHead)) commit = gitHead;
                    }
                    catch { }
                    var meta = new
                    {
                        run_id = Path.GetFileName(runDir),
                        start_time_iso = DateTime.UtcNow.ToString("o"),
                        host = Environment.MachineName,
                        git_commit = commit,
                        config_snapshot = new
                        {
                            simulation = new
                            {
                                enabled = cfg.UseSimulation,
                                fill_probability = cfg.Simulation?.FillProbability,
                                simulate_partial_fills = cfg.Simulation?.SimulatePartialFills
                            },
                            execution = new { fee_per_trade = cfg.Execution?.FeePerTrade, fee_percent = cfg.Execution?.FeePercent, spread_pips = cfg.Execution?.SpreadPips },
                            log_path = cfg.LogPath,
                            hours = cfg.Hours,
                            seconds_per_hour = cfg.SecondsPerHour,
                            drain_seconds = cfg.DrainSeconds,
                            graceful_shutdown_wait_seconds = cfg.GracefulShutdownWaitSeconds
                        },
                        log_paths = new { run = runDir, base_log = cfg.LogPath },
                        extra
                    };
                    File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
                }
                return runDir!;
            }
            catch
            {
                return cfg.LogPath;
            }
        }
    }
}
