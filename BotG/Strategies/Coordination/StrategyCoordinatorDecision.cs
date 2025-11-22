using Strategies;

namespace BotG.Strategies.Coordination
{
    /// <summary>
    /// Represents the coordinated output pairing the scored signal with its originating evaluation.
    /// </summary>
    public sealed record StrategyCoordinatorDecision(TradingSignal Signal, StrategyEvaluation Evaluation);
}
