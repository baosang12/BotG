using System;
using Analysis.Structure;
using Analysis.Imbalance;
using Analysis.OrderBlocks;
using Analysis.Zones;

namespace Signals
{
    public class SmcPlanner
    {
        public class SmcConfig
        {
            public int ConfirmationsRequired = 2;
            public double VolSpikeMult = 1.8;
            public double AtrSpikeMult = 1.2;
        }

        public enum SmcSignal { None, LongLimit, ShortLimit }

        public static SmcSignal Evaluate(
            double[] open, double[] high, double[] low, double[] close, double[] volume,
            double atrM15, double atrBaselineM15,
            double lastCloseH1, PremiumDiscount.Range h1Range,
            MarketStructureDetector.StructureEvent h1Event,
            SmcConfig cfg,
            out double entry, out double sl, out double tp, out string reason)
        {
            entry = sl = tp = 0; reason = "";
            // Trend filter H1: chỉ trade theo {BOS/ChoCH} cùng hướng
            bool trendUp = h1Event == StructureEvent.BOS_Bull || h1Event == StructureEvent.ChoCH_Bull;
            bool trendDn = h1Event == StructureEvent.BOS_Bear || h1Event == StructureEvent.ChoCH_Bear;

            var swings = MarketStructureDetector.DetectSwings(high, low, 2);
            var evt    = MarketStructureDetector.DetectEvent(swings, close.Length-1, close[^1]);
            var fvgs   = FairValueGapDetector.Detect(high, low);

            // Xác nhận: BOS + (FVG hoặc OB) + (volume spike hoặc sweep) >= cfg.ConfirmationsRequired
            int conf = 0;
            bool bosUp  = evt==StructureEvent.BOS_Bull || evt==StructureEvent.ChoCH_Bull;
            bool bosDn  = evt==StructureEvent.BOS_Bear || evt==StructureEvent.ChoCH_Bear;
            if (bosUp || bosDn) conf++;

            bool hasBullFvg = fvgs.Exists(f=>f.IsBullish && f.Index>=close.Length-5);
            bool hasBearFvg = fvgs.Exists(f=>!f.IsBullish && f.Index>=close.Length-5);
            if ((bosUp && hasBullFvg) || (bosDn && hasBearFvg)) conf++;

            // Volume/ATR spike (đơn giản)
            double medianVol = Median(volume, Math.Min(20, volume.Length));
            double tr = Math.Abs(high[^1]-low[^1]);
            bool volSpike = volume[^1] > medianVol * cfg.VolSpikeMult;
            bool atrSpike = atrM15     > atrBaselineM15 * cfg.AtrSpikeMult;
            if (volSpike && atrSpike) conf++;

            // Liquidity sweep heuristic: râu vượt swing gần nhất > 0.25*ATR rồi đóng lại
            bool sweptUp=false, sweptDn=false;
            var lastHighSwing = Last(swings, true);
            var lastLowSwing  = Last(swings, false);
            if (lastHighSwing!=null && high[^1] > lastHighSwing.Price && (high[^1]-lastHighSwing.Price) > 0.25*atrM15 && close[^1] < high[^1]) sweptUp = true;
            if (lastLowSwing !=null && low[^1]  < lastLowSwing.Price  && (lastLowSwing.Price-low[^1])  > 0.25*atrM15 && close[^1] > low[^1])  sweptDn = true;
            if ((bosUp && sweptUp) || (bosDn && sweptDn)) conf++;

            // PD zone filter theo H1
            bool inDiscount = PremiumDiscount.InDiscount(close[^1], h1Range);
            bool inPremium  = PremiumDiscount.InPremium(close[^1],  h1Range);

            // Entry/SL/TP đơn giản: OB mid hoặc 50% FVG, SL swing, TP=2R
            if (trendUp && bosUp && conf >= cfg.ConfirmationsRequired && inDiscount)
            {
                var ob = OrderBlockDetector.FindLastSourceCandle(open, high, low, close, close.Length-1, true);
                entry = ob!=null ? ob.Mid : MidBullFvg(fvgs);
                if (entry == 0) return SmcSignal.None;
                double swingLow = lastLowSwing?.Price ?? (entry - atrM15*1.5);
                sl = swingLow - 0.5*atrM15;
                double risk = entry - sl;
                tp = entry + 2.0 * risk;
                reason = $"LONG: conf={conf}, trendUp={trendUp}, inDiscount={inDiscount}";
                return SmcSignal.LongLimit;
            }
            if (trendDn && bosDn && conf >= cfg.ConfirmationsRequired && inPremium)
            {
                var ob = OrderBlockDetector.FindLastSourceCandle(open, high, low, close, close.Length-1, false);
                entry = ob!=null ? ob.Mid : MidBearFvg(fvgs);
                if (entry == 0) return SmcSignal.None;
                double swingHigh = lastHighSwing?.Price ?? (entry + atrM15*1.5);
                sl = swingHigh + 0.5*atrM15;
                double risk = sl - entry;
                tp = entry - 2.0 * risk;
                reason = $"SHORT: conf={conf}, trendDn={trendDn}, inPremium={inPremium}";
                return SmcSignal.ShortLimit;
            }
            return SmcSignal.None;
        }

        private static double Median(double[] arr, int lastN)
        {
            int n = Math.Max(1, Math.Min(arr.Length, lastN));
            var slice = new double[n];
            Array.Copy(arr, arr.Length - n, slice, 0, n);
            Array.Sort(slice);
            return (n%2==1) ? slice[n/2] : 0.5*(slice[n/2-1]+slice[n/2]);
        }
        private static Analysis.Structure.Swing Last(System.Collections.Generic.List<Analysis.Structure.Swing> swings, bool isHigh)
            => swings.FindLast(s=>s.IsHigh==isHigh);

        private static double MidBullFvg(System.Collections.Generic.List<Analysis.Imbalance.Fvg> fvgs)
        {
            for (int i = fvgs.Count-1; i>=0; i--) if (fvgs[i].IsBullish) return 0.5*(fvgs[i].GapLow + fvgs[i].GapHigh);
            return 0;
        }
        private static double MidBearFvg(System.Collections.Generic.List<Analysis.Imbalance.Fvg> fvgs)
        {
            for (int i = fvgs.Count-1; i>=0; i--) if (!fvgs[i].IsBullish) return 0.5*(fvgs[i].GapLow + fvgs[i].GapHigh);
            return 0;
        }
    }
}