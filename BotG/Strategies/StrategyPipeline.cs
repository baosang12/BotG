using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BotG.Runtime.Logging;
using BotG.MarketRegime;
using BotG.Strategies.Coordination;
using BotG.Threading;
using TradeManager;

namespace Strategies
{
    /// <summary>
    /// Orchestrates strategy evaluation, risk scoring, and dispatch to the trade manager.
    /// </summary>
    public sealed class StrategyPipeline
    {
        private readonly IReadOnlyList<IStrategy> _strategies;
        private readonly ITradeManager _tradeManager;
        private readonly ExecutionSerializer _serializer;
        private readonly IStrategyCoordinator _coordinator;

        public StrategyPipeline(
            IReadOnlyList<IStrategy> strategies,
            ITradeManager tradeManager,
            ExecutionSerializer serializer,
            IStrategyCoordinator coordinator)
        {
            _strategies = strategies ?? Array.Empty<IStrategy>();
            _tradeManager = tradeManager ?? throw new ArgumentNullException(nameof(tradeManager));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public async Task<PipelineTickResult> ProcessAsync(MarketData data, MarketContext context, CancellationToken ct)
        {
            var evaluations = new List<StrategyEvaluation>(_strategies.Count);
            var tickSw = Stopwatch.StartNew();

            // Regime-based strategy selection (Mixture of Experts)
            var activeStrategies = _strategies;
            try
            {
                var regimeAnalysis = context.RegimeAnalysis ?? RegimeAnalysisResult.CreateFallback(context.CurrentRegime);
                var regime = regimeAnalysis.Regime;
                var confidenceOverride = TryGetRegimeConfidenceFloor(context.Metadata);
                var confident = regimeAnalysis.IsConfident(confidenceOverride);
                var thresholdUsed = confidenceOverride ?? regime.GetConfidenceThreshold();

                activeStrategies = confident
                    ? _strategies.Where(s => regime.IsStrategyCompatible(s.Name)).ToList()
                    : _strategies.ToList();

                var meta = new Dictionary<string, object?>
                {
                    ["regime"] = regime.ToString(),
                    ["regime_display"] = regime.ToDisplayString(),
                    ["regime_confidence"] = regimeAnalysis.Confidence,
                    ["confidence_threshold"] = thresholdUsed,
                    ["profile_threshold"] = regime.GetConfidenceThreshold(),
                    ["override_threshold"] = confidenceOverride,
                    ["total_strategies"] = _strategies.Count,
                    ["active_strategies"] = activeStrategies.Count,
                    ["filter_applied"] = confident,
                    ["override_applied"] = confidenceOverride.HasValue
                };
                PipelineLogger.Log("STRATEGY", "RegimeFilter", "Regime-based strategy filtering applied", meta, null);
            }
            catch { /* if anything fails, fall back to all strategies */ }

            foreach (var strategy in activeStrategies)
            {
                ct.ThrowIfCancellationRequested();

                Signal? signal = null;
                RiskScore risk;
                var evalSw = Stopwatch.StartNew();

                try
                {
                    signal = await strategy.EvaluateAsync(data, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var meta = new Dictionary<string, object?> { ["error"] = ex.Message, ["strategy"] = strategy.Name };
                    PipelineLogger.Log("STRATEGY", "EvaluateError", $"Strategy {strategy.Name} evaluation failed", meta, null);
                    evaluations.Add(new StrategyEvaluation(strategy.Name, null, new RiskScore(0, RiskLevel.Blocked, false, "EvaluationFailure"), TimeSpan.Zero));
                    continue;
                }

                evalSw.Stop();

                try
                {
                    risk = strategy.CalculateRisk(context);
                }
                catch (Exception ex)
                {
                    PipelineLogger.Log("STRATEGY", "RiskError", $"Strategy {strategy.Name} risk calculation failed", new { error = ex.Message }, null);
                    evaluations.Add(new StrategyEvaluation(strategy.Name, null, new RiskScore(0, RiskLevel.Blocked, false, "RiskFailure"), evalSw.Elapsed));
                    continue;
                }

                evaluations.Add(new StrategyEvaluation(strategy.Name, signal, risk, evalSw.Elapsed));
                if (signal != null)
                {
                    PipelineLogger.Log(
                        "STRATEGY",
                        "Signal",
                        $"Strategy {strategy.Name} generated {signal.Action}",
                        new Dictionary<string, object?>
                        {
                            ["strategy"] = strategy.Name,
                            ["action"] = signal.Action.ToString(),
                            ["price"] = signal.Price,
                            ["confidence"] = signal.Confidence,
                            ["risk_score"] = risk.Score,
                            ["risk_level"] = risk.Level.ToString()
                        },
                        null);
                }
            }

            IReadOnlyList<StrategyCoordinatorDecision> selections;
            try
            {
                selections = await _coordinator.CoordinateAsync(context, evaluations, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PipelineLogger.Log("COORD", "Error", "Strategy coordinator failure", new { error = ex.Message }, null);
                selections = Array.Empty<StrategyCoordinatorDecision>();
            }

            foreach (var decision in selections)
            {
                var signal = decision.Evaluation.Signal;
                if (signal == null)
                {
                    continue;
                }

                PipelineLogger.Log(
                    "STRATEGY",
                    "Dispatch",
                    $"Dispatching signal for {decision.Signal.Symbol}",
                    new
                    {
                        strategy = decision.Signal.StrategyName,
                        direction = decision.Signal.Direction.ToString(),
                        symbol = decision.Signal.Symbol,
                        confidence = decision.Signal.ConfidenceScore,
                        conflicts = decision.Signal.ConflictingSignals
                    },
                    null);

                await _serializer.RunAsync(() =>
                {
                    try
                    {
                        _tradeManager.Process(signal, decision.Evaluation.Risk);
                    }
                    catch (Exception ex)
                    {
                        PipelineLogger.Log("TRADE", "ProcessError", $"TradeManager.Process failed for {decision.Signal.StrategyName}", new { error = ex.Message }, null);
                    }

                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }

            tickSw.Stop();
            var coordinatedSignals = selections.Select(s => s.Signal).ToList();
            return new PipelineTickResult(data, evaluations, tickSw.Elapsed, coordinatedSignals);
        }

        private static double? TryGetRegimeConfidenceFloor(IReadOnlyDictionary<string, object>? metadata)
        {
            if (metadata == null)
            {
                return null;
            }

            if (!metadata.TryGetValue("regime_confidence_floor", out var raw) || raw == null)
            {
                return null;
            }

            switch (raw)
            {
                case double doubleValue:
                    return doubleValue;
                case float floatValue:
                    return floatValue;
                case decimal decimalValue:
                    return (double)decimalValue;
                case int intValue:
                    return intValue;
                case long longValue:
                    return longValue;
                case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedText):
                    return parsedText;
                case IConvertible convertible:
                    if (double.TryParse(convertible.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return parsed;
                    }
                    break;
            }

            return null;
        }
    }

    public sealed record StrategyEvaluation(string StrategyName, Signal? Signal, RiskScore Risk, TimeSpan EvaluationLatency);

    public sealed record PipelineTickResult(
        MarketData MarketData,
        IReadOnlyList<StrategyEvaluation> Evaluations,
        TimeSpan TotalLatency,
        IReadOnlyList<TradingSignal> CoordinatedSignals);
}
