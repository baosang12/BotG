using System;

namespace AnalysisModule.Preprocessor.DataModels;

public static class TimeFrameExtensions
{
    public static TimeSpan ToTimeSpan(this TimeFrame timeFrame) => timeFrame switch
    {
        TimeFrame.M1 => TimeSpan.FromMinutes(1),
        TimeFrame.M5 => TimeSpan.FromMinutes(5),
        TimeFrame.M15 => TimeSpan.FromMinutes(15),
        TimeFrame.M30 => TimeSpan.FromMinutes(30),
        TimeFrame.H1 => TimeSpan.FromHours(1),
        TimeFrame.H4 => TimeSpan.FromHours(4),
        TimeFrame.D1 => TimeSpan.FromDays(1),
        TimeFrame.W1 => TimeSpan.FromDays(7),
        TimeFrame.MN1 => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, "Unsupported timeframe"),
    };

    public static DateTime AlignTimestampUtc(this TimeFrame timeFrame, DateTime timestampUtc)
    {
        var duration = timeFrame.ToTimeSpan();
        var ticks = (timestampUtc.Ticks / duration.Ticks) * duration.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }
}
