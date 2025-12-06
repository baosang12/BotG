namespace Protection
{
    public sealed class ProtectionInputs
    {
        public cAlgo.API.TradeType TradeType { get; set; }
        public double EntryPrice { get; set; }
        public double PipSize { get; set; }
        public double RiskRewardRatio { get; set; }
        public int ConfiguredStopLossPips { get; set; } // Optional; 0 means derive purely from structure/buffer
        // Optional market/context metrics (pips)
        public double AtrPips { get; set; }
        public double SpreadPips { get; set; }
        // Optional structure anchors (price)
        public double StructureLowPrice { get; set; }
        public double StructureHighPrice { get; set; }
    }

    public sealed class ProtectionResult
    {
        public int StopLossPips { get; set; }
        public int TakeProfitPips { get; set; }
        public double StopLossPrice { get; set; }
        public double TakeProfitPrice { get; set; }
        public string Notes { get; set; }
    }

    public interface IProtectionCalculator
    {
        ProtectionResult Compute(ProtectionInputs inputs);
    }
}
