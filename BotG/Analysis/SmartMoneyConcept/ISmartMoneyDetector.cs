using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Interface cho các bộ phát hiện Smart Money (Break of Structure, Change of Character, etc.).
    /// </summary>
    public interface ISmartMoneyDetector
    {
        SmartMoneyType Type { get; }
        bool IsEnabled { get; set; }
        /// <summary>
        /// Phát hiện smart money trên chuỗi nến.
        /// </summary>
        bool Detect(IList<Bar> bars, out SmartMoneySignal signal);
    }
}
