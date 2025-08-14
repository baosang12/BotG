using System;
using System.Timers;
using DataFetcher.Models;
using DataFetcher.Interfaces;

namespace DataFetcher.Providers
{
    /// <summary>
    /// Kết nối broker (WebSocket/API), phát event TickReceived khi có tick mới.
    /// Demo: mô phỏng tick realtime bằng Timer.
    /// </summary>
    public class RealTimeTickFetcher : ITickProvider
    {
        public event EventHandler<Tick> TickReceived;
        private Timer _timer;
        private Random _rnd = new();
        private bool _running;

        public void Start()
        {
            if (_running) return;
            _timer = new Timer(1000); // 1 tick mỗi giây
            _timer.Elapsed += (s, e) => GenerateTick();
            _timer.Start();
            _running = true;
        }

        public void Stop()
        {
            if (!_running) return;
            _timer?.Stop();
            _timer?.Dispose();
            _running = false;
        }

        private void GenerateTick()
        {
            var tick = new Tick
            {
                Timestamp = DateTime.UtcNow,
                Bid = 1.1 + _rnd.NextDouble() * 0.01,
                Ask = 1.1 + _rnd.NextDouble() * 0.01,
                Volume = _rnd.Next(1, 100)
            };
            TickReceived?.Invoke(this, tick);
        }
    }
}
