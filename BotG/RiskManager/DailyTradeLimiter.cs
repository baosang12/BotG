using System;
using cAlgo.API;
using BotG.Runtime.Logging;

namespace BotG.RiskManager
{
    /// <summary>
    /// SCALPING CONSERVATIVE: Limits daily trades and losses
    /// </summary>
    public class DailyTradeLimiter
    {
        private int _tradesToday = 0;
        private DateTime _lastResetDate;
        private readonly Robot _robot;
        private readonly int _maxTradesPerDay;
        private readonly double _maxDailyLossPercent;
        private double _startOfDayBalance;

        public DailyTradeLimiter(Robot robot, int maxTradesPerDay = 5, double maxDailyLossPercent = 0.03)
        {
            _robot = robot ?? throw new ArgumentNullException(nameof(robot));
            _maxTradesPerDay = maxTradesPerDay;
            _maxDailyLossPercent = maxDailyLossPercent;
            _lastResetDate = DateTime.Today;
            _startOfDayBalance = robot.Account.Balance;
        }

        /// <summary>
        /// Check if new trade can be entered (respects daily limits)
        /// </summary>
        public bool CanEnterTrade(out string reason)
        {
            ResetIfNewDay();

            // Check daily trade limit
            if (_tradesToday >= _maxTradesPerDay)
            {
                reason = $"Daily trade limit reached ({_tradesToday}/{_maxTradesPerDay})";
                return false;
            }

            // Check daily loss limit
            if (IsDailyLossLimitReached(out double dailyPnL))
            {
                reason = $"Daily loss limit reached (PnL: {dailyPnL:F2}, limit: {_maxDailyLossPercent * 100}%)";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Record a new trade (increment counter)
        /// </summary>
        public void RecordTrade()
        {
            ResetIfNewDay();
            _tradesToday++;
            
            _robot.Print($"[DailyLimiter] Trade recorded: {_tradesToday}/{_maxTradesPerDay} today");
            
            try
            {
                PipelineLogger.Log("RISK", "TradeRecorded", $"Daily trades: {_tradesToday}", 
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["trades_today"] = _tradesToday,
                        ["max_trades"] = _maxTradesPerDay
                    }, null);
            }
            catch { }
        }

        /// <summary>
        /// Check if daily loss limit has been reached
        /// </summary>
        public bool IsDailyLossLimitReached(out double dailyPnL)
        {
            ResetIfNewDay();

            double currentBalance = _robot.Account.Balance;
            dailyPnL = currentBalance - _startOfDayBalance;

            // Add unrealized P&L from open positions
            double unrealizedPnL = 0;
            foreach (var position in _robot.Positions)
            {
                unrealizedPnL += position.NetProfit;
            }

            double totalDailyPnL = dailyPnL + unrealizedPnL;
            double lossThreshold = -(_startOfDayBalance * _maxDailyLossPercent);

            bool limitReached = totalDailyPnL < lossThreshold;

            if (limitReached)
            {
                _robot.Print($"[DailyLimiter] LOSS LIMIT HIT! Daily P&L: {totalDailyPnL:F2} < {lossThreshold:F2}");
                
                try
                {
                    PipelineLogger.Log("RISK", "DailyLossLimit", "Daily loss limit reached", 
                        new System.Collections.Generic.Dictionary<string, object>
                        {
                            ["daily_pnl"] = totalDailyPnL,
                            ["loss_threshold"] = lossThreshold,
                            ["start_balance"] = _startOfDayBalance,
                            ["current_balance"] = currentBalance,
                            ["unrealized_pnl"] = unrealizedPnL
                        }, null);
                }
                catch { }
            }

            dailyPnL = totalDailyPnL;
            return limitReached;
        }

        /// <summary>
        /// Reset counters if new day started
        /// </summary>
        private void ResetIfNewDay()
        {
            DateTime today = DateTime.Today;
            
            if (today > _lastResetDate)
            {
                _robot.Print($"[DailyLimiter] New day reset: {today:yyyy-MM-dd} | Previous trades: {_tradesToday}");
                
                _tradesToday = 0;
                _lastResetDate = today;
                _startOfDayBalance = _robot.Account.Balance;
                
                try
                {
                    PipelineLogger.Log("RISK", "DailyReset", "Daily limits reset", 
                        new System.Collections.Generic.Dictionary<string, object>
                        {
                            ["date"] = today,
                            ["start_balance"] = _startOfDayBalance
                        }, null);
                }
                catch { }
            }
        }

        /// <summary>
        /// Get current daily statistics
        /// </summary>
        public (int TradesToday, int MaxTrades, double DailyPnL, double LossLimit) GetDailyStats()
        {
            ResetIfNewDay();
            IsDailyLossLimitReached(out double dailyPnL);
            double lossLimit = -(_startOfDayBalance * _maxDailyLossPercent);
            
            return (_tradesToday, _maxTradesPerDay, dailyPnL, lossLimit);
        }
    }
}
