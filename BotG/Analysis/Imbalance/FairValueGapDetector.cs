using System.Collections.Generic;

namespace Analysis.Imbalance
{
    public class Fvg
    {
        public int Index;      // bar trung tâm (i)
        public double GapLow;  // bullish: Low[i+1] > High[i-1]
        public double GapHigh;
        public bool IsBullish;
    }

    public static class FairValueGapDetector
    {
        public static List<Fvg> Detect(IList<double> highs, IList<double> lows)
        {
            var res = new List<Fvg>();
            for (int i = 1; i < highs.Count-1; i++)
            {
                if (lows[i+1] > highs[i-1])
                    res.Add(new Fvg{ Index=i, GapLow=highs[i-1], GapHigh=lows[i+1], IsBullish=true });
                else if (highs[i+1] < lows[i-1])
                    res.Add(new Fvg{ Index=i, GapLow=highs[i+1], GapHigh=lows[i-1], IsBullish=false });
            }
            return res;
        }
    }
}