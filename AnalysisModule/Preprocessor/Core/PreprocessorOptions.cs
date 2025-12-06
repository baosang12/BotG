#nullable enable
using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.Core;

/// <summary>
/// Chứa cấu hình điều khiển pipeline (timeframe, indicator set, buffer depth...).
/// </summary>
public sealed class PreprocessorOptions
{
    public PreprocessorOptions(
        IReadOnlyCollection<TimeFrame> timeFrames,
        IReadOnlyCollection<string> indicators,
        int maxRecentTicks,
        TimeSpan snapshotDebounce,
        int barHistoryCapacity = 500)
    {
        TimeFrames = timeFrames ?? throw new ArgumentNullException(nameof(timeFrames));
        Indicators = indicators ?? throw new ArgumentNullException(nameof(indicators));
        if (maxRecentTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecentTicks));
        }

        if (snapshotDebounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotDebounce));
        }

        if (barHistoryCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(barHistoryCapacity));
        }

        MaxRecentTicks = maxRecentTicks;
        SnapshotDebounce = snapshotDebounce;
        BarHistoryCapacity = barHistoryCapacity;
    }

    public IReadOnlyCollection<TimeFrame> TimeFrames { get; }

    public IReadOnlyCollection<string> Indicators { get; }

    public int MaxRecentTicks { get; }

    public TimeSpan SnapshotDebounce { get; }

    public int BarHistoryCapacity { get; }
}
