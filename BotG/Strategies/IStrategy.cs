using System.Threading;
using System.Threading.Tasks;

namespace Strategies
{
    /// <summary>
    /// Defines the contract for pluggable trading strategies composed within the strategy pipeline.
    /// </summary>
    public interface IStrategy
    {
        /// <summary>Display name used in logs, telemetry, and reports.</summary>
        string Name { get; }

        /// <summary>
        /// Evaluates the latest market data and returns a trading signal when actionable criteria are met.
        /// Returning null indicates no action should be taken for the current tick.
        /// </summary>
        /// <param name="data">Snapshot of market data and derived indicators.</param>
        /// <param name="ct">Cancellation token propagated from the pipeline for cooperative shutdown.</param>
        /// <returns>Actionable signal or null when holding position.</returns>
        Task<Signal?> EvaluateAsync(MarketData data, CancellationToken ct);

        /// <summary>
        /// Derives a risk score for the provided market context. The pipeline uses this to gate trade execution.
        /// </summary>
        /// <param name="context">Aggregated market and account context.</param>
        /// <returns>Risk score with gating metadata.</returns>
        RiskScore CalculateRisk(MarketContext context);
    }
}
