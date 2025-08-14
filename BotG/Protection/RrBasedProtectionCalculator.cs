namespace Protection
{
    // Default implementation: TP = SL * RR; SL pips comes from configuredStopLossPips.
    // Can be replaced with structure/ATR-based calculators later.
    public class RrBasedProtectionCalculator : IProtectionCalculator
    {
        public ProtectionResult Compute(ProtectionInputs inputs)
        {
            var slPips = inputs.ConfiguredStopLossPips;
            if (slPips <= 0) slPips = 1; // guard
            var tpPips = (int)System.Math.Round(slPips * inputs.RiskRewardRatio);

            double slPrice;
            double tpPrice;
            if (inputs.TradeType == cAlgo.API.TradeType.Buy)
            {
                slPrice = inputs.EntryPrice - slPips * inputs.PipSize;
                tpPrice = inputs.EntryPrice + tpPips * inputs.PipSize;
            }
            else
            {
                slPrice = inputs.EntryPrice + slPips * inputs.PipSize;
                tpPrice = inputs.EntryPrice - tpPips * inputs.PipSize;
            }

            return new ProtectionResult
            {
                StopLossPips = slPips,
                TakeProfitPips = tpPips,
                StopLossPrice = slPrice,
                TakeProfitPrice = tpPrice,
                Notes = "RR-based protection"
            };
        }
    }
}
