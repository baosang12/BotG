using System;
using DataFetcher.Models;

namespace Indicators
{
    /// <summary>
    /// Exponential Moving Average indicator.
    /// </summary>
    public class EmaIndicator : IIndicator<Bar, double>
    {
        public event EventHandler<double> Updated;

        private readonly int _period;
        private readonly double _multiplier;
        private double _current;
        private bool _initialized;

        public EmaIndicator(int period)
        {
            if (period <= 0)
                throw new ArgumentException("Period must be greater than zero.", nameof(period));

            _period = period;
            _multiplier = 2.0 / (period + 1);
        }

        public void Update(Bar input)
        {
            var close = input.Close;
            if (!_initialized)
            {
                _current = close;
                _initialized = true;
            }
            else
            {
                _current = ((close - _current) * _multiplier) + _current;
            }

            Updated?.Invoke(this, _current);
        }
    }
}
