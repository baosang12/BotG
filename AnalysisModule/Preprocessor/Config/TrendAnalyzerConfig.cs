using System;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;

namespace AnalysisModule.Preprocessor.Config
{
    /// <summary>
    /// Cấu hình cho MarketTrendAnalyzer, cho phép bật/tắt layer và hiệu chỉnh ngưỡng.
    /// </summary>
    public sealed class TrendAnalyzerConfig
    {
        public string Version { get; set; } = "1.0";
        public bool Enabled { get; set; } = true;
        public FeatureFlagsConfig FeatureFlags { get; set; } = new();
        public LayerWeightsConfig LayerWeights { get; set; } = new();
        public TimeframeWeightsConfig TimeframeWeights { get; set; } = new();
        public HysteresisConfig Hysteresis { get; set; } = new();
        public ThresholdsConfig Thresholds { get; set; } = new();
        public PatternLayerConfig PatternLayer { get; set; } = new();
        public PatternLayerTelemetryConfig PatternTelemetry { get; set; } = new();
    }

    public sealed class FeatureFlagsConfig
    {
        public bool UseStructureLayer { get; set; } = true;
        public bool UseMALayer { get; set; } = true;
        public bool UseMomentumLayer { get; set; } = false;
        public bool UsePatternLayer { get; set; } = false;
        public bool EnableTelemetry { get; set; } = true;
    }

    public sealed class LayerWeightsConfig
    {
        public double Structure { get; set; } = 0.40;
        public double MovingAverages { get; set; } = 0.30;
        public double Momentum { get; set; } = 0.20;
        public double Patterns { get; set; } = 0.10;

        public void Validate()
        {
            var sum = Structure + MovingAverages + Momentum + Patterns;
            if (sum <= 0)
            {
                throw new InvalidOperationException("Layer weights tổng phải > 0");
            }
        }
    }

    public sealed class TimeframeWeightsConfig
    {
        public double Daily { get; set; } = 0.40;
        public double H4 { get; set; } = 0.30;
        public double H1 { get; set; } = 0.20;
        public double M15 { get; set; } = 0.10;

        public void Validate()
        {
            var sum = Daily + H4 + H1 + M15;
            if (sum <= 0)
            {
                throw new InvalidOperationException("Timeframe weights tổng phải > 0");
            }
        }
    }

    public sealed class HysteresisConfig
    {
        public bool LayerSpecific { get; set; } = true;
        public int RequiredConfirmations { get; set; } = 3;
        public int MaxWhipsawPerHour { get; set; } = 2;
    }

    public sealed class ThresholdsConfig
    {
        public double StrongBullish { get; set; } = 70.0;
        public double Bullish { get; set; } = 55.0;
        public double NeutralBullish { get; set; } = 45.0;
        public double Range { get; set; } = 40.0;
        public double NeutralBearish { get; set; } = 25.0;
        public double Bearish { get; set; } = 10.0;
    }

    public sealed class PatternLayerConfig
    {
        public PatternDetectorConfig Liquidity { get; set; } = PatternDetectorConfig.CreateDefault(weight: 0.3);
        public BreakoutQualityDetectorConfig BreakoutQuality { get; set; } = new();
        public AccumulationDistributionDetectorConfig AccumulationDistribution { get; set; } = new();
        public MarketStructureDetectorConfig MarketStructure { get; set; } = new();
        public VolumeProfileConfig VolumeProfile { get; set; } = new();

        public void EnsureDefaults()
        {
            Liquidity ??= PatternDetectorConfig.CreateDefault(weight: 0.3);
            BreakoutQuality ??= new BreakoutQualityDetectorConfig();
            BreakoutQuality.Parameters ??= BreakoutQualityParameters.CreateDefault();
            AccumulationDistribution ??= new AccumulationDistributionDetectorConfig();
            AccumulationDistribution.Parameters ??= AccumulationDistributionParameters.CreateDefault();
            MarketStructure ??= new MarketStructureDetectorConfig();
            MarketStructure.Parameters ??= MarketStructureParameters.CreateDefault();
            VolumeProfile ??= new VolumeProfileConfig();
            VolumeProfile.Parameters ??= VolumeProfileParameters.CreateDefault();
            VolumeProfile.Parameters.Normalize();
        }
    }

    public class PatternDetectorConfig
    {
        public bool Enabled { get; set; } = true;
        public double Weight { get; set; } = 0.25;

        public static PatternDetectorConfig CreateDefault(double weight)
        {
            return new PatternDetectorConfig
            {
                Weight = Math.Max(0, weight)
            };
        }
    }

    public sealed class BreakoutQualityDetectorConfig : PatternDetectorConfig
    {
        public BreakoutQualityDetectorConfig()
        {
            Weight = 0.2;
        }

        public BreakoutQualityParameters Parameters { get; set; } = BreakoutQualityParameters.CreateDefault();
    }

    public sealed class AccumulationDistributionDetectorConfig : PatternDetectorConfig
    {
        public AccumulationDistributionDetectorConfig()
        {
            Weight = 0.2;
        }

        public AccumulationDistributionParameters Parameters { get; set; } = AccumulationDistributionParameters.CreateDefault();
    }

    public sealed class MarketStructureDetectorConfig : PatternDetectorConfig
    {
        public MarketStructureDetectorConfig()
        {
            Weight = 0.2;
        }

        public MarketStructureParameters Parameters { get; set; } = MarketStructureParameters.CreateDefault();
    }

    public sealed class VolumeProfileConfig : PatternDetectorConfig
    {
        public VolumeProfileConfig()
        {
            Weight = 0.2;
        }

        public VolumeProfileParameters Parameters { get; set; } = VolumeProfileParameters.CreateDefault();
    }

    public sealed class VolumeProfileParameters
    {
        public int LookbackBars { get; set; } = 120;
        public int MinBars { get; set; } = 80;
        public int NumberOfBuckets { get; set; } = 24;
        public double ValueAreaPercentage { get; set; } = 0.70;
        public double HighVolumeThreshold { get; set; } = 1.4;
        public double LowVolumeThreshold { get; set; } = 0.6;
        public string PrimaryTimeFrame { get; set; } = "H1";

        public static VolumeProfileParameters CreateDefault()
        {
            return new VolumeProfileParameters();
        }

        public VolumeProfileParameters Clone()
        {
            return new VolumeProfileParameters
            {
                LookbackBars = LookbackBars,
                MinBars = MinBars,
                NumberOfBuckets = NumberOfBuckets,
                ValueAreaPercentage = ValueAreaPercentage,
                HighVolumeThreshold = HighVolumeThreshold,
                LowVolumeThreshold = LowVolumeThreshold,
                PrimaryTimeFrame = PrimaryTimeFrame
            };
        }

        public void Normalize()
        {
            LookbackBars = Math.Clamp(LookbackBars, 40, 600);
            MinBars = Math.Clamp(MinBars, 10, LookbackBars);
            NumberOfBuckets = Math.Clamp(NumberOfBuckets, 10, 80);
            ValueAreaPercentage = Math.Clamp(ValueAreaPercentage, 0.50, 0.90);
            HighVolumeThreshold = Math.Max(1.1, HighVolumeThreshold);
            LowVolumeThreshold = Math.Clamp(LowVolumeThreshold, 0.1, 0.95);
            if (HighVolumeThreshold <= LowVolumeThreshold)
            {
                HighVolumeThreshold = LowVolumeThreshold + 0.2;
            }

            if (string.IsNullOrWhiteSpace(PrimaryTimeFrame))
            {
                PrimaryTimeFrame = "H1";
            }
        }
    }

    /// <summary>
    /// Cấu hình telemetry/logging cho PatternLayer khi chạy trong môi trường cTrader.
    /// </summary>
    public sealed class PatternLayerTelemetryConfig
    {
        /// <summary>
        /// Bật/tắt toàn bộ logging cho PatternLayer.
        /// </summary>
        public bool EnablePatternLogging { get; set; } = false;

        /// <summary>
        /// Ghi nhận chi tiết điểm số từng detector (ví dụ LiquidityScore).
        /// </summary>
        public bool LogPatternScores { get; set; } = true;

        /// <summary>
        /// Ghi nhận danh sách pattern flags (LiquidityGrab, CleanBreakout...).
        /// </summary>
        public bool LogPatternFlags { get; set; } = true;

        /// <summary>
        /// Ghi nhận thời gian xử lý để theo dõi hiệu năng.
        /// </summary>
        public bool LogProcessingTime { get; set; } = true;

        /// <summary>
        /// Tần suất logging (1 = mọi lần phân tích, 10 = mỗi 10 lần).
        /// </summary>
        public int SampleRate { get; set; } = 1;

        /// <summary>
        /// Thư mục đích ghi file CSV telemetry.
        /// </summary>
        public string LogDirectory { get; set; } = @"d:\botg\logs\patternlayer\";

        /// <summary>
        /// Bật console output trực tiếp trên cTrader để dễ quan sát.
        /// </summary>
        public bool EnableConsoleOutput { get; set; } = false;

        /// <summary>
        /// Ngưỡng điểm tối thiểu để ghi log (mặc định 0 nghĩa là ghi tất cả).
        /// </summary>
        public double MinScoreThreshold { get; set; } = 0.0;

        /// <summary>
        /// Ngưỡng điểm tối đa để ghi log (mặc định 100 nghĩa là ghi tất cả).
        /// </summary>
        public double MaxScoreThreshold { get; set; } = 100.0;

        /// <summary>
        /// Bật chế độ debug chi tiết cho PatternLayer (rất verbose).
        /// </summary>
        public bool EnableDebugMode { get; set; } = false;

        /// <summary>
        /// Tần suất xuất debug (1 = mọi lần, 10 = mỗi 10 lần phân tích).
        /// </summary>
        public int DebugSampleRate { get; set; } = 10;

        /// <summary>
        /// Ngưỡng điểm tối thiểu để in debug (0-100).
        /// </summary>
        public double DebugMinScoreThreshold { get; set; } = 50.0;

        /// <summary>
        /// Bao gồm chi tiết từng detector trong debug output.
        /// </summary>
        public bool DebugIncludeDetectorDetails { get; set; } = true;

        /// <summary>
        /// Bao gồm raw metrics trong debug output (có thể rất dài).
        /// </summary>
        public bool DebugIncludeRawMetrics { get; set; } = false;

        /// <summary>
        /// Đảm bảo cấu hình an toàn, được gọi sau khi deserialize.
        /// </summary>
        public void Normalize()
        {
            if (SampleRate < 1)
            {
                SampleRate = 1;
            }

            if (string.IsNullOrWhiteSpace(LogDirectory))
            {
                LogDirectory = @"d:\botg\logs\patternlayer\";
            }

            MinScoreThreshold = Math.Clamp(MinScoreThreshold, 0.0, 100.0);
            MaxScoreThreshold = Math.Clamp(MaxScoreThreshold, 0.0, 100.0);

            if (MaxScoreThreshold < MinScoreThreshold)
            {
                (MinScoreThreshold, MaxScoreThreshold) = (0.0, 100.0);
            }

            if (DebugSampleRate < 1)
            {
                DebugSampleRate = 1;
            }

            DebugMinScoreThreshold = Math.Clamp(DebugMinScoreThreshold, 0.0, 100.0);

            if (EnableDebugMode && !EnableConsoleOutput)
            {
                EnableConsoleOutput = true;
            }
        }
    }
}
