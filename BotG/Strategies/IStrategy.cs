using System;

namespace Strategies
{
    /// <summary>
    /// Interface cho các chiến lược, nhận dữ liệu đã xử lý và phát event tín hiệu giao dịch.
    /// </summary>
    public interface IStrategy<TSignal>
    {
        /// <summary>
        /// Event phát tín hiệu mua/bán/thoát.
        /// </summary>
        event EventHandler<TSignal> SignalGenerated;

        /// <summary>
        /// Đánh giá dữ liệu để tạo tín hiệu giao dịch.
        /// </summary>
        void Evaluate(object data);
    }
}
