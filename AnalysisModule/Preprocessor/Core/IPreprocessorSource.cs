#nullable enable
using System;
using System.Threading;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.Core;

/// <summary>
/// Chuẩn adapter cho mọi nguồn dữ liệu đẩy tick vào pipeline.
/// </summary>
public interface IPreprocessorSource : IDisposable
{
    /// <summary>
    /// Phát sinh khi nhận được tick mới đã vượt qua bước vệ sinh sơ bộ.
    /// </summary>
    event EventHandler<Tick>? TickReceived;

    /// <summary>
    /// Bắt đầu đọc dữ liệu và forwarding tick lên pipeline.
    /// </summary>
    void Start(CancellationToken cancellationToken = default);

    /// <summary>
    /// Dừng đọc dữ liệu; không nên throw trong phương thức này.
    /// </summary>
    void Stop();
}
