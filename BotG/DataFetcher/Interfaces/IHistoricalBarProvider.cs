using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace DataFetcher.Interfaces
{
    /// <summary>
    /// Interface cung cấp bar lịch sử theo timeframe và khoảng thời gian.
    /// </summary>
    public interface IHistoricalBarProvider
    {
        /// <summary>
        /// Lấy danh sách bar lịch sử theo timeframe và khoảng thời gian.
        /// </summary>
        IEnumerable<Bar> GetBars(TimeFrame tf, DateTime from, DateTime to);
    }
}
