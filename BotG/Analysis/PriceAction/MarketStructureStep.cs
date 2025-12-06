using System.Collections.Generic;
using Analysis.PriceAction;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
    /// <summary>
    /// Step to identify market structure: swings, breakout, and change of character.
    /// </summary>
    public class MarketStructureStep : IPriceActionStep
    {
        public void Execute(IDictionary<string, IList<Bar>> multiTfBars,
                            IList<Bar> currentBars,
                            PriceActionContext context)
        {
            context.SwingHighs.Clear();
            context.SwingLows.Clear();
            int count = currentBars.Count;
            // Identify simple swing highs and lows
            for (int i = 1; i < count - 1; i++)
            {
                var prev = currentBars[i - 1];
                var curr = currentBars[i];
                var next = currentBars[i + 1];
                if (curr.High > prev.High && curr.High > next.High)
                {
                    context.SwingHighs.Add(curr);
                }
                if (curr.Low < prev.Low && curr.Low < next.Low)
                {
                    context.SwingLows.Add(curr);
                }
            }
            // TODO: detect Break of Structure (BOS) and Change of Character (CHoCH)
        }
    }
}
