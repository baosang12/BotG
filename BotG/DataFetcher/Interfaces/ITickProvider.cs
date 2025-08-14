using System;
using DataFetcher.Models;

namespace DataFetcher.Interfaces
{
    /// <summary>
    /// Interface cung cấp tick realtime hoặc lịch sử. Phát event khi nhận tick mới.
    /// </summary>
    public interface ITickProvider
    {
        /// <summary>
        /// Sự kiện phát tick mới (sau khi nhận từ nguồn dữ liệu).
        /// </summary>
        event EventHandler<Tick> TickReceived;

        /// <summary>
        /// Bắt đầu lấy dữ liệu tick (kết nối broker, file, ...).
        /// </summary>
        void Start();

        /// <summary>
        /// Dừng lấy dữ liệu tick.
        /// </summary>
        void Stop();
    }
}
