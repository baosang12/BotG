using System;
using Analysis.SmartMoneyConcept;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Represents a detected Smart Money event (BOS/CHoCH).
    /// </summary>
    public class SmartMoneySignal
    {
        public SmartMoneyType Type { get; set; }
        public DateTime Time { get; set; }
        public bool IsBullish { get; set; }
        public double Price { get; set; } // Giá tại thời điểm sweep hoặc tín hiệu
    }
}
