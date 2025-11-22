namespace BotG.MarketRegime
{
    /// <summary>
    /// Configuration parameters for market regime detection.
    /// All thresholds are adjustable to adapt to different symbols and market conditions.
    /// </summary>
    public class RegimeConfiguration
    {
        /// <summary>
        /// ADX threshold for identifying strong trending markets.
        /// Default: 25.0 (values above indicate trend strength).
        /// </summary>
        public double AdxTrendThreshold { get; set; } = 25.0;

        /// <summary>
        /// ADX threshold for identifying ranging/sideways markets.
        /// Default: 20.0 (values below indicate weak or no trend).
        /// </summary>
        public double AdxRangeThreshold { get; set; } = 20.0;

        /// <summary>
        /// Multiplier for ATR to detect volatile conditions.
        /// Default: 1.5 (current ATR > 1.5× average → Volatile).
        /// </summary>
        public double VolatilityThreshold { get; set; } = 1.5;

        /// <summary>
        /// Multiplier for ATR to detect calm/low-volatility conditions.
        /// Default: 0.5 (current ATR &lt; 0.5× average → Calm).
        /// </summary>
        public double CalmThreshold { get; set; } = 0.5;

        /// <summary>
        /// Minimum confidence required before regime-based strategy filtering is applied.
        /// Lower values make the router more permissive during backtests.
        /// Default: 0.5.
        /// </summary>
        public double MinimumRegimeConfidence { get; set; } = 0.5;

        /// <summary>
        /// Number of historical bars to analyze for regime classification.
        /// Default: 50 (provides sufficient context for ADX/ATR calculations).
        /// </summary>
        public int LookbackPeriod { get; set; } = 50;

        /// <summary>
        /// Period for ADX calculation (DirectionalMovementIndex).
        /// Default: 14 (industry standard).
        /// </summary>
        public int AdxPeriod { get; set; } = 14;

        /// <summary>
        /// Period for ATR (Average True Range) calculation.
        /// Default: 14 (industry standard).
        /// </summary>
        public int AtrPeriod { get; set; } = 14;

        /// <summary>
        /// Period for Bollinger Bands SMA baseline.
        /// Default: 20 (industry standard).
        /// </summary>
        public int BollingerPeriod { get; set; } = 20;

        /// <summary>
        /// Standard deviations for Bollinger Bands width.
        /// Default: 2.0 (industry standard).
        /// </summary>
        public double BollingerDeviations { get; set; } = 2.0;

        /// <summary>
        /// Bollinger Band Width percentage threshold to classify volatility expansion.
        /// Default: 8.0 (% width) – widths above indicate explosive / volatile conditions.
        /// </summary>
        public double BollingerVolatilityThreshold { get; set; } = 8.0;

        /// <summary>
        /// Bollinger Band Width percentage threshold to classify squeeze / calm conditions.
        /// Default: 3.0 (% width) – widths below indicate compressed / low-volatility conditions.
        /// </summary>
        public double BollingerCalmThreshold { get; set; } = 3.0;

        /// <summary>
        /// Enables Bollinger Band Width contribution to volatility & calm regime classification.
        /// Set false to rely purely on ATR.
        /// </summary>
        public bool UseBollingerInClassification { get; set; } = true;
    }
}
