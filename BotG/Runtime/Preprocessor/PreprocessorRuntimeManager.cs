using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.Indicators;
using AnalysisModule.Preprocessor.Indicators.Common;
using AnalysisModule.Preprocessor.Indicators.Interfaces;
using BotG.Config;
using BotG.Runtime.Logging;

namespace BotG.Runtime.Preprocessor;

/// <summary>
/// Bridges BotGRobot runtime with the AnalysisModule preprocessor engine.
/// Responsible for instantiating the pipeline, wiring indicator calculators
/// from config, and providing a simple tick ingest API for OnTick.
/// </summary>
public sealed class PreprocessorRuntimeManager : IDisposable
{
    private readonly Action<string>? _print;
    private readonly object _sync = new();
    private AnalysisPreprocessorEngine? _engine;
    private ManualTickSource? _manualSource;
    private PreprocessorRuntimeConfig? _config;
    private PreprocessorOptions? _options;
    private bool _isStarted;
    private bool _disposed;
    private DateTime _lastDegradedLogUtc = DateTime.MinValue;

    public PreprocessorRuntimeManager(Action<string>? print = null)
    {
        _print = print;
    }

    public event EventHandler<PreprocessorSnapshot>? SnapshotGenerated;

    public PreprocessorSnapshot? LatestSnapshot { get; private set; }

    public PreprocessorStatus? GetStatus()
    {
        lock (_sync)
        {
            return _engine?.GetStatus();
        }
    }

    public IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> GetBarHistorySnapshot()
    {
        lock (_sync)
        {
            if (_engine == null)
            {
                return new Dictionary<TimeFrame, IReadOnlyList<Bar>>();
            }

            return _engine.GetBarHistorySnapshot();
        }
    }

    public bool TryStart(PreprocessorRuntimeConfig? config)
    {
        LogDebug(
            "TryStart",
            "Preprocessor.TryStart invoked",
            new
            {
                hasConfig = config != null,
                config?.Enabled,
                configType = config?.GetType().FullName,
                timeframes = config?.Timeframes,
                indicators = DescribeIndicators(config?.Indicators)
            });

        if (config == null)
        {
            LogDebug("ConfigMissing", "Không tìm thấy cấu hình preprocessor – bỏ qua khởi động");
            return false;
        }

        if (!config.Enabled)
        {
            PipelineLogger.Log(
                "PREPROCESSOR",
                "Disabled",
                "Analysis preprocessor disabled via config",
                new { enabled = config.Enabled },
                _print);
            return false;
        }

        lock (_sync)
        {
            ThrowIfDisposed();

            if (_isStarted)
            {
                return true;
            }

            try
            {
                var timeframes = ParseTimeFrames(config.Timeframes);
                var calculators = CreateIndicatorCalculators(config.Indicators);
                var uniqueCalculators = calculators
                    .GroupBy(c => c.IndicatorName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (uniqueCalculators.Count != calculators.Count)
                {
                    var duplicates = calculators
                        .GroupBy(c => c.IndicatorName, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Key)
                        .ToArray();

                    LogDebug(
                        "DuplicateIndicators",
                        "Loại bỏ indicator trùng lặp trước khi đăng ký",
                        new { duplicates });
                }

                var indicatorNames = uniqueCalculators.Select(c => c.IndicatorName).ToArray();
                var snapshotDebounce = TimeSpan.FromMilliseconds(Math.Max(1, config.SnapshotDebounceMs));
                var options = new PreprocessorOptions(timeframes, indicatorNames, Math.Max(64, config.RecentTickCapacity), snapshotDebounce, Math.Max(32, config.BarHistoryCapacity));

                var orchestrator = new IndicatorOrchestrator();
                foreach (var calculator in uniqueCalculators)
                {
                    orchestrator.RegisterCalculator(calculator);
                }

                var engine = new AnalysisPreprocessorEngine(orchestrator);
                engine.SnapshotGenerated += OnSnapshotGenerated;

                var source = new ManualTickSource();
                engine.Start(source, options, CancellationToken.None);

                _engine = engine;
                _manualSource = source;
                _options = options;
                _config = config;
                _isStarted = true;

                PipelineLogger.Log(
                    "PREPROCESSOR",
                    "Ready",
                    "Analysis preprocessor started",
                    new
                    {
                        timeframes,
                        indicators = indicatorNames,
                        options.MaxRecentTicks,
                        options.BarHistoryCapacity,
                        debounce_ms = snapshotDebounce.TotalMilliseconds
                    },
                    _print);

                return true;
            }
            catch (Exception ex)
            {
                LogDebug(
                    "StartException",
                    "Preprocessor khởi động thất bại",
                    new { ex.Message });
                PipelineLogger.Log(
                    "PREPROCESSOR",
                    "StartFailed",
                    "Failed to start analysis preprocessor",
                    new { error = ex.Message },
                    _print);
                StopInternal();
                return false;
            }
        }
    }

    public void PublishTick(DateTime timestampUtc, double bid, double ask, long volume = 0)
    {
        if (bid <= 0 || ask <= 0 || double.IsNaN(bid) || double.IsNaN(ask))
        {
            return;
        }

        ManualTickSource? source;
        lock (_sync)
        {
            if (!_isStarted || _manualSource == null)
            {
                return;
            }

            source = _manualSource;
        }

        var utc = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);

        var tick = new Tick(utc, bid, ask, volume);
        source.Publish(tick);
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopInternal();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            StopInternal();
            _disposed = true;
        }
    }

    private void StopInternal()
    {
        if (_engine != null)
        {
            _engine.SnapshotGenerated -= OnSnapshotGenerated;
            try
            {
                _engine.Stop();
            }
            catch { }

            _engine.Dispose();
            _engine = null;
        }

        if (_manualSource != null)
        {
            _manualSource.Dispose();
            _manualSource = null;
        }

        _options = null;
        _config = null;
        _isStarted = false;
    }

    private void OnSnapshotGenerated(object? sender, PreprocessorSnapshot snapshot)
    {
        LatestSnapshot = snapshot;
        SnapshotGenerated?.Invoke(this, snapshot);

        if (snapshot.IsDegraded)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastDegradedLogUtc) > TimeSpan.FromSeconds(5))
            {
                _lastDegradedLogUtc = now;
                PipelineLogger.Log(
                    "PREPROCESSOR",
                    "Degraded",
                    "Snapshot indicates degraded pipeline state",
                    new { snapshot.DegradedReason },
                    _print);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PreprocessorRuntimeManager));
        }
    }

    private static TimeFrame[] ParseTimeFrames(IEnumerable<string>? raw)
    {
        var list = new List<TimeFrame>();
        if (raw != null)
        {
            foreach (var token in raw)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (Enum.TryParse<TimeFrame>(token, ignoreCase: true, out var tf))
                {
                    if (!list.Contains(tf))
                    {
                        list.Add(tf);
                    }
                }
                else
                {
                    throw new ArgumentException($"Unsupported timeframe '{token}'", nameof(raw));
                }
            }
        }

        if (list.Count == 0)
        {
            list.Add(TimeFrame.M1);
        }

        return list.ToArray();
    }

    private static IReadOnlyList<IIndicatorCalculator> CreateIndicatorCalculators(IList<PreprocessorIndicatorConfig>? indicatorConfigs)
    {
        if (indicatorConfigs == null || indicatorConfigs.Count == 0)
        {
            throw new ArgumentException("At least one indicator must be configured", nameof(indicatorConfigs));
        }

        var calculators = new List<IIndicatorCalculator>();
        foreach (var cfg in indicatorConfigs)
        {
            if (cfg == null)
            {
                continue;
            }

            var timeframe = ParseTimeFrame(cfg.Timeframe);
            var period = cfg.Period > 0 ? cfg.Period : 14;

            calculators.Add(cfg.Type?.Trim().ToUpperInvariant() switch
            {
                "SMA" => new SmaCalculator(timeframe, period),
                "RSI" => new RsiCalculator(timeframe, period),
                "ATR" => new AtrCalculator(timeframe, period),
                _ => throw new ArgumentException($"Unsupported indicator type '{cfg.Type}'", nameof(indicatorConfigs))
            });
        }

        if (calculators.Count == 0)
        {
            throw new ArgumentException("Indicators list resolved to empty", nameof(indicatorConfigs));
        }

        return calculators;
    }

    private static TimeFrame ParseTimeFrame(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Timeframe must be provided");
        }

        if (Enum.TryParse<TimeFrame>(raw, ignoreCase: true, out var tf))
        {
            return tf;
        }

        throw new ArgumentException($"Unsupported timeframe '{raw}'");
    }

    private sealed class ManualTickSource : IPreprocessorSource
    {
        public event EventHandler<Tick>? TickReceived;

        public void Publish(Tick tick)
        {
            TickReceived?.Invoke(this, tick);
        }

        public void Start(CancellationToken cancellationToken = default)
        {
            // Manual source is push-based; nothing to start.
        }

        public void Stop()
        {
            // Nothing to stop - receives ticks from Publish().
        }

        public void Dispose()
        {
            TickReceived = null;
        }
    }

    private static string[] DescribeIndicators(IList<PreprocessorIndicatorConfig>? configs)
    {
        if (configs == null)
        {
            return Array.Empty<string>();
        }

        return configs
            .Where(cfg => cfg != null && !string.IsNullOrWhiteSpace(cfg.Type))
            .Select(cfg =>
            {
                var tf = string.IsNullOrWhiteSpace(cfg.Timeframe) ? "(null)" : cfg.Timeframe!.Trim();
                return $"{cfg.Type?.Trim()}({tf},{cfg.Period})";
            })
            .ToArray();
    }

    private void LogDebug(string evt, string message, object? data = null)
    {
        PipelineLogger.Log("PREPROCESSOR", evt, message, data, _print);
    }
}
