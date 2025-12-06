using System;
using System.Collections.Generic;

namespace Analysis.Realtime
{
    /// <summary>
    /// Output container for analyzer results.
    /// </summary>
    public sealed class RealtimeFeatureResult
    {
        public static RealtimeFeatureResult Empty { get; } = new RealtimeFeatureResult(
            name: "empty",
            metrics: Array.Empty<KeyValuePair<string, double>>(),
            tags: Array.Empty<KeyValuePair<string, string>>(),
            payload: null);

        public string AnalyzerName { get; }
        public IReadOnlyDictionary<string, double> Metrics { get; }
        public IReadOnlyDictionary<string, string> Tags { get; }
        public object? Payload { get; }

        public RealtimeFeatureResult(
            string name,
            IEnumerable<KeyValuePair<string, double>> metrics,
            IEnumerable<KeyValuePair<string, string>> tags,
            object? payload)
        {
            AnalyzerName = string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
            Metrics = new Dictionary<string, double>(
                metrics ?? Array.Empty<KeyValuePair<string, double>>(),
                StringComparer.OrdinalIgnoreCase);
            Tags = new Dictionary<string, string>(
                tags ?? Array.Empty<KeyValuePair<string, string>>(),
                StringComparer.OrdinalIgnoreCase);
            Payload = payload;
        }
    }
}
