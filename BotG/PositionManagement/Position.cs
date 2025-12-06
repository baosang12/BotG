using System;
using cAlgo.API;

namespace BotG.PositionManagement
{
    /// <summary>
    /// Đại diện cho một vị thế giao dịch với theo dõi P&L và exit parameters
    /// </summary>
    public class Position
    {
        /// <summary>ID duy nhất của position (cTrader Position.Id hoặc GUID nội bộ)</summary>
        public string Id { get; set; }

        /// <summary>Tên cặp tiền tệ/symbol (EURUSD, BTCUSD...)</summary>
        public string Symbol { get; set; }

        /// <summary>Hướng giao dịch: Buy hoặc Sell</summary>
        public TradeType Direction { get; set; }

        /// <summary>Giá vào lệnh (entry)</summary>
        public double EntryPrice { get; set; }

        /// <summary>Khối lượng theo đơn vị (units), không phải lots</summary>
        public double VolumeInUnits { get; set; }

        /// <summary>Thời điểm mở position</summary>
        public DateTime OpenTime { get; set; }

        /// <summary>Thời điểm đóng position (null nếu vẫn đang mở)</summary>
        public DateTime? CloseTime { get; set; }

        /// <summary>Giá đóng lệnh (null nếu chưa đóng)</summary>
        public double? ClosePrice { get; set; }

        /// <summary>Giá thị trường hiện tại (cập nhật mỗi tick)</summary>
        public double CurrentPrice { get; set; }

        /// <summary>Lãi/lỗ chưa thực hiện (unrealized P&L) tính theo USD</summary>
        public double UnrealizedPnL { get; set; }

        /// <summary>Lãi/lỗ đã thực hiện (realized P&L) sau khi đóng, tính theo USD</summary>
        public double RealizedPnL { get; set; }

        /// <summary>Trạng thái của position</summary>
        public PositionStatus Status { get; set; }

        /// <summary>Tham số exit (SL, TP, trailing stop...)</summary>
        public ExitParameters ExitParams { get; set; }
        public double? PipSize { get; set; }

        /// <summary>Nhãn/label từ chiến lược (SMA_Crossover, RSI_Reversal...)</summary>
        public string Label { get; set; }

        /// <summary>Giá trị point/pip (để tính P&L từ price movement)</summary>
        public double PointValue { get; set; }

        /// <summary>
        /// Tính P&L chưa thực hiện dựa trên giá hiện tại
        /// </summary>
        public void UpdateUnrealizedPnL(double currentPrice, double pointValue)
        {
            CurrentPrice = currentPrice;
            PointValue = pointValue;

            double priceDifference = Direction == TradeType.Buy
                ? (currentPrice - EntryPrice)
                : (EntryPrice - currentPrice);

            UnrealizedPnL = priceDifference * VolumeInUnits * pointValue;
        }

        /// <summary>
        /// Đóng position và tính realized P&L
        /// </summary>
        public void Close(double closePrice, DateTime closeTime)
        {
            ClosePrice = closePrice;
            CloseTime = closeTime;
            Status = PositionStatus.Closed;

            double priceDifference = Direction == TradeType.Buy
                ? (closePrice - EntryPrice)
                : (EntryPrice - closePrice);

            RealizedPnL = priceDifference * VolumeInUnits * PointValue;
            UnrealizedPnL = 0; // Reset unrealized khi đã close
        }

        /// <summary>
        /// Kiểm tra xem position có đang mở hay không
        /// </summary>
        public bool IsOpen => Status == PositionStatus.Open;

        /// <summary>
        /// Thời gian position đã mở (số giây)
        /// </summary>
        public double DurationInSeconds(DateTime currentTime)
        {
            return (currentTime - OpenTime).TotalSeconds;
        }

        /// <summary>
        /// Số bars position đã giữ (giả định 1 bar = 3600s cho H1)
        /// </summary>
        public int BarsHeld(DateTime currentTime, int secondsPerBar = 3600)
        {
            return (int)Math.Floor(DurationInSeconds(currentTime) / secondsPerBar);
        }
    }

    /// <summary>
    /// Trạng thái của position
    /// </summary>
    public enum PositionStatus
    {
        /// <summary>Position đang mở, chưa đóng</summary>
        Open,

        /// <summary>Position đã đóng</summary>
        Closed,

        /// <summary>Position đang chờ xử lý đóng</summary>
        PendingClose,

        /// <summary>Position bị lỗi</summary>
        Error
    }
}
