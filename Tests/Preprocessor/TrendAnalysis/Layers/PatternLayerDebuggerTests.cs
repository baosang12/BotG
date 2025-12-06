using System;
using System.Collections.Generic;
using System.IO;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using Xunit;

namespace BotG.Tests.Preprocessor.TrendAnalysis.Layers
{
    [Collection("PatternLayerDebuggerTests")]
    public sealed class PatternLayerDebuggerTests : IDisposable
    {
        private readonly StringWriter _consoleWriter;
        private readonly TextWriter _originalConsole;

        public PatternLayerDebuggerTests()
        {
            _originalConsole = Console.Out;
            _consoleWriter = new StringWriter();
            Console.SetOut(_consoleWriter);
            PatternLayerDebugger.Initialize(false);
        }

        [Fact]
        public void Initialize_WhenEnabled_WritesBanner()
        {
            PatternLayerDebugger.Initialize(true, sampleRate: 5, minScoreThreshold: 50.0);

            var status = PatternLayerDebugger.GetStatus();
            Assert.True(status.Enabled);
            Assert.Equal(5, status.SampleRate);

            var output = GetOutput();
            Assert.Contains("PatternLayer Debugger ENABLED", output);
            Assert.Contains("Sample rate: 1/5", output);
        }

        [Fact]
        public void LogAnalysisStart_WhenDisabled_DoesNotOutput()
        {
            PatternLayerDebugger.Initialize(false);

            PatternLayerDebugger.LogAnalysisStart("EURUSD", "M5", DateTime.UtcNow);

            Assert.DoesNotContain("PATTERN LAYER ANALYSIS", GetOutput());
        }

        [Fact]
        public void LogAnalysisStart_WhenEnabled_OutputsHeading()
        {
            var timestamp = new DateTime(2024, 1, 15, 14, 30, 0, DateTimeKind.Utc);
            PatternLayerDebugger.Initialize(true, sampleRate: 1);

            PatternLayerDebugger.LogAnalysisStart("GBPUSD", "H1", timestamp);

            var output = GetOutput();
            Assert.Contains("PATTERN LAYER ANALYSIS - GBPUSD H1", output);
            Assert.Contains("2024-01-15 14:30:00.000", output);
        }

        [Fact]
        public void LogDetectorAnalysis_RespectsSampleRate()
        {
            PatternLayerDebugger.Initialize(true, sampleRate: 2);
            var metrics = new Dictionary<string, double> { { "MetricA", 1.0 } };
            var scores = new Dictionary<string, double> { { "ScoreA", 0.8 } };
            var flags = new List<string> { "FlagA" };

            PatternLayerDebugger.LogAnalysisStart("EURUSD", "M5", DateTime.UtcNow);
            PatternLayerDebugger.LogDetectorAnalysis("TestDetector", metrics, scores, flags, 75.0);

            PatternLayerDebugger.LogAnalysisStart("EURUSD", "M5", DateTime.UtcNow);
            PatternLayerDebugger.LogDetectorAnalysis("TestDetector", metrics, scores, flags, 75.0);

            var output = GetOutput();
            Assert.Equal(1, CountOccurrences(output, "TESTDETECTOR"));
        }

        [Fact]
        public void LogDetectorAnalysis_RespectsMinScoreThreshold()
        {
            PatternLayerDebugger.Initialize(true, sampleRate: 1, minScoreThreshold: 60.0);
            var metrics = new Dictionary<string, double> { { "Metric", 1.0 } };
            var scores = new Dictionary<string, double> { { "Score", 0.5 } };

            PatternLayerDebugger.LogAnalysisStart("EURUSD", "M5", DateTime.UtcNow);
            PatternLayerDebugger.LogDetectorAnalysis("TestDetector", metrics, scores, null, 45.0);

            Assert.DoesNotContain("TESTDETECTOR", GetOutput());
        }

        [Fact]
        public void LogPatternLayerResult_PrintsSummary()
        {
            PatternLayerDebugger.Initialize(true, sampleRate: 1, minScoreThreshold: 0.0);
            var flags = new List<string> { "LiquidityGrab", "CleanBreakout" };

            PatternLayerDebugger.LogAnalysisStart("EURUSD", "M5", DateTime.UtcNow);
            PatternLayerDebugger.LogPatternLayerResult(85.5, 90.0, 80.0, flags, TimeSpan.TicksPerMillisecond * 2, 0.75);

            var output = GetOutput();
            Assert.Contains("PATTERN LAYER SUMMARY", output);
            Assert.Contains("Pattern Score: 85.50/100", output);
            Assert.Contains("Liquidity Score: 90.00/100", output);
            Assert.Contains("Breakout Score: 80.00/100", output);
            Assert.Contains("Overall Confidence: 0.75", output);
            Assert.Contains("📌 LiquidityGrab", output);
            Assert.Contains("🚀 CleanBreakout", output);
            Assert.Contains("Performance: 2.000 ms", output);
        }

        [Fact]
        public void LogWarning_WhenEnabled_WritesMessage()
        {
            PatternLayerDebugger.Initialize(true);

            PatternLayerDebugger.LogWarning("LiquidityAnalyzer", "Test warning message");

            var output = GetOutput();
            Assert.Contains("[LiquidityAnalyzer] Test warning message", output);
        }

        [Fact]
        public void LogError_WhenEnabled_WritesErrorAndException()
        {
            PatternLayerDebugger.Initialize(true);
            var exception = new InvalidOperationException("Test exception");

            PatternLayerDebugger.LogError("BreakoutEvaluator", "Something went wrong", exception);

            var output = GetOutput();
            Assert.Contains("[BreakoutEvaluator] ERROR: Something went wrong", output);
            Assert.Contains("InvalidOperationException - Test exception", output);
        }

        public void Dispose()
        {
            PatternLayerDebugger.Initialize(false);
            Console.SetOut(_originalConsole);
            _consoleWriter.Dispose();
        }

        private string GetOutput()
        {
            return _consoleWriter.ToString();
        }

        private static int CountOccurrences(string text, string pattern)
        {
            var count = 0;
            var index = 0;
            while (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(pattern))
            {
                var next = text.IndexOf(pattern, index, StringComparison.Ordinal);
                if (next < 0)
                {
                    break;
                }

                count++;
                index = next + pattern.Length;
            }

            return count;
        }
    }

    [CollectionDefinition("PatternLayerDebuggerTests", DisableParallelization = true)]
    public sealed class PatternLayerDebuggerCollectionDefinition
    {
    }
}
