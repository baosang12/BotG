using System;
using System.Collections.Generic;
using BotG.MarketRegime;

namespace BotG.Strategies.Coordination
{
    /// <summary>
    /// Configuration used by the strategy coordinator to gate and score signals.
    /// </summary>
    public sealed class StrategyCoordinationConfig
    {
        /// <summary>
        /// Minimum confidence score required for a signal to be considered.
        /// </summary>
        public double MinimumConfidence { get; init; } = 0.55;

        // Backwards-compatible alias expected by integrations
        public double MinimumConfidenceThreshold
        {
            get => MinimumConfidence;
            init => MinimumConfidence = value;
        }

        /// <summary>
        /// Cooldown window enforced between trades for the same symbol and direction.
        /// </summary>
        public TimeSpan MinimumTimeBetweenTrades { get; init; } = TimeSpan.FromMinutes(2);

        public TimeSpan MinimumTradeInterval
        {
            get => MinimumTimeBetweenTrades;
            init => MinimumTimeBetweenTrades = value;
        }

        /// <summary>
        /// Maximum number of coordinated signals allowed per symbol in a single tick.
        /// </summary>
        public int MaxSignalsPerSymbol { get; init; } = 1;

        /// <summary>
        /// Maximum number of open positions allowed per symbol (portfolio coherence).
        /// Backwards-compatible with MaxSignalsPerSymbol semantics for coordinator.
        /// </summary>
        public int MaxPositionsPerSymbol
        {
            get => MaxSignalsPerSymbol;
            init => MaxSignalsPerSymbol = value;
        }

        /// <summary>
        /// Maximum number of coordinated signals allowed across all symbols in a single tick.
        /// </summary>
        public int MaxSignalsPerTick { get; init; } = 2;

        /// <summary>
        /// Confidence penalty applied when triggering within the cooldown window.
        /// </summary>
        public double CooldownPenalty { get; init; } = 0.35;

        /// <summary>
        /// Minimum fallback confidence when a strategy does not provide a value.
        /// </summary>
        public double ConfidenceFloor { get; init; } = 0.15;

        /// <summary>
        /// Enable or disable conflict resolution.
        /// </summary>
        public bool EnableConflictResolution { get; init; } = true;

        /// <summary>
        /// Enable or disable time-based filtering (cooldowns).
        /// </summary>
        public bool EnableTimeBasedFiltering { get; init; } = true;

        /// <summary>
        /// Per-strategy weight multipliers applied to base confidence when combining signals.
        /// </summary>
        public Dictionary<string, double>? StrategyWeights { get; init; } = null;

        /// <summary>
        /// Prevent opposite-direction signals for the same symbol within the same coordination cycle.
        /// </summary>
        public bool PreventOppositeSignals { get; init; } = false;

        /// <summary>
        /// Feature flag that enables the enhanced coordinator and Bayesian fusion pipeline.
        /// </summary>
        public bool EnableBayesianFusion { get; init; } = false;

        public double GetStrategyWeight(string strategyName, double defaultWeight = 1.0)
        {
            if (string.IsNullOrWhiteSpace(strategyName))
            {
                return defaultWeight;
            }

            if (StrategyWeights == null || StrategyWeights.Count == 0)
            {
                return defaultWeight;
            }

            foreach (var kvp in StrategyWeights)
            {
                if (string.Equals(kvp.Key, strategyName, StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeWeight(kvp.Value, defaultWeight);
                }
            }

            return defaultWeight;
        }

        private static double NormalizeWeight(double value, double defaultWeight)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return defaultWeight;
            }

            if (value <= 0)
            {
                return 0.1;
            }

            return value;
        }

        /// <summary>
        /// Confidence bonus applied when the risk level is preferred.
        /// </summary>
        public double PreferredRiskBonus { get; init; } = 0.10;

        /// <summary>
        /// Confidence penalty applied when the risk level is elevated.
        /// </summary>
        public double ElevatedRiskPenalty { get; init; } = 0.20;

        /// <summary>
        /// Confidence penalty applied when the risk level is blocked.
        /// </summary>
        public double BlockedRiskPenalty { get; init; } = 0.60;

        /// <summary>
        /// Additional margin required to replace an existing winner during conflict resolution.
        /// </summary>
        public double ConflictMargin { get; init; } = 0.05;

        /// <summary>
        /// Optional adaptive confidence control per regime.
        /// </summary>
        public AdaptiveConfidenceOptions AdaptiveConfidence { get; init; } = new();

        /// <summary>
        /// Settings used to reduce cooldown penalty when thị trường khát lệnh quá lâu.
        /// </summary>
        public CooldownRecoverySettings CooldownRecovery { get; init; } = new();

        /// <summary>
        /// Progressive threshold reduction when hệ thống “khát lệnh” quá lâu.
        /// </summary>
        public DroughtAdaptiveOptions DroughtAdaptive { get; init; } = new();

        /// <summary>
        /// Options controlling the fusion method used to aggregate strategy evidence.
        /// </summary>
        public BayesianFusionConfig Fusion { get; init; } = new();
    }

    public sealed class AdaptiveConfidenceOptions
    {
        public bool Enabled { get; init; } = false;

        public Dictionary<string, double>? Overrides { get; init; }

        public bool TryGetThreshold(RegimeType regime, out double threshold)
        {
            threshold = 0.0;
            if (!Enabled || Overrides == null || Overrides.Count == 0)
            {
                return false;
            }

            foreach (var kvp in Overrides)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                {
                    continue;
                }

                if (regime.ToString().Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    threshold = Math.Clamp(kvp.Value, 0.05, 1.0);
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class DroughtAdaptiveOptions
    {
        public bool Enabled { get; init; } = false;

        /// <summary>
        /// Số phút không có lệnh trước khi bắt đầu giảm ngưỡng.
        /// </summary>
        public double WindowMinutes { get; init; } = 720; // 12h

        /// <summary>
        /// Giảm bao nhiêu điểm confidence mỗi “window”.
        /// </summary>
        public double ReductionPerWindow { get; init; } = 0.05;

        /// <summary>
        /// Giới hạn tối đa cho tổng mức giảm.
        /// </summary>
        public double MaximumReduction { get; init; } = 0.15;

        /// <summary>
        /// Sàn tuyệt đối để tránh bot quá hung hăng.
        /// </summary>
        public double MinimumFloor { get; init; } = 0.12;

        private TimeSpan GetWindow()
        {
            var minutes = Math.Clamp(WindowMinutes, 1.0, 60.0 * 24.0 * 7.0); // tối đa 1 tuần
            return TimeSpan.FromMinutes(minutes);
        }

        public double GetReduction(TimeSpan droughtDuration)
        {
            if (!Enabled || droughtDuration <= TimeSpan.Zero)
            {
                return 0.0;
            }

            var window = GetWindow();
            if (window <= TimeSpan.Zero)
            {
                return 0.0;
            }

            var windows = droughtDuration.TotalMinutes / window.TotalMinutes;
            var reduction = windows * Math.Clamp(ReductionPerWindow, 0.0, 1.0);
            return Math.Min(Math.Clamp(reduction, 0.0, 1.0), Math.Max(0.0, MaximumReduction));
        }
    }

    public sealed class CooldownRecoverySettings
    {
        public bool Enabled { get; init; } = false;
        public int MaxCooldownBlocksPerHour { get; init; } = 20;
        public double CooldownRecoveryRate { get; init; } = 0.05;
        public double MaximumRecoveryReduction { get; init; } = 0.8;
        public double MinimumPenaltyMultiplier { get; init; } = 0.2;
        public double LongDroughtHours { get; init; } = 2.0;
        public double DroughtPenaltyMultiplier { get; init; } = 0.5;
    }
}
