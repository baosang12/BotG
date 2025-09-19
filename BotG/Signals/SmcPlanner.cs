using System;
using Analysis.Structure;
using Analysis.Imbalance;
using Analysis.OrderBlocks;
using Analysis.Zones;
using BotG.Analysis.Trend;
using static Analysis.Structure.StructureEvent;

namespace Signals
{
    public class SmcPlanner
    {
        public class SmcConfig
        {
            public int ConfirmationsRequired = 2;
            public double VolSpikeMult = 1.8; // Strict default (was relaxed to 1.6)
            public double AtrSpikeMult = 1.2; // Strict default (was relaxed to 1.1)
            public bool UseObOrFvg = true; // Allow OB or FVG instead of requiring both
            public bool EnableFallbackTrend = false; // Only true in Test mode
            public double FallbackAtrFloor = 0.5; // Minimum ATR multiplier for fallback signals
            public int LinRegLen = 50; // Linear regression period for trend detection
            public int DonchianLen = 55; // Donchian breakout period
            public double LinRegSlopeMin = 0.0; // Minimum slope for trend confirmation
        }

        public class SmcResult
        {
            public SmcSignal Signal = SmcSignal.None;
            public double Entry = 0;
            public double StopLoss = 0;
            public double TakeProfit = 0;
            public string Reason = "";
            public List<string> NoTradeReasons = new List<string>();
            public int Confirmations = 0;
            public bool HasFvg = false;
            public bool HasOb = false;
            public bool InPdZone = false;
            public bool BosDirection = false;
        }

        public enum SmcSignal { None, LongLimit, ShortLimit }

        /// <summary>
        /// Configure SMC settings based on trade mode and overrides
        /// </summary>
        public static SmcConfig ConfigureForTradeMode(string tradeMode, double? relaxVol = null, double? relaxAtr = null, int? confirm = null)
        {
            var config = new SmcConfig();
            
            if (tradeMode.Equals("Test", StringComparison.OrdinalIgnoreCase))
            {
                // Test mode: relaxed thresholds for easier signal generation
                config.ConfirmationsRequired = 1; // Relaxed from 2
                config.VolSpikeMult = 1.5; // Relaxed from 1.8
                config.AtrSpikeMult = 1.05; // Relaxed from 1.2
                config.EnableFallbackTrend = true; // Enable fallback trend signals
                config.FallbackAtrFloor = 0.5;
            }
            // Strict mode uses default values (already set in constructor)
            
            // Apply CLI/ENV overrides if provided
            if (relaxVol.HasValue) config.VolSpikeMult = relaxVol.Value;
            if (relaxAtr.HasValue) config.AtrSpikeMult = relaxAtr.Value;
            if (confirm.HasValue) config.ConfirmationsRequired = confirm.Value;
            
            return config;
        }

        public static SmcSignal Evaluate(
            double[] open, double[] high, double[] low, double[] close, double[] volume,
            double atrM15, double atrBaselineM15,
            double lastCloseH1, PremiumDiscount.Range h1Range,
            StructureEvent h1Event,
            SmcConfig cfg,
            out double entry, out double sl, out double tp, out string reason,
            double[]? h1Closes = null, double[]? h1Highs = null, double[]? h1Lows = null)
        {
            var result = EvaluateDetailed(open, high, low, close, volume, atrM15, atrBaselineM15, lastCloseH1, h1Range, h1Event, cfg, h1Closes, h1Highs, h1Lows);
            entry = result.Entry;
            sl = result.StopLoss;
            tp = result.TakeProfit;
            reason = result.Reason;
            return result.Signal;
        }

        public static SmcResult EvaluateDetailed(
            double[] open, double[] high, double[] low, double[] close, double[] volume,
            double atrM15, double atrBaselineM15,
            double lastCloseH1, PremiumDiscount.Range h1Range,
            StructureEvent h1Event,
            SmcConfig cfg,
            double[]? h1Closes = null, double[]? h1Highs = null, double[]? h1Lows = null)
        {
            var result = new SmcResult();
            
            // Trend filter H1: chỉ trade theo {BOS/ChoCH} cùng hướng
            bool trendUp = h1Event == StructureEvent.BOS_Bull || h1Event == StructureEvent.ChoCH_Bull;
            bool trendDn = h1Event == StructureEvent.BOS_Bear || h1Event == StructureEvent.ChoCH_Bear;

            if (!trendUp && !trendDn)
            {
                result.NoTradeReasons.Add("no_trend");
            }

            var swings = MarketStructureDetector.DetectSwings(high, low, 2);
            var evt    = MarketStructureDetector.DetectEvent(swings, close.Length-1, close[^1]);
            var fvgs   = FairValueGapDetector.Detect(high, low);

            // Xác nhận: BOS + (FVG hoặc OB) + (volume spike hoặc sweep) >= cfg.ConfirmationsRequired
            int conf = 0;
            bool bosUp  = evt==StructureEvent.BOS_Bull || evt==StructureEvent.ChoCH_Bull;
            bool bosDn  = evt==StructureEvent.BOS_Bear || evt==StructureEvent.ChoCH_Bear;
            
            result.BosDirection = bosUp || bosDn;
            if (bosUp || bosDn) 
                conf++;
            else
                result.NoTradeReasons.Add("no_bos");

            bool hasBullFvg = fvgs.Exists(f=>f.IsBullish && f.Index>=close.Length-5);
            bool hasBearFvg = fvgs.Exists(f=>!f.IsBullish && f.Index>=close.Length-5);
            result.HasFvg = (bosUp && hasBullFvg) || (bosDn && hasBearFvg);
            
            // Check for OB
            var bullOb = OrderBlockDetector.FindLastSourceCandle(open, high, low, close, close.Length-1, true);
            var bearOb = OrderBlockDetector.FindLastSourceCandle(open, high, low, close, close.Length-1, false);
            bool hasOb = (bosUp && bullOb != null) || (bosDn && bearOb != null);
            result.HasOb = hasOb;

            if (cfg.UseObOrFvg)
            {
                // Allow either OB or FVG
                if (result.HasFvg || result.HasOb)
                    conf++;
                else
                    result.NoTradeReasons.Add("no_ob_or_fvg");
            }
            else
            {
                // Require both OB and FVG (original logic)
                if (result.HasFvg && result.HasOb)
                    conf++;
                else
                    result.NoTradeReasons.Add("no_ob_and_fvg");
            }

            // Volume/ATR spike (đơn giản)
            double medianVol = Median(volume, Math.Min(20, volume.Length));
            double tr = Math.Abs(high[^1]-low[^1]);
            bool volSpike = volume[^1] > medianVol * cfg.VolSpikeMult;
            bool atrSpike = atrM15     > atrBaselineM15 * cfg.AtrSpikeMult;
            
            if (volSpike && atrSpike) 
                conf++;
            else
            {
                if (!volSpike) result.NoTradeReasons.Add("low_vol");
                if (!atrSpike) result.NoTradeReasons.Add("low_atr");
            }

            // Liquidity sweep heuristic: râu vượt swing gần nhất > 0.25*ATR rồi đóng lại
            bool sweptUp=false, sweptDn=false;
            var lastHighSwing = Last(swings, true);
            var lastLowSwing  = Last(swings, false);
            if (lastHighSwing!=null && high[^1] > lastHighSwing.Price && (high[^1]-lastHighSwing.Price) > 0.25*atrM15 && close[^1] < high[^1]) sweptUp = true;
            if (lastLowSwing !=null && low[^1]  < lastLowSwing.Price  && (lastLowSwing.Price-low[^1])  > 0.25*atrM15 && close[^1] > low[^1])  sweptDn = true;
            
            if ((bosUp && sweptUp) || (bosDn && sweptDn)) 
                conf++;
            else
                result.NoTradeReasons.Add("no_sweep");

            result.Confirmations = conf;

            // PD zone filter theo H1
            bool inDiscount = PremiumDiscount.InDiscount(close[^1], h1Range);
            bool inPremium  = PremiumDiscount.InPremium(close[^1],  h1Range);
            result.InPdZone = inDiscount || inPremium;

            if (!inDiscount && !inPremium)
                result.NoTradeReasons.Add("no_pd_zone");

            if (conf < cfg.ConfirmationsRequired)
                result.NoTradeReasons.Add("low_conf");

            // Entry/SL/TP đơn giản: OB mid hoặc 50% FVG, SL swing, TP=2R
            if (trendUp && bosUp && conf >= cfg.ConfirmationsRequired && inDiscount)
            {
                var ob = OrderBlockDetector.FindLastSourceCandle(open, high, low, close, close.Length-1, true);
                result.Entry = ob!=null ? ob.Mid : MidBullFvg(fvgs);
                if (result.Entry == 0) 
                {
                    result.NoTradeReasons.Add("no_entry_level");
                    return result;
                }
                double swingLow = lastLowSwing?.Price ?? (result.Entry - atrM15*1.5);
                result.StopLoss = swingLow - 0.5*atrM15;
                double risk = result.Entry - result.StopLoss;
                result.TakeProfit = result.Entry + 2.0 * risk;
                result.Reason = $"LONG: conf={conf}, trendUp={trendUp}, inDiscount={inDiscount}";
                result.Signal = SmcSignal.LongLimit;
                return result;
            }
            if (trendDn && bosDn && conf >= cfg.ConfirmationsRequired && inPremium)
            {
                var ob = OrderBlockDetector.FindLastSourceCandle(open, high, low, close, close.Length-1, false);
                result.Entry = ob!=null ? ob.Mid : MidBearFvg(fvgs);
                if (result.Entry == 0) 
                {
                    result.NoTradeReasons.Add("no_entry_level");
                    return result;
                }
                double swingHigh = lastHighSwing?.Price ?? (result.Entry + atrM15*1.5);
                result.StopLoss = swingHigh + 0.5*atrM15;
                double risk = result.StopLoss - result.Entry;
                result.TakeProfit = result.Entry - 2.0 * risk;
                result.Reason = $"SHORT: conf={conf}, trendDn={trendDn}, inPremium={inPremium}";
                result.Signal = SmcSignal.ShortLimit;
                return result;
            }

            // Fallback trend logic (Test mode only)
            if (cfg.EnableFallbackTrend && result.Signal == SmcSignal.None && atrM15 >= atrBaselineM15 * cfg.FallbackAtrFloor)
            {
                bool fallbackTrendUp = false;
                bool fallbackTrendDn = false;
                
                // Use TrendDetector if H1 data is available with adaptive periods
                if (h1Closes != null && h1Highs != null && h1Lows != null && h1Closes.Length >= 10)
                {
                    // Adaptive periods based on available H1 data
                    int adaptiveLinReg = Math.Min(cfg.LinRegLen, Math.Max(10, h1Closes.Length - 5));
                    int adaptiveDonchian = Math.Min(cfg.DonchianLen, Math.Max(20, h1Highs.Length - 5));
                    
                    double slope = TrendDetector.LinRegSlope(h1Closes, adaptiveLinReg);
                    var (donchianUp, donchianDn) = TrendDetector.DonchianBreak(h1Highs, h1Lows, adaptiveDonchian);
                    
                    // Trend up: positive slope OR Donchian breakout up
                    fallbackTrendUp = slope > cfg.LinRegSlopeMin || donchianUp;
                    // Trend down: negative slope OR Donchian breakout down  
                    fallbackTrendDn = slope < -cfg.LinRegSlopeMin || donchianDn;
                }
                else if (trendUp || trendDn)
                {
                    // Fallback to H1 structure events if insufficient H1 data
                    fallbackTrendUp = trendUp;
                    fallbackTrendDn = trendDn;
                }

                if (fallbackTrendUp && inDiscount)
                {
                    result.Entry = close[^1] - 0.2 * atrM15;
                    result.StopLoss = result.Entry - 1.0 * atrM15;
                    result.TakeProfit = result.Entry + 2.0 * atrM15;
                    result.Reason = "fallback_trend_long";
                    result.Signal = SmcSignal.LongLimit;
                    result.NoTradeReasons.Clear();
                    return result;
                }
                else if (fallbackTrendDn && inPremium)
                {
                    result.Entry = close[^1] + 0.2 * atrM15;
                    result.StopLoss = result.Entry + 1.0 * atrM15;
                    result.TakeProfit = result.Entry - 2.0 * atrM15;
                    result.Reason = "fallback_trend_short";
                    result.Signal = SmcSignal.ShortLimit;
                    result.NoTradeReasons.Clear();
                    return result;
                }
            }

            // Add final reason if we didn't trade
            if (result.Signal == SmcSignal.None && result.NoTradeReasons.Count == 0)
            {
                result.NoTradeReasons.Add("unknown");
            }

            return result;
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