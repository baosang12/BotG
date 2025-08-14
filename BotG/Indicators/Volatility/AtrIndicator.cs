using System;
using DataFetcher.Models;
using DataFetcher.Caching;

namespace Indicators.Volatility
{
    /// <summary>
    /// Custom ATR (Average True Range) indicator implementing Wilder's smoothing.
    /// </summary>
    public class AtrIndicator : IIndicator<Bar, double>
    {
        private readonly int _period;
        private readonly CircularBuffer<double> _trBuffer;
        private double _currentAtr = double.NaN;
        private double _lastClose = double.NaN;

        /// <summary>
        /// Event fired when ATR is updated.
        /// </summary>
        public event EventHandler<double> Updated;

        public AtrIndicator(int period)
        {
            if (period <= 0) throw new ArgumentException("Period must be positive", nameof(period));
            _period = period;
            _trBuffer = new CircularBuffer<double>(period);
        }

        /// <summary>
        /// Update the ATR with a new bar.
        /// </summary>
        /// <param name="bar">The latest bar data.</param>
        public void Update(Bar bar)
        {
            // Compute True Range
            double tr;
            if (double.IsNaN(_lastClose))
            {
                tr = bar.High - bar.Low;
            }
            else
            {
                double range1 = bar.High - bar.Low;
                double range2 = Math.Abs(bar.High - _lastClose);
                double range3 = Math.Abs(bar.Low - _lastClose);
                tr = Math.Max(range1, Math.Max(range2, range3));
            }

            // Add to buffer
            _trBuffer.Add(tr);

            // Calculate ATR
            if (double.IsNaN(_currentAtr) && _trBuffer.IsFull)
            {
                // initial ATR = simple average of TRs
                double sum = 0;
                foreach (var v in _trBuffer) sum += v;
                _currentAtr = sum / _period;
            }
            else if (!double.IsNaN(_currentAtr))
            {
                // Wilder's smoothing
                _currentAtr = (_currentAtr * (_period - 1) + tr) / _period;
            }

            // Update last close
            _lastClose = bar.Close;

            // Fire update event
            if (!double.IsNaN(_currentAtr))
            {
                Updated?.Invoke(this, _currentAtr);
            }
        }

        /// <summary>
        /// Get the current ATR value.
        /// </summary>
        public double GetCurrentAtr() => _currentAtr;
    }
}
