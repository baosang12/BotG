using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BotG.MarketRegime;
using BotG.Runtime.Logging;
using cAlgo.API;
using DataFetcher.Models;
using Strategies;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.Strategies.Coordination
{
    /// <summary>
    /// Feature-flagged coordinator that layers Bayesian fusion on top of the legacy coordinator.
    /// </summary>
    public sealed class EnhancedStrategyCoordinator : IStrategyCoordinator
    {
        private StrategyCoordinationConfig _config;
        private readonly StrategyCoordinator _fallback;
        private readonly StrategyRegistry _registry;
        private readonly BayesianFusionCore _fusionCore;
        private readonly IEvidenceAssembler _assembler;
        private bool _enabled;

        public EnhancedStrategyCoordinator(
            StrategyCoordinationConfig? config = null,
            StrategyRegistry? registry = null,
            BayesianFusionCore? fusionCore = null,
            IEvidenceAssembler? assembler = null)
        {
            _config = config ?? new StrategyCoordinationConfig();
            _fallback = new StrategyCoordinator(_config);
            _registry = registry ?? new StrategyRegistry();
            _fusionCore = fusionCore ?? new BayesianFusionCore(_config.Fusion);
            _assembler = assembler ?? new DefaultEvidenceAssembler();
            _enabled = ShouldEnable(_config);
            RegisterDefaultStrategies();
        }

        public Task<IReadOnlyList<StrategyCoordinatorDecision>> CoordinateAsync(
            MarketContext context,
            IReadOnlyList<StrategyEvaluation> evaluations,
            CancellationToken cancellationToken)
        {
            if (!_enabled)
            {
                return _fallback.CoordinateAsync(context, evaluations, cancellationToken);
            }

            return CoordinateWithFusionAsync(context, evaluations ?? Array.Empty<StrategyEvaluation>(), cancellationToken);
        }

        public void UpdateConfiguration(StrategyCoordinationConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _config = config;
            _fallback.UpdateConfiguration(config);
            _registry.UpdateConfiguration(config);
            _fusionCore.UpdateConfig(config.Fusion);
            _enabled = ShouldEnable(_config);
            RegisterDefaultStrategies();
        }

        private async Task<IReadOnlyList<StrategyCoordinatorDecision>> CoordinateWithFusionAsync(
            MarketContext context,
            IReadOnlyList<StrategyEvaluation> evaluations,
            CancellationToken cancellationToken)
        {
            if (evaluations.Count == 0)
            {
                return await _fallback.CoordinateAsync(context, evaluations, cancellationToken).ConfigureAwait(false);
            }

            var fusionSignals = new List<StrategyFusionSignal>(evaluations.Count);
            foreach (var evaluation in evaluations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evaluation.Signal == null)
                {
                    continue;
                }

                if (!_registry.IsStrategyEnabled(evaluation.StrategyName, context.CurrentRegime))
                {
                    continue;
                }

                var metadata = _registry.GetStrategyMetadata(evaluation.StrategyName);
                var fusionSignal = _assembler.CreateSignal(evaluation, context, metadata);
                if (fusionSignal == null)
                {
                    continue;
                }

                fusionSignals.Add(fusionSignal);
                _registry.RecordEvaluation(evaluation.StrategyName, evaluation.EvaluationLatency, evaluation.Signal.Confidence);
            }

            if (fusionSignals.Count == 0)
            {
                return await _fallback.CoordinateAsync(context, evaluations, cancellationToken).ConfigureAwait(false);
            }

            var fusionResult = _fusionCore.Fuse(fusionSignals);
            if (!fusionResult.HasDecision || fusionResult.Direction == TradeAction.None)
            {
                PipelineLogger.Log(
                    "COORD",
                    "FusionSkip",
                    "Fusion result below threshold, falling back to legacy coordinator",
                    new { method = fusionResult.Method.ToString(), fusionResult.Reason },
                    null);

                return await _fallback.CoordinateAsync(context, evaluations, cancellationToken).ConfigureAwait(false);
            }

            var winner = SelectWinningEvaluation(evaluations, fusionResult.Direction);
            if (winner == null)
            {
                return await _fallback.CoordinateAsync(context, evaluations, cancellationToken).ConfigureAwait(false);
            }

            var tradingSignal = BuildTradingSignal(winner, fusionResult, context);
            _registry.RecordExecution(winner.StrategyName);
            LogFusionSelection(context, fusionResult);

            var decision = new StrategyCoordinatorDecision(tradingSignal, winner);
            return new[] { decision };
        }

        private void RegisterDefaultStrategies()
        {
            var breakout = new StrategyMetadata(
                "BreakoutStrategy",
                "Breakout Strategy",
                ModelTimeFrame.H1,
                new[] { RegimeType.Trending, RegimeType.Ranging, RegimeType.Volatile },
                _config.GetStrategyWeight("BreakoutStrategy", 0.5),
                true,
                "Deterministic multi-timeframe breakout producer feeding Bayesian fusion");

            EnsureRegistryEntry(breakout);
        }

        private void EnsureRegistryEntry(StrategyMetadata metadata)
        {
            if (_registry.GetStrategyMetadata(metadata.StrategyId) != null)
            {
                return;
            }

            _registry.RegisterStrategy(metadata);
            _registry.SetStrategyEnabled(metadata.StrategyId, metadata.EnabledByDefault);
        }

        private static bool ShouldEnable(StrategyCoordinationConfig config)
        {
            if (config == null)
            {
                return false;
            }

            return config.EnableBayesianFusion && config.Fusion != null;
        }

        private static void LogFusionSelection(MarketContext context, FusionResult result)
        {
            PipelineLogger.Log(
                "COORD",
                "BayesFusion",
                "Fusion decision selected",
                new
                {
                    regime = context.CurrentRegime.ToString(),
                    direction = result.Direction.ToString(),
                    confidence = result.CombinedConfidence,
                    method = result.Method.ToString(),
                    contributions = result.Contributions.Select(c => new
                    {
                        strategy = c.StrategyId,
                        direction = c.Direction.ToString(),
                        weight = c.Weight,
                        confidence = c.Confidence
                    })
                },
                null);
        }

        private static StrategyEvaluation? SelectWinningEvaluation(
            IReadOnlyList<StrategyEvaluation> evaluations,
            TradeAction direction)
        {
            return evaluations
                .Where(e => e.Signal != null && e.Signal.Action == direction)
                .OrderByDescending(e => e.Signal!.Confidence)
                .FirstOrDefault();
        }

        private static TradingSignal BuildTradingSignal(
            StrategyEvaluation evaluation,
            FusionResult fusionResult,
            MarketContext context)
        {
            var signal = evaluation.Signal ?? throw new InvalidOperationException("Evaluation missing signal");
            var tradeType = MapDirection(signal.Action);
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["fusion_confidence"] = fusionResult.CombinedConfidence,
                ["fusion_method"] = (double)fusionResult.Method,
                ["original_confidence"] = signal.Confidence
            };

            int idx = 0;
            foreach (var contribution in fusionResult.Contributions)
            {
                metrics[$"fusion_{idx}_weight"] = contribution.Weight;
                metrics[$"fusion_{idx}_confidence"] = contribution.Confidence;
                idx++;
            }

            return new TradingSignal
            {
                StrategyName = evaluation.StrategyName,
                Direction = tradeType,
                Symbol = signal.Notes != null && signal.Notes.TryGetValue("symbol", out var sym)
                    ? sym
                    : context.LatestTick.Symbol,
                ConfidenceScore = fusionResult.CombinedConfidence,
                GeneratedTime = signal.TimestampUtc,
                RegimeContext = context.CurrentRegime,
                SignalMetrics = metrics,
                IsConfirmed = true,
                SourceSignal = signal,
                TimeSinceLastTrade = TimeSpan.Zero
            };
        }

        private static TradeType MapDirection(TradeAction action)
        {
            return action switch
            {
                TradeAction.Buy => TradeType.Buy,
                TradeAction.Sell => TradeType.Sell,
                _ => TradeType.Buy
            };
        }
    }
}
