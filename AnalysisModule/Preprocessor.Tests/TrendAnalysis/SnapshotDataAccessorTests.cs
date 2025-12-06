using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis
{
    public sealed class SnapshotDataAccessorTests
    {
        [Fact]
        public void GetBars_ReturnsTailFromHistory()
        {
            var history = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
            {
                [TimeFrame.H1] = new List<Bar>
                {
                    Bar(TimeFrame.H1, 1),
                    Bar(TimeFrame.H1, 2),
                    Bar(TimeFrame.H1, 3),
                    Bar(TimeFrame.H1, 4),
                    Bar(TimeFrame.H1, 5)
                }
            };

            var accessor = new SnapshotDataAccessor(CreateSnapshot(), history);

            var bars = accessor.GetBars(TimeFrame.H1, 3);

            Assert.Equal(3, bars.Count);
            Assert.Equal(3, bars[0].Close);
            Assert.Equal(5, bars[^1].Close);
        }

        [Fact]
        public void GetIndicatorValue_FindsMatchByNameAndTimeframe()
        {
            var indicators = new Dictionary<string, double>
            {
                ["RSI(H1,14)"] = 62.5
            };

            var accessor = new SnapshotDataAccessor(CreateSnapshot(indicators: indicators));

            var value = accessor.GetIndicatorValue("RSI", TimeFrame.H1);

            Assert.Equal(62.5, value);
        }

        [Fact]
        public void GetLatestPrice_FallsBackToLatestBar()
        {
            var latestBars = new Dictionary<TimeFrame, Bar>
            {
                [TimeFrame.H4] = Bar(TimeFrame.H4, 123.45)
            };

            var accessor = new SnapshotDataAccessor(CreateSnapshot(latestBars: latestBars));

            var price = accessor.GetLatestPrice(TimeFrame.H4);

            Assert.Equal(123.45, price);
        }

        [Fact]
        public void GetMultiTimeframeData_ComposesIndicatorsPerTimeframe()
        {
            var indicators = new Dictionary<string, double>
            {
                ["trend.bias.h1"] = 0.75,
                ["trend.bias.h4"] = -0.25
            };

            var history = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
            {
                [TimeFrame.H1] = new List<Bar> { Bar(TimeFrame.H1, 1) },
                [TimeFrame.H4] = new List<Bar> { Bar(TimeFrame.H4, 1) }
            };

            var accessor = new SnapshotDataAccessor(CreateSnapshot(indicators: indicators), history);

            var data = accessor.GetMultiTimeframeData();

            Assert.True(data.ContainsKey(TimeFrame.H1));
            Assert.Single(data[TimeFrame.H1].Indicators);
            Assert.True(data.ContainsKey(TimeFrame.H4));
            Assert.Single(data[TimeFrame.H4].Indicators);
        }

        private static PreprocessorSnapshot CreateSnapshot(
            IReadOnlyDictionary<string, double> indicators = null,
            IReadOnlyDictionary<TimeFrame, Bar> latestBars = null)
        {
            return new PreprocessorSnapshot(
                DateTime.UtcNow,
                indicators ?? new Dictionary<string, double>(),
                latestBars ?? new Dictionary<TimeFrame, Bar>(),
                null);
        }

        private static Bar Bar(TimeFrame timeframe, double close)
        {
            return new Bar(DateTime.UtcNow, close, close, close, close, 0, timeframe);
        }
    }
}
