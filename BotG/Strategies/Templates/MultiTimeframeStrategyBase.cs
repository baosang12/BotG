using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BotG.MultiTimeframe;
using BotG.Runtime.Preprocessor;
using ModelBar = DataFetcher.Models.Bar;
using ModelTimeFrame = DataFetcher.Models.TimeFrame;
using Strategies;

namespace Strategies.Templates
{
    /// <summary>
    /// Provides a template method implementation for strategies that require multi-timeframe context.
    /// </summary>
    public abstract class MultiTimeframeStrategyBase : IStrategy
    {
        private readonly TimeframeManager _timeframeManager;
        private readonly TimeframeSynchronizer _synchronizer;
        private readonly SessionAwareAnalyzer _sessionAnalyzer;
        private readonly IPreprocessorStrategyDataBridge? _preprocessorBridge;
        private static readonly TimeSpan DefaultIndicatorFreshness = TimeSpan.FromSeconds(2);
        private readonly int _minimumAlignedTimeframes;

        protected MultiTimeframeStrategyBase(
            string name,
            TimeframeManager timeframeManager,
            TimeframeSynchronizer synchronizer,
            SessionAwareAnalyzer sessionAnalyzer,
            IPreprocessorStrategyDataBridge? preprocessorBridge = null,
            int minimumAlignedTimeframes = 2)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Strategy name is required.", nameof(name));
            }

            if (minimumAlignedTimeframes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumAlignedTimeframes));
            }

            Name = name;
            _timeframeManager = timeframeManager ?? throw new ArgumentNullException(nameof(timeframeManager));
            _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
            _sessionAnalyzer = sessionAnalyzer ?? throw new ArgumentNullException(nameof(sessionAnalyzer));
            _preprocessorBridge = preprocessorBridge;
            _minimumAlignedTimeframes = minimumAlignedTimeframes;
        }

        public string Name { get; }

        public async Task<Signal?> EvaluateAsync(MarketData data, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var snapshot = _timeframeManager.CaptureSnapshot(data.Symbol, data.TimestampUtc);
            var alignment = _synchronizer.GetAlignmentResult(snapshot);

            if (!ValidateAlignment(alignment))
            {
                return null;
            }

            var session = _sessionAnalyzer.GetCurrentSession(snapshot.TimestampUtc);
            var evaluationContext = new MultiTimeframeEvaluationContext(
                data,
                snapshot,
                alignment,
                session,
                _preprocessorBridge);

            var signal = await EvaluateMultiTimeframeAsync(evaluationContext, ct).ConfigureAwait(false);
            return signal;
        }

        public abstract RiskScore CalculateRisk(MarketContext context);

        protected virtual bool ValidateAlignment(TimeframeAlignmentResult alignment)
        {
            if (alignment == null)
            {
                return false;
            }

            if (!alignment.AntiRepaintSafe)
            {
                return false;
            }

            var meetsThreshold = alignment.AlignedTimeframes >= Math.Max(_minimumAlignedTimeframes, 1);
            return alignment.IsAligned && meetsThreshold;
        }

        protected IDictionary<string, object?> BuildTelemetryMetadata(MultiTimeframeEvaluationContext context)
        {
            var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["timeframe_aligned"] = context.Alignment.AlignedTimeframes,
                ["timeframe_total"] = context.Alignment.TotalTimeframes,
                ["timeframe_anti_repaint"] = context.Alignment.AntiRepaintSafe,
                ["timeframe_reason"] = context.Alignment.Reason,
                ["session"] = context.Session.ToString()
            };

            return metadata;
        }

        protected abstract Task<Signal?> EvaluateMultiTimeframeAsync(
            MultiTimeframeEvaluationContext context,
            CancellationToken ct);

        protected double GetSessionMultiplier(TradingSession session) => _sessionAnalyzer.GetPositionSizeMultiplier(session);

        protected double? GetPreprocessorIndicatorValue(
            MultiTimeframeEvaluationContext context,
            string indicatorName,
            TimeSpan? maxStaleness = null)
        {
            if (string.IsNullOrWhiteSpace(indicatorName) || context?.PreprocessorBridge == null)
            {
                return null;
            }

            var snapshotTime = context.PreprocessorBridge.LatestSnapshotTime;
            if (!snapshotTime.HasValue)
            {
                return null;
            }

            var freshnessBudget = maxStaleness ?? DefaultIndicatorFreshness;
            if (freshnessBudget > TimeSpan.Zero)
            {
                var age = context.MarketData.TimestampUtc - snapshotTime.Value;
                if (age < TimeSpan.Zero || age > freshnessBudget)
                {
                    return null;
                }
            }

            var value = context.PreprocessorBridge.GetIndicator(indicatorName);
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                return null;
            }

            return value.Value;
        }

        protected ModelBar? GetPreprocessorBar(
            MultiTimeframeEvaluationContext context,
            ModelTimeFrame timeframe,
            TimeSpan? maxStaleness = null)
        {
            if (context?.PreprocessorBridge == null)
            {
                return null;
            }

            var snapshotTime = context.PreprocessorBridge.LatestSnapshotTime;
            if (maxStaleness.HasValue && snapshotTime.HasValue)
            {
                var age = context.MarketData.TimestampUtc - snapshotTime.Value;
                if (age < TimeSpan.Zero || age > maxStaleness.Value)
                {
                    return null;
                }
            }

            return context.PreprocessorBridge.GetLatestBar(timeframe);
        }
    }

    public sealed record MultiTimeframeEvaluationContext(
        MarketData MarketData,
        TimeframeSnapshot Snapshot,
        TimeframeAlignmentResult Alignment,
        TradingSession Session,
        IPreprocessorStrategyDataBridge? PreprocessorBridge);
}
