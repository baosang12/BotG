using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.Realtime
{
    /// <summary>
    /// Immutable wrapper that exposes synchronized multi-timeframe bars for feature extraction.
    /// </summary>
    public sealed class RealtimeFeatureContext
    {
        public DateTime TimestampUtc { get; }
        public IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> Bars { get; }

        public RealtimeFeatureContext(
            DateTime timestampUtc,
            IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> bars)
        {
            TimestampUtc = timestampUtc;
            Bars = bars ?? throw new ArgumentNullException(nameof(bars));
        }

        public bool TryGetBars(TimeFrame timeframe, out IReadOnlyList<Bar> bars)
        {
            return Bars.TryGetValue(timeframe, out bars);
        }
    }
}
