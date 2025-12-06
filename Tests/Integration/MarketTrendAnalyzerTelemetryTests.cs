#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;
using AnalysisModule.Telemetry;
using BotG.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotG.Tests.Integration
{
    [Collection("PatternLayerDebuggerTests")]
    public sealed class MarketTrendAnalyzerTelemetryTests : IDisposable
    {
        private readonly List<string> _tempDirectories = new();

        [Fact]
        public void MarketTrendAnalyzer_PublishesPatternTelemetrySnapshot()
        {
            var logDir = CreateTempDirectory();
            var config = TestPatternLayerData.CreateTelemetryConfig(enableLogging: true, enableDebug: false, logDirectory: logDir);

            using var telemetry = new TrendAnalysisTelemetry(NullLogger<TrendAnalysisTelemetry>.Instance);
            var bridge = new TestTrendAnalysisBridge();
            var analyzer = new MarketTrendAnalyzer(NullLogger<MarketTrendAnalyzer>.Instance, bridge, telemetry);
            analyzer.Initialize(config);
            InjectPatternLayer(analyzer, config);

            var snapshot = TestPatternLayerData.CreateSnapshot();
            var signal = analyzer.Analyze(snapshot);

            Assert.NotNull(signal);
            Assert.NotNull(bridge.LastPublished);

            var csvFile = WaitForCsvFile(logDir);
            var lines = ReadAllLinesWithRetry(csvFile);
            Assert.True(lines.Length >= 2, "Telemetry CSV phải chứa dữ liệu");
        }

        private void InjectPatternLayer(MarketTrendAnalyzer analyzer, TrendAnalyzerConfig config)
        {
            var detectors = new IPatternDetector[]
            {
                new StubPatternDetector()
            };

            var patternLayer = new PatternLayer(NullLogger.Instance, detectors, baselineScore: 50.0);
            patternLayer.UpdateConfig(config);

            var layersField = typeof(MarketTrendAnalyzer).GetField("_layers", BindingFlags.Instance | BindingFlags.NonPublic);
            var layers = layersField?.GetValue(analyzer) as Dictionary<string, ILayerCalculator>;
            Assert.NotNull(layers);
            layers![patternLayer.LayerName] = patternLayer;

            var patternLayerField = typeof(MarketTrendAnalyzer).GetField("_patternLayer", BindingFlags.Instance | BindingFlags.NonPublic);
            patternLayerField?.SetValue(analyzer, patternLayer);
        }

        private string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), $"MarketTrendTelemetry_{Guid.NewGuid():N}");
            _tempDirectories.Add(path);
            return path;
        }

        private static string WaitForCsvFile(string directory, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (Directory.Exists(directory))
                {
                    var file = Directory.GetFiles(directory, "*.csv").FirstOrDefault();
                    if (!string.IsNullOrEmpty(file))
                    {
                        try
                        {
                            if (new FileInfo(file).Length > 0)
                            {
                                return file;
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                }

                Thread.Sleep(25);
            }

            throw new TimeoutException($"Không tìm thấy file CSV trong {directory}");
        }

        private static string[] ReadAllLinesWithRetry(string filePath, int attempts = 25)
        {
            for (var i = 0; i < attempts; i++)
            {
                try
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var lines = new List<string>();
                    while (!reader.EndOfStream)
                    {
                        lines.Add(reader.ReadLine() ?? string.Empty);
                    }

                    if (lines.Count > 0)
                    {
                        return lines.ToArray();
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                }
            }

            throw new IOException($"Không thể đọc file telemetry {filePath} vì đang bị khoá.");
        }

        public void Dispose()
        {
            foreach (var dir in _tempDirectories)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        private sealed class StubPatternDetector : IPatternDetector
        {
            public string Name => "StubDetector";

            public double Weight { get; set; } = 1.0;

            public bool IsEnabled { get; set; } = true;

            public PatternDetectionResult Detect(SnapshotDataAccessor accessor)
            {
                return new PatternDetectionResult
                {
                    Score = 88.0,
                    Confidence = 0.9,
                    Flags = new List<string> { "LiquidityGrab", "CleanBreakout" },
                    Diagnostics = new Dictionary<string, object>
                    {
                        ["ScoreBreakdown"] = new Dictionary<string, double>
                        {
                            ["Base"] = 0.8,
                            ["Momentum"] = 0.7
                        }
                    }
                };
            }
        }

        private sealed class TestTrendAnalysisBridge : ITrendAnalysisBridge
        {
            public TrendSignal? LastPublished { get; private set; }

            public TrendSignal? GetCurrentTrend()
            {
                return LastPublished;
            }

            public void PublishTrendSignal(TrendSignal signal)
            {
                LastPublished = signal;
            }

            public bool IsTrendAnalysisEnabled => true;

            public DateTime LastTrendUpdateTime => DateTime.UtcNow;
        }
    }
}
