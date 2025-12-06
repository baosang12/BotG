using System;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;
using ModelAccountInfo = DataFetcher.Models.AccountInfo;

namespace BotG.Runtime.Preprocessor;

public interface IPreprocessorStrategyDataBridge
{
    ModelBar? GetLatestBar(ModelTimeFrame timeframe);
    double? GetIndicator(string indicatorName);
    DateTime? LatestSnapshotTime { get; }
    ModelAccountInfo? GetAccountInfo();
}

/// <summary>
/// Lightweight bridge that exposes preprocessor data snapshots to strategy code without
/// coupling them directly to pipeline types. This allows strategies to opportunistically
/// read enriched indicators while falling back to legacy calculations when unavailable.
/// </summary>
public sealed partial class PreprocessorStrategyDataBridge : IPreprocessorStrategyDataBridge, ITrendAnalysisBridge
{
    private readonly Func<PreprocessorSnapshot?> _snapshotAccessor;
    private readonly Func<PreprocessorTimeframeAdapter?> _adapterAccessor;
    private readonly ILogger<PreprocessorStrategyDataBridge> _logger;
    private readonly Func<bool> _trendEnabledProvider;
    private readonly TimeSpan _trendCacheDuration;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly object _trendLock = new();
    private TrendSignal? _cachedTrendSignal;
    private DateTime _trendLastUpdatedUtc = DateTime.MinValue;

    public PreprocessorStrategyDataBridge(
        Func<PreprocessorSnapshot?> snapshotAccessor,
        Func<PreprocessorTimeframeAdapter?> adapterAccessor,
        ILogger<PreprocessorStrategyDataBridge>? logger = null,
        Func<bool>? isTrendAnalysisEnabled = null,
        TimeSpan? trendCacheDuration = null,
        Func<DateTime>? utcNowProvider = null)
    {
        _snapshotAccessor = snapshotAccessor ?? throw new ArgumentNullException(nameof(snapshotAccessor));
        _adapterAccessor = adapterAccessor ?? throw new ArgumentNullException(nameof(adapterAccessor));
        _logger = logger ?? NullLogger<PreprocessorStrategyDataBridge>.Instance;
        _trendEnabledProvider = isTrendAnalysisEnabled ?? (() => true);
        _trendCacheDuration = trendCacheDuration ?? TimeSpan.FromSeconds(30);
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    public ModelBar? GetLatestBar(ModelTimeFrame timeframe)
    {
        var adapter = _adapterAccessor();
        return adapter?.GetLatestBar(timeframe);
    }

    public double? GetIndicator(string indicatorName)
    {
        if (string.IsNullOrWhiteSpace(indicatorName))
        {
            return null;
        }

        var snapshot = _snapshotAccessor();
        if (snapshot?.Indicators == null)
        {
            return null;
        }

        return snapshot.Indicators.TryGetValue(indicatorName, out var value)
            ? value
            : (double?)null;
    }

    public DateTime? LatestSnapshotTime => _snapshotAccessor()?.TimestampUtc;

    public ModelAccountInfo? GetAccountInfo()
    {
        var account = _snapshotAccessor()?.Account;
        if (account == null)
        {
            return null;
        }

        return new ModelAccountInfo
        {
            Equity = account.Equity,
            Balance = account.Balance,
            Margin = account.Margin,
            Positions = account.OpenPositions
        };
    }

    /// <summary>
    /// Tín hiệu xu hướng hiện tại đã được cache (có thể null khi hết hạn hoặc chưa có dữ liệu).
    /// </summary>
    public TrendSignal? CurrentTrend
    {
        get
        {
            lock (_trendLock)
            {
                if (!IsTrendAnalysisEnabled)
                {
                    return null;
                }

                if (_cachedTrendSignal == null)
                {
                    return null;
                }

                var now = _utcNowProvider();
                if (IsTrendExpiredLocked(now))
                {
                    ClearTrendSignalLocked();
                    return null;
                }

                return _cachedTrendSignal;
            }
        }
        private set
        {
            lock (_trendLock)
            {
                _cachedTrendSignal = value;
                _trendLastUpdatedUtc = value == null ? DateTime.MinValue : _utcNowProvider();
            }
        }
    }

    public TrendSignal? GetCurrentTrend() => CurrentTrend;

    public TrendSignal? GetTrendSignal() => GetCurrentTrend();

    public void PublishTrendSignal(TrendSignal signal)
    {
        if (!IsTrendAnalysisEnabled || signal == null)
        {
            return;
        }

        if (!signal.IsValidForTrading(_trendCacheDuration))
        {
            _logger.LogWarning(
                "TrendSignal bị bỏ qua vì không hợp lệ (Score={Score:F2}, Confidence={Confidence:F2}).",
                signal.Score,
                signal.Confidence);
            return;
        }

        UpdateTrendSignal(signal);
    }

    public bool IsTrendAnalysisEnabled => EvaluateTrendFlag();

    public DateTime LastTrendUpdateTime
    {
        get
        {
            lock (_trendLock)
            {
                return _trendLastUpdatedUtc;
            }
        }
    }

    private void UpdateTrendSignal(TrendSignal signal)
    {
        if (signal == null)
        {
            return;
        }

        CurrentTrend = signal;
    }

    private bool IsTrendExpiredLocked(DateTime now)
    {
        if (_trendLastUpdatedUtc == DateTime.MinValue)
        {
            return true;
        }

        return now - _trendLastUpdatedUtc >= _trendCacheDuration;
    }

    private void ClearTrendSignalLocked()
    {
        _cachedTrendSignal = null;
        _trendLastUpdatedUtc = DateTime.MinValue;
    }

    private bool EvaluateTrendFlag()
    {
        try
        {
            return _trendEnabledProvider();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể đọc trạng thái TrendAnalyzer, trả về disabled.");
            return false;
        }
    }
}
