using System;
using System.Collections.Generic;
using System.Linq;

namespace Analysis.Structure
{
    public enum StructureEvent { None, BOS_Bull, BOS_Bear, ChoCH_Bull, ChoCH_Bear }

    public class Swing
    {
        public int Index;
        public DateTime Time;
        public double Price;
        public bool IsHigh; // true = swing high, false = swing low
    }

    public class MarketStructureDetector
    {
        // Fractal 2-2 (configurable)
        public static List<Swing> DetectSwings(IList<double> highs, IList<double> lows, int lb = 2)
        {
            var swings = new List<Swing>();
            for (int i = lb; i < highs.Count - lb; i++)
            {
                bool isHigh = true, isLow = true;
                for (int k = 1; k <= lb; k++)
                {
                    if (!(highs[i] > highs[i - k] && highs[i] >= highs[i + k])) isHigh = false;
                    if (!(lows[i] < lows[i - k] && lows[i] <= lows[i + k]))  isLow  = false;
                    if (!isHigh && !isLow) break;
                }
                if (isHigh) swings.Add(new Swing{ Index=i, Time=DateTime.MinValue, Price=highs[i], IsHigh=true });
                if (isLow)  swings.Add(new Swing{ Index=i, Time=DateTime.MinValue, Price=lows[i],  IsHigh=false });
            }
            return swings.OrderBy(s=>s.Index).ToList();
        }

        public static StructureEvent DetectEvent(List<Swing> swings, int lastIndex, double lastClose)
        {
            var highs = swings.Where(s=>s.IsHigh).OrderByDescending(s=>s.Index).Take(2).ToList();
            var lows  = swings.Where(s=>!s.IsHigh).OrderByDescending(s=>s.Index).Take(2).ToList();

            bool bosBull = highs.Count>=1 && lastClose > highs[0].Price;
            bool bosBear = lows.Count>=1  && lastClose < lows[0].Price;

            if (bosBull && lows.Count>=2 && highs.Count>=1 && highs[0].Index > lows[0].Index) return StructureEvent.ChoCH_Bull;
            if (bosBear && highs.Count>=2 && lows.Count>=1  && lows[0].Index  > highs[0].Index) return StructureEvent.ChoCH_Bear;

            if (bosBull) return StructureEvent.BOS_Bull;
            if (bosBear) return StructureEvent.BOS_Bear;
            return StructureEvent.None;
        }
    }
}