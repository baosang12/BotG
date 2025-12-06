using BotG.MarketRegime;
using BotG.MultiTimeframe;
using BotG.Runtime.Preprocessor;

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
            MarketRegimeDetector? regimeDetector,
            IPreprocessorStrategyDataBridge? preprocessorBridge)
        {
            TimeframeManager = timeframeManager;
            TimeframeSynchronizer = timeframeSynchronizer;
            SessionAnalyzer = sessionAnalyzer;
            RegimeDetector = regimeDetector;
            PreprocessorBridge = preprocessorBridge;
        }

        public TimeframeManager? TimeframeManager { get; }
        public TimeframeSynchronizer? TimeframeSynchronizer { get; }
        public SessionAwareAnalyzer? SessionAnalyzer { get; }
        public MarketRegimeDetector? RegimeDetector { get; }
        public IPreprocessorStrategyDataBridge? PreprocessorBridge { get; }
    }
}
