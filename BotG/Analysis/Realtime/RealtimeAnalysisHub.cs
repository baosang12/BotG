using System;
using System.Collections.Generic;

namespace Analysis.Realtime
{
    /// <summary>
    /// Coordinates realtime analyzers and dispatches aggregated snapshots.
    /// </summary>
    public sealed class RealtimeAnalysisHub
    {
        private readonly List<IRealtimeAnalyzer> _analyzers = new();

        public event EventHandler<RealtimeAnalysisSnapshot>? SnapshotGenerated;

        public void RegisterAnalyzer(IRealtimeAnalyzer analyzer)
        {
            if (analyzer == null)
            {
                throw new ArgumentNullException(nameof(analyzer));
            }

            _analyzers.Add(analyzer);
        }

        public RealtimeAnalysisSnapshot Process(RealtimeFeatureContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var payloads = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var analyzer in _analyzers)
            {
                var result = analyzer.Analyze(context) ?? RealtimeFeatureResult.Empty;
                var prefix = analyzer.Name;

                foreach (var metric in result.Metrics)
                {
                    var key = string.IsNullOrEmpty(prefix)
                        ? metric.Key
                        : $"{prefix}.{metric.Key}";
                    metrics[key] = metric.Value;
                }

                foreach (var tag in result.Tags)
                {
                    var key = string.IsNullOrEmpty(prefix)
                        ? tag.Key
                        : $"{prefix}.{tag.Key}";
                    tags[key] = tag.Value;
                }

                if (result.Payload != null)
                {
                    payloads[prefix] = result.Payload;
                }
            }

            var snapshot = new RealtimeAnalysisSnapshot(context.TimestampUtc, metrics, tags, payloads);
            SnapshotGenerated?.Invoke(this, snapshot);
            return snapshot;
        }
    }
}
