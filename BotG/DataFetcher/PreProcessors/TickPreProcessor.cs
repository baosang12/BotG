using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace DataFetcher.PreProcessors
{
    /// <summary>
    /// Tiền xử lý tick: chuẩn hóa timestamp, loại duplicate, gap filling, phát event TickCleaned.
    /// </summary>
    public class TickPreProcessor
    {
        public event EventHandler<Tick> TickCleaned;
        private Tick _lastTick;
        private readonly HashSet<DateTime> _seenTimestamps = new();

        /// <summary>
        /// Nhận tick thô, xử lý và phát event tick sạch.
        /// </summary>
        public void Process(Tick tick)
        {
            // Chuẩn hóa timestamp về UTC
            tick.Timestamp = tick.Timestamp.ToUniversalTime();

            // Loại duplicate theo timestamp
            if (_seenTimestamps.Contains(tick.Timestamp))
                return;
            _seenTimestamps.Add(tick.Timestamp);

            // Gap filling: nếu có gap lớn, có thể phát tick giả (tùy logic)
            // ...

            // Phát event tick sạch
            TickCleaned?.Invoke(this, tick);
            _lastTick = tick;
        }
    }
}
