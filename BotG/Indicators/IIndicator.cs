using System;

namespace Indicators
{
    /// <summary>
    /// Interface cho các chỉ báo, nhận input và phát event khi có giá trị mới.
    /// </summary>
    public interface IIndicator<TInput, TValue>
    {
        /// <summary>
        /// Event phát giá trị chỉ báo mới.
        /// </summary>
        event EventHandler<TValue> Updated;

        /// <summary>
        /// Cập nhật chỉ báo với dữ liệu mới.
        /// </summary>
        void Update(TInput input);
    }
}
