using System;
using System.Collections.Generic;
using System.Linq;
using BotG.Runtime.Logging;
using cAlgo.API;

namespace BotG.Strategies.Coordination
{
    /// <summary>
    /// Resolves conflicting trading signals across strategies for the same symbol.
    /// </summary>
    public sealed class ConflictResolver
    {
        private StrategyCoordinationConfig _config;

        public ConflictResolver(StrategyCoordinationConfig config)
        {
            UpdateConfig(config);
        }

        public void UpdateConfig(StrategyCoordinationConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _config = config;
        }

        public IReadOnlyList<StrategyCoordinatorDecision> Resolve(IReadOnlyList<StrategyCoordinatorDecision> decisions)
        {
            if (decisions == null) throw new ArgumentNullException(nameof(decisions));
            if (decisions.Count == 0) return Array.Empty<StrategyCoordinatorDecision>();

            var grouped = decisions.GroupBy(d => d.Signal.Symbol, StringComparer.OrdinalIgnoreCase);
            var resolved = new List<StrategyCoordinatorDecision>();

            foreach (var group in grouped)
            {
                var groupList = group
                    .OrderByDescending(d => d.Signal.ConfidenceScore)
                    .ThenBy(d => d.Signal.GeneratedTime)
                    .ToList();

                if (groupList.Count == 0)
                {
                    continue;
                }

                if (_config.PreventOppositeSignals)
                {
                    LogHedgingDetection(group.Key, groupList);

                    var bestPerDirection = groupList
                        .GroupBy(d => d.Signal.Direction)
                        .Select(g => g
                            .OrderByDescending(d => d.Signal.ConfidenceScore)
                            .ThenBy(d => d.Signal.GeneratedTime)
                            .First())
                        .ToList();

                    var best = bestPerDirection
                        .OrderByDescending(d => d.Signal.ConfidenceScore)
                        .ThenBy(d => d.Signal.GeneratedTime)
                        .First();

                    if (best != null)
                    {
                        PipelineLogger.Log(
                            "COORD",
                            "CONFLICT/PreventedHedging",
                            $"Selected single direction for {group.Key}",
                            new
                            {
                                symbol = group.Key,
                                direction = best.Signal.Direction.ToString(),
                                confidence = best.Signal.ConfidenceScore
                            },
                            null);
                        resolved.Add(best);
                    }

                    continue;
                }

                var accepted = new List<StrategyCoordinatorDecision>();

                foreach (var candidate in groupList)
                {
                    if (accepted.Count >= _config.MaxSignalsPerSymbol)
                    {
                        foreach (var winner in accepted)
                        {
                            winner.Signal.ConflictingSignals.Add(candidate.Signal.StrategyName);
                        }
                        continue;
                    }

                    var conflicting = accepted.FirstOrDefault(a => a.Signal.Direction != candidate.Signal.Direction);
                    if (conflicting == null)
                    {
                        accepted.Add(candidate);
                        continue;
                    }

                    if (candidate.Signal.ConfidenceScore > conflicting.Signal.ConfidenceScore + _config.ConflictMargin)
                    {
                        conflicting.Signal.ConflictingSignals.Add(candidate.Signal.StrategyName);
                        accepted.Clear();
                        accepted.Add(candidate);
                    }
                    else
                    {
                        conflicting.Signal.ConflictingSignals.Add(candidate.Signal.StrategyName);
                    }
                }

                resolved.AddRange(accepted);
            }

            if (resolved.Count == 0)
            {
                return Array.Empty<StrategyCoordinatorDecision>();
            }

            return resolved
                .OrderByDescending(r => r.Signal.ConfidenceScore)
                .ThenBy(r => r.Signal.GeneratedTime)
                .Take(_config.MaxSignalsPerTick)
                .ToList();
        }

        private static void LogHedgingDetection(string symbol, IReadOnlyCollection<StrategyCoordinatorDecision> signals)
        {
            if (signals == null || signals.Count < 2)
            {
                return;
            }

            var buyCount = signals.Count(s => s.Signal.Direction == TradeType.Buy);
            var sellCount = signals.Count(s => s.Signal.Direction == TradeType.Sell);

            if (buyCount > 0 && sellCount > 0)
            {
                PipelineLogger.Log(
                    "COORD",
                    "HEDGING/Detected",
                    $"Opposite signals detected for {symbol}",
                    new
                    {
                        symbol,
                        buySignals = buyCount,
                        sellSignals = sellCount,
                        total = signals.Count
                    },
                    null);
            }
        }
    }
}
