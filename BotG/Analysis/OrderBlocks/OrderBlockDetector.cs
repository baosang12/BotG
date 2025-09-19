using System.Collections.Generic;

namespace Analysis.OrderBlocks
{
    public class OrderBlock
    {
        public int Index;
        public double Open, High, Low, Close;
        public bool IsBullish;   // bullish OB = nến giảm cuối trước BOS lên
        public double Mid => (High+Low)/2.0;
    }

    public static class OrderBlockDetector
    {
        public static OrderBlock FindLastSourceCandle(IList<double> open, IList<double> high, IList<double> low, IList<double> close, int bosIndex, bool bosBull)
        {
            if (bosIndex<=0) return null;
            if (bosBull)
            {
                for (int i = bosIndex-1; i >= 0; i--)
                    if (close[i] < open[i]) return new OrderBlock{ Index=i, Open=open[i], High=high[i], Low=low[i], Close=close[i], IsBullish=true };
            }
            else
            {
                for (int i = bosIndex-1; i >= 0; i--)
                    if (close[i] > open[i]) return new OrderBlock{ Index=i, Open=open[i], High=high[i], Low=low[i], Close=close[i], IsBullish=false };
            }
            return null;
        }
    }
}