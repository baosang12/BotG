using System;
using DataFetcher.Models;
using Strategies;

namespace Analysis.Wyckoff
{
    public class WyckoffTradePlanner
    {
        public TradeSignal Plan(WyckoffPatternResult result, bool inPosition, double lastEntry = 0, double rr = 2.0, double atr = 0)
        {
            if (!inPosition && result.LPSorLPSY != null && result.LPSorLPSY.IsValid)
            {
                if (result.IsAccumulation && !result.LPSorLPSY.IsLPSY)
                {
                    double entry = result.LPSorLPSY.Price;
                    double stop = result.ST != null ? result.ST.Price : (atr > 0 ? entry - atr * 1.5 : entry * 0.99);
                    double tp = entry + (entry - stop) * rr;
                    return new TradeSignal
                    {
                        Action = TradeAction.Buy,
                        Price = entry,
                        StopLoss = stop,
                        TakeProfit = tp
                    };
                }
                else if (result.IsDistribution && result.LPSorLPSY.IsLPSY)
                {
                    double entry = result.LPSorLPSY.Price;
                    double stop = result.ST != null ? result.ST.Price : (atr > 0 ? entry + atr * 1.5 : entry * 1.01);
                    double tp = entry - (stop - entry) * rr;
                    return new TradeSignal
                    {
                        Action = TradeAction.Sell,
                        Price = entry,
                        StopLoss = stop,
                        TakeProfit = tp
                    };
                }
            }
            if (inPosition && result.CurrentPhase == WyckoffPatternPhase.PhaseE)
            {
                return new TradeSignal
                {
                    Action = TradeAction.Exit
                };
            }
            return null;
        }
    }
}
