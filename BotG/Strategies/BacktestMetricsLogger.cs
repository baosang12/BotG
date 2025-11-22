using System;
using System.Collections.Generic;
using System.Linq;
using BotG.MarketRegime;
using BotG.Runtime.Logging;
using BotG.Strategies.Coordination;

namespace Strategies
{
    /// <summary>
    /// Centralizes additional telemetry for cTrader backtests so we can validate regime-aware routing quickly.
    /// Logs only when Mode/backtest is active to avoid spamming live runs.
    /// </summary>
    internal static class BacktestMetricsLogger
    {
        private static bool IsBacktestMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                return false;
            }

            return mode.Equals("backtest", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("simulation", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("sim", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveMode(MarketContext context)
        {
            if (context.Metadata != null &&
                context.Metadata.TryGetValue("mode", out var value) &&
                value is string modeFromContext && !string.IsNullOrWhiteSpace(modeFromContext))
            {
                return modeFromContext;
            }

            return Environment.GetEnvironmentVariable("BOTG_MODE") ??
                   Environment.GetEnvironmentVariable("Mode") ??
                   string.Empty;
        }

        public static void LogTick(
            MarketContext context,
            IReadOnlyList<IStrategy> activeStrategies,
            IReadOnlyList<StrategyEvaluation> evaluations,
            IReadOnlyList<StrategyCoordinatorDecision> selections)
        {
            var mode = ResolveMode(context);
            if (!IsBacktestMode(mode))
            {
                return;
            }

            var regime = context.RegimeAnalysis;
            var riskMultiplier = context.RiskMetrics != null && context.RiskMetrics.TryGetValue("regime_risk_multiplier", out var multiplier)
                ? multiplier
                : context.CurrentRegime.GetRiskMultiplier();

            var payload = new Dictionary<string, object?>
            {
                ["mode"] = mode,
                ["symbol"] = context.LatestTick.Symbol,
                ["regime"] = (regime?.Regime ?? context.CurrentRegime).ToString(),
                ["confidence"] = regime?.Confidence,
                ["risk_multiplier"] = riskMultiplier,
                ["active_strategies"] = activeStrategies.Select(s => s.Name).ToArray(),
                ["total_strategies"] = evaluations.Count,
                ["evaluated_signals"] = evaluations.Count(e => e.Signal != null),
                ["confirmed_signals"] = selections.Count,
                ["timestamp"] = context.LatestTick.TimestampUtc
            };

            PipelineLogger.Log("BACKTEST", "Tick", "Backtest regime snapshot", payload, null);
        }

        public static void LogDecision(StrategyCoordinatorDecision decision, MarketContext context)
        {
            var mode = ResolveMode(context);
            if (!IsBacktestMode(mode))
            {
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["mode"] = mode,
                ["strategy"] = decision.Signal.StrategyName,
                ["direction"] = decision.Signal.Direction.ToString(),
                ["confidence"] = decision.Signal.ConfidenceScore,
                ["regime"] = decision.Signal.RegimeContext.ToString(),
                ["risk_multiplier"] = context.RiskMetrics != null && context.RiskMetrics.TryGetValue("regime_risk_multiplier", out var multiplier)
                    ? multiplier
                    : context.CurrentRegime.GetRiskMultiplier(),
                ["weight"] = decision.Signal.SignalMetrics != null && decision.Signal.SignalMetrics.TryGetValue("strategy_weight", out var weight)
                    ? weight
                    : (double?)null,
                ["conflicts"] = decision.Signal.ConflictingSignals.ToArray(),
                ["time_since_last_trade"] = decision.Signal.TimeSinceLastTrade.TotalSeconds
            };

            PipelineLogger.Log("BACKTEST", "Decision", "Coordinator decision snapshot", payload, null);
        }
    }
}
