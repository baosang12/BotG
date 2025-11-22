using BotG.MarketRegime;
using BotG.MultiTimeframe;

namespace Strategies.Registry
{
    public enum StrategyDependency
    {
        MultiTimeframe,
        RegimeDetector,
        SessionAnalyzer
    }

    public sealed class StrategyFactoryContext
    {
        public StrategyFactoryContext(
            TimeframeManager? timeframeManager,
            TimeframeSynchronizer? timeframeSynchronizer,
            SessionAwareAnalyzer? sessionAnalyzer,
            MarketRegimeDetector? regimeDetector)
        {
            TimeframeManager = timeframeManager;
            TimeframeSynchronizer = timeframeSynchronizer;
            SessionAnalyzer = sessionAnalyzer;
            RegimeDetector = regimeDetector;
        }

        public TimeframeManager? TimeframeManager { get; }
        public TimeframeSynchronizer? TimeframeSynchronizer { get; }
        public SessionAwareAnalyzer? SessionAnalyzer { get; }
        public MarketRegimeDetector? RegimeDetector { get; }
    }
}
