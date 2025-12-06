using System;
using System.Collections.Generic;
using System.IO;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using Xunit;

namespace BotG.Tests.Integration
{
    [Collection("PatternLayerDebuggerTests")]
    public sealed class PatternLayerDebuggerIntegrationTests : IDisposable
    {
        private readonly TextWriter _originalConsole;

        public PatternLayerDebuggerIntegrationTests()
        {
            _originalConsole = Console.Out;
            Console.SetOut(TextWriter.Null);
            PatternLayerDebugger.Initialize(false);
        }

        [Fact]
        public void PatternLayerDebugger_WithConfig_InitializesCorrectly()
        {
            var config = new TrendAnalyzerConfig
            {
                PatternTelemetry = new PatternLayerTelemetryConfig
                {
                    EnableDebugMode = true,
                    DebugSampleRate = 3,
                    DebugMinScoreThreshold = 40.0,
                    DebugIncludeDetectorDetails = true,
                    DebugIncludeRawMetrics = false
                }
            };

            PatternLayerDebugger.Initialize(
                config.PatternTelemetry.EnableDebugMode,
                config.PatternTelemetry.DebugSampleRate,
                config.PatternTelemetry.DebugMinScoreThreshold,
                config.PatternTelemetry.DebugIncludeDetectorDetails,
                config.PatternTelemetry.DebugIncludeRawMetrics);

            var status = PatternLayerDebugger.GetStatus();
            Assert.True(status.Enabled);
            Assert.Equal(3, status.SampleRate);
        }

        [Fact]
        public void PatternLayerDebugger_LogsCompleteWorkflow()
        {
            PatternLayerDebugger.Initialize(true, sampleRate: 1, minScoreThreshold: 0.0);

            var timestamp = DateTime.UtcNow;
            var metrics = new Dictionary<string, double>
            {
                { "WickRatio", 0.75 },
                { "BreakoutStrength", 0.8 }
            };

            var scores = new Dictionary<string, double>
            {
                { "WickRejectionScore", 0.85 },
                { "BreakoutQualityScore", 0.72 }
            };

            var flags = new List<string> { "WickRejection", "StrongBreakout" };

            PatternLayerDebugger.LogAnalysisStart("EURUSD", "M5", timestamp);
            PatternLayerDebugger.LogDetectorAnalysis("LiquidityAnalyzer", metrics, scores, flags, 78.5);
            PatternLayerDebugger.LogLiquidityAnalysis(0.75, 0.3, true, false, 0.85);
            PatternLayerDebugger.LogBreakoutAnalysis(0.8, 0.7, 0.6, true, true, false);
            PatternLayerDebugger.LogPatternLayerResult(78.5, 85.0, 72.0, flags, TimeSpan.TicksPerMillisecond * 1, 0.8);

            var status = PatternLayerDebugger.GetStatus();
            Assert.True(status.Counter >= 1);
        }

        public void Dispose()
        {
            PatternLayerDebugger.Initialize(false);
            Console.SetOut(_originalConsole);
        }
    }
}
