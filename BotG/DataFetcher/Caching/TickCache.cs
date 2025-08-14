using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace DataFetcher.Caching
{
    /// <summary>
    /// Cache tick gần nhất bằng CircularBuffer, phục vụ truy xuất nhanh cho downstream.
    /// </summary>
    public class TickCache
    {
        private CircularBuffer<Tick> buffer;
        public TickCache(int capacity)
        {
            buffer = new CircularBuffer<Tick>(capacity);
        }

        /// <summary>
        /// Thêm tick vào cache.
        /// </summary>
        public void Add(Tick tick)
        {
            buffer.Add(tick);
        }

        /// <summary>
        /// Lấy toàn bộ tick trong cache.
        /// </summary>
        public IEnumerable<Tick> GetTicks() => buffer;

        /// <summary>
        /// Lấy tick cuối cùng trong cache.
        /// </summary>
        public Tick GetLastTick() => buffer.Count > 0 ? buffer[buffer.Count - 1] : null;
    }
}
