using System.Collections.Generic;
using Analysis.PriceAction;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
    /// <summary>
    /// Detects candle patterns using CandlePatternAnalyzer.
    /// </summary>
    public class PatternDetectionStep : IPriceActionStep
    {
        private readonly CandlePatternAnalyzer _analyzer = new CandlePatternAnalyzer();

        public void Execute(IDictionary<string, IList<Bar>> multiTfBars, IList<Bar> currentBars, PriceActionContext context)
        {
            context.PatternSignals.Clear();
            // use ATR from context if available, else default 0
            var signals = _analyzer.Analyze(currentBars);
            foreach (var sig in signals)
                context.PatternSignals.Add(sig);
        }
    }
}
