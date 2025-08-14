using System;

namespace Analysis.Wyckoff
{
    /// <summary>
    /// Sự kiện Spring (giả break đáy) trong pha Accumulation.
    /// </summary>
    public class SpringEvent
    {
        /// <summary>
        /// Thời gian sự kiện Spring.
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// Cờ xác định sự kiện Spring có hợp lệ hay không.
        /// </summary>
        public bool IsValidSpring { get; set; }
    }
}
