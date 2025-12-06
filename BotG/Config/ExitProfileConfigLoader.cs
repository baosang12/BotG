using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotG.PositionManagement;
using BotG.Runtime.Logging;

namespace BotG.Config
{
    /// <summary>
    /// Loads the ExitProfiles section from config.runtime.json with validation and fallbacks.
    /// </summary>
    public static class ExitProfileConfigLoader
    {
        private const string DefaultFileName = "config.runtime.json";
        private const string EnvConfigPath = "BOTG_RUNTIME_CONFIG_PATH";

        public static ExitProfilesConfig LoadFromRuntimeConfig()
        {
            if (TryLoadConfig(out var config, out var errors, out var path))
            {
                PipelineLogger.Log(
                    "CONFIG",
                    "ExitProfilesLoaded",
                    "Exit profile configuration loaded",
                    new Dictionary<string, object?>
                    {
                        ["path"] = path,
                        ["profile_count"] = config.Profiles.Count,
                        ["default_profile"] = config.DefaultProfile
                    },
                    null);
                return config;
            }

            var fallback = BuildFallback();
            PipelineLogger.Log(
                "CONFIG",
                "ExitProfilesFallback",
                "Using fallback exit profile configuration",
                new Dictionary<string, object?>
                {
                    ["errors"] = errors,
                    ["profile_count"] = fallback.Profiles.Count,
                    ["default_profile"] = fallback.DefaultProfile
                },
                null);
            return fallback;
        }

        private static bool TryLoadConfig(out ExitProfilesConfig config, out List<string> errors, out string? path)
        {
            config = BuildFallback();
            errors = new List<string>();
            path = ResolveConfigPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                errors.Add("config.runtime.json not found");
                return false;
            }

            if (!TryLoadFromPath(path!, out config, out errors))
            {
                return false;
            }

            return true;
        }

        private static bool TryLoadFromPath(string path, out ExitProfilesConfig config, out List<string> errors)
        {
            config = BuildFallback();
            errors = new List<string>();

            try
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                var runtime = JsonSerializer.Deserialize<RuntimeConfig>(json, options);
                if (runtime?.ExitProfiles == null)
                {
                    errors.Add("ExitProfiles section missing from runtime config.");
                    return false;
                }

                Normalize(runtime.ExitProfiles);
                if (!TryValidate(runtime.ExitProfiles, out errors))
                {
                    return false;
                }

                config = runtime.ExitProfiles;
                return true;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return false;
            }
        }

        private static void Normalize(ExitProfilesConfig config)
        {
            config.EnsureDefaults();

            config.Profiles = NormalizeDictionary(config.Profiles);
            config.StrategyOverrides = NormalizeDictionary(config.StrategyOverrides);
            config.SymbolOverrides = NormalizeDictionary(config.SymbolOverrides);
        }

        private static Dictionary<string, T> NormalizeDictionary<T>(Dictionary<string, T> source)
        {
            var normalized = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return normalized;
            }

            foreach (var kvp in source)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                {
                    continue;
                }

                normalized[kvp.Key.Trim()] = kvp.Value;
            }

            return normalized;
        }

        private static bool TryValidate(ExitProfilesConfig config, out List<string> errors)
        {
            errors = new List<string>();

            if (config.Profiles.Count == 0)
            {
                errors.Add("At least one exit profile must be defined.");
            }

            if (!config.Profiles.ContainsKey(config.DefaultProfile))
            {
                errors.Add($"Default profile '{config.DefaultProfile}' is not defined.");
            }

            foreach (var (profileName, profile) in config.Profiles)
            {
                if (!HasRiskConfiguration(profile))
                {
                    errors.Add($"Profile '{profileName}' must define StopLossPips or InitialRiskPips greater than zero.");
                }

                if (profile.StopLossPips.HasValue && profile.StopLossPips.Value <= 0)
                {
                    errors.Add($"Profile '{profileName}' StopLossPips must be positive.");
                }

                if (profile.TakeProfitPips.HasValue && profile.TakeProfitPips.Value <= 0)
                {
                    errors.Add($"Profile '{profileName}' TakeProfitPips must be positive when specified.");
                }

                if (profile.TrailingStopPips.HasValue && profile.TrailingStopPips.Value <= 0)
                {
                    errors.Add($"Profile '{profileName}' TrailingStopPips must be positive when specified.");
                }

                if (profile.BreakevenTriggerRMultiple.HasValue && profile.BreakevenTriggerRMultiple.Value <= 0)
                {
                    errors.Add($"Profile '{profileName}' BreakevenTriggerRMultiple must be positive when specified.");
                }

                if (profile.TrailingLevels != null)
                {
                    foreach (var level in profile.TrailingLevels)
                    {
                        if (level.TriggerR <= 0 || level.StopOffsetR <= 0)
                        {
                            errors.Add($"Profile '{profileName}' has invalid trailing level (TriggerR={level.TriggerR}, StopOffsetR={level.StopOffsetR}).");
                        }
                    }
                }
            }

            foreach (var (strategy, profileName) in config.StrategyOverrides)
            {
                if (!config.Profiles.ContainsKey(profileName))
                {
                    errors.Add($"Strategy override '{strategy}' references unknown profile '{profileName}'.");
                }
            }

            foreach (var (symbol, profileName) in config.SymbolOverrides)
            {
                if (!config.Profiles.ContainsKey(profileName))
                {
                    errors.Add($"Symbol override '{symbol}' references unknown profile '{profileName}'.");
                }
            }

            return errors.Count == 0;
        }

        private static bool HasRiskConfiguration(ExitProfileDefinition profile)
        {
            double? stop = profile?.StopLossPips;
            double? risk = profile?.InitialRiskPips;
            return (stop.HasValue && stop.Value > 0) || (risk.HasValue && risk.Value > 0);
        }

        private static ExitProfilesConfig BuildFallback()
        {
            return new ExitProfilesConfig
            {
                DefaultProfile = "scalping_conservative",
                Profiles = new Dictionary<string, ExitProfileDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scalping_conservative"] = new ExitProfileDefinition
                    {
                        StopLossPips = 15,
                        TakeProfitPips = 30,
                        InitialRiskPips = 15,
                        RiskRewardRatio = 2.0,
                        BreakevenTriggerRMultiple = 0.75,
                        BreakevenFeePips = 0.0,
                        MultiLevelTrailingEnabled = true,
                        TrailingLevels = new List<TrailingLevel>
                        {
                            new TrailingLevel { TriggerR = 1.0, StopOffsetR = 0.5 },
                            new TrailingLevel { TriggerR = 1.5, StopOffsetR = 1.0 },
                            new TrailingLevel { TriggerR = 2.0, StopOffsetR = 1.5 }
                        },
                        TrailingDynamicTriggerR = 2.0,
                        TrailingDynamicOffsetR = 0.5,
                        TrailingHysteresisR = 0.05,
                        TrailingCooldownSeconds = 1.0
                    }
                },
                StrategyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                SymbolOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static string? ResolveConfigPath()
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
                // ignored
            }

            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir))
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
                // ignored
            }

            var hardcoded = new[]
            {
                @"D:\\botg\\config.runtime.json",
                @"D:\\botg\\config\\config.runtime.json",
                @"D:\\botg\\logs\\config.runtime.json"
            };

            foreach (var candidate in hardcoded)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return null;
        }

        private sealed class RuntimeConfig
        {
            [JsonPropertyName("ExitProfiles")]
            public ExitProfilesConfig? ExitProfiles { get; set; }
        }
    }
}
