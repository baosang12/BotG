using System;
using Telemetry;

namespace Execution
{
    public static class FeeCalculator
    {
        // notional = price * size * pointValuePerLotOrUnit
        public static double ComputeFee(double price, double size, TelemetryConfig cfg, double pointValuePerUnit = 1.0)
        {
            if (cfg == null) return 0.0;
            var exec = cfg.Execution ?? new ExecutionConfig();
            double notional = Math.Abs(price * size * (pointValuePerUnit <= 0 ? 1.0 : pointValuePerUnit));
            return Math.Max(0.0, exec.FeePerTrade) + Math.Max(0.0, exec.FeePercent) * notional;
        }

        public static (double entryAdj, double exitAdj) ComputeSpreadAdjustments(TelemetryConfig cfg, double pipValue = 0.0001)
        {
            var exec = cfg.Execution ?? new ExecutionConfig();
            if (exec.SpreadPips <= 0) return (0.0, 0.0);
            double half = exec.SpreadPips * pipValue / 2.0;
            // For BUY then SELL: pay +half on entry, receive -half on exit (worse P&L)
            return (half, -half);
        }
    }
}
