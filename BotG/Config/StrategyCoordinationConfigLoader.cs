using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotG.Runtime.Logging;
using BotG.Strategies.Coordination;

namespace BotG.Config
{
    /// <summary>
    /// Loads <see cref="StrategyCoordinationConfig"/> instances from the runtime configuration file.
    /// </summary>
    public static class StrategyCoordinationConfigLoader
    {
        private const string DefaultFileName = "config.runtime.json";
        private const string EnvConfigPath = "BOTG_RUNTIME_CONFIG_PATH";

        /// <summary>
        /// Attempts to load the coordination config from the runtime configuration JSON.
        /// Falls back to defaults when the file is missing or invalid.
        /// </summary>
        public static StrategyCoordinationConfig LoadFromRuntimeConfig()
        {
            StrategyCoordinationConfig? loadedConfig = null;
            List<string>? loadErrors = null;

            try
            {
                var path = GetRuntimeConfigPath();
                PipelineLogger.Log(
                    "CONFIG",
                    "CoordinationAttempt",
                    "Attempting to load strategy coordination config",
                    new { path },
                    null);

                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (!File.Exists(path))
                    {
                        PipelineLogger.Log("CONFIG", "CoordinationFileNotFound", "Runtime config path resolved but file missing", new { path }, null);
                    }
                    else if (TryLoadConfigFromPath(path, out loadedConfig, out var errors))
                    {
                        PipelineLogger.Log(
                            "CONFIG",
                            "CoordinationLoaded",
                            "Loaded coordination config from runtime file",
                            new Dictionary<string, object?>
                            {
                                ["path"] = path,
                                ["min_confidence"] = loadedConfig.MinimumConfidence,
                                ["min_interval_seconds"] = loadedConfig.MinimumTimeBetweenTrades.TotalSeconds,
                                ["max_positions_per_symbol"] = loadedConfig.MaxSignalsPerSymbol,
                                ["enable_conflict_resolution"] = loadedConfig.EnableConflictResolution,
                                ["enable_time_filter"] = loadedConfig.EnableTimeBasedFiltering
                            },
                            null);
                        return loadedConfig;
                    }
                    else
                    {
                        loadErrors = errors;
                        PipelineLogger.Log(
                            "CONFIG",
                            "CoordinationValidationFailed",
                            "Runtime config found but validation failed; using defaults",
                            new { path, errors },
                            null);
                    }
                }
                else
                {
                    PipelineLogger.Log("CONFIG", "CoordinationPathUnresolved", "Unable to resolve runtime config path; using defaults", null, null);
                }
            }
            catch (Exception ex)
            {
                PipelineLogger.Log("CONFIG", "CoordinationLoadException", "Unexpected error loading runtime config", new { error = ex.Message }, null);
            }

            var fallback = BuildValidationDefaults();
            PipelineLogger.Log(
                "CONFIG",
                "CoordinationValidationDefaults",
                "Using validation fallback configuration",
                new Dictionary<string, object?>
                {
                    ["min_confidence"] = fallback.MinimumConfidence,
                    ["min_interval_seconds"] = fallback.MinimumTimeBetweenTrades.TotalSeconds,
                    ["errors"] = loadErrors
                },
                null);
            return fallback;
        }

        public static StrategyCoordinationConfig? SafeReloadConfig()
        {
            var path = GetRuntimeConfigPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                PipelineLogger.Log("CONFIG", "CoordinationReloadSkipped", "Hot reload skipped because config file is missing", new { path }, null);
                return null;
            }

            if (TryLoadConfigFromPath(path, out var config, out var errors))
            {
                PipelineLogger.Log(
                    "CONFIG",
                    "CoordinationReloaded",
                    "Runtime coordination config hot-reloaded",
                    new Dictionary<string, object?>
                    {
                        ["min_confidence"] = config.MinimumConfidence,
                        ["min_interval_seconds"] = config.MinimumTimeBetweenTrades.TotalSeconds,
                        ["max_positions_per_symbol"] = config.MaxSignalsPerSymbol,
                        ["weights_count"] = config.StrategyWeights?.Count ?? 0
                    },
                    null);
                return config;
            }

            PipelineLogger.Log(
                "CONFIG",
                "CoordinationReloadRejected",
                "Hot reload rejected due to validation errors",
                new { errors },
                null);

            return null;
        }

        public static bool TryValidateConfig(StrategyCoordinationConfig config, out List<string> errors)
        {
            errors = new List<string>();

            if (config.MinimumTimeBetweenTrades <= TimeSpan.Zero)
            {
                errors.Add("MinimumTimeBetweenTrades must be greater than zero.");
            }

            if (config.MinimumConfidence is < 0.0 or > 1.0)
            {
                errors.Add("MinimumConfidence must be between 0.0 and 1.0.");
            }

            if (config.CooldownPenalty < 0)
            {
                errors.Add("CooldownPenalty must be non-negative.");
            }

            if (config.MaxSignalsPerSymbol < 1)
            {
                errors.Add("MaxSignalsPerSymbol must be at least 1.");
            }

            if (config.MaxSignalsPerTick < 1)
            {
                errors.Add("MaxSignalsPerTick must be at least 1.");
            }

            if (config.StrategyWeights != null)
            {
                foreach (var kvp in config.StrategyWeights)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                    {
                        errors.Add("Strategy weight keys must be non-empty.");
                        continue;
                    }

                    if (kvp.Value <= 0)
                    {
                        errors.Add($"Strategy weight for '{kvp.Key}' must be positive.");
                    }
                }
            }

            return errors.Count == 0;
        }

        public static string? GetRuntimeConfigPath()
        {
            return ResolveConfigPathInternal();
        }

        private static bool TryLoadConfigFromPath(string path, out StrategyCoordinationConfig config, out List<string> errors)
        {
            config = new StrategyCoordinationConfig();
            errors = new List<string>();

            try
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };
                options.Converters.Add(new JsonStringEnumConverter());

                var runtime = JsonSerializer.Deserialize<RuntimeConfig>(json, options);
                if (runtime?.StrategyCoordination == null)
                {
                    errors.Add("StrategyCoordination section missing from runtime config.");
                    return false;
                }

                if (!TryValidateConfig(runtime.StrategyCoordination, out var validationErrors))
                {
                    errors = validationErrors;
                    return false;
                }

                config = runtime.StrategyCoordination;
                NormalizeStrategyWeights(config);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return false;
            }
        }

        private static string? ResolveConfigPathInternal()
        {
            try
            {
                var envPath = Environment.GetEnvironmentVariable(EnvConfigPath);
                if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
                {
                    return envPath;
                }
            }
            catch
            {
                // ignore env path retrieval issues
            }

            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                var candidate = Path.Combine(baseDir, DefaultFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            try
            {
                var currentDir = Environment.CurrentDirectory;
                var candidate = Path.Combine(currentDir, DefaultFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore current directory issues
            }

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(appData))
                {
                    var candidate = Path.Combine(appData, "BotG", DefaultFileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // ignore local app data issues
            }

            var hardcodedPaths = new[]
            {
                @"D:\botg\config.runtime.json",
                @"D:\botg\config\config.runtime.json",
                @"D:\botg\logs\config.runtime.json",
                Path.Combine(AppContext.BaseDirectory ?? string.Empty, "config", DefaultFileName)
            };

            foreach (var candidate in hardcodedPaths)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // ignore path issues
                }
            }

            return null;
        }

        private static StrategyCoordinationConfig BuildValidationDefaults()
        {
            return new StrategyCoordinationConfig
            {
                MinimumConfidenceThreshold = 0.08,
                MinimumTradeInterval = TimeSpan.FromMinutes(10),
                CooldownPenalty = 0.01,
                EnableTimeBasedFiltering = true,
                StrategyWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SmaCrossoverStrategy"] = 2.0,
                    ["RsiReversalStrategy"] = 2.0,
                    ["BreakoutStrategy"] = 1.5,
                    ["PriceActionStrategy"] = 1.5
                }
            };
        }

        private sealed class RuntimeConfig
        {
            [JsonPropertyName("StrategyCoordination")]
            public StrategyCoordinationConfig? StrategyCoordination { get; set; }
        }

        private static void NormalizeStrategyWeights(StrategyCoordinationConfig config)
        {
            if (config.StrategyWeights == null || config.StrategyWeights.Count == 0)
            {
                return;
            }

            var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in config.StrategyWeights)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                {
                    continue;
                }

                normalized[kvp.Key.Trim()] = kvp.Value;
            }

            config.StrategyWeights.Clear();
            foreach (var kvp in normalized)
            {
                config.StrategyWeights[kvp.Key] = kvp.Value;
            }
        }
    }
}
