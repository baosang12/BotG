using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace DataFetcher.Aggregators
{
    /// <summary>
    /// Gom tick sạch thành bar, phát event BarClosed khi bar đóng.
    /// </summary>
    public class BarAggregator
    {
        public event EventHandler<Bar> BarClosed;
        private List<Tick> _currentTicks = new();
        private Bar _currentBar;
        private TimeFrame _timeFrame;
        private DateTime _barOpenTime;
        private int _tickCount;

        public BarAggregator(TimeFrame timeFrame = TimeFrame.M1)
        {
            _timeFrame = timeFrame;
        }

        /// <summary>
        /// Nhận tick sạch, gom thành bar, phát event khi bar đóng.
        /// </summary>
        public void Process(Tick tick)
        {
            if (_currentBar == null || tick.Timestamp >= _barOpenTime.Add(TimeFrameToSpan(_timeFrame)))
            {
                if (_currentBar != null)
                    BarClosed?.Invoke(this, _currentBar);
                StartNewBar(tick);
            }
            UpdateBar(tick);
        }

        private void StartNewBar(Tick tick)
        {
            _barOpenTime = RoundTime(tick.Timestamp, _timeFrame);
            _currentBar = new Bar
            {
                OpenTime = _barOpenTime,
                Open = tick.Bid,
                High = tick.Bid,
                Low = tick.Bid,
                Close = tick.Bid,
                Volume = tick.Volume,
                Tf = _timeFrame
            };
            _tickCount = 1;
        }

        private void UpdateBar(Tick tick)
        {
            if (_currentBar == null) return;
            _currentBar.High = Math.Max(_currentBar.High, tick.Bid);
            _currentBar.Low = Math.Min(_currentBar.Low, tick.Bid);
            _currentBar.Close = tick.Bid;
            _currentBar.Volume += tick.Volume;
            _tickCount++;
        }

        private DateTime RoundTime(DateTime time, TimeFrame tf)
        {
            // Chỉ xử lý các timeframe phổ biến
            if (tf == TimeFrame.M1)
                return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
            if (tf == TimeFrame.M5)
                return new DateTime(time.Year, time.Month, time.Day, time.Hour, (time.Minute / 5) * 5, 0);
            // Có thể mở rộng cho các timeframe khác
            return time;
        }

        private TimeSpan TimeFrameToSpan(TimeFrame tf)
        {
            return tf switch
            {
                TimeFrame.M1 => TimeSpan.FromMinutes(1),
                TimeFrame.M5 => TimeSpan.FromMinutes(5),
                TimeFrame.M15 => TimeSpan.FromMinutes(15),
                TimeFrame.M30 => TimeSpan.FromMinutes(30),
                TimeFrame.H1 => TimeSpan.FromHours(1),
                TimeFrame.H4 => TimeSpan.FromHours(4),
                TimeFrame.D1 => TimeSpan.FromDays(1),
                TimeFrame.W1 => TimeSpan.FromDays(7),
                TimeFrame.MN1 => TimeSpan.FromDays(30),
                _ => TimeSpan.FromMinutes(1)
            };
        }
    }
}
