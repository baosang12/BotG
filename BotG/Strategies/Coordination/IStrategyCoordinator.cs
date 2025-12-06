using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Strategies;

namespace BotG.Strategies.Coordination
{
    public interface IStrategyCoordinator
    {
        Task<IReadOnlyList<StrategyCoordinatorDecision>> CoordinateAsync(
            MarketContext context,
            IReadOnlyList<StrategyEvaluation> evaluations,
            CancellationToken cancellationToken);

        void UpdateConfiguration(StrategyCoordinationConfig config);
    }
}
