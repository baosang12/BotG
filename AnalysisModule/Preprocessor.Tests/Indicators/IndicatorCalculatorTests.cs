#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.Indicators.Common;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.Indicators;

public sealed class IndicatorCalculatorTests
{
    private static PreprocessorContext BuildContext(TimeFrame timeFrame, params double[] closes)
    {
        var bars = new List<Bar>();
        var start = DateTime.UtcNow.AddMinutes(-closes.Length);
        for (var i = 0; i < closes.Length; i++)
        {
            bars.Add(new Bar(start.AddMinutes(i), closes[i], closes[i], closes[i], closes[i], 1, timeFrame));
        }

        var dict = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
        {
            [timeFrame] = bars
        };

        return new PreprocessorContext(Array.Empty<Tick>(), dict, DateTime.UtcNow, new Dictionary<string, object>());
    }

    [Fact]
    public async Task SmaCalculator_ReturnsAverage()
    {
        var context = BuildContext(TimeFrame.M1, 1, 2, 3, 4, 5);
        var calculator = new SmaCalculator(TimeFrame.M1, 5);

        var result = await calculator.CalculateAsync(context);
        Assert.Equal(3, result.Value);
    }

    [Fact]
    public async Task RsiCalculator_Returns50WhenFlat()
    {
        var context = BuildContext(TimeFrame.M1, 1, 1, 1, 1, 1, 1);
        var calculator = new RsiCalculator(TimeFrame.M1, 5);

        var result = await calculator.CalculateAsync(context);
        Assert.Equal(50, result.Value);
    }

    [Fact]
    public async Task AtrCalculator_ComputesAverageTrueRange()
    {
        var bars = new List<Bar>
        {
            new(DateTime.UtcNow.AddMinutes(-4), 10, 11, 9, 10, 1, TimeFrame.M1),
            new(DateTime.UtcNow.AddMinutes(-3), 10, 12, 10, 11, 1, TimeFrame.M1),
            new(DateTime.UtcNow.AddMinutes(-2), 11, 13, 10, 12, 1, TimeFrame.M1),
            new(DateTime.UtcNow.AddMinutes(-1), 12, 14, 11, 13, 1, TimeFrame.M1),
        };

        var dict = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
        {
            [TimeFrame.M1] = bars
        };

        var context = new PreprocessorContext(Array.Empty<Tick>(), dict, DateTime.UtcNow, new Dictionary<string, object>());
        var calculator = new AtrCalculator(TimeFrame.M1, 3);

        var result = await calculator.CalculateAsync(context);
        Assert.NotNull(result.Value);
        Assert.True(result.Value > 0);
    }
}
