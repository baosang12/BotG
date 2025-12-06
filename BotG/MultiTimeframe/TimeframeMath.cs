using System;
using System.Collections.Generic;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.MultiTimeframe
{
    internal static class TimeframeMath
    {
        private static readonly IReadOnlyDictionary<ModelTimeFrame, TimeSpan> Durations = new Dictionary<ModelTimeFrame, TimeSpan>
        {
            [ModelTimeFrame.M1] = TimeSpan.FromMinutes(1),
            [ModelTimeFrame.M5] = TimeSpan.FromMinutes(5),
            [ModelTimeFrame.M15] = TimeSpan.FromMinutes(15),
            [ModelTimeFrame.M30] = TimeSpan.FromMinutes(30),
            [ModelTimeFrame.H1] = TimeSpan.FromHours(1),
            [ModelTimeFrame.H4] = TimeSpan.FromHours(4),
            [ModelTimeFrame.D1] = TimeSpan.FromDays(1),
            [ModelTimeFrame.W1] = TimeSpan.FromDays(7),
            [ModelTimeFrame.MN1] = TimeSpan.FromDays(30)
        };

        private static readonly ModelTimeFrame[] DefaultStack = new[]
        {
            ModelTimeFrame.H4,
            ModelTimeFrame.H1,
            ModelTimeFrame.M15
        };

        public static IReadOnlyList<ModelTimeFrame> DefaultTimeframes => DefaultStack;

        public static TimeSpan GetDuration(ModelTimeFrame timeframe)
        {
            if (!Durations.TryGetValue(timeframe, out var duration))
            {
                throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported timeframe.");
            }

            return duration;
        }

        public static DateTime GetCloseTime(ModelBar bar)
        {
            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            return bar.OpenTime + GetDuration(bar.Tf);
        }
    }
}
