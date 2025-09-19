using System;
using System.Linq;

namespace BotG.Analysis.Trend
{
    public static class TrendDetector
    {
        // Linear Regression slope trên N cây (đơn vị: giá / bar)
        public static double LinRegSlope(double[] closes, int n)
        {
            if (closes == null || closes.Length < n) return 0;
            int start = closes.Length - n;
            double[] y = closes.Skip(start).Take(n).ToArray();
            double[] x = Enumerable.Range(0, n).Select(i => (double)i).ToArray();

            double xbar = x.Average(), ybar = y.Average();
            double num = 0, den = 0;
            for (int i = 0; i < n; i++) 
            { 
                num += (x[i] - xbar) * (y[i] - ybar); 
                den += (x[i] - xbar) * (x[i] - xbar); 
            }
            return den == 0 ? 0 : num / den;
        }

        // Donchian breakout (N)
        public static (bool up, bool dn) DonchianBreak(double[] highs, double[] lows, int n)
        {
            if (highs == null || lows == null || highs.Length < n || lows.Length < n) 
                return (false, false);
            
            int start = highs.Length - n;
            double hh = highs.Skip(start).Take(n).Max();
            double ll = lows.Skip(start).Take(n).Min();
            double last = (highs[^1] + lows[^1]) * 0.5;
            return (last >= hh, last <= ll);
        }
    }
}