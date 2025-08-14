using System.Collections.Generic;
using DataFetcher.Models;
using Analysis.PriceAction;
using Config;

namespace Analysis.PriceAction
{
    /// <summary>
    /// Confirms signals across multiple timeframes.
    /// </summary>
    public class MultiTimeframeConfirmationStep : IPriceActionStep
    {
        private readonly PAConfig _config;
        public MultiTimeframeConfirmationStep(PAConfig config)
        {
            _config = config;
        }
        public void Execute(IDictionary<string, IList<Bar>> multiTfBars, IList<Bar> currentBars, PriceActionContext ctx)
        {
            var currentTf = _config.CurrentTf;
            if (!ctx.StructureByTf.TryGetValue(currentTf, out var currentStruct)) return;
            int confirmCount = 0;
            foreach (var tf in _config.HigherTimeframes)
            {
                if (tf == currentTf) continue;
                if (!ctx.StructureByTf.TryGetValue(tf, out var ms)) continue;
                if (ms.BreakOfStructure == currentStruct.BreakOfStructure || ms.ChangeOfCharacter == currentStruct.ChangeOfCharacter)
                    confirmCount++;
            }
            if (confirmCount >= _config.MinConfirmCount)
            {
                ctx.ConfirmedSignals.Clear();
                foreach (var sig in ctx.PatternSignals)
                    ctx.ConfirmedSignals.Add(sig);
            }
            else
            {
                ctx.ConfirmedSignals.Clear();
            }
        }
    }
}
