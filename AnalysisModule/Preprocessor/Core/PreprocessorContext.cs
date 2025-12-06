#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.Core;

/// <summary>
/// Dữ liệu đầu vào được gom lại để truyền vào từng indicator calculator.
/// </summary>
public sealed class PreprocessorContext
{
    private static readonly IReadOnlyList<Bar> EmptyBars = Array.Empty<Bar>();
    private readonly IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> _barsByTimeFrame;

    public PreprocessorContext(
        IReadOnlyList<Tick> recentTicks,
        IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> barsByTimeFrame,
        DateTime timestampUtc,
        IReadOnlyDictionary<string, object> metadata)
    {
        RecentTicks = recentTicks ?? throw new ArgumentNullException(nameof(recentTicks));
        _barsByTimeFrame = barsByTimeFrame ?? throw new ArgumentNullException(nameof(barsByTimeFrame));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        TimestampUtc = timestampUtc;
    }

    public IReadOnlyList<Tick> RecentTicks { get; }

    public DateTime TimestampUtc { get; }

    public IReadOnlyDictionary<string, object> Metadata { get; }

    public IReadOnlyList<Bar> Bars(TimeFrame timeFrame)
    {
        if (_barsByTimeFrame.TryGetValue(timeFrame, out var bars) && bars is not null)
        {
            return bars;
        }

        return EmptyBars;
    }

    public IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> BarsByTimeFrame => _barsByTimeFrame;
}
