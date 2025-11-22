using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using BotG.MarketRegime;
using DataFetcher.Models;

namespace BotG.Strategies.Coordination
{
    public sealed record StrategyMetadata(
        string StrategyId,
        string DisplayName,
        TimeFrame PrimaryTimeframe,
        IReadOnlyCollection<RegimeType>? CompatibleRegimes = null,
        double DefaultWeight = 1.0,
        bool EnabledByDefault = true,
        string? Description = null);

    public sealed record StrategyRuntimeStats(
        long Evaluations,
        long Signals,
        long Executions,
        double AverageLatencyMs,
        double AverageConfidence,
        DateTime LastUpdatedUtc);

    public sealed record StrategyRegistryEntry(
        StrategyMetadata Metadata,
        bool Enabled,
        StrategyRuntimeStats Stats);

    internal sealed class StrategyRegistryMutableStats
    {
        private long _evaluations;
        private long _signals;
        private long _executions;
        private double _latencySumMs;
        private double _confidenceSum;
        private DateTime _lastUpdatedUtc;

        public void RecordEvaluation(TimeSpan latency, double? confidence)
        {
            _evaluations++;
            _latencySumMs += Math.Max(0.0, latency.TotalMilliseconds);
            if (confidence.HasValue)
            {
                _signals++;
                _confidenceSum += confidence.Value;
            }

            _lastUpdatedUtc = DateTime.UtcNow;
        }

        public void RecordExecution()
        {
            _executions++;
            _lastUpdatedUtc = DateTime.UtcNow;
        }

        public StrategyRuntimeStats ToSnapshot()
        {
            var avgLatency = _evaluations > 0 ? _latencySumMs / _evaluations : 0.0;
            var avgConfidence = _signals > 0 ? _confidenceSum / _signals : 0.0;
            return new StrategyRuntimeStats(
                _evaluations,
                _signals,
                _executions,
                avgLatency,
                avgConfidence,
                _lastUpdatedUtc == default ? DateTime.UtcNow : _lastUpdatedUtc);
        }
    }

    public sealed class StrategyRegistry
    {
        private sealed class Entry
        {
            public StrategyMetadata Metadata { get; }
            public StrategyRegistryMutableStats Stats { get; } = new();
            public bool Enabled { get; set; }

            public Entry(StrategyMetadata metadata)
            {
                Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
                Enabled = metadata.EnabledByDefault;
            }
        }

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

        public void RegisterStrategy(StrategyMetadata metadata)
        {
            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            _lock.EnterWriteLock();
            try
            {
                _entries[metadata.StrategyId] = new Entry(metadata);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public bool SetStrategyEnabled(string strategyId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(strategyId))
            {
                return false;
            }

            _lock.EnterUpgradeableReadLock();
            try
            {
                if (!_entries.TryGetValue(strategyId, out var entry))
                {
                    return false;
                }

                if (entry.Enabled == enabled)
                {
                    return true;
                }

                _lock.EnterWriteLock();
                try
                {
                    entry.Enabled = enabled;
                    return true;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }
            finally
            {
                _lock.ExitUpgradeableReadLock();
            }
        }

        public bool IsStrategyEnabled(string strategyId, RegimeType? currentRegime = null)
        {
            if (string.IsNullOrWhiteSpace(strategyId))
            {
                return false;
            }

            _lock.EnterReadLock();
            try
            {
                if (!_entries.TryGetValue(strategyId, out var entry))
                {
                    return false;
                }

                if (!entry.Enabled)
                {
                    return false;
                }

                if (currentRegime.HasValue && entry.Metadata.CompatibleRegimes?.Count > 0)
                {
                    return entry.Metadata.CompatibleRegimes.Contains(currentRegime.Value);
                }

                return true;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public StrategyMetadata? GetStrategyMetadata(string strategyId)
        {
            if (string.IsNullOrWhiteSpace(strategyId))
            {
                return null;
            }

            _lock.EnterReadLock();
            try
            {
                return _entries.TryGetValue(strategyId, out var entry) ? entry.Metadata : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public IReadOnlyList<StrategyRegistryEntry> GetSnapshot()
        {
            _lock.EnterReadLock();
            try
            {
                var entries = _entries.Values
                    .Select(e => new StrategyRegistryEntry(e.Metadata, e.Enabled, e.Stats.ToSnapshot()))
                    .ToList();
                return new ReadOnlyCollection<StrategyRegistryEntry>(entries);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public IReadOnlyList<StrategyMetadata> GetActiveStrategies(RegimeType? regime = null)
        {
            _lock.EnterReadLock();
            try
            {
                return _entries.Values
                    .Where(e => e.Enabled && (regime == null || e.Metadata.CompatibleRegimes == null || e.Metadata.CompatibleRegimes.Count == 0 || e.Metadata.CompatibleRegimes.Contains(regime.Value)))
                    .Select(e => e.Metadata)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void RecordEvaluation(string strategyId, TimeSpan latency, double? confidence)
        {
            if (string.IsNullOrWhiteSpace(strategyId))
            {
                return;
            }

            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_entries.TryGetValue(strategyId, out var entry))
                {
                    _lock.EnterWriteLock();
                    try
                    {
                        entry.Stats.RecordEvaluation(latency, confidence);
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }
                }
            }
            finally
            {
                _lock.ExitUpgradeableReadLock();
            }
        }

        public void RecordExecution(string strategyId)
        {
            if (string.IsNullOrWhiteSpace(strategyId))
            {
                return;
            }

            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_entries.TryGetValue(strategyId, out var entry))
                {
                    _lock.EnterWriteLock();
                    try
                    {
                        entry.Stats.RecordExecution();
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }
                }
            }
            finally
            {
                _lock.ExitUpgradeableReadLock();
            }
        }

        public void UpdateConfiguration(StrategyCoordinationConfig config)
        {
            // Future enhancement: adjust metadata/weights based on config.
            _ = config ?? throw new ArgumentNullException(nameof(config));
        }
    }
}
