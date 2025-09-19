namespace Risk
{
    public class RiskManager
    {
        public double BaseRiskPct = 0.01;   // 1%
        public double MinRiskPct  = 0.005;  // 0.5%
        public double MaxRiskPct  = 0.015;  // 1.5%
        public double DailyStopR  = -3.0;   // -3R
        public double DailyStopPct= -0.05;  // -5%

        private double _dailyR = 0.0;
        private System.DateTime _dailyKey = System.DateTime.UtcNow.Date;

        public void OnTradeClosed(double pnlCurrency, double riskCurrency)
        {
            if (System.DateTime.UtcNow.Date != _dailyKey) { _dailyKey = System.DateTime.UtcNow.Date; _dailyR = 0; }
            double r = (riskCurrency != 0) ? (pnlCurrency / riskCurrency) : 0;
            _dailyR += r;
        }

        public bool IsHalted(double dailyPctReturn)
        {
            if (System.DateTime.UtcNow.Date != _dailyKey) { _dailyKey = System.DateTime.UtcNow.Date; _dailyR = 0; }
            return (_dailyR <= DailyStopR) || (dailyPctReturn <= DailyStopPct);
        }

        public double ComputeRiskPct(double atr, double atrBaseline)
        {
            if (atrBaseline <= 0) return BaseRiskPct;
            double scale = atr / atrBaseline; // vol cao → giảm risk
            double rpct = BaseRiskPct / System.Math.Max(0.75, System.Math.Min(1.5, scale));
            return System.Math.Max(MinRiskPct, System.Math.Min(MaxRiskPct, rpct));
        }

        /// <summary>
        /// Apply Test mode risk clamping for safer testing
        /// </summary>
        public void ApplyTestModeClamp()
        {
            BaseRiskPct = System.Math.Min(BaseRiskPct, 0.005); // Max 0.5% in test mode
            DailyStopR = System.Math.Max(DailyStopR, -1.5); // Limit daily stop to -1.5R in test mode
        }
    }
}