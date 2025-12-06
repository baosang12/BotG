using System;
using System.Collections.Generic;
using System.Linq;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.MultiTimeframe
{
    public sealed class TimeframeSnapshot
    {
        public string Symbol { get; }
        public DateTime TimestampUtc { get; }
        public IReadOnlyList<ModelTimeFrame> OrderedTimeframes { get; }
        public IReadOnlyDictionary<ModelTimeFrame, IReadOnlyList<ModelBar>> BarsByTimeframe { get; }
        public int TotalTimeframes => OrderedTimeframes.Count;

        public TimeframeSnapshot(
            string symbol,
            DateTime timestampUtc,
            IReadOnlyList<ModelTimeFrame> orderedTimeframes,
            IReadOnlyDictionary<ModelTimeFrame, IReadOnlyList<ModelBar>> barsByTimeframe)
        {
            Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            TimestampUtc = timestampUtc;
            OrderedTimeframes = orderedTimeframes?.ToArray() ?? throw new ArgumentNullException(nameof(orderedTimeframes));
            BarsByTimeframe = barsByTimeframe ?? throw new ArgumentNullException(nameof(barsByTimeframe));
        }

        public IReadOnlyList<ModelBar> GetBars(ModelTimeFrame timeframe)
        {
            return BarsByTimeframe.TryGetValue(timeframe, out var bars)
                ? bars
                : Array.Empty<ModelBar>();
        }

        public static TimeframeSnapshot Empty(string symbol, DateTime timestampUtc, IReadOnlyList<ModelTimeFrame> timeframes)
        {
            var ordered = timeframes?.ToArray() ?? Array.Empty<ModelTimeFrame>();
            var dict = ordered.ToDictionary(tf => tf, _ => (IReadOnlyList<ModelBar>)Array.Empty<ModelBar>());
            return new TimeframeSnapshot(symbol, timestampUtc, ordered, dict);
        }
    }
}
