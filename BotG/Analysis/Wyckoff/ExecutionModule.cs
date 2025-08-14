
using System;
using Strategies;
// Nếu TradeSignal/TradeAction nằm ở Analysis.Wyckoff thì không cần thêm dòng dưới
// using Analysis.Wyckoff;

namespace Analysis.Wyckoff
{
    public class ExecutionModule
    {
        private bool _inPosition = false;
        private double _entry = 0;
        private double _stop = 0;
        private double _tp = 0;
        private string _side = "";
        private bool _trailingActive = false;
        private double _trailStartRR = 1.0; // bắt đầu trailing khi đạt 1R
        private double _trailDistanceR = 0.5; // trailing SL cách giá hiện tại 0.5R

        public void Execute(TradeSignal signal, double currentPrice = 0)
        {
            switch (signal.Action)
            {
                case TradeAction.Buy:
                    if (!_inPosition)
                    {
                        _inPosition = true;
                        _entry = signal.Price;
                        _stop = signal.StopLoss ?? 0;
                        _tp = signal.TakeProfit ?? 0;
                        _side = "Buy";
                        _trailingActive = false;
                        Print($"Open BUY {signal.Price:F2} SL={_stop:F2} TP={_tp:F2}");
                    }
                    break;
                case TradeAction.Sell:
                    if (!_inPosition)
                    {
                        _inPosition = true;
                        _entry = signal.Price;
                        _stop = signal.StopLoss ?? 0;
                        _tp = signal.TakeProfit ?? 0;
                        _side = "Sell";
                        _trailingActive = false;
                        Print($"Open SELL {signal.Price:F2} SL={_stop:F2} TP={_tp:F2}");
                    }
                    break;
                case TradeAction.Exit:
                    if (_inPosition)
                    {
                        Print($"Close {_side} at market");
                        _inPosition = false;
                        _entry = _stop = _tp = 0;
                        _side = "";
                        _trailingActive = false;
                    }
                    break;
                default:
                    // Không làm gì
                    break;
            }

            // Trailing stop logic (giả lập, cần gọi hàm này mỗi bar với currentPrice)
            if (_inPosition && currentPrice > 0)
            {
                double r = Math.Abs(_entry - _stop);
                if (_side == "Buy")
                {
                    double rr = (currentPrice - _entry) / r;
                    if (!_trailingActive && rr >= _trailStartRR)
                    {
                        _trailingActive = true;
                        Print($"[Trailing] Start trailing for BUY at {currentPrice:F2}");
                    }
                    if (_trailingActive)
                    {
                        double newStop = currentPrice - _trailDistanceR * r;
                        if (newStop > _stop)
                        {
                            _stop = newStop;
                            Print($"[Trailing] Move SL to {_stop:F2}");
                        }
                    }
                }
                else if (_side == "Sell")
                {
                    double rr = (_entry - currentPrice) / r;
                    if (!_trailingActive && rr >= _trailStartRR)
                    {
                        _trailingActive = true;
                        Print($"[Trailing] Start trailing for SELL at {currentPrice:F2}");
                    }
                    if (_trailingActive)
                    {
                        double newStop = currentPrice + _trailDistanceR * r;
                        if (newStop < _stop)
                        {
                            _stop = newStop;
                            Print($"[Trailing] Move SL to {_stop:F2}");
                        }
                    }
                }
            }
        }

        private void Print(string msg)
        {
            // Thay thế bằng log thực tế hoặc API cBot
            System.Console.WriteLine(msg);
        }
    }
}
