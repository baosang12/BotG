using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;
using BotG.Runtime.Preprocessor;
using Xunit;

namespace BotG.Tests.Runtime.Preprocessor
{
    public sealed class PreprocessorStrategyDataBridgeTests
    {
        [Fact]
        public void GetTrendSignal_ReturnsNull_WhenNoSignalPublished()
        {
            var bridge = CreateBridge();

            var signal = bridge.GetTrendSignal();

            Assert.Null(signal);
        }

        [Fact]
        public void PublishTrendSignal_ExposesSignal_WhenValid()
        {
            var clock = new TestClock(DateTime.UtcNow);
            var bridge = CreateBridge(utcNowProvider: clock.GetUtcNow);
            var signal = CreateTrendSignal(clock.Now);

            bridge.PublishTrendSignal(signal);

            Assert.Same(signal, bridge.GetTrendSignal());
            Assert.Equal(clock.Now, bridge.LastTrendUpdateTime);
        }

        [Fact]
        public void GetTrendSignal_ReturnsNull_AfterExpiration()
        {
            var clock = new TestClock(DateTime.UtcNow);
            var bridge = CreateBridge(utcNowProvider: clock.GetUtcNow);
            var signal = CreateTrendSignal(clock.Now);

            bridge.PublishTrendSignal(signal);
            clock.Advance(TimeSpan.FromSeconds(31));

            var expired = bridge.GetTrendSignal();

            Assert.Null(expired);
            Assert.Equal(DateTime.MinValue, bridge.LastTrendUpdateTime);
        }

        [Fact]
        public void PublishTrendSignal_Ignored_WhenFeatureDisabled()
        {
            var clock = new TestClock(DateTime.UtcNow);
            var bridge = CreateBridge(isTrendEnabled: false, utcNowProvider: clock.GetUtcNow);
            var signal = CreateTrendSignal(clock.Now);

            bridge.PublishTrendSignal(signal);

            Assert.Null(bridge.GetTrendSignal());
        }

        [Fact]
        public void PublishTrendSignal_IsThreadSafe_UnderParallelAccess()
        {
            var bridge = CreateBridge();

            var exception = Record.Exception(() =>
            {
                Parallel.For(0, 250, i =>
                {
                    var signal = CreateTrendSignal(DateTime.UtcNow.AddMilliseconds(i));
                    bridge.PublishTrendSignal(signal);
                    _ = bridge.GetTrendSignal();
                });
            });

            Assert.Null(exception);
            Assert.NotEqual(DateTime.MinValue, bridge.LastTrendUpdateTime);
        }

        [Fact]
        public void GetIndicator_StillWorks_AfterTrendExtension()
        {
            var indicators = new Dictionary<string, double> { ["ema_h1"] = 123.45 };
            var snapshot = new PreprocessorSnapshot(
                DateTime.UtcNow,
                indicators,
                new Dictionary<TimeFrame, Bar>(),
                null);

            var bridge = new PreprocessorStrategyDataBridge(() => snapshot, () => null);

            var value = bridge.GetIndicator("ema_h1");

            Assert.Equal(123.45, value);
        }

        private static PreprocessorStrategyDataBridge CreateBridge(
            Func<PreprocessorSnapshot> snapshotFactory = null,
            bool isTrendEnabled = true,
            TimeSpan? ttl = null,
            Func<DateTime> utcNowProvider = null)
        {
            snapshotFactory ??= () => new PreprocessorSnapshot(
                DateTime.UtcNow,
                new Dictionary<string, double>(),
                new Dictionary<TimeFrame, Bar>(),
                null);

            return new PreprocessorStrategyDataBridge(
                snapshotFactory,
                () => null,
                null,
                () => isTrendEnabled,
                ttl,
                utcNowProvider ?? (() => DateTime.UtcNow));
        }

        private static TrendSignal CreateTrendSignal(DateTime generatedAtUtc)
        {
            return new TrendSignal
            {
                Direction = TrendDirection.Bullish,
                Strength = TrendStrength.Strong,
                Score = 80,
                Confidence = 0.9,
                StructureScore = 82,
                MovingAverageScore = 78,
                MomentumScore = 75,
                PatternScore = 70,
                Confirmations = new[] { "structure", "ma", "momentum" },
                Warnings = Array.Empty<string>(),
                PrimaryTimeFrame = TimeFrame.H1,
                TimeFrameScores = new Dictionary<TimeFrame, double> { [TimeFrame.H1] = 80 },
                GeneratedAtUtc = generatedAtUtc,
                Version = "test"
            };
        }

        private sealed class TestClock
        {
            private DateTime _now;

            public TestClock(DateTime seed)
            {
                _now = DateTime.SpecifyKind(seed, DateTimeKind.Utc);
            }

            public DateTime Now => _now;

            public DateTime GetUtcNow() => _now;

            public void Advance(TimeSpan delta)
            {
                _now = _now.Add(delta);
            }
        }
    }
}
