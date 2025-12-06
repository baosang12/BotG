using System;
using System.Collections.Generic;
using BotG.MarketRegime;
using DataFetcher.Models;
using Strategies;

namespace BotG.Strategies.Coordination
{
    public interface IEvidenceAssembler
    {
        StrategyFusionSignal? CreateSignal(
            StrategyEvaluation evaluation,
            MarketContext context,
            StrategyMetadata? metadata);
    }

    public sealed class DefaultEvidenceAssembler : IEvidenceAssembler
    {
        public StrategyFusionSignal? CreateSignal(
            StrategyEvaluation evaluation,
            MarketContext context,
            StrategyMetadata? metadata)
        {
            if (evaluation == null)
            {
                throw new ArgumentNullException(nameof(evaluation));
            }

            var signal = evaluation.Signal;
            if (signal == null)
            {
                return null;
            }

            if (signal.Action == TradeAction.None)
            {
                return null;
            }

            var evidence = BuildEvidence(signal, evaluation);
            var timeframe = metadata?.PrimaryTimeframe ?? TimeFrame.H1;
            var strategyId = string.IsNullOrWhiteSpace(signal.StrategyName)
                ? evaluation.StrategyName
                : signal.StrategyName!;

            return new StrategyFusionSignal(
                strategyId,
                signal.Action,
                Clamp(signal.Confidence),
                evidence,
                signal.TimestampUtc,
                timeframe);
        }

        private static IReadOnlyDictionary<string, double>? BuildEvidence(Signal signal, StrategyEvaluation evaluation)
        {
            var buffer = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["confidence"] = Clamp(signal.Confidence),
                ["risk_score"] = evaluation.Risk.Score
            };

            if (signal.Indicators != null)
            {
                foreach (var kvp in signal.Indicators)
                {
                    if (double.IsNaN(kvp.Value) || double.IsInfinity(kvp.Value))
                    {
                        continue;
                    }

                    buffer[$"ind_{kvp.Key}"] = kvp.Value;
                }
            }

            if (evaluation.Risk.Factors != null)
            {
                foreach (var kvp in evaluation.Risk.Factors)
                {
                    buffer[$"risk_{kvp.Key}"] = kvp.Value;
                }
            }

            return buffer;
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }

            return Math.Clamp(value, 0.0, 1.0);
        }
    }
}
