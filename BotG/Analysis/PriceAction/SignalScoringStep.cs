using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
    /// <summary>
    /// Assigns scores to confirmed signals and filters by threshold.
    /// </summary>
    public class SignalScoringStep : IPriceActionStep
    {
        private const int Threshold = 8;

        public void Execute(IDictionary<string, IList<Bar>> multiTfBars, IList<Bar> currentBars, PriceActionContext context)
        {
            context.SignalScores.Clear();
            context.FinalSignals.Clear();
            foreach (var sig in context.ConfirmedSignals)
            {
                int score = 0;
                // example scoring logic
                if (sig.Pattern.ToString().Contains("PinBar"))
                    score += 10;
                if (sig.Pattern == CandlePattern.BullishEngulfing || sig.Pattern == CandlePattern.BearishEngulfing)
                    score += 5;
                // TODO: more scoring factors (ATR spike, volume, multi-tf)
                context.SignalScores[sig] = score;
                if (score >= Threshold)
                    context.FinalSignals.Add(sig);
            }
        }
    }
}
