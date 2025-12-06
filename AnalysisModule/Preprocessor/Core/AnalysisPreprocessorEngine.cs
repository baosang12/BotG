#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Aggregators;
using AnalysisModule.Preprocessor.Caching;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.Indicators.Interfaces;
using AnalysisModule.Preprocessor.TrendAnalysis;

namespace AnalysisModule.Preprocessor.Core;

/// <summary>
/// Hiện thực cơ bản của pipeline tiền xử lý dựa trên data flow đã thiết kế.
/// </summary>
public sealed class AnalysisPreprocessorEngine : IPreprocessorPipeline, IDisposable
{
    private readonly IIndicatorOrchestrator _indicatorOrchestrator;
    private readonly TickPreProcessor _tickPreProcessor = new();
    private readonly Dictionary<TimeFrame, BarAggregator> _aggregators = new();
    private readonly Dictionary<string, object> _metadata = new(StringComparer.Ordinal);
    private readonly List<IPreprocessorSource> _sources = new();
    private readonly object _sync = new();

    private TickCache? _tickCache;
    private BarCache? _barCache;
    private CancellationTokenSource? _cts;
    private PreprocessorOptions? _options;
    private PreprocessorState _state = PreprocessorState.Stopped;
    private long _processedTicks;
    private DateTime? _lastTickTimestampUtc;
    private string? _degradedReason;
    private bool _tickProcessorAttached;

    public AnalysisPreprocessorEngine(IIndicatorOrchestrator indicatorOrchestrator)
    {
        _indicatorOrchestrator = indicatorOrchestrator ?? throw new ArgumentNullException(nameof(indicatorOrchestrator));
    }

    public event EventHandler<PreprocessorSnapshot>? SnapshotGenerated;

    public PreprocessorState State => _state;

    public void Start(IPreprocessorSource source, PreprocessorOptions options, CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        lock (_sync)
        {
            if (_state == PreprocessorState.Running)
            {
                throw new InvalidOperationException("Pipeline đang chạy.");
            }

            _options = options;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _state = PreprocessorState.Starting;
            _degradedReason = null;
            _processedTicks = 0;
            _lastTickTimestampUtc = null;
            _tickCache = new TickCache(options.MaxRecentTicks);
            _barCache = new BarCache(options.BarHistoryCapacity);
            _aggregators.Clear();

            foreach (var timeframe in options.TimeFrames)
            {
                var aggregator = new BarAggregator(timeframe);
                aggregator.BarClosed += OnBarClosed;
                _aggregators[timeframe] = aggregator;
            }

            if (!_tickProcessorAttached)
            {
                _tickPreProcessor.TickCleaned += OnTickCleaned;
                _tickProcessorAttached = true;
            }

            _sources.Add(source);
            source.TickReceived += OnSourceTick;
            source.Start(_cts.Token);
            _state = PreprocessorState.Running;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_state == PreprocessorState.Stopped)
            {
                return;
            }

            _state = PreprocessorState.Stopped;
            foreach (var source in _sources)
            {
                source.TickReceived -= OnSourceTick;
                source.Stop();
                source.Dispose();
            }

            _sources.Clear();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            foreach (var aggregator in _aggregators.Values)
            {
                aggregator.BarClosed -= OnBarClosed;
            }

            if (_tickProcessorAttached)
            {
                _tickPreProcessor.TickCleaned -= OnTickCleaned;
                _tickProcessorAttached = false;
            }
        }
    }

    public PreprocessorStatus GetStatus()
    {
        lock (_sync)
        {
            return new PreprocessorStatus(_state, _processedTicks, _lastTickTimestampUtc, _state == PreprocessorState.Degraded, _degradedReason);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnSourceTick(object? sender, Tick tick)
    {
        _tickPreProcessor.Process(tick);
    }

    private void OnTickCleaned(object? sender, Tick tick)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        _ = ProcessTickAsync(tick, token);
    }

    private void OnBarClosed(object? sender, Bar bar)
    {
        lock (_sync)
        {
            _barCache?.AddBar(bar.TimeFrame, bar);
        }
    }

    public async ValueTask ProcessTickAsync(Tick tick, CancellationToken cancellationToken = default)
    {
        TickCache? tickCache;
        BarCache? barCache;
        IReadOnlyList<Tick> ticksSnapshot;
        IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> barsSnapshot;

        lock (_sync)
        {
            EnsureStarted();
            tickCache = _tickCache!;
            barCache = _barCache!;

            tickCache.Add(tick);
            foreach (var aggregator in _aggregators.Values)
            {
                aggregator.Process(tick);
            }

            ticksSnapshot = tickCache.Snapshot();
            barsSnapshot = barCache.SnapshotAll();
            _processedTicks++;
            _lastTickTimestampUtc = tick.TimestampUtc;
            if (_state == PreprocessorState.Degraded)
            {
                _state = PreprocessorState.Running;
                _degradedReason = null;
            }
        }

        var context = new PreprocessorContext(ticksSnapshot, barsSnapshot, tick.TimestampUtc, _metadata);
        IReadOnlyDictionary<string, IndicatorResult> indicatorResults;

        try
        {
            indicatorResults = await _indicatorOrchestrator.CalculateAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _state = PreprocessorState.Degraded;
                _degradedReason = ex.Message;
            }

            var degradedSnapshot = new PreprocessorSnapshot(
                tick.TimestampUtc,
                new Dictionary<string, double>(),
                ExtractLatestBars(barsSnapshot, barCache),
                null,
                true,
                ex.Message);

            SnapshotHistoryRegistry.Attach(degradedSnapshot, barsSnapshot);
            SnapshotGenerated?.Invoke(this, degradedSnapshot);
            return;
        }

        var numericIndicators = indicatorResults
            .Where(pair => pair.Value.Value.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Value!.Value, StringComparer.Ordinal);

        var snapshot = new PreprocessorSnapshot(
            tick.TimestampUtc,
            numericIndicators,
            ExtractLatestBars(barsSnapshot, barCache),
            null,
            false,
            null);

        SnapshotHistoryRegistry.Attach(snapshot, barsSnapshot);
        SnapshotGenerated?.Invoke(this, snapshot);
    }

    private void EnsureStarted()
    {
        if (_options is null || _tickCache is null || _barCache is null)
        {
            throw new InvalidOperationException("Pipeline chưa được khởi động.");
        }
    }

    private static IReadOnlyDictionary<TimeFrame, Bar> ExtractLatestBars(
        IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> barsSnapshot,
        BarCache barCache)
    {
        var result = new Dictionary<TimeFrame, Bar>();
        foreach (var timeframe in barsSnapshot.Keys)
        {
            var latest = barCache.GetLastBar(timeframe);
            if (latest is not null)
            {
                result[timeframe] = latest;
            }
        }

        return result;
    }

    public IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> GetBarHistorySnapshot()
    {
        lock (_sync)
        {
            if (_barCache == null)
            {
                return new Dictionary<TimeFrame, IReadOnlyList<Bar>>();
            }

            return _barCache.SnapshotAll();
        }
    }
}
