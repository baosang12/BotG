using System;
using System.Collections.Generic;
using System.IO;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;

namespace BotG.Tests.TestHelpers
{
    /// <summary>
    /// Tập hợp tiện ích tạo dữ liệu giả phục vụ kiểm thử PatternLayer telemetry.
    /// </summary>
    public static class TestPatternLayerData
    {
        public static TrendAnalyzerConfig CreateTelemetryConfig(
            bool enableLogging = true,
            bool enableDebug = false,
            string logDirectory = null,
            int sampleRate = 1)
        {
            logDirectory ??= Path.Combine(Path.GetTempPath(), $"PatternLayerTelemetry_{Guid.NewGuid():N}");

            return new TrendAnalyzerConfig
            {
                FeatureFlags = new FeatureFlagsConfig
                {
                    UsePatternLayer = true,
                    EnableTelemetry = true,
                    UseStructureLayer = false,
                    UseMALayer = false,
                    UseMomentumLayer = false
                },
                LayerWeights = new LayerWeightsConfig
                {
                    Structure = 0.0,
                    MovingAverages = 0.0,
                    Momentum = 0.0,
                    Patterns = 1.0
                },
                PatternTelemetry = new PatternLayerTelemetryConfig
                {
                    EnablePatternLogging = enableLogging,
                    LogDirectory = logDirectory,
                    SampleRate = Math.Max(1, sampleRate),
                    EnableConsoleOutput = enableDebug,
                    LogProcessingTime = true,
                    MinScoreThreshold = 0.0,
                    MaxScoreThreshold = 100.0,
                    EnableDebugMode = enableDebug,
                    DebugSampleRate = 1,
                    DebugMinScoreThreshold = 0.0,
                    DebugIncludeDetectorDetails = true,
                    DebugIncludeRawMetrics = false
                }
            };
        }

        public static PreprocessorSnapshot CreateSnapshot(
            string symbol = "EURUSD",
            TimeFrame timeframe = TimeFrame.M15)
        {
            var indicators = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["trend.bias.m15"] = 0.65,
                ["trend.bias.h1"] = 0.42,
                [$"EMA_20_{timeframe}"] = 1.0850,
                [$"EMA_50_{timeframe}"] = 1.0830,
                [$"ATR_14_{timeframe}"] = 0.0015,
                [$"VWAP_{symbol}_{timeframe}"] = 1.0842
            };

            var latestBar = new Bar(
                DateTime.UtcNow.AddMinutes(-1),
                open: 1.0845,
                high: 1.0862,
                low: 1.0838,
                close: 1.0857,
                volume: 1500,
                timeFrame: timeframe);

            var latestBars = new Dictionary<TimeFrame, Bar>
            {
                [timeframe] = latestBar
            };

            return new PreprocessorSnapshot(
                TimestampUtc: DateTime.UtcNow,
                Indicators: indicators,
                LatestBars: latestBars,
                Account: null);
        }

        public static PatternLayerTelemetrySnapshot CreateTelemetrySnapshot(
            double patternScore = 82.0,
            double liquidityScore = 88.0,
            double breakoutScore = 76.0,
            double accumulationScore = 64.0,
            double accumulationConfidence = 0.58,
            string phase = "Accumulation",
            double marketStructureScore = 71.0,
            string marketStructureState = "Uptrend",
            int marketStructureTrend = 1,
            bool marketStructureBreakDetected = true,
            int marketStructureSwingPoints = 6,
            double lastSwingHigh = 1.0975,
            double lastSwingLow = 1.0830)
        {
            var detectorScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Liquidity"] = liquidityScore,
                ["BreakoutQuality"] = breakoutScore,
                ["AccumulationDistribution"] = accumulationScore,
                ["MarketStructure"] = marketStructureScore
            };

            var detectorFlags = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Liquidity"] = new[] { "LiquidityGrab" },
                ["BreakoutQuality"] = new[] { "CleanBreakout" },
                ["AccumulationDistribution"] = new[] { phase },
                ["MarketStructure"] = new[] { marketStructureState }
            };

            var detectorDiagnostics = new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Liquidity"] = new Dictionary<string, object>
                {
                    ["ScoreBreakdown"] = new Dictionary<string, double>
                    {
                        ["ImbalanceDepth"] = 0.72,
                        ["WickRatio"] = 0.81
                    },
                    ["PeakVolume"] = 1.4
                },
                ["BreakoutQuality"] = new Dictionary<string, object>
                {
                    ["ScoreBreakdown"] = new Dictionary<string, double>
                    {
                        ["StructureBreak"] = 0.78,
                        ["FollowThrough"] = 0.69
                    }
                },
                ["AccumulationDistribution"] = new Dictionary<string, object>
                {
                    ["Phase"] = phase,
                    ["VolumeRatio"] = 0.92
                },
                ["MarketStructure"] = new Dictionary<string, object>
                {
                    ["Structure"] = marketStructureState,
                    ["TrendDirection"] = marketStructureTrend,
                    ["SwingPoints"] = marketStructureSwingPoints,
                    ["BreakDetected"] = marketStructureBreakDetected,
                    ["LastSwingHigh"] = lastSwingHigh,
                    ["LastSwingLow"] = lastSwingLow
                }
            };

            var patternFlags = new[] { "LiquidityGrab", "CleanBreakout" };
            var accumulationFlags = new[] { "Accumulation", "RangeCompression" };

            return new PatternLayerTelemetrySnapshot(
                timestampUtc: DateTime.UtcNow,
                finalScore: patternScore,
                detectorScores: detectorScores,
                detectorFlags: detectorFlags,
                detectorDiagnostics: detectorDiagnostics,
                patternFlags: patternFlags,
                accumulationScore: accumulationScore,
                accumulationFlags: accumulationFlags,
                accumulationConfidence: accumulationConfidence,
                marketPhase: phase,
                marketStructureScore: marketStructureScore,
                marketStructureState: marketStructureState,
                marketStructureTrendDirection: marketStructureTrend,
                marketStructureBreakDetected: marketStructureBreakDetected,
                marketStructureSwingPoints: marketStructureSwingPoints,
                lastSwingHigh: lastSwingHigh,
                lastSwingLow: lastSwingLow);
        }
    }
}
