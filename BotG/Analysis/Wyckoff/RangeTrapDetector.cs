using System;
using Analysis.SmartMoneyConcept;

namespace Analysis.Wyckoff
{
    /// <summary>
    /// RangeTrapDetector: Theo dõi lịch sử sweep hai đầu để xác định trap range (giai đoạn tích lũy/phân phối) theo Wyckoff.
    /// Nhận SmartMoneySignal từ LiquiditySweepDetector, không sửa code cũ.
    /// </summary>
    public class RangeTrapDetector
    {
        private DateTime? _lastSweepHigh;
        private DateTime? _lastSweepLow;
        public decimal? LastHighSweepPrice { get; private set; }
        public decimal? LastLowSweepPrice { get; private set; }
        private readonly TimeSpan _rangeWindow;

        public bool IsConfirmedTrap { get; private set; }
        public DateTime? ConfirmedTime { get; private set; }

        private readonly Action<string> _logger;
        public RangeTrapDetector(TimeSpan? rangeWindow = null, Action<string> logger = null)
        {
            _rangeWindow = rangeWindow ?? TimeSpan.FromHours(12);
            _logger = logger;
            Reset();
        }

        /// <summary>
        /// Cập nhật trạng thái với tín hiệu sweep mới, trả về true nếu phát hiện sweep hai đầu trong cùng 1 range window.
        /// Ưu tiên trap khi sweep thứ 2 gần thời điểm hiện tại hơn.
        /// </summary>
        /// <param name="signal">SmartMoneySignal từ LiquiditySweepDetector</param>
        /// <returns>true nếu có sweep hai đầu trong range window</returns>
        public bool UpdateAndCheck(SmartMoneySignal signal)
        {
            if (signal == null || signal.Type != SmartMoneyType.LiquiditySweep) {
                _logger?.Invoke($"[RangeTrapDetector] Input signal is null or not LiquiditySweep. signal: {(signal == null ? "null" : signal.Type.ToString())}");
                return false;
            }

            if (signal.IsBullish)
            {
                _lastSweepHigh = signal.Time;
                LastHighSweepPrice = (decimal?)signal.Price;
                _logger?.Invoke($"[RangeTrapDetector] Bullish sweep at {signal.Time}, price={signal.Price}");
            }
            else
            {
                _lastSweepLow = signal.Time;
                LastLowSweepPrice = (decimal?)signal.Price;
                _logger?.Invoke($"[RangeTrapDetector] Bearish sweep at {signal.Time}, price={signal.Price}");
            }

            // Ưu tiên trap khi sweep thứ 2 gần hiện tại hơn
            if (_lastSweepHigh.HasValue && _lastSweepLow.HasValue)
            {
                // Xác định sweep nào là mới nhất
                var latestSweepTime = _lastSweepHigh > _lastSweepLow ? _lastSweepHigh : _lastSweepLow;
                var earliestSweepTime = _lastSweepHigh < _lastSweepLow ? _lastSweepHigh : _lastSweepLow;
                var diff = (latestSweepTime.Value - earliestSweepTime.Value).Duration();
                _logger?.Invoke($"[RangeTrapDetector] Both sweeps present. High: {_lastSweepHigh}, Low: {_lastSweepLow}, diff={diff}, window={_rangeWindow}");
                if (diff <= _rangeWindow)
                {
                    IsConfirmedTrap = true;
                    ConfirmedTime = latestSweepTime;
                    _logger?.Invoke($"[RangeTrapDetector] Trap confirmed at {latestSweepTime}. Range diff={diff}");
                    return true; // Có sweep hai đầu trong cùng 1 range
                }
            }
            IsConfirmedTrap = false;
            ConfirmedTime = null;
            _logger?.Invoke($"[RangeTrapDetector] No trap detected after update.");
            return false;
        }

        /// <summary>
        /// Reset trạng thái detector (nếu cần chuyển pha hoặc bắt đầu range mới)
        /// </summary>
        public void Reset()
        {
            _lastSweepHigh = null;
            _lastSweepLow = null;
            LastHighSweepPrice = null;
            LastLowSweepPrice = null;
            IsConfirmedTrap = false;
            ConfirmedTime = null;
        }
    }
}
