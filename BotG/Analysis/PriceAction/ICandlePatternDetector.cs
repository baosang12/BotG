using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
    public interface ICandlePatternDetector
    {
        CandlePattern Pattern { get; }
        bool IsEnabled { get; set; }
        bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal);
    }
}
