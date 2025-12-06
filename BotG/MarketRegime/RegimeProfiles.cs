using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BotG.Config;
using BotG.Runtime.Logging;

namespace BotG.MarketRegime
{
    /// <summary>
    /// Describes the profile for a market regime, including preferred strategies and risk guidance.
    /// Profiles can be loaded from runtime configuration to align with production tuning.
    /// </summary>
    public sealed class RegimeProfile
    {
        private static readonly IReadOnlyList<string> EmptyList = Array.Empty<string>();

        public RegimeProfile(
            RegimeType regime,
            IEnumerable<string>? strategyTags,
            IEnumerable<string>? preferredStrategies,
            double riskMultiplier,
            double minimumConfidence,
            string? displayName,
            string? recommendation,
            bool allowFallbackToAll)
        {
            Regime = regime;
            StrategyTags = (strategyTags ?? EmptyList).Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            PreferredStrategies = (preferredStrategies ?? EmptyList).Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RiskMultiplier = riskMultiplier <= 0 ? 1.0 : riskMultiplier;
            MinimumConfidence = minimumConfidence is >= 0.05 and <= 0.99 ? minimumConfidence : 0.6;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? regime.ToString() : displayName.Trim();
            Recommendation = string.IsNullOrWhiteSpace(recommendation) ? null : recommendation.Trim();
            AllowFallbackToAllStrategies = allowFallbackToAll;
        }

        public RegimeType Regime { get; }

        public IReadOnlyList<string> StrategyTags { get; }

        public IReadOnlyList<string> PreferredStrategies { get; }

        public double RiskMultiplier { get; }

        public double MinimumConfidence { get; }

        public string DisplayName { get; }

        public string? Recommendation { get; }

        public bool AllowFallbackToAllStrategies { get; }

        public static RegimeProfile CreateDefault(RegimeType regime)
        {
            return regime switch
            {
                RegimeType.Trending => new RegimeProfile(
                    regime,
                    new[] { "trend", "ema", "sma", "momentum", "macd" },
                    new[] { "SmaCrossoverStrategy", "EmaCrossoverStrategy", "TrendFollowingStrategy", "MomentumStrategy" },
                    riskMultiplier: 1.0,
                    minimumConfidence: 0.65,
                    displayName: "Trending Market",
                    recommendation: "Trend-following strategies recommended",
                    allowFallbackToAll: false),
                RegimeType.Ranging => new RegimeProfile(
                    regime,
                    new[] { "range", "rsi", "stochastic", "mean" },
                    new[] { "RsiReversionStrategy", "StochasticStrategy", "RangeTradingStrategy", "MeanReversionStrategy" },
                    riskMultiplier: 0.95,
                    minimumConfidence: 0.6,
                    displayName: "Ranging Market",
                    recommendation: "Mean-reversion strategies preferred",
                    allowFallbackToAll: false),
                RegimeType.Volatile => new RegimeProfile(
                    regime,
                    new[] { "breakout", "volatility", "atr", "bollinger" },
                    new[] { "BreakoutStrategy", "VolatilityStrategy", "AtrExpansionStrategy", "BollingerBreakoutStrategy" },
                    riskMultiplier: 0.5,
                    minimumConfidence: 0.7,
                    displayName: "High Volatility",
                    recommendation: "Caution: reduce position sizes",
                    allowFallbackToAll: false),
                RegimeType.Calm => new RegimeProfile(
                    regime,
                    new[] { "scalp", "scalping", "vwap", "arbitrage", "marketmaking" },
                    new[] { "ScalpingStrategy", "VwapStrategy", "MarketMakingStrategy" },
                    riskMultiplier: 1.2,
                    minimumConfidence: 0.55,
                    displayName: "Calm Market",
                    recommendation: "Scalping and breakout anticipation strategies applicable",
                    allowFallbackToAll: false),
                _ => new RegimeProfile(
                    RegimeType.Uncertain,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    riskMultiplier: 0.7,
                    minimumConfidence: 0.5,
                    displayName: "Uncertain Conditions",
                    recommendation: "Conservative approach recommended",
                    allowFallbackToAll: true)
            };
        }
    }

    /// <summary>
    /// Central registry responsible for providing regime profiles, optionally backed by runtime configuration.
    /// </summary>
    public static class RegimeProfileRegistry
    {
        private static readonly Lazy<IReadOnlyDictionary<RegimeType, RegimeProfile>> ProfilesLazy = new(BuildProfiles, isThreadSafe: true);

        public static RegimeProfile GetProfile(RegimeType regime)
        {
            var profiles = ProfilesLazy.Value;
            if (profiles.TryGetValue(regime, out var profile))
            {
                return profile;
            }

            return RegimeProfile.CreateDefault(regime);
        }

        public static IReadOnlyList<string> GetRecommendedStrategies(RegimeType regime)
        {
            return GetProfile(regime).PreferredStrategies;
        }

        public static IReadOnlyList<string> GetStrategyTags(RegimeType regime)
        {
            return GetProfile(regime).StrategyTags;
        }

        public static bool IsStrategyCompatible(RegimeType regime, string? strategyName)
        {
            if (string.IsNullOrWhiteSpace(strategyName))
            {
                return false;
            }

            var profile = GetProfile(regime);
            if (profile.AllowFallbackToAllStrategies)
            {
                return true;
            }

            if (profile.PreferredStrategies.Any(s => string.Equals(s, strategyName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return profile.StrategyTags.Any(tag => strategyName.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IReadOnlyDictionary<RegimeType, RegimeProfile> BuildProfiles()
        {
            var dictionary = new Dictionary<RegimeType, RegimeProfile>();

            if (TryLoadFromRuntimeConfig(out var configured))
            {
                foreach (var kvp in configured)
                {
                    dictionary[kvp.Key] = kvp.Value;
                }
            }

            // Ensure every regime has at least a default profile
            foreach (RegimeType regime in Enum.GetValues(typeof(RegimeType)))
            {
                if (!dictionary.ContainsKey(regime))
                {
                    dictionary[regime] = RegimeProfile.CreateDefault(regime);
                }
            }

            return dictionary;
        }

        private static bool TryLoadFromRuntimeConfig(out Dictionary<RegimeType, RegimeProfile> profiles)
        {
            profiles = new Dictionary<RegimeType, RegimeProfile>();
            try
            {
                var path = StrategyCoordinationConfigLoader.GetRuntimeConfigPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return false;
                }

                var json = File.ReadAllText(path);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!document.RootElement.TryGetProperty("MarketRegimeProfiles", out var section))
                {
                    return false;
                }

                foreach (var property in section.EnumerateObject())
                {
                    if (!Enum.TryParse<RegimeType>(property.Name, ignoreCase: true, out var regime))
                    {
                        continue;
                    }

                    var profile = ParseProfile(regime, property.Value);
                    profiles[regime] = profile;
                }

                return profiles.Count > 0;
            }
            catch (Exception ex)
            {
                PipelineLogger.Log("REGIME", "ProfileLoadFailed", "Failed to load regime profiles from runtime config", new { error = ex.Message }, null);
                profiles.Clear();
                return false;
            }
        }

        private static RegimeProfile ParseProfile(RegimeType regime, JsonElement element)
        {
            var defaults = RegimeProfile.CreateDefault(regime);
            var tags = ReadStringArray(element, "StrategyTags");
            var strategies = ReadStringArray(element, "PreferredStrategies");
            var riskMultiplier = element.TryGetProperty("RiskMultiplier", out var riskProperty) && riskProperty.TryGetDouble(out var risk)
                ? risk
                : defaults.RiskMultiplier;
            var minConfidence = element.TryGetProperty("MinimumConfidence", out var confidenceProperty) && confidenceProperty.TryGetDouble(out var confidence)
                ? confidence
                : defaults.MinimumConfidence;
            string? displayName = element.TryGetProperty("DisplayName", out var displayProperty) && displayProperty.ValueKind == JsonValueKind.String
                ? displayProperty.GetString()
                : null;
            string? recommendation = element.TryGetProperty("Recommendation", out var recommendationProperty) && recommendationProperty.ValueKind == JsonValueKind.String
                ? recommendationProperty.GetString()
                : null;
            bool allowFallback = defaults.AllowFallbackToAllStrategies;
            if (element.TryGetProperty("AllowFallbackToAll", out var fallbackProperty))
            {
                if (fallbackProperty.ValueKind == JsonValueKind.True)
                {
                    allowFallback = true;
                }
                else if (fallbackProperty.ValueKind == JsonValueKind.False)
                {
                    allowFallback = false;
                }
            }

            return new RegimeProfile(regime, tags, strategies, riskMultiplier, minConfidence, displayName, recommendation, allowFallback);
        }

        private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        list.Add(value.Trim());
                    }
                }
            }

            return list.Count == 0 ? Array.Empty<string>() : list;
        }
    }
}
