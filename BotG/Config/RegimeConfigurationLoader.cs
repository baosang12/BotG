using System;
using System.IO;
using System.Text.Json;
using BotG.MarketRegime;
using BotG.Runtime.Logging;

namespace BotG.Config
{
    /// <summary>
    /// Loads <see cref="RegimeConfiguration"/> instances from the shared runtime configuration file.
    /// </summary>
    public static class RegimeConfigurationLoader
    {
        public static RegimeConfiguration LoadFromRuntimeConfig()
        {
            var path = StrategyCoordinationConfigLoader.GetRuntimeConfigPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                PipelineLogger.Log("REGIME", "ConfigDefault", "Runtime config missing; using default regime thresholds", new { path }, null);
                return new RegimeConfiguration();
            }

            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(path),
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });

                if (!document.RootElement.TryGetProperty("RegimeConfiguration", out var section))
                {
                    PipelineLogger.Log("REGIME", "ConfigSectionMissing", "RegimeConfiguration section not found; using defaults", new { path }, null);
                    return new RegimeConfiguration();
                }

                var config = JsonSerializer.Deserialize<RegimeConfiguration>(
                                 section.GetRawText(),
                                 new JsonSerializerOptions
                                 {
                                     PropertyNameCaseInsensitive = true,
                                     ReadCommentHandling = JsonCommentHandling.Skip
                                 })
                             ?? new RegimeConfiguration();

                PipelineLogger.Log(
                    "REGIME",
                    "ConfigLoaded",
                    "Loaded regime configuration from runtime file",
                    new
                    {
                        path,
                        config.AdxTrendThreshold,
                        config.AdxRangeThreshold,
                        config.VolatilityThreshold,
                        config.CalmThreshold,
                        config.MinimumRegimeConfidence
                    },
                    null);

                return config;
            }
            catch (Exception ex)
            {
                PipelineLogger.Log(
                    "REGIME",
                    "ConfigLoadFailed",
                    "Failed to load regime configuration; using defaults",
                    new { path, error = ex.Message },
                    null);
                return new RegimeConfiguration();
            }
        }
    }
}
