#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.Indicators.Interfaces;

namespace AnalysisModule.Preprocessor.Indicators.Common;

public sealed class RsiCalculator : IIndicatorCalculator
{
    private readonly TimeFrame _timeFrame;
    private readonly int _period;

    public RsiCalculator(TimeFrame timeFrame, int period)
    {
        if (period < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _timeFrame = timeFrame;
        _period = period;
    }

    public string IndicatorName => $"RSI({_timeFrame},{_period})";

    public bool IsEnabled { get; set; } = true;

    public ValueTask<IndicatorResult> CalculateAsync(PreprocessorContext context, CancellationToken cancellationToken = default)
    {
        var bars = context.Bars(_timeFrame);
        if (bars.Count < _period + 1)
        {
            return ValueTask.FromResult(new IndicatorResult(IndicatorName, null));
        }

        double gains = 0;
        double losses = 0;
        for (var i = bars.Count - _period; i < bars.Count; i++)
        {
            var change = bars[i].Close - bars[i - 1].Close;
            if (change >= 0)
            {
                gains += change;
            }
            else
            {
                losses += -change;
            }
        }

        var avgGain = gains / _period;
        var avgLoss = losses / _period;
        double? value;

        if (avgLoss == 0 && avgGain == 0)
        {
            value = 50d;
        }
        else if (avgLoss == 0)
        {
            value = 100d;
        }
        else
        {
            var rs = avgGain / avgLoss;
            value = 100 - (100 / (1 + rs));
        }

        return ValueTask.FromResult(new IndicatorResult(IndicatorName, value));
    }
}
