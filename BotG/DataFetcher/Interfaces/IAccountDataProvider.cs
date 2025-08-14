using System;
using DataFetcher.Models;

namespace DataFetcher.Interfaces
{
    /// <summary>
    /// Interface cung cấp dữ liệu tài khoản, phát event khi có thay đổi.
    /// </summary>
    public interface IAccountDataProvider
    {
        /// <summary>
        /// Sự kiện phát khi dữ liệu tài khoản thay đổi.
        /// </summary>
        event EventHandler<AccountInfo> AccountChanged;
    }
}
