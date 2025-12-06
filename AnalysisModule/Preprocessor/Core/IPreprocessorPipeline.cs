#nullable enable
using System;
using System.Threading;

namespace AnalysisModule.Preprocessor.Core;

/// <summary>
/// Điều phối pipeline tiền xử lý, kết nối nguồn tick và phát snapshot cho downstream consumers.
/// </summary>
public interface IPreprocessorPipeline
{
    /// <summary>
    /// Raised mỗi khi có snapshot hoàn chỉnh được tạo ra.
    /// </summary>
    event EventHandler<PreprocessorSnapshot>? SnapshotGenerated;

    /// <summary>
    /// Trạng thái hiện tại của pipeline.
    /// </summary>
    PreprocessorState State { get; }

    /// <summary>
    /// Khởi động pipeline với nguồn dữ liệu và cấu hình đã cung cấp.
    /// </summary>
    /// <param name="source">Nguồn tick phù hợp chuẩn <see cref="IPreprocessorSource"/>.</param>
    /// <param name="options">Cấu hình pipeline.</param>
    /// <param name="cancellationToken">Token dùng để hủy khởi động nếu cần.</param>
    void Start(IPreprocessorSource source, PreprocessorOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dừng pipeline và giải phóng tài nguyên.
    /// </summary>
    void Stop();

    /// <summary>
    /// Lấy trạng thái thống kê gần nhất của pipeline.
    /// </summary>
    PreprocessorStatus GetStatus();
}
