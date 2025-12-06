using System;
using System.IO;
using AnalysisModule.Preprocessor.Config;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.Config
{
    public sealed class TrendAnalyzerConfigLoaderTests : IDisposable
    {
        private readonly string _tempFolder;

        public TrendAnalyzerConfigLoaderTests()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), "TrendAnalyzerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempFolder);
        }

        [Fact]
        public void Load_ReturnsDefaults_WhenFileMissing()
        {
            var path = Path.Combine(_tempFolder, "missing.json");
            var loader = new TrendAnalyzerConfigLoader(path);

            var config = loader.Load();

            Assert.NotNull(config);
            Assert.True(config.FeatureFlags.UseStructureLayer);
            Assert.Equal(0.40, config.LayerWeights.Structure, 2);
        }

        [Fact]
        public void Load_NormalizesWeights_WhenSumNotOne()
        {
            var path = CreateConfigFile(@"{
  ""LayerWeights"": {
    ""Structure"": 2.0,
    ""MovingAverages"": 2.0,
    ""Momentum"": 0.0,
    ""Patterns"": 0.0
  },
  ""TimeframeWeights"": {
    ""Daily"": 0.5,
    ""H4"": 0.5,
    ""H1"": 0.5,
    ""M15"": 0.5
  }
}");

            var loader = new TrendAnalyzerConfigLoader(path);
            var config = loader.Load();

            var layerSum = config.LayerWeights.Structure + config.LayerWeights.MovingAverages + config.LayerWeights.Momentum + config.LayerWeights.Patterns;
            var timeframeSum = config.TimeframeWeights.Daily + config.TimeframeWeights.H4 + config.TimeframeWeights.H1 + config.TimeframeWeights.M15;

            Assert.InRange(layerSum, 0.99, 1.01);
            Assert.InRange(timeframeSum, 0.99, 1.01);
        }

        [Fact]
        public void Load_UsesFileValues_WhenValid()
        {
            var path = CreateConfigFile(@"{
  ""FeatureFlags"": {
    ""UseMomentumLayer"": true
  },
  ""Thresholds"": {
    ""Bullish"": 60.0
  }
}");

            var loader = new TrendAnalyzerConfigLoader(path);
            var config = loader.Load();

            Assert.True(config.FeatureFlags.UseMomentumLayer);
            Assert.Equal(60.0, config.Thresholds.Bullish, 3);
        }

        [Fact]
        public void Load_PatternLayerOverrides_AreApplied()
        {
            var path = CreateConfigFile(@"{
    ""PatternLayer"": {
        ""Liquidity"": {
            ""Enabled"": false,
            ""Weight"": 0.15
        },
        ""BreakoutQuality"": {
            ""Enabled"": true,
            ""Weight"": 0.25,
            ""Parameters"": {
                ""Scoring"": {
                    ""FailurePenalty"": 35.0
                }
            }
        }
    }
}");

            var loader = new TrendAnalyzerConfigLoader(path);
            var config = loader.Load();

            Assert.False(config.PatternLayer.Liquidity.Enabled);
            Assert.Equal(0.15, config.PatternLayer.Liquidity.Weight, 3);
            Assert.True(config.PatternLayer.BreakoutQuality.Enabled);
            Assert.Equal(0.25, config.PatternLayer.BreakoutQuality.Weight, 3);
            Assert.Equal(35.0, config.PatternLayer.BreakoutQuality.Parameters.Scoring.FailurePenalty, 3);
        }

        [Fact]
        public void Load_InitializesPatternLayerDefaults()
        {
            var path = CreateConfigFile("{}");
            var loader = new TrendAnalyzerConfigLoader(path);

            var config = loader.Load();

            Assert.NotNull(config.PatternLayer);
            Assert.True(config.PatternLayer.Liquidity.Enabled);
            Assert.NotNull(config.PatternLayer.BreakoutQuality.Parameters);
            Assert.Equal(0.2, config.PatternLayer.BreakoutQuality.Weight, 3);
        }

        private string CreateConfigFile(string json)
        {
            var path = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempFolder))
                {
                    Directory.Delete(_tempFolder, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }
}
