using System;
using System.Threading;
using System.Threading.Tasks;

namespace Strategies
{
    /// <summary>
    /// Simple price-action strategy tạo tín hiệu dựa trên biến động giá ngắn hạn.
    /// </summary>
    public sealed class PriceActionStrategy : IStrategy
    {
        private readonly string _name;
        private double? _previousPrice;

        public PriceActionStrategy(string name = "PriceActionStrategy")
        {
            _name = name;
        }

        public string Name => _name;

        public Task<Signal?> EvaluateAsync(MarketData data, CancellationToken ct)
        {
            var current = data.Mid;
            if (_previousPrice is null || _previousPrice <= 0)
            {
                _previousPrice = current;
                return Task.FromResult<Signal?>(null);
            }

            var changeRatio = Math.Abs(current - _previousPrice.Value) / Math.Max(1e-6, _previousPrice.Value);
            _previousPrice = current;

            if (changeRatio < 0.001)
            {
                return Task.FromResult<Signal?>(null);
            }

            var direction = current >= _previousPrice ? TradeAction.Buy : TradeAction.Sell;
            var confidence = Math.Min(changeRatio * 15.0, 0.8);
            if (confidence < 0.05)
            {
                return Task.FromResult<Signal?>(null);
            }

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
            var spread = context.LatestTick.Spread;
            var score = spread < 0.0005 ? 6.0 : 5.0;
            var level = score >= 5.5 ? RiskLevel.Preferred : RiskLevel.Normal;
            return new RiskScore(score, level, level != RiskLevel.Blocked);
        }
    }
}
