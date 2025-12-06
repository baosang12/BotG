#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.Indicators.Interfaces;

namespace AnalysisModule.Preprocessor.Indicators.Common;

public sealed class AtrCalculator : IIndicatorCalculator
{
    private readonly TimeFrame _timeFrame;
    private readonly int _period;

    public AtrCalculator(TimeFrame timeFrame, int period)
    {
        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _timeFrame = timeFrame;
        _period = period;
    }

    public string IndicatorName => $"ATR({_timeFrame},{_period})";

    public bool IsEnabled { get; set; } = true;

    public ValueTask<IndicatorResult> CalculateAsync(PreprocessorContext context, CancellationToken cancellationToken = default)
    {
        var bars = context.Bars(_timeFrame);
        if (bars.Count < _period + 1)
        {
            return ValueTask.FromResult(new IndicatorResult(IndicatorName, null));
        }

        var trueRanges = new double[_period];
        for (var i = bars.Count - _period; i < bars.Count; i++)
        {
            var current = bars[i];
            var prev = bars[i - 1];
            var tr = Math.Max(
                current.High - current.Low,
                Math.Max(
                    Math.Abs(current.High - prev.Close),
                    Math.Abs(current.Low - prev.Close)));
            trueRanges[i - (bars.Count - _period)] = tr;
        }

        var atr = trueRanges.Average();
        return ValueTask.FromResult(new IndicatorResult(IndicatorName, atr));
    }
}
