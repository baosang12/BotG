namespace Indicators.Volatility
{
    /// <summary>
    /// Kết quả đánh giá rule dựa trên ATR
    /// </summary>
    public class AtrRuleResult
    {
        public bool AllowTrade { get; set; }
        public double StopLossDistance { get; set; }
        public double PositionSizeFactor { get; set; }
        public string Reason { get; set; }
    }
}
