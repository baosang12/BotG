using System;
using DataFetcher.Models;

namespace Indicators
{
    /// <summary>
    /// Volume Weighted Average Price indicator.
    /// </summary>
    public class VwapIndicator : IIndicator<Bar, double>
    {
        public event EventHandler<double> Updated;

        private double _cumPv;
        private double _cumVol;

        public void Update(Bar input)
        {
            // Typical price = (High + Low + Close) / 3
            double typicalPrice = (input.High + input.Low + input.Close) / 3.0;
            _cumPv += typicalPrice * input.Volume;
            _cumVol += input.Volume;

            if (_cumVol > 0)
            {
                double vwap = _cumPv / _cumVol;
                Updated?.Invoke(this, vwap);
            }
        }
    }
}
