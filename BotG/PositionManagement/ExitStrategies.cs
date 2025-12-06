using System;
using cAlgo.API;

namespace BotG.PositionManagement
{
    /// <summary>
    /// Exit strategy cố định dựa trên Stop Loss và Take Profit đã đặt
    /// </summary>
    public class FixedStopLossTakeProfitStrategy : IExitStrategy
    {
        public ExitSignal CheckExit(Position position, double currentPrice, DateTime timestamp)
        {
            if (position == null || !position.IsOpen)
                return ExitSignal.NoExit;

            var exitParams = position.ExitParams;
            if (exitParams == null)
                return ExitSignal.NoExit;

            // 1. Kiểm tra Stop Loss
            if (exitParams.StopLossPrice.HasValue)
            {
                bool slHit = position.Direction == TradeType.Buy
                    ? currentPrice <= exitParams.StopLossPrice.Value
                    : currentPrice >= exitParams.StopLossPrice.Value;

                if (slHit)
                {
                    return ExitSignal.Exit($"Stop Loss hit @ {currentPrice:F5}");
                }
            }

            // 2. Kiểm tra Take Profit
            if (exitParams.TakeProfitPrice.HasValue)
            {
                bool tpHit = position.Direction == TradeType.Buy
                    ? currentPrice >= exitParams.TakeProfitPrice.Value
                    : currentPrice <= exitParams.TakeProfitPrice.Value;

                if (tpHit)
                {
                    return ExitSignal.Exit($"Take Profit hit @ {currentPrice:F5}");
                }
            }

            // 3. Kiểm tra Trailing Stop (nếu đã kích hoạt)
            if (exitParams.TrailingStopPrice.HasValue)
            {
                bool trailingHit = position.Direction == TradeType.Buy
                    ? currentPrice <= exitParams.TrailingStopPrice.Value
                    : currentPrice >= exitParams.TrailingStopPrice.Value;

                if (trailingHit)
                {
                    return ExitSignal.Exit($"Trailing Stop hit @ {currentPrice:F5}");
                }
            }

            return ExitSignal.NoExit;
        }
    }

    /// <summary>
    /// Exit strategy dựa trên thời gian giữ lệnh (time-based)
    /// </summary>
    public class TimeBasedExitStrategy : IExitStrategy
    {
        private readonly int _secondsPerBar;

        public TimeBasedExitStrategy(int secondsPerBar = 3600)
        {
            _secondsPerBar = secondsPerBar;
        }

        public ExitSignal CheckExit(Position position, double currentPrice, DateTime timestamp)
        {
            if (position == null || !position.IsOpen)
                return ExitSignal.NoExit;

            var exitParams = position.ExitParams;
            if (exitParams == null)
                return ExitSignal.NoExit;

            // 1. Kiểm tra MaxBarsHold
            if (exitParams.MaxBarsHold.HasValue)
            {
                int barsHeld = position.BarsHeld(timestamp, _secondsPerBar);
                if (barsHeld >= exitParams.MaxBarsHold.Value)
                {
                    return ExitSignal.Exit($"Max bars hold reached ({barsHeld} bars)");
                }
            }

            // 2. Kiểm tra MaxHoldUntil
            if (exitParams.MaxHoldUntil.HasValue)
            {
                if (timestamp >= exitParams.MaxHoldUntil.Value)
                {
                    return ExitSignal.Exit($"Max hold time reached @ {timestamp}");
                }
            }

            return ExitSignal.NoExit;
        }
    }

    /// <summary>
    /// Compound exit strategy: kết hợp nhiều strategy, exit nếu BẤT KỲ strategy nào trigger
    /// </summary>
    public class CompoundExitStrategy : IExitStrategy
    {
        private readonly IExitStrategy[] _strategies;

        public CompoundExitStrategy(params IExitStrategy[] strategies)
        {
            _strategies = strategies ?? new IExitStrategy[0];
        }

        /// <summary>
        /// Tạo compound strategy mặc định: Fixed SL/TP + Time-based
        /// </summary>
        /// <param name="secondsPerBar">Seconds per bar (900 for M15, 3600 for H1)</param>
        public static CompoundExitStrategy CreateDefault(int secondsPerBar = 3600)
        {
            return new CompoundExitStrategy(
                new FixedStopLossTakeProfitStrategy(),
                new TimeBasedExitStrategy(secondsPerBar)
            );
        }

        public ExitSignal CheckExit(Position position, double currentPrice, DateTime timestamp)
        {
            // Kiểm tra tất cả strategies, return ngay khi có strategy nào signal exit
            foreach (var strategy in _strategies)
            {
                var signal = strategy.CheckExit(position, currentPrice, timestamp);
                if (signal.ShouldExit)
                {
                    return signal;
                }
            }

            return ExitSignal.NoExit;
        }
    }

    /// <summary>
    /// Advanced trailing stop strategy với nhiều tùy chọn
    /// </summary>
    public class AdvancedTrailingStopStrategy : IExitStrategy
    {
        private readonly double _activationProfitPercent; // % profit để kích hoạt trailing
        private readonly double _trailingStepPercent; // % trailing step

        public AdvancedTrailingStopStrategy(double activationProfitPercent = 0.01, double trailingStepPercent = 0.005)
        {
            _activationProfitPercent = activationProfitPercent;
            _trailingStepPercent = trailingStepPercent;
        }

        public ExitSignal CheckExit(Position position, double currentPrice, DateTime timestamp)
        {
            if (position == null || !position.IsOpen)
                return ExitSignal.NoExit;

            var exitParams = position.ExitParams;
            if (exitParams == null)
                return ExitSignal.NoExit;

            // Tính profit hiện tại
            double profitPercent = position.Direction == TradeType.Buy
                ? (currentPrice - position.EntryPrice) / position.EntryPrice
                : (position.EntryPrice - currentPrice) / position.EntryPrice;

            // Kích hoạt trailing nếu đạt activation threshold
            if (profitPercent >= _activationProfitPercent && !exitParams.TrailingStopDistance.HasValue)
            {
                double trailingDistance = position.EntryPrice * _trailingStepPercent;
                exitParams.TrailingStopDistance = trailingDistance;
                exitParams.UpdateTrailingStop(currentPrice, position.Direction);
            }

            // Kiểm tra trailing stop
            if (exitParams.TrailingStopPrice.HasValue)
            {
                bool trailingHit = position.Direction == TradeType.Buy
                    ? currentPrice <= exitParams.TrailingStopPrice.Value
                    : currentPrice >= exitParams.TrailingStopPrice.Value;

                if (trailingHit)
                {
                    return ExitSignal.Exit($"Advanced Trailing Stop hit @ {currentPrice:F5}");
                }
            }

            return ExitSignal.NoExit;
        }
    }
}
