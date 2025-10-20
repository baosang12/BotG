using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

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
                        var gitHead = Environment.GetEnvironmentVariable("GIT_COMMIT");
                        if (string.IsNullOrWhiteSpace(commit) && !string.IsNullOrWhiteSpace(gitHead)) commit = gitHead;
                    }
                    catch { }

                    var meta = new JsonObject
                    {
                        ["run_id"] = Path.GetFileName(runDir),
                        ["start_time_iso"] = DateTime.UtcNow.ToString("o"),
                        ["host"] = Environment.MachineName,
                        ["git_commit"] = commit,
                        ["mode"] = cfg.Mode,
                        ["hours"] = cfg.Hours,
                        ["seconds_per_hour"] = cfg.SecondsPerHour,
                        ["simulation"] = new JsonObject
                        {
                            ["enabled"] = cfg.UseSimulation,
                            ["fill_probability"] = cfg.Simulation?.FillProbability,
                            ["simulate_partial_fills"] = cfg.Simulation?.SimulatePartialFills
                        },
                        ["config_snapshot"] = new JsonObject
                        {
                            ["simulation"] = new JsonObject
                            {
                                ["enabled"] = cfg.UseSimulation,
                                ["fill_probability"] = cfg.Simulation?.FillProbability,
                                ["simulate_partial_fills"] = cfg.Simulation?.SimulatePartialFills
                            },
                            ["execution"] = new JsonObject
                            {
                                ["fee_per_trade"] = cfg.Execution?.FeePerTrade,
                                ["fee_percent"] = cfg.Execution?.FeePercent,
                                ["spread_pips"] = cfg.Execution?.SpreadPips
                            },
                            ["log_path"] = cfg.LogPath,
                            ["hours"] = cfg.Hours,
                            ["seconds_per_hour"] = cfg.SecondsPerHour,
                            ["drain_seconds"] = cfg.DrainSeconds,
                            ["graceful_shutdown_wait_seconds"] = cfg.GracefulShutdownWaitSeconds
                        },
                        ["log_paths"] = new JsonObject
                        {
                            ["run"] = runDir,
                            ["base_log"] = cfg.LogPath
                        }
                    };

                    if (extra != null)
                    {
                        try
                        {
                            meta["extra"] = JsonNode.Parse(JsonSerializer.Serialize(extra));
                        }
                        catch { }
                    }

                    TelemetryContext.InvokeMetadataHook(meta);
                    File.WriteAllText(metaPath, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                else if (extra != null)
                {
                    // Merge/append the provided extra fields into existing run_metadata.json under the "extra" property
                    try
                    {
                        var json = File.ReadAllText(metaPath);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = new System.Text.Json.Nodes.JsonObject();
                        foreach (var p in doc.RootElement.EnumerateObject())
                        {
                            root[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
                        }
                        // Overwrite or create the "extra" node
                        root["extra"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(extra));
                        TelemetryContext.InvokeMetadataHook(root);
                        File.WriteAllText(metaPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }
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
