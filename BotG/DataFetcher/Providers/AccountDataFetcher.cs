using System;
using System.Timers;
using DataFetcher.Models;
using DataFetcher.Interfaces;

namespace DataFetcher.Providers
{
    /// <summary>
    /// Theo dõi thay đổi tài khoản, phát event AccountChanged khi có thay đổi.
    /// Demo: mô phỏng thay đổi account bằng Timer.
    /// </summary>
    public class AccountDataFetcher : IAccountDataProvider
    {
        public event EventHandler<AccountInfo> AccountChanged;
        private Timer _timer;
        private Random _rnd = new();
        private bool _running;
        private AccountInfo _lastInfo;

        public void Start()
        {
            if (_running) return;
            _timer = new Timer(5000); // Kiểm tra mỗi 5 giây
            _timer.Elapsed += (s, e) => CheckAccount();
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

        private void CheckAccount()
        {
            var info = new AccountInfo
            {
                Equity = 10000 + _rnd.NextDouble() * 100,
                Balance = 10000 + _rnd.NextDouble() * 100,
                Margin = 1000 + _rnd.NextDouble() * 50,
                Positions = _rnd.Next(0, 5)
            };
            if (_lastInfo == null || info.Equity != _lastInfo.Equity || info.Balance != _lastInfo.Balance || info.Margin != _lastInfo.Margin || info.Positions != _lastInfo.Positions)
            {
                AccountChanged?.Invoke(this, info);
                _lastInfo = info;
            }
        }
    }
}
