#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.Indicators.Interfaces;

namespace AnalysisModule.Preprocessor.Indicators.Common;

public sealed class SmaCalculator : IIndicatorCalculator
{
    private readonly TimeFrame _timeFrame;
    private readonly int _period;

    public SmaCalculator(TimeFrame timeFrame, int period)
    {
        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _timeFrame = timeFrame;
        _period = period;
    }

    public string IndicatorName => $"SMA({_timeFrame},{_period})";

    public bool IsEnabled { get; set; } = true;

    public ValueTask<IndicatorResult> CalculateAsync(PreprocessorContext context, CancellationToken cancellationToken = default)
    {
        var bars = context.Bars(_timeFrame);
        if (bars.Count < _period)
        {
            return ValueTask.FromResult(new IndicatorResult(IndicatorName, null));
        }

        var relevant = bars.Skip(bars.Count - _period).Select(b => b.Close);
        var value = relevant.Average();
        return ValueTask.FromResult(new IndicatorResult(IndicatorName, value));
    }
}
