using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace DataFetcher.Caching
{
    /// <summary>
    /// Cache bar theo từng timeframe, dùng CircularBuffer cho truy xuất nhanh.
    /// </summary>
    public class BarCache
    {
        private Dictionary<TimeFrame, CircularBuffer<Bar>> cache = new();
        private int _bufferSize;

        public BarCache(int bufferSize = 500)
        {
            _bufferSize = bufferSize;
        }

        /// <summary>
        /// Thêm bar vào cache theo timeframe.
        /// </summary>
        public void AddBar(TimeFrame tf, Bar bar)
        {
            if (!cache.ContainsKey(tf))
                cache[tf] = new CircularBuffer<Bar>(_bufferSize);
            cache[tf].Add(bar);
        }

        /// <summary>
        /// Lấy tất cả bar của timeframe.
        /// </summary>
        public IEnumerable<Bar> GetBars(TimeFrame tf)
        {
            if (!cache.ContainsKey(tf)) return Array.Empty<Bar>();
            return cache[tf];
        }

        /// <summary>
        /// Lấy bar cuối cùng của timeframe.
        /// </summary>
        public Bar GetLastBar(TimeFrame tf)
        {
            if (!cache.ContainsKey(tf) || cache[tf].Count == 0) return null;
            return cache[tf][cache[tf].Count - 1];
        }
    }
}
