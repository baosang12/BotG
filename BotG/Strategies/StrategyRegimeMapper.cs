using BotG.MarketRegime;

namespace Strategies
{
    /// <summary>
    /// Simple name-based strategy suitability mapping for regime-aware routing.
    /// This can be replaced with attribute or metadata-driven mapping later.
    /// </summary>
    public static class StrategyRegimeMapper
    {
        public static bool IsSuitable(string strategyName, RegimeType regime)
        {
            return regime.IsStrategyCompatible(strategyName);
        }
    }
}
