using System;

namespace Analysis.PriceAction
{
    public class CandlePatternSignal
    {
        public CandlePattern Pattern { get; set; }
        public DateTime Time { get; set; }
        public bool IsBullish { get; set; }
    }
}
