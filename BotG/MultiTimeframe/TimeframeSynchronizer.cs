using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BotG.Runtime.Logging;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.MultiTimeframe
{
    public sealed class TimeframeSynchronizerConfig
    {
        public int MinimumAlignedTimeframes { get; set; } = 3;
        public int MinimumBarsPerTimeframe { get; set; } = 1;
        public TimeSpan MaximumAllowedSkew { get; set; } = TimeSpan.FromHours(4);
        public TimeSpan AntiRepaintGuard { get; set; } = TimeSpan.FromSeconds(1);
        public int WarmupBarsRequired { get; set; } = 12;
        public Dictionary<ModelTimeFrame, int>? WarmupBarsPerTimeframe { get; set; }
            = new Dictionary<ModelTimeFrame, int>
            {
                [ModelTimeFrame.H4] = 8,
                [ModelTimeFrame.H1] = 20,
                [ModelTimeFrame.M15] = 48
            };
        public double RequiredAlignmentRatio { get; set; } = 1.0;
        public bool EnableAntiRepaint { get; set; } = true;
        public bool EnableSkewCheck { get; set; } = true;
        public bool IgnoreSkewDuringWarmup { get; set; } = true;

        public TimeframeSynchronizerConfig Clone()
        {
            return new TimeframeSynchronizerConfig
            {
                MinimumAlignedTimeframes = MinimumAlignedTimeframes,
                MinimumBarsPerTimeframe = MinimumBarsPerTimeframe,
                MaximumAllowedSkew = MaximumAllowedSkew,
                AntiRepaintGuard = AntiRepaintGuard,
                WarmupBarsRequired = WarmupBarsRequired,
                WarmupBarsPerTimeframe = WarmupBarsPerTimeframe != null
                    ? new Dictionary<ModelTimeFrame, int>(WarmupBarsPerTimeframe)
                    : null,
                RequiredAlignmentRatio = RequiredAlignmentRatio,
                EnableAntiRepaint = EnableAntiRepaint,
                EnableSkewCheck = EnableSkewCheck,
                IgnoreSkewDuringWarmup = IgnoreSkewDuringWarmup
            };
        }
    }

    public sealed class TimeframeSynchronizer
    {
        private TimeframeSynchronizerConfig _config;
        private DateTime _lastDiagnosticLogUtc = DateTime.MinValue;
        private DateTime _lastWarmupLogUtc = DateTime.MinValue;
        private static readonly TimeSpan DiagnosticLogInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan WarmupLogInterval = TimeSpan.FromMinutes(2);

        public TimeframeSynchronizer(TimeframeSynchronizerConfig? config = null)
        {
            _config = Sanitize(config);
        }

        public void UpdateConfig(TimeframeSynchronizerConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _config = Sanitize(config);
        }

        public TimeframeAlignmentResult GetAlignmentResult(TimeframeSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var statuses = new Dictionary<ModelTimeFrame, TimeframeSeriesStatus>(snapshot.TotalTimeframes);
            var warmupProgress = new Dictionary<ModelTimeFrame, (int required, int available)>(snapshot.TotalTimeframes);
            int aligned = 0;
            DateTime? earliestClose = null;
            DateTime? latestClose = null;
            bool warmupSatisfied = true;
            var warmupShortfalls = new List<string>();

            foreach (var timeframe in snapshot.OrderedTimeframes)
            {
                var series = snapshot.GetBars(timeframe);
                var hasSufficient = series.Count >= _config.MinimumBarsPerTimeframe;
                ModelBar? latestBar = null;
                DateTime? latestBarClose = null;

                if (hasSufficient)
                {
                    latestBar = series[series.Count - 1];
                    latestBarClose = TimeframeMath.GetCloseTime(latestBar);
                    aligned++;
                    earliestClose = earliestClose.HasValue
                        ? (earliestClose.Value <= latestBarClose.Value ? earliestClose.Value : latestBarClose.Value)
                        : latestBarClose;
                    latestClose = latestClose.HasValue
                        ? (latestClose.Value >= latestBarClose.Value ? latestClose.Value : latestBarClose.Value)
                        : latestBarClose;
                }

                statuses[timeframe] = new TimeframeSeriesStatus(
                    series.Count,
                    hasSufficient,
                    latestBar,
                    latestBarClose);

                var warmupRequirement = GetWarmupRequirement(timeframe);
                warmupProgress[timeframe] = (warmupRequirement, series.Count);
                var timeframeWarmupMet = warmupRequirement <= 0 || series.Count >= warmupRequirement;
                if (!timeframeWarmupMet)
                {
                    warmupSatisfied = false;
                    warmupShortfalls.Add($"{timeframe}:{series.Count}/{warmupRequirement}");
                }
            }

            LogWarmupProgress(snapshot.TimestampUtc, warmupProgress);

            var observedSkew = earliestClose.HasValue && latestClose.HasValue
                ? latestClose.Value - earliestClose.Value
                : (TimeSpan?)null;

            var enforceSkewCheck = _config.EnableSkewCheck &&
                                   (_config.IgnoreSkewDuringWarmup ? warmupSatisfied : true);

            var skewOk = !enforceSkewCheck ||
                         !observedSkew.HasValue ||
                         observedSkew.Value <= _config.MaximumAllowedSkew;

            var antiRepaintSafe = !_config.EnableAntiRepaint ||
                                  (latestClose.HasValue &&
                                   snapshot.TimestampUtc - latestClose.Value >= _config.AntiRepaintGuard);

            var requiredAligned = Math.Max(
                1,
                Math.Min(
                    snapshot.TotalTimeframes,
                    Math.Max(
                        _config.MinimumAlignedTimeframes,
                        (int)Math.Ceiling(_config.RequiredAlignmentRatio * snapshot.TotalTimeframes))));

            var reasons = new List<string>();
            if (!warmupSatisfied)
            {
                reasons.Add(warmupShortfalls.Count > 0
                    ? $"warmup({string.Join(",", warmupShortfalls)})"
                    : "warmup");
            }
            if (aligned < requiredAligned)
            {
                reasons.Add($"aligned={aligned} required={requiredAligned}");
            }
            if (!skewOk)
            {
                reasons.Add("skew");
            }
            if (_config.EnableAntiRepaint && !antiRepaintSafe)
            {
                reasons.Add("anti-repaint");
            }

            var isAligned = warmupSatisfied && aligned >= requiredAligned && skewOk && antiRepaintSafe;

            if (!isAligned && ShouldEmitDiagnostics())
            {
                var detail = new
                {
                    snapshot_time = snapshot.TimestampUtc,
                    aligned,
                    required = requiredAligned,
                    total = snapshot.TotalTimeframes,
                    warmup_required = _config.WarmupBarsRequired,
                    warmup_details = warmupProgress.ToDictionary(
                        kvp => kvp.Key.ToString(),
                        kvp => new { required = kvp.Value.required, available = kvp.Value.available }),
                    warmupSatisfied,
                    max_skew_ms = _config.MaximumAllowedSkew.TotalMilliseconds,
                    observed_skew_ms = observedSkew?.TotalMilliseconds,
                    anti_repaint_guard_ms = _config.AntiRepaintGuard.TotalMilliseconds,
                    antiRepaintSafe,
                    ignore_skew_during_warmup = _config.IgnoreSkewDuringWarmup,
                    reasons,
                    timeframes = statuses.ToDictionary(
                        kvp => kvp.Key.ToString(),
                        kvp => new
                        {
                            bars = kvp.Value.AvailableBars,
                            hasData = kvp.Value.HasSufficientData,
                            closeUtc = kvp.Value.LatestCloseTime
                        })
                };

                PipelineLogger.Log(
                    "MTF",
                    "AlignmentDiagnostics",
                    "Multi-timeframe alignment blocked",
                    detail,
                    null);
            }

            return new TimeframeAlignmentResult(
                isAligned,
                antiRepaintSafe,
                aligned,
                snapshot.TotalTimeframes,
                new ReadOnlyDictionary<ModelTimeFrame, TimeframeSeriesStatus>(statuses),
                snapshot,
                reasons.Count == 0 ? null : string.Join("; ", reasons),
                requiredAligned,
                warmupSatisfied,
                observedSkew);
        }

        private static TimeframeSynchronizerConfig Sanitize(TimeframeSynchronizerConfig? config)
        {
            var clone = (config ?? new TimeframeSynchronizerConfig()).Clone();
            if (clone.MinimumAlignedTimeframes <= 0)
            {
                clone.MinimumAlignedTimeframes = 1;
            }

            if (clone.MinimumBarsPerTimeframe <= 0)
            {
                clone.MinimumBarsPerTimeframe = 1;
            }

            if (clone.MaximumAllowedSkew <= TimeSpan.Zero)
            {
                clone.MaximumAllowedSkew = TimeSpan.FromHours(2);
            }

            if (clone.AntiRepaintGuard <= TimeSpan.Zero)
            {
                clone.AntiRepaintGuard = TimeSpan.FromSeconds(1);
            }

            if (clone.WarmupBarsRequired < 0)
            {
                clone.WarmupBarsRequired = 0;
            }

            if (clone.RequiredAlignmentRatio <= 0 || clone.RequiredAlignmentRatio > 1)
            {
                clone.RequiredAlignmentRatio = 1.0;
            }

            if (clone.WarmupBarsPerTimeframe != null && clone.WarmupBarsPerTimeframe.Count > 0)
            {
                var sanitized = new Dictionary<ModelTimeFrame, int>();
                foreach (var kvp in clone.WarmupBarsPerTimeframe)
                {
                    if (kvp.Value <= 0)
                    {
                        continue;
                    }

                    sanitized[kvp.Key] = kvp.Value;
                }

                clone.WarmupBarsPerTimeframe = sanitized.Count > 0 ? sanitized : null;
            }
            else
            {
                clone.WarmupBarsPerTimeframe = null;
            }

            return clone;
        }

        private int GetWarmupRequirement(ModelTimeFrame timeframe)
        {
            if (_config.WarmupBarsPerTimeframe != null &&
                _config.WarmupBarsPerTimeframe.TryGetValue(timeframe, out var specific) &&
                specific >= 0)
            {
                return specific;
            }

            return _config.WarmupBarsRequired;
        }

        private bool ShouldEmitDiagnostics()
        {
            var now = DateTime.UtcNow;
            if (now - _lastDiagnosticLogUtc < DiagnosticLogInterval)
            {
                return false;
            }

            _lastDiagnosticLogUtc = now;
            return true;
        }

        private void LogWarmupProgress(
            DateTime snapshotTimeUtc,
            IReadOnlyDictionary<ModelTimeFrame, (int required, int available)> warmupProgress)
        {
            if (!ShouldEmitWarmupLog())
            {
                return;
            }

            var summary = string.Join(
                ", ",
                warmupProgress
                    .OrderBy(kvp => kvp.Key.ToString())
                    .Select(kvp =>
                    {
                        var required = kvp.Value.required <= 0 ? 0 : kvp.Value.required;
                        return $"{kvp.Key}={kvp.Value.available}/{required}";
                    }));

            var payload = warmupProgress.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => new { required = kvp.Value.required, available = kvp.Value.available });

            PipelineLogger.Log(
                "MTF",
                "WarmupProgress",
                $"WARMUP_PROGRESS: {summary}",
                new
                {
                    snapshot_time = snapshotTimeUtc,
                    progress = payload
                },
                null);
        }

        private bool ShouldEmitWarmupLog()
        {
            var now = DateTime.UtcNow;
            if (now - _lastWarmupLogUtc < WarmupLogInterval)
            {
                return false;
            }

            _lastWarmupLogUtc = now;
            return true;
        }
    }

    public sealed record TimeframeSeriesStatus(
        int AvailableBars,
        bool HasSufficientData,
        ModelBar? LatestBar,
        DateTime? LatestCloseTime);

    public sealed record TimeframeAlignmentResult(
        bool IsAligned,
        bool AntiRepaintSafe,
        int AlignedTimeframes,
        int TotalTimeframes,
        IReadOnlyDictionary<ModelTimeFrame, TimeframeSeriesStatus> SeriesStatuses,
        TimeframeSnapshot Snapshot,
        string? Reason,
        int RequiredAlignedTimeframes,
        bool WarmupSatisfied,
        TimeSpan? ObservedSkew);
}
