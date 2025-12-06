using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AnalysisModule.Preprocessor.Config
{
    /// <summary>
    /// Đọc TrendAnalyzerConfig từ JSON với cơ chế fallback và chuẩn hóa trọng số.
    /// </summary>
    public sealed class TrendAnalyzerConfigLoader
    {
        private const double WeightTolerance = 0.01;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly string _configPath;
        private readonly ILogger? _logger;

        public TrendAnalyzerConfigLoader(string configPath, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new ArgumentException("Config path must be provided", nameof(configPath));
            }

            _configPath = configPath;
            _logger = logger;
        }

        public TrendAnalyzerConfig Load()
        {
            TrendAnalyzerConfig config;

            if (!File.Exists(_configPath))
            {
                _logger?.LogWarning("TrendAnalyzer config file '{Path}' not found. Using defaults.", _configPath);
                config = new TrendAnalyzerConfig();
            }
            else
            {
                config = ReadConfig() ?? new TrendAnalyzerConfig();
            }

            EnsureSubConfigs(config);
            ValidateAndNormalize(config);
            return config;
        }

        private TrendAnalyzerConfig? ReadConfig()
        {
            try
            {
                using var stream = File.OpenRead(_configPath);
                var loaded = JsonSerializer.Deserialize<TrendAnalyzerConfig>(stream, JsonOptions);
                if (loaded != null)
                {
                    return loaded;
                }
                _logger?.LogWarning("TrendAnalyzer config file '{Path}' is empty or invalid. Using defaults.", _configPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load TrendAnalyzerConfig from '{Path}'. Using defaults.", _configPath);
            }

            return null;
        }

        private static void EnsureSubConfigs(TrendAnalyzerConfig config)
        {
            config.FeatureFlags ??= new FeatureFlagsConfig();
            config.LayerWeights ??= new LayerWeightsConfig();
            config.TimeframeWeights ??= new TimeframeWeightsConfig();
            config.Hysteresis ??= new HysteresisConfig();
            config.Thresholds ??= new ThresholdsConfig();
            config.PatternLayer ??= new PatternLayerConfig();
            config.PatternLayer.EnsureDefaults();
        }

        private void ValidateAndNormalize(TrendAnalyzerConfig config)
        {
            NormalizeLayerWeights(config.LayerWeights, nameof(config.LayerWeights));
            NormalizeTimeframeWeights(config.TimeframeWeights, nameof(config.TimeframeWeights));
        }

        private void NormalizeLayerWeights(LayerWeightsConfig weights, string name)
        {
            var sum = weights.Structure + weights.MovingAverages + weights.Momentum + weights.Patterns;
            if (Math.Abs(sum - 1.0) <= WeightTolerance)
            {
                return;
            }

            if (sum <= 0)
            {
                _logger?.LogWarning("{Label} total weights <= 0. Reverting to defaults.", name);
                var defaults = new LayerWeightsConfig();
                weights.Structure = defaults.Structure;
                weights.MovingAverages = defaults.MovingAverages;
                weights.Momentum = defaults.Momentum;
                weights.Patterns = defaults.Patterns;
                return;
            }

            _logger?.LogWarning("{Label} total weights ({Sum:F3}) outside tolerance ±{Tolerance}. Normalizing to 1.0.", name, sum, WeightTolerance);
            weights.Structure /= sum;
            weights.MovingAverages /= sum;
            weights.Momentum /= sum;
            weights.Patterns /= sum;
        }

        private void NormalizeTimeframeWeights(TimeframeWeightsConfig weights, string name)
        {
            var sum = weights.Daily + weights.H4 + weights.H1 + weights.M15;
            if (Math.Abs(sum - 1.0) <= WeightTolerance)
            {
                return;
            }

            if (sum <= 0)
            {
                _logger?.LogWarning("{Label} total timeframe weights <= 0. Reverting to defaults.", name);
                var defaults = new TimeframeWeightsConfig();
                weights.Daily = defaults.Daily;
                weights.H4 = defaults.H4;
                weights.H1 = defaults.H1;
                weights.M15 = defaults.M15;
                return;
            }

            _logger?.LogWarning("{Label} total weights ({Sum:F3}) outside tolerance ±{Tolerance}. Normalizing to 1.0.", name, sum, WeightTolerance);
            weights.Daily /= sum;
            weights.H4 /= sum;
            weights.H1 /= sum;
            weights.M15 /= sum;
        }
    }
}
