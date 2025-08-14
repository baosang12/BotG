using System.Collections.Generic;
using DataFetcher.Models;
using Analysis.PriceAction;
using Config;

namespace Analysis.PriceAction
{
    public class MultiTimeframeStructureStep : IPriceActionStep
    {
        private readonly PAConfig _config;
        private readonly MarketStructureDetector _detector;
        public MultiTimeframeStructureStep(PAConfig config, MarketStructureDetector detector)
        {
            _config = config;
            _detector = detector;
        }
        public void Execute(IDictionary<string, IList<Bar>> multiTfBars, IList<Bar> currentBars, PriceActionContext ctx)
        {
            foreach (var tf in _config.HigherTimeframes)
            {
                if (!multiTfBars.TryGetValue(tf, out var bars) || bars == null) continue;
                var ms = new PriceActionContext.MarketStructure
                {
                    SwingHighs = _detector.GetSwingHighs(bars),
                    SwingLows = _detector.GetSwingLows(bars),
                    BreakOfStructure = _detector.GetBreakOfStructure(bars),
                    ChangeOfCharacter = _detector.GetChangeOfCharacter(bars),
                    BosHistory = _detector.GetBosHistory(bars),
                    ChochHistory = _detector.GetChochHistory(bars),
                    TrendStrength = _detector.GetTrendStrength(bars),
                    Volatility = _detector.GetVolatility(bars),
                    Range = _detector.GetRange(bars),
                    VolumeProfile = _detector.GetVolumeProfile(bars),
                    ConfidenceScore = _detector.GetConfidenceScore(bars),
                    State = _detector.GetMarketState(bars)
                };
                ctx.StructureByTf[tf] = ms;
            }
        }
    }
}
