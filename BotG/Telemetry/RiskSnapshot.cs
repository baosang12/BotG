using System;

namespace Telemetry
{
    public class RiskSnapshot
    {
        public DateTime Timestamp { get; set; }
        public double Equity { get; set; }
        public double Balance { get; set; }
        public double Margin { get; set; }
        public string RiskState { get; set; } = "UNKNOWN";
        public double DailyR { get; set; }
        public double DailyPct { get; set; }
    }
}
