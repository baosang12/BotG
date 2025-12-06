using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using BotG.Runtime.Logging;

namespace BotG.PositionManagement
{
    /// <summary>
    /// Quản lý toàn bộ vị thế giao dịch: tracking, P&L, exit logic
    /// </summary>
    public class PositionManager
    {
        private readonly Dictionary<string, Position> _openPositions;
        private readonly Dictionary<string, Position> _closedPositions;
        private readonly IExitStrategy _exitStrategy;
        private readonly Robot _robot;
        private double _totalRealizedPnL;

        public PositionManager(Robot robot, IExitStrategy exitStrategy)
        {
            _robot = robot ?? throw new ArgumentNullException(nameof(robot));
            _exitStrategy = exitStrategy ?? throw new ArgumentNullException(nameof(exitStrategy));
            _openPositions = new Dictionary<string, Position>();
            _closedPositions = new Dictionary<string, Position>();
            _totalRealizedPnL = 0;
        }

        /// <summary>
        /// Callback khi một position mới được mở
        /// </summary>
        public void OnPositionOpened(Position position)
        {
            if (position == null) throw new ArgumentNullException(nameof(position));

            if (_openPositions.ContainsKey(position.Id))
            {
                _robot.Print($"[PositionManager] WARNING: Position {position.Id} already tracked, skipping duplicate.");
                return;
            }

            position.Status = PositionStatus.Open;
            _openPositions[position.Id] = position;

            // Log position opened
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["position_id"] = position.Id,
                    ["symbol"] = position.Symbol,
                    ["direction"] = position.Direction.ToString(),
                    ["entry_price"] = position.EntryPrice,
                    ["volume_units"] = position.VolumeInUnits,
                    ["open_time"] = position.OpenTime,
                    ["sl"] = position.ExitParams?.StopLossPrice,
                    ["tp"] = position.ExitParams?.TakeProfitPrice
                };
                PipelineLogger.Log("POSITION", "OPENED", $"{position.Direction} {position.Symbol} @{position.EntryPrice}", data, null);
            }
            catch { }

            _robot.Print($"[PositionManager] Position OPENED: {position.Id} | {position.Direction} {position.Symbol} | Entry: {position.EntryPrice} | Vol: {position.VolumeInUnits} units");
        }

        /// <summary>
        /// Callback mỗi tick: cập nhật P&L, kiểm tra exit conditions
        /// </summary>
        public void OnTick(DateTime timestamp, double bidPrice, double askPrice, string symbolName)
        {
            if (_openPositions.Count == 0) return;
            if (bidPrice <= 0 || askPrice <= 0) return;

            // Lấy danh sách positions của symbol hiện tại
            var symbolPositions = _openPositions.Values
                .Where(p => p.Symbol == symbolName && p.IsOpen)
                .ToList();

            if (symbolPositions.Count == 0) return;

            // Tính point value cho P&L (dùng _robot.Symbol để tính)
            double pointValue = CalculatePointValue();

            foreach (var position in symbolPositions)
            {
                double effectivePrice = position.Direction == TradeType.Buy ? bidPrice : askPrice;

                // 1. Cập nhật P&L
                position.UpdateUnrealizedPnL(effectivePrice, pointValue);

                // 2. Cập nhật trailing stop nếu có
                position.ExitParams?.UpdateTrailingStop(effectivePrice, position.Direction);

                // 3. Kiểm tra breakeven (đảm bảo B/E >= 0 bằng cách bù tối thiểu spread hiện tại)
                double pipSize = position.PipSize ?? GuessPipSize(position.Symbol);
                double spreadPips = (pipSize > 0)
                    ? Math.Max((askPrice - bidPrice) / pipSize, 0)
                    : 0;
                position.ExitParams?.CheckBreakeven(
                    position.Symbol,
                    effectivePrice,
                    position.EntryPrice,
                    pipSize,
                    spreadPips,
                    position.Direction);

                double? riskPips = position.ExitParams?.InitialRiskPips;
                if ((!riskPips.HasValue || riskPips.Value <= 0) && position.ExitParams?.StopLossPrice.HasValue == true && pipSize > 0)
                {
                    riskPips = Math.Abs(position.EntryPrice - position.ExitParams.StopLossPrice.Value) / pipSize;
                }

                position.ExitParams?.ApplyMultiLevelTrailing(
                    position.Symbol,
                    effectivePrice,
                    position.EntryPrice,
                    pipSize,
                    riskPips,
                    position.Direction,
                    timestamp,
                    position.ExitParams?.TakeProfitPrice);

                // 4. Kiểm tra exit conditions
                var exitSignal = _exitStrategy.CheckExit(position, effectivePrice, timestamp);

                if (exitSignal.ShouldExit)
                {
                    ClosePosition(position.Id, exitSignal.Reason, effectivePrice, timestamp);
                }
            }
        }

        /// <summary>
        /// Đóng một position với lý do cụ thể
        /// </summary>
        public void ClosePosition(string positionId, string reason, double closePrice, DateTime closeTime)
        {
            if (!_openPositions.TryGetValue(positionId, out var position))
            {
                _robot.Print($"[PositionManager] WARNING: Position {positionId} not found for closing.");
                return;
            }

            // Đóng position trong tracking
            position.Close(closePrice, closeTime);
            _totalRealizedPnL += position.RealizedPnL;

            // Di chuyển từ open sang closed
            _openPositions.Remove(positionId);
            _closedPositions[positionId] = position;

            // Log position closed
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["position_id"] = position.Id,
                    ["symbol"] = position.Symbol,
                    ["direction"] = position.Direction.ToString(),
                    ["entry_price"] = position.EntryPrice,
                    ["close_price"] = closePrice,
                    ["volume_units"] = position.VolumeInUnits,
                    ["open_time"] = position.OpenTime,
                    ["close_time"] = closeTime,
                    ["realized_pnl"] = position.RealizedPnL,
                    ["reason"] = reason,
                    ["duration_seconds"] = position.DurationInSeconds(closeTime)
                };
                PipelineLogger.Log("POSITION", "CLOSED", reason, data, null);
            }
            catch { }

            _robot.Print($"[PositionManager] Position CLOSED: {position.Id} | {reason} | P&L: {position.RealizedPnL:F2} USD | Duration: {position.DurationInSeconds(closeTime):F0}s");

            // Đóng position thực tế trong cTrader
            ClosePositionInCTrader(positionId);
        }

        /// <summary>
        /// Đóng position trong cTrader platform
        /// </summary>
        private void ClosePositionInCTrader(string positionId)
        {
            try
            {
                // Tìm position tương ứng trong cTrader
                var ctraderPosition = _robot.Positions.FirstOrDefault(p => p.Id.ToString() == positionId);
                if (ctraderPosition != null)
                {
                    var result = _robot.ClosePosition(ctraderPosition);
                    if (!result.IsSuccessful)
                    {
                        _robot.Print($"[PositionManager] ERROR closing position in cTrader: {result.Error}");
                    }
                }
                else
                {
                    _robot.Print($"[PositionManager] WARNING: cTrader position {positionId} not found for closing.");
                }
            }
            catch (Exception ex)
            {
                _robot.Print($"[PositionManager] EXCEPTION closing position in cTrader: {ex.Message}");
            }
        }

        /// <summary>
        /// Lấy tổng giá trị portfolio (account balance + unrealized P&L)
        /// </summary>
        public double GetTotalPortfolioValue()
        {
            double totalUnrealizedPnL = _openPositions.Values.Sum(p => p.UnrealizedPnL);
            return _robot.Account.Balance + totalUnrealizedPnL;
        }

        /// <summary>
        /// Lấy danh sách các position đang mở
        /// </summary>
        public List<Position> GetOpenPositions()
        {
            return _openPositions.Values.ToList();
        }

        /// <summary>
        /// Lấy danh sách các position đã đóng
        /// </summary>
        public List<Position> GetClosedPositions()
        {
            return _closedPositions.Values.ToList();
        }

        /// <summary>
        /// Lấy tổng realized P&L
        /// </summary>
        public double GetTotalRealizedPnL()
        {
            return _totalRealizedPnL;
        }

        /// <summary>
        /// Lấy tổng unrealized P&L
        /// </summary>
        public double GetTotalUnrealizedPnL()
        {
            return _openPositions.Values.Sum(p => p.UnrealizedPnL);
        }

        /// <summary>
        /// Đếm số position đang mở
        /// </summary>
        public int GetOpenPositionCount()
        {
            return _openPositions.Count;
        }

        /// <summary>
        /// Tính point value để chuyển đổi price movement sang USD
        /// </summary>
        private double CalculatePointValue()
        {
            try
            {
                var symbol = _robot.Symbol;
                // Sử dụng TickValue và TickSize từ symbol
                // PointValue = TickValue / TickSize
                double tickValue = symbol.TickValue;
                double tickSize = symbol.TickSize;

                if (tickSize > 0)
                {
                    return tickValue / tickSize;
                }

                // Fallback: estimate dựa trên pip value (forex)
                return symbol.PipValue / 0.0001; // 1 pip = 0.0001 for most pairs
            }
            catch
            {
                // Ultimate fallback
                return 10.0; // conservative estimate
            }
        }

        private double GuessPipSize(string symbol)
        {
            try
            {
                var sym = _robot.Symbol;
                if (!string.IsNullOrEmpty(symbol) && !string.Equals(symbol, sym?.Name, StringComparison.OrdinalIgnoreCase))
                {
                    sym = _robot.MarketData?.GetSymbol(symbol);
                }

                if (sym != null && sym.TickSize > 0)
                {
                    return sym.TickSize;
                }

                return symbol != null && symbol.Contains("JPY") ? 0.01 : 0.0001;
            }
            catch
            {
                return 0.0001;
            }
        }

        /// <summary>
        /// Log trạng thái portfolio định kỳ
        /// </summary>
        public void LogPortfolioStatus()
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["open_positions"] = _openPositions.Count,
                    ["closed_positions"] = _closedPositions.Count,
                    ["total_realized_pnl"] = _totalRealizedPnL,
                    ["total_unrealized_pnl"] = GetTotalUnrealizedPnL(),
                    ["portfolio_value"] = GetTotalPortfolioValue(),
                    ["account_balance"] = _robot.Account.Balance
                };
                PipelineLogger.Log("PORTFOLIO", "STATUS", "Portfolio update", data, null);
            }
            catch { }
        }

        /// <summary>
        /// SCALPING CONSERVATIVE: Tính position size với 0.5% risk per trade
        /// </summary>
        public double CalculateConservativePositionSize(string symbol, double entryPrice, double stopLossPrice)
        {
            try
            {
                double accountBalance = _robot.Account.Balance;
                double riskPerTrade = accountBalance * 0.005; // 0.5% risk per trade

                // Calculate stop distance in price
                double stopDistance = Math.Abs(entryPrice - stopLossPrice);

                // Calculate pip value (standard for forex)
                double pipSize = symbol.Contains("JPY") ? 0.01 : 0.0001;
                double pipValue = _robot.Symbol.PipValue; // USD per pip for 1 standard lot

                // Calculate pips in stop distance
                double pipsInStop = stopDistance / pipSize;

                // Volume calculation: Risk / (Pips * PipValue)
                // For 0.01 lot = 1000 units, pipValue for EURUSD is ~0.0001 per unit
                double volumeInUnits = (riskPerTrade / pipsInStop) / (pipValue / 100000); // Adjust for unit size

                // Apply broker constraints
                volumeInUnits = Math.Max(_robot.Symbol.VolumeInUnitsMin, volumeInUnits);
                volumeInUnits = Math.Min(_robot.Symbol.VolumeInUnitsMax, volumeInUnits);

                // Normalize to step size
                volumeInUnits = _robot.Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);

                _robot.Print($"[PositionSize] Balance: {accountBalance:F2} | Risk: {riskPerTrade:F2} | SL: {pipsInStop:F1}p | Volume: {volumeInUnits} units");

                return volumeInUnits;
            }
            catch (Exception ex)
            {
                _robot.Print($"[PositionSize] ERROR: {ex.Message}");
                return _robot.Symbol.VolumeInUnitsMin; // Fallback to minimum
            }
        }
    }

    /// <summary>
    /// Interface cho exit strategy
    /// </summary>
    public interface IExitStrategy
    {
        ExitSignal CheckExit(Position position, double currentPrice, DateTime timestamp);
    }

    /// <summary>
    /// Kết quả kiểm tra exit condition
    /// </summary>
    public class ExitSignal
    {
        public bool ShouldExit { get; set; }
        public string Reason { get; set; }

        public static ExitSignal NoExit => new ExitSignal { ShouldExit = false, Reason = null };

        public static ExitSignal Exit(string reason) => new ExitSignal { ShouldExit = true, Reason = reason };
    }
}
