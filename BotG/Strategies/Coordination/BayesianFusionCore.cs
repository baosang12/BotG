using System;
using System.Collections.Generic;
using System.Linq;
using Strategies;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.Strategies.Coordination
{
    public enum FusionMethod
    {
        WeightedAverage,
        ConsensusVoting,
        BayesianProbability
    }

    public sealed class BayesianFusionConfig
    {
        public FusionMethod Method { get; init; } = FusionMethod.BayesianProbability;
        public Dictionary<string, double>? StrategyWeights { get; init; }
        public double MinimumConfidenceThreshold { get; init; } = 0.6;
        public int LookbackPeriodForPerformance { get; init; } = 250;
        public double VotingConsensusMargin { get; init; } = 0.1;
        public double BayesianPrior { get; init; } = 0.5;
        public double EvidenceFloor { get; init; } = 0.05;
        public double EvidenceCeiling { get; init; } = 0.95;
    }

    public sealed record StrategyFusionSignal(
        string StrategyId,
        TradeAction Direction,
        double Confidence,
        IReadOnlyDictionary<string, double>? Evidence,
        DateTime GeneratedAt,
        ModelTimeFrame PrimaryTimeframe);

    public sealed record FusionContribution(
        string StrategyId,
        TradeAction Direction,
        double Confidence,
        double Weight);

    public sealed record FusionResult(
        bool HasDecision,
        TradeAction Direction,
        double CombinedConfidence,
        FusionMethod Method,
        string? Reason,
        IReadOnlyList<FusionContribution> Contributions)
    {
        public static FusionResult Empty(FusionMethod method, string? reason = null)
        {
            return new FusionResult(false, TradeAction.None, 0.0, method, reason, Array.Empty<FusionContribution>());
        }
    }

    public sealed class BayesianFusionCore
    {
        private BayesianFusionConfig _config;

        public BayesianFusionCore(BayesianFusionConfig? config = null)
        {
            _config = Sanitize(config ?? new BayesianFusionConfig());
        }

        public void UpdateConfig(BayesianFusionConfig? config)
        {
            if (config == null)
            {
                return;
            }

            _config = Sanitize(config);
        }

        public FusionResult Fuse(IReadOnlyList<StrategyFusionSignal>? signals)
        {
            if (signals == null || signals.Count == 0)
            {
                return FusionResult.Empty(_config.Method, "no-signals");
            }

            var normalized = signals
                .Where(s => s != null && s.Direction != TradeAction.None)
                .Select(NormalizeSignal)
                .Where(s => s != null)
                .Cast<StrategyFusionSignal>()
                .ToList();

            if (normalized.Count == 0)
            {
                return FusionResult.Empty(_config.Method, "invalid-signals");
            }

            return _config.Method switch
            {
                FusionMethod.WeightedAverage => RunWeightedAverage(normalized),
                FusionMethod.ConsensusVoting => RunConsensusVoting(normalized),
                FusionMethod.BayesianProbability => RunBayesianProbability(normalized),
                _ => FusionResult.Empty(_config.Method, "unknown-method")
            };
        }

        private FusionResult RunWeightedAverage(IReadOnlyList<StrategyFusionSignal> signals)
        {
            var aggregates = signals
                .GroupBy(s => s.Direction)
                .Select(group =>
                {
                    var contributions = group.Select(BuildContribution).ToList();
                    var weightedConfidence = contributions.Sum(c => c.Confidence * c.Weight);
                    var weightSum = contributions.Sum(c => c.Weight);
                    var averageConfidence = weightSum > 0 ? weightedConfidence / weightSum : 0.0;
                    return new
                    {
                        Direction = group.Key,
                        WeightedConfidence = weightedConfidence,
                        WeightSum = weightSum,
                        AverageConfidence = averageConfidence,
                        Contributions = (IReadOnlyList<FusionContribution>)contributions
                    };
                })
                .Where(x => x.WeightSum > 0)
                .ToList();

            if (aggregates.Count == 0)
            {
                return FusionResult.Empty(_config.Method, "no-weighted-signal");
            }

            var totalWeightedConfidence = aggregates.Sum(x => x.WeightedConfidence);
            if (totalWeightedConfidence <= 0)
            {
                return FusionResult.Empty(_config.Method, "no-weighted-signal");
            }

            var winner = aggregates
                .OrderByDescending(x => x.WeightedConfidence)
                .ThenByDescending(x => x.AverageConfidence)
                .First();

            var combined = aggregates.Count == 1
                ? winner.AverageConfidence
                : winner.WeightedConfidence / totalWeightedConfidence;

            var meetsThreshold = combined >= _config.MinimumConfidenceThreshold;
            return new FusionResult(
                meetsThreshold,
                meetsThreshold ? winner.Direction : TradeAction.None,
                combined,
                FusionMethod.WeightedAverage,
                meetsThreshold ? null : "below-threshold",
                winner.Contributions);
        }

        private FusionResult RunConsensusVoting(IReadOnlyList<StrategyFusionSignal> signals)
        {
            var tallies = signals
                .GroupBy(s => s.Direction)
                .Select(group => new
                {
                    Direction = group.Key,
                    VoteWeight = group.Sum(s => GetStrategyWeight(s.StrategyId)),
                    Confidence = group.Average(s => s.Confidence),
                    Contributions = group.Select(BuildContribution).ToList()
                })
                .OrderByDescending(x => x.VoteWeight)
                .ToList();

            if (tallies.Count == 0)
            {
                return FusionResult.Empty(FusionMethod.ConsensusVoting, "no-votes");
            }

            var winner = tallies[0];
            var runnerUpWeight = tallies.Count > 1 ? tallies[1].VoteWeight : 0.0;
            var margin = winner.VoteWeight - runnerUpWeight;
            var hasConsensus = margin >= Math.Max(_config.VotingConsensusMargin, 0.0);
            var meetsThreshold = winner.Confidence >= _config.MinimumConfidenceThreshold;

            if (!hasConsensus || !meetsThreshold)
            {
                var reason = !hasConsensus ? "no-consensus" : "below-threshold";
                return new FusionResult(false, TradeAction.None, winner.Confidence, FusionMethod.ConsensusVoting, reason, winner.Contributions);
            }

            return new FusionResult(true, winner.Direction, winner.Confidence, FusionMethod.ConsensusVoting, null, winner.Contributions);
        }

        private FusionResult RunBayesianProbability(IReadOnlyList<StrategyFusionSignal> signals)
        {
            var grouped = signals
                .GroupBy(s => s.Direction)
                .Select(group => new
                {
                    Direction = group.Key,
                    LogOdds = ComputeLogOdds(group),
                    Contributions = group.Select(BuildContribution).ToList()
                })
                .ToList();

            if (grouped.Count == 0)
            {
                return FusionResult.Empty(FusionMethod.BayesianProbability, "no-groups");
            }

            var scored = grouped
                .Select(item => new
                {
                    item.Direction,
                    Probability = LogOddsToProbability(item.LogOdds),
                    item.Contributions
                })
                .OrderByDescending(x => x.Probability)
                .First();

            var meetsThreshold = scored.Probability >= _config.MinimumConfidenceThreshold;
            return new FusionResult(
                meetsThreshold,
                meetsThreshold ? scored.Direction : TradeAction.None,
                scored.Probability,
                FusionMethod.BayesianProbability,
                meetsThreshold ? null : "below-threshold",
                scored.Contributions);
        }

        private double ComputeLogOdds(IEnumerable<StrategyFusionSignal> signals)
        {
            var prior = ClampProbability(_config.BayesianPrior);
            var logOdds = ProbabilityToLogOdds(prior);

            foreach (var signal in signals)
            {
                var confidence = ClampProbability(signal.Confidence);
                var evidence = ExtractEvidence(signal) ?? confidence;
                evidence = ClampProbability(evidence, _config.EvidenceFloor, _config.EvidenceCeiling);
                var weight = GetStrategyWeight(signal.StrategyId);
                logOdds += ProbabilityToLogOdds(evidence) * weight;
            }

            return logOdds;
        }

        private static double ProbabilityToLogOdds(double probability)
        {
            return Math.Log(probability / (1 - probability));
        }

        private static double LogOddsToProbability(double logOdds)
        {
            var exp = Math.Exp(logOdds);
            return exp / (1 + exp);
        }

        private FusionContribution BuildContribution(StrategyFusionSignal signal)
        {
            return new FusionContribution(signal.StrategyId, signal.Direction, signal.Confidence, GetStrategyWeight(signal.StrategyId));
        }

        private double GetStrategyWeight(string strategyId)
        {
            if (string.IsNullOrWhiteSpace(strategyId) || _config.StrategyWeights == null)
            {
                return 1.0;
            }

            foreach (var kvp in _config.StrategyWeights)
            {
                if (string.Equals(kvp.Key, strategyId, StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeWeight(kvp.Value);
                }
            }

            return 1.0;
        }

        private static double NormalizeWeight(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                return 0.1;
            }

            return Math.Min(value, 10.0);
        }

        private StrategyFusionSignal? NormalizeSignal(StrategyFusionSignal signal)
        {
            if (string.IsNullOrWhiteSpace(signal.StrategyId))
            {
                return null;
            }

            var confidence = ClampProbability(signal.Confidence);
            return signal with { Confidence = confidence };
        }

        private static double ClampProbability(double value, double? floor = null, double? ceiling = null)
        {
            var min = floor ?? 0.001;
            var max = ceiling ?? 0.999;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return min;
            }

            return Math.Clamp(value, min, max);
        }

        private double? ExtractEvidence(StrategyFusionSignal signal)
        {
            if (signal.Evidence == null || signal.Evidence.Count == 0)
            {
                return null;
            }

            if (signal.Evidence.TryGetValue("likelihood", out var likelihood))
            {
                return likelihood;
            }

            if (signal.Evidence.TryGetValue("strength", out var strength))
            {
                return strength;
            }

            return null;
        }

        private static BayesianFusionConfig Sanitize(BayesianFusionConfig config)
        {
            var prior = Clamp(config.BayesianPrior);
            var evidenceFloor = Clamp(config.EvidenceFloor);
            var evidenceCeiling = Clamp(config.EvidenceCeiling);
            if (evidenceFloor >= evidenceCeiling)
            {
                evidenceFloor = 0.05;
                evidenceCeiling = 0.95;
            }

            return new BayesianFusionConfig
            {
                Method = config.Method,
                StrategyWeights = config.StrategyWeights,
                MinimumConfidenceThreshold = Math.Clamp(config.MinimumConfidenceThreshold, 0.1, 0.99),
                LookbackPeriodForPerformance = Math.Max(10, config.LookbackPeriodForPerformance),
                VotingConsensusMargin = Math.Max(0.0, config.VotingConsensusMargin),
                BayesianPrior = prior,
                EvidenceFloor = evidenceFloor,
                EvidenceCeiling = evidenceCeiling
            };
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.5;
            }

            return Math.Clamp(value, 0.001, 0.999);
        }
    }
}
