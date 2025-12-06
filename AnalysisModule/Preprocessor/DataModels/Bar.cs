#nullable enable
using System;

namespace AnalysisModule.Preprocessor.DataModels;

public sealed class Bar
{
    public Bar(
        DateTime openTimeUtc,
        double open,
        double high,
        double low,
        double close,
        long volume,
        TimeFrame timeFrame)
    {
        OpenTimeUtc = openTimeUtc;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        TimeFrame = timeFrame;
    }

    public DateTime OpenTimeUtc { get; }

    public double Open { get; }

    public double High { get; }

    public double Low { get; }

    public double Close { get; }

    public long Volume { get; }

    public TimeFrame TimeFrame { get; }
}
