using System;
using System.Collections.Generic;

namespace Analysis.Realtime
{
    /// <summary>
    /// Aggregated view of realtime analysis metrics.
    /// </summary>
    public sealed class RealtimeAnalysisSnapshot
    {
        public DateTime TimestampUtc { get; }
        public IReadOnlyDictionary<string, double> Metrics { get; }
        public IReadOnlyDictionary<string, string> Tags { get; }
        public IReadOnlyDictionary<string, object?> Payloads { get; }

        public RealtimeAnalysisSnapshot(
            DateTime timestampUtc,
            IReadOnlyDictionary<string, double> metrics,
            IReadOnlyDictionary<string, string> tags,
            IReadOnlyDictionary<string, object?> payloads)
        {
            TimestampUtc = timestampUtc;
            Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            Tags = tags ?? throw new ArgumentNullException(nameof(tags));
            Payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        }

        public bool TryGetMetric(string key, out double value)
        {
            return Metrics.TryGetValue(key, out value);
        }
    }
}
