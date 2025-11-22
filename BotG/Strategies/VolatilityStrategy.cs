using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Strategies
{
    /// <summary>
    /// Volatility breakout strategy tạo tín hiệu khi ATR ngắn hạn vượt ngưỡng.
    /// </summary>
    public sealed class VolatilityStrategy : IStrategy
    {
        private readonly Queue<double> _window = new();
        private readonly int _period;
        private readonly double _threshold;
        private double? _lastClose;

        public VolatilityStrategy(string name = "VolatilityStrategy", int period = 8, double threshold = 0.004)
        {
            Name = name;
            _period = Math.Max(3, period);
            _threshold = Math.Max(0.0005, threshold);
        }

        public string Name { get; }

        public Task<Signal?> EvaluateAsync(MarketData data, CancellationToken ct)
        {
            if (_lastClose is not null)
            {
                _window.Enqueue(Math.Abs(data.Mid - _lastClose.Value));
                if (_window.Count > _period)
                {
                    _window.Dequeue();
                }
            }

            _lastClose = data.Mid;

            if (_window.Count < _period)
            {
                return Task.FromResult<Signal?>(null);
            }

            double avgMove = 0.0;
            foreach (var diff in _window)
            {
                avgMove += diff;
            }
            avgMove /= _window.Count;

            var normalized = avgMove / Math.Max(1e-6, data.Mid);
            if (normalized < _threshold)
            {
                return Task.FromResult<Signal?>(null);
            }

            var direction = data.Mid >= _lastClose ? TradeAction.Buy : TradeAction.Sell;
            var confidence = Math.Min(normalized / _threshold, 1.0);
            var signal = new Signal
            {
                StrategyName = Name,
                Action = direction,
                Confidence = confidence,
                Price = direction == TradeAction.Buy ? data.Ask : data.Bid,
                TimestampUtc = data.TimestampUtc
            };

            return Task.FromResult<Signal?>(signal);
        }

        public RiskScore CalculateRisk(MarketContext context)
        {
            var vol = context.RegimeAnalysis?.Atr ?? 0.0001;
            var score = Math.Min(7.0, 5.0 + (vol * 1000.0));
            var level = score >= 6.0 ? RiskLevel.Preferred : RiskLevel.Normal;
            return new RiskScore(score, level, level != RiskLevel.Blocked);
        }
    }
}
