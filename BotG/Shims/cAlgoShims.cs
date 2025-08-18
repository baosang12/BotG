namespace cAlgo.API
{
    // Minimal Symbol shim to support RiskManager.SetPointValueFromSymbol in unit tests/build
    public class Symbol
    {
        // Optional properties; may not be present in real API but used reflectively
        public double TickSize { get; set; }
        public double TickValue { get; set; }
        public double PipSize { get; set; }
        public double PipValue { get; set; }
        public double LotSize { get; set; }

        public double LotsToVolumeInUnits(double lots) => lots * LotSize;
    }
}
