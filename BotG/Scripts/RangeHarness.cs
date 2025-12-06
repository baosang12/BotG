using System;
using System.Collections.Generic;
using Analysis.Wyckoff;
using DataFetcher.Models;

namespace Scripts
{
    // Lightweight synthetic harness to sanity-check RangeAnalyzer phase transitions.
    public static class RangeHarness
    {
        public static void Run()
        {
            var logger = new List<string>();
            var analyzer = new RangeAnalyzer(s => logger.Add(s))
            {
                Verbose = false
            };
            analyzer.StructuredLogger = (line, rs) => { /* no-op; could collect for analysis */ };
            var bars = new List<Bar>();
            DateTime t = DateTime.UtcNow;
            // Generate synthetic climax bar sequence (selling climax then retrace)
            double px = 100;
            for (int i = 0; i < 5; i++) bars.Add(Mk(t.AddMinutes(i), px + 0.2, px + 0.5, px - 0.5, px, 1000));
            // Climax large range down spike
            bars.Add(Mk(t.AddMinutes(5), px, px + 0.3, px - 2.0, px - 1.5, 4000));
            // Retrace leg upward pivots
            double baseLow = px - 2.0; double cur = px - 1.5;
            for (int i = 6; i < 25; i++)
            {
                cur += 0.08; double high = cur + 0.15; double low = cur - 0.10; bars.Add(Mk(t.AddMinutes(i), cur, high, low, cur + 0.05, 1500));
            }
            var climax = new ClimaxEvent { Index = 5, Bar = bars[5], Type = ClimaxType.SellingClimax, IsCluster = false };
            var rs = analyzer.AnalyzeInitialRange(bars, climax);
            analyzer.EvolveRangeState(bars, rs);
            // Extend bars to cause expansions & compression
            for (int k = 0; k < 120; k++)
            {
                var last = bars[^1];
                // oscillate inside bounds with occasional expansion attempt
                double drift = Math.Sin(k / 15.0) * 0.2;
                double mid = (rs.CurrentUpperBound.Value + rs.CurrentLowerBound.Value) / 2.0;
                double width = rs.CurrentUpperBound.Value - rs.CurrentLowerBound.Value;
                double open = mid + drift * width * 0.1;
                double high = open + width * 0.05 + (k % 37 == 0 ? width * 0.25 : 0); // sporadic expansion
                double low = open - width * 0.05 - (k % 53 == 0 ? width * 0.20 : 0);
                double close = (open + high + low) / 3.0;
                bars.Add(Mk(last.OpenTime.AddMinutes(1), open, high, low, close, 1200 + (k % 20 == 0 ? 2500 : 500)));
                analyzer.EvolveRangeState(bars, rs);
                if (rs.PhaseState == PhaseState.BreakoutExecuted || rs.PhaseState == PhaseState.FailAbort) break;
            }
            Console.WriteLine($"Harness done phase={rs.PhaseState} exp={rs.ExpansionEvents.Count} locked={rs.FinalRangeLocked} mini={rs.IsMiniPattern}");
        }

        private static Bar Mk(DateTime t, double o, double h, double l, double c, long v) => new Bar { OpenTime = t, Open = o, High = h, Low = l, Close = c, Volume = v, Tf = TimeFrame.M5 };
        // No conversion needed; ClimaxEvent uses Bar directly
    }
}
