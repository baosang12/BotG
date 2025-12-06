using System;
using System.Collections.Generic;
using System.Linq;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors
{
    /// <summary>
    /// Đánh giá chất lượng breakout dựa trên cấu trúc, retest và follow-through đa timeframe.
    /// </summary>
    public sealed class BreakoutQualityEvaluator : IPatternDetector
    {
        private BreakoutQualityParameters _parameters;

        public BreakoutQualityEvaluator(BreakoutQualityParameters? parameters = null)
        {
            _parameters = (parameters ?? BreakoutQualityParameters.CreateDefault()).Clone();
        }

        public string Name => "BreakoutQuality";

        public double Weight { get; set; } = 0.2;

        public bool IsEnabled { get; set; } = true;

        public PatternDetectionResult Detect(SnapshotDataAccessor accessor)
        {
            var result = PatternDetectionResult.Neutral();

            if (accessor == null)
            {
                result.Diagnostics["Status"] = "AccessorNull";
                return result;
            }

            var parameters = _parameters ?? BreakoutQualityParameters.CreateDefault();
            parameters.EnsureDefaults();
            var dataReq = parameters.DataRequirements;

            var bars = accessor.GetBars(dataReq.PrimaryTimeFrame, dataReq.AnalysisBars);
            if (bars == null || bars.Count < dataReq.MinimumBars)
            {
                result.Diagnostics["Status"] = "InsufficientData";
                result.Diagnostics["BarCount"] = bars?.Count ?? 0;
                result.Score = parameters.Scoring.Baseline;
                result.Confidence = parameters.Scoring.ConfidenceBase;
                return result;
            }

            var atr = EstimateAtr(bars, dataReq.AtrPeriod);
            var breakout = DetectBreakout(bars, accessor, parameters, atr);
            if (!breakout.IsValid)
            {
                result.Diagnostics["Status"] = "NoBreakout";
                result.Diagnostics["Reason"] = breakout.DiagnosticsReason ?? "NoLevelBreak";
                result.Score = parameters.Scoring.Baseline;
                result.Confidence = parameters.Scoring.ConfidenceBase * 0.9;
                return result;
            }

            var retest = AnalyzeRetest(bars, breakout, parameters, atr);
            var followThrough = AnalyzeFollowThrough(bars, accessor, breakout, retest, parameters, atr);

            var scoring = parameters.Scoring;
            var followThroughQualityForScore = followThrough.Quality;
            var retestQualityForScore = retest.Quality;
            var retestPenalty = 0.0;
            switch (retest.Status)
            {
                case "RetestTooDeep":
                    followThroughQualityForScore = Math.Min(followThroughQualityForScore, 0.2);
                    retestQualityForScore = Math.Min(retestQualityForScore, 0.25);
                    retestPenalty = scoring.RetestDeepPenalty;
                    break;
                case "RetestTooShallow":
                    followThroughQualityForScore = Math.Min(followThroughQualityForScore, 0.4);
                    retestQualityForScore = Math.Min(retestQualityForScore, 0.45);
                    retestPenalty = scoring.RetestShallowPenalty;
                    break;
                case "RetestMissing":
                case "RetestOptional":
                    followThroughQualityForScore = Math.Min(followThroughQualityForScore, 0.35);
                    retestQualityForScore = Math.Min(retestQualityForScore, 0.4);
                    retestPenalty = scoring.RetestMissingPenalty;
                    break;
            }

            if (followThrough.HasFailure)
            {
                followThroughQualityForScore = Math.Min(followThroughQualityForScore, 0.2);
                retestQualityForScore = Math.Min(retestQualityForScore, 0.4);
                retestPenalty += scoring.FailurePenalty;
            }

            var score = scoring.Baseline
                        + ConvertQualityToDelta(breakout.Quality, scoring.BreakoutWeight)
                        + ConvertQualityToDelta(retestQualityForScore, scoring.RetestWeight)
                        + ConvertQualityToDelta(followThroughQualityForScore, scoring.FollowThroughWeight)
                        - retestPenalty;

            result.Score = Math.Clamp(score, 0, 100);

            var confidence = scoring.ConfidenceBase
                             + breakout.Quality * scoring.ConfidenceBreakoutWeight
                             + retest.Quality * scoring.ConfidenceRetestWeight
                             + followThrough.Quality * scoring.ConfidenceFollowThroughWeight
                             + retest.ConfidenceDelta
                             + followThrough.ConfidenceBonus
                             - followThrough.FailurePenalty;
            result.Confidence = Math.Clamp(confidence, 0.1, 0.98);

            var flags = BuildFlags(breakout, retest, followThrough);
            if (flags.Count > 0)
            {
                result.Flags = flags;
            }

            PopulateDiagnostics(result.Diagnostics, breakout, retest, followThrough, atr, bars.Count, dataReq.PrimaryTimeFrame);

            return result;
        }

        public void UpdateParameters(BreakoutQualityParameters? parameters)
        {
            if (parameters == null)
            {
                return;
            }

            _parameters = parameters.Clone();
            _parameters.EnsureDefaults();
        }

        private static double ConvertQualityToDelta(double quality, double weight)
        {
            var normalized = Math.Clamp(quality, 0, 1);
            var centered = (normalized - 0.5) * 2; // -1 .. 1
            return centered * weight;
        }

        private static BreakoutInfo DetectBreakout(
            IReadOnlyList<Bar> bars,
            SnapshotDataAccessor accessor,
            BreakoutQualityParameters parameters,
            double atr)
        {
            var detection = parameters.Breakout;
            var dataReq = parameters.DataRequirements;

            if (bars.Count <= detection.SwingLookback + detection.ConfirmationBars)
            {
                return BreakoutInfo.Invalid("InsufficientSwingWindow");
            }

            BreakoutInfo best = BreakoutInfo.Invalid("NoCandidate");
            var buffer = Math.Max(atr * detection.LevelBufferAtrMultiplier, 1e-4);

            var lookaheadWindow = detection.ConfirmationBars + parameters.Retest.RetestWindow + parameters.FollowThrough.EvaluationWindow;
            var searchWindow = Math.Max(lookaheadWindow, detection.SwingLookback + detection.ConfirmationBars);
            var startIndex = Math.Max(detection.SwingLookback, bars.Count - searchWindow);
            const double qualityBiasTolerance = 0.05;
            for (var index = startIndex; index < bars.Count; index++)
            {
                if (index <= detection.SwingLookback)
                {
                    continue;
                }

                var referenceStart = Math.Max(0, index - detection.SwingLookback);
                var levelHigh = GetMaxHigh(bars, referenceStart, index - 1);
                var levelLow = GetMinLow(bars, referenceStart, index - 1);
                var bar = bars[index];
                var avgRange = GetAverageRange(bars, Math.Max(0, index - dataReq.RangeLookback), index - 1);
                var volumeBaseline = GetAverageVolume(bars, Math.Max(0, index - dataReq.VolumeLookback), index - 1);
                var range = Math.Max(bar.High - bar.Low, 1e-5);
                var body = Math.Abs(bar.Close - bar.Open);
                var bodyRatio = body / range;
                var rangeFactor = range / Math.Max(avgRange, 1e-5);
                var volumeSpike = bar.Volume / Math.Max(volumeBaseline, 1);
                var roc = ComputeRoc(bars, index, detection.MomentumLookback);
                var candidate = BreakoutInfo.Invalid("NoBreakout");

                if (bar.Close > levelHigh + buffer)
                {
                    candidate = CreateBreakoutInfo(
                        BreakoutDirection.Bullish,
                        index,
                        levelHigh,
                        bar.Close,
                        range,
                        bodyRatio,
                        rangeFactor,
                        volumeSpike,
                        bar.Close - levelHigh,
                        roc,
                        detection,
                        parameters);
                }
                else if (bar.Close < levelLow - buffer)
                {
                    candidate = CreateBreakoutInfo(
                        BreakoutDirection.Bearish,
                        index,
                        levelLow,
                        bar.Close,
                        range,
                        bodyRatio,
                        rangeFactor,
                        volumeSpike,
                        levelLow - bar.Close,
                        roc,
                        detection,
                        parameters);
                }

                if (!candidate.IsValid)
                {
                    continue;
                }

                if (!best.IsValid
                    || candidate.Quality > best.Quality + qualityBiasTolerance
                    || (Math.Abs(candidate.Quality - best.Quality) <= qualityBiasTolerance && candidate.Index < best.Index))
                {
                    best = candidate;
                }
            }

            return best.IsValid ? best : BreakoutInfo.Invalid("NoBreakout");
        }

        private static BreakoutInfo CreateBreakoutInfo(
            BreakoutDirection direction,
            int index,
            double level,
            double close,
            double range,
            double bodyRatio,
            double rangeFactor,
            double volumeSpike,
            double distanceFromLevel,
            double roc,
            BreakoutDetectionParams detection,
            BreakoutQualityParameters parameters)
        {
            if (bodyRatio < detection.MinBodyRatio && rangeFactor < detection.MinRangeFactor)
            {
                return BreakoutInfo.Invalid("WeakImpulse");
            }

            var quality = 0.0;
            quality += Normalize(bodyRatio, detection.MinBodyRatio, detection.TargetBodyRatio) * 0.4;
            quality += Normalize(rangeFactor, detection.MinRangeFactor, detection.TargetRangeFactor) * 0.3;
            quality += Normalize(volumeSpike, detection.MinVolumeSpike, detection.TargetVolumeSpike) * 0.3;
            quality = Math.Clamp(quality, 0, 1);

            var momentumConfirmed = roc >= detection.MomentumRocThreshold
                                     || (direction == BreakoutDirection.Bullish
                                         ? close > level && distanceFromLevel > parameters.DataRequirements.MomentumDistanceFloor
                                         : close < level && distanceFromLevel > parameters.DataRequirements.MomentumDistanceFloor);

            if (!momentumConfirmed)
            {
                quality *= 0.8;
            }

            return new BreakoutInfo
            {
                IsValid = true,
                Direction = direction,
                Index = index,
                Level = level,
                BreakoutClose = close,
                Range = range,
                BodyRatio = bodyRatio,
                RangeFactor = rangeFactor,
                VolumeSpike = volumeSpike,
                DistanceFromLevel = distanceFromLevel,
                ConfirmationDistance = distanceFromLevel,
                Quality = quality,
                MomentumConfirmed = momentumConfirmed,
                DiagnosticsReason = momentumConfirmed ? "BreakoutConfirmed" : "BreakoutWeakMomentum"
            };
        }

        private static RetestAnalysis AnalyzeRetest(
            IReadOnlyList<Bar> bars,
            BreakoutInfo breakout,
            BreakoutQualityParameters parameters,
            double atr)
        {
            var retestParams = parameters.Retest;
            var start = breakout.Index + 1;
            var end = Math.Min(bars.Count - 1, breakout.Index + retestParams.RetestWindow);
            if (start >= bars.Count)
            {
                return RetestAnalysis.Missing(retestParams.RequireRetest);
            }

            var bestDepth = double.MaxValue;
            RetestCandidate? bestCandidate = null;
            var shallowDetected = false;
            double shallowDepth = 0;
            var shallowIndex = -1;

            for (var i = start; i <= end; i++)
            {
                var bar = bars[i];
                var depthFraction = breakout.Direction == BreakoutDirection.Bullish
                    ? (breakout.BreakoutClose - bar.Low) / Math.Max(breakout.Range, atr)
                    : (bar.High - breakout.BreakoutClose) / Math.Max(breakout.Range, atr);

                if (depthFraction < 0)
                {
                    continue;
                }

                if (depthFraction > retestParams.MaxDepthFraction)
                {
                    return RetestAnalysis.Deep(depthFraction, i);
                }

                if (depthFraction < retestParams.MinDepthFraction)
                {
                    if (depthFraction > 1e-3)
                    {
                        shallowDetected = true;
                        if (depthFraction > shallowDepth)
                        {
                            shallowDepth = depthFraction;
                            shallowIndex = i;
                        }
                    }
                    continue;
                }

                var distanceFromIdeal = Math.Abs(depthFraction - retestParams.IdealDepthMid);
                if (distanceFromIdeal < bestDepth)
                {
                    bestDepth = distanceFromIdeal;
                    bestCandidate = new RetestCandidate(i, depthFraction, bar.Volume);
                }
            }

            if (bestCandidate == null)
            {
                if (shallowDetected)
                {
                    return RetestAnalysis.Shallow(shallowDepth, shallowIndex);
                }

                return RetestAnalysis.Missing(retestParams.RequireRetest);
            }

            var quality = 1.0 - Math.Min(1.0, bestDepth / retestParams.IdealDepthSpan);
            var volumeRatio = bestCandidate.Volume / Math.Max(1, bars[breakout.Index].Volume);
            if (volumeRatio <= retestParams.VolumeDropThreshold)
            {
                quality = Math.Min(1.0, quality + 0.15);
            }

            return RetestAnalysis.Success(bestCandidate.DepthFraction, bestCandidate.Index, quality, volumeRatio <= retestParams.VolumeDropThreshold);
        }

        private static FollowThroughAnalysis AnalyzeFollowThrough(
            IReadOnlyList<Bar> bars,
            SnapshotDataAccessor accessor,
            BreakoutInfo breakout,
            RetestAnalysis retest,
            BreakoutQualityParameters parameters,
            double atr)
        {
            var followParams = parameters.FollowThrough;
            var evaluationStart = retest.HasRetest ? retest.Index : breakout.Index;
            var end = Math.Min(bars.Count - 1, evaluationStart + followParams.EvaluationWindow);
            if (evaluationStart >= bars.Count)
            {
                return FollowThroughAnalysis.Empty;
            }

            var referencePrice = retest.HasRetest
                ? bars[Math.Min(retest.Index, bars.Count - 1)].Close
                : breakout.BreakoutClose;

            double maxExtension = 0;
            double giveback = 0;
            var directionalSteps = 0;
            var totalSteps = 0;
            var prevClose = bars[evaluationStart].Close;

            for (var i = evaluationStart + 1; i <= end; i++)
            {
                var bar = bars[i];
                totalSteps++;
                var move = breakout.Direction == BreakoutDirection.Bullish
                    ? bar.High - referencePrice
                    : referencePrice - bar.Low;
                maxExtension = Math.Max(maxExtension, move);

                var close = bar.Close;
                if ((breakout.Direction == BreakoutDirection.Bullish && close > prevClose) ||
                    (breakout.Direction == BreakoutDirection.Bearish && close < prevClose))
                {
                    directionalSteps++;
                }
                prevClose = close;

                var givebackCandidate = breakout.Direction == BreakoutDirection.Bullish
                    ? Math.Max(0, breakout.Level - bar.Close)
                    : Math.Max(0, bar.Close - breakout.Level);
                giveback = Math.Max(giveback, givebackCandidate);
            }

            var extensionAtr = maxExtension / Math.Max(atr, 1e-4);
            var quality = Normalize(extensionAtr, followParams.MinExtensionAtr, followParams.StrongExtensionAtr);
            var hasMomentum = totalSteps > 0 && directionalSteps >= Math.Max(1, totalSteps / 2);
            if (!hasMomentum)
            {
                quality *= 0.8;
            }

            var failure = giveback >= breakout.Range * followParams.FailureGivebackFraction;
            if (failure)
            {
                quality *= 0.5;
            }

            var higherAligned = IsHigherTimeframeAligned(accessor, breakout.Direction, parameters);
            if (higherAligned)
            {
                quality = Math.Min(1.0, quality + 0.1);
            }

            var confidenceBonus = 0.0;
            if (extensionAtr >= followParams.StrongExtensionAtr)
            {
                confidenceBonus += 0.05;
            }

            if (hasMomentum)
            {
                confidenceBonus += 0.03;
            }

            var failurePenalty = failure ? 0.08 : 0.0;

            return new FollowThroughAnalysis
            {
                Quality = quality,
                ExtensionAtr = extensionAtr,
                HasMomentum = hasMomentum,
                HasFailure = failure,
                ConfidenceBonus = confidenceBonus,
                FailurePenalty = failurePenalty,
                HigherTimeframeAligned = higherAligned
            };
        }

        private static bool IsHigherTimeframeAligned(
            SnapshotDataAccessor accessor,
            BreakoutDirection direction,
            BreakoutQualityParameters parameters)
        {
            var dataReq = parameters.DataRequirements;
            if (dataReq.ConfirmationTimeFrame == dataReq.PrimaryTimeFrame)
            {
                return false;
            }

            var higherBars = accessor.GetBars(dataReq.ConfirmationTimeFrame, dataReq.HigherTimeframeBars);
            if (higherBars == null || higherBars.Count < dataReq.HigherTimeframeMinBars)
            {
                return false;
            }

            var slopeWindow = Math.Min(dataReq.HigherTimeframeSlopeWindow, higherBars.Count - 1);
            if (slopeWindow <= 0)
            {
                return false;
            }

            var anchorIndex = Math.Max(0, higherBars.Count - slopeWindow - 1);
            var slope = higherBars[^1].Close - higherBars[anchorIndex].Close;
            return direction == BreakoutDirection.Bullish ? slope > 0 : slope < 0;
        }

        private static List<string> BuildFlags(BreakoutInfo breakout, RetestAnalysis retest, FollowThroughAnalysis followThrough)
        {
            var flags = new List<string>();

            if (breakout.IsValid)
            {
                flags.Add("BreakoutCandidate");
                if (breakout.BodyRatio >= 0.75 && breakout.RangeFactor >= 1.5)
                {
                    flags.Add("HighQualityBreakout");
                }
            }

            if (retest.HasRetest)
            {
                flags.Add("ValidatedRetest");
            }
            else if (retest.Status == "RetestMissing")
            {
                flags.Add("RetestMissing");
            }

            if (retest.Status == "RetestTooDeep")
            {
                flags.Add("RetestTooDeep");
            }
            else if (retest.Status == "RetestTooShallow")
            {
                flags.Add("RetestTooShallow");
            }

            if (followThrough.ExtensionAtr >= 1.2)
            {
                flags.Add("BreakoutFollowThrough");
            }

            if (followThrough.HasFailure)
            {
                flags.Add("FailedBreakout");
            }

            if (followThrough.HigherTimeframeAligned)
            {
                flags.Add("MultiTimeframeConfluence");
            }

            if (followThrough.HasMomentum)
            {
                flags.Add("MomentumConfirmation");
            }

            return flags;
        }

        private static void PopulateDiagnostics(
            IDictionary<string, object> diagnostics,
            BreakoutInfo breakout,
            RetestAnalysis retest,
            FollowThroughAnalysis followThrough,
            double atr,
            int barCount,
            TimeFrame timeframe)
        {
            diagnostics["Status"] = "OK";
            diagnostics["PrimaryTimeframe"] = timeframe.ToString();
            diagnostics["BarsAnalyzed"] = barCount;
            diagnostics["ATR"] = Math.Round(atr, 5);

            diagnostics["Breakout"] = new Dictionary<string, object>
            {
                ["Direction"] = breakout.Direction.ToString(),
                ["BodyRatio"] = Math.Round(breakout.BodyRatio, 3),
                ["RangeFactor"] = Math.Round(breakout.RangeFactor, 3),
                ["VolumeSpike"] = Math.Round(breakout.VolumeSpike, 3),
                ["Distance"] = Math.Round(breakout.DistanceFromLevel, 5),
                ["Quality"] = Math.Round(breakout.Quality, 3),
                ["MomentumConfirmed"] = breakout.MomentumConfirmed
            };

            diagnostics["Retest"] = new Dictionary<string, object>
            {
                ["Status"] = retest.Status,
                ["DepthFraction"] = Math.Round(retest.DepthFraction, 3),
                ["Quality"] = Math.Round(retest.Quality, 3),
                ["Index"] = retest.Index,
                ["VolumeDrop"] = retest.VolumeDrop
            };

            diagnostics["FollowThrough"] = new Dictionary<string, object>
            {
                ["ExtensionAtr"] = Math.Round(followThrough.ExtensionAtr, 3),
                ["Quality"] = Math.Round(followThrough.Quality, 3),
                ["Momentum"] = followThrough.HasMomentum,
                ["HigherTFAligned"] = followThrough.HigherTimeframeAligned,
                ["Failure"] = followThrough.HasFailure
            };
        }

        private static double EstimateAtr(IReadOnlyList<Bar> bars, int period)
        {
            if (bars == null || bars.Count < 2)
            {
                return 0.5;
            }

            var lookback = Math.Min(period, bars.Count - 1);
            double sum = 0;
            for (var i = 0; i < lookback; i++)
            {
                var current = bars[^(i + 1)];
                var previous = bars[^(i + 2)];
                var tr = Math.Max(
                    current.High - current.Low,
                    Math.Max(
                        Math.Abs(current.High - previous.Close),
                        Math.Abs(current.Low - previous.Close)));
                sum += tr;
            }

            return Math.Max(0.0005, sum / lookback);
        }

        private static double GetAverageRange(IReadOnlyList<Bar> bars, int start, int end)
        {
            start = Math.Max(0, start);
            end = Math.Max(start, end);
            var count = 0;
            double sum = 0;
            for (var i = start; i <= end && i < bars.Count; i++)
            {
                sum += Math.Max(bars[i].High - bars[i].Low, 1e-5);
                count++;
            }

            return count == 0 ? 1e-4 : sum / count;
        }

        private static double GetAverageVolume(IReadOnlyList<Bar> bars, int start, int end)
        {
            start = Math.Max(0, start);
            end = Math.Max(start, end);
            var count = 0;
            double sum = 0;
            for (var i = start; i <= end && i < bars.Count; i++)
            {
                sum += bars[i].Volume;
                count++;
            }

            return count == 0 ? 1 : sum / count;
        }

        private static double GetMaxHigh(IReadOnlyList<Bar> bars, int start, int end)
        {
            var value = double.MinValue;
            for (var i = Math.Max(0, start); i <= end && i < bars.Count; i++)
            {
                value = Math.Max(value, bars[i].High);
            }

            return value;
        }

        private static double GetMinLow(IReadOnlyList<Bar> bars, int start, int end)
        {
            var value = double.MaxValue;
            for (var i = Math.Max(0, start); i <= end && i < bars.Count; i++)
            {
                value = Math.Min(value, bars[i].Low);
            }

            return value;
        }

        private static double ComputeRoc(IReadOnlyList<Bar> bars, int index, int lookback)
        {
            if (index - lookback < 0)
            {
                return 0;
            }

            var latest = bars[index].Close;
            var past = bars[index - lookback].Close;
            if (Math.Abs(past) < 1e-5)
            {
                return 0;
            }

            return (latest - past) / past * 100.0;
        }

        private static double Normalize(double value, double min, double target)
        {
            var span = Math.Max(1e-6, target - min);
            var normalized = (value - min) / span;
            return Math.Clamp(normalized, 0, 1);
        }

        private sealed record RetestCandidate(int Index, double DepthFraction, long Volume);
    }

    public enum BreakoutDirection
    {
        Bullish,
        Bearish
    }

    public sealed class BreakoutQualityParameters
    {
        public BreakoutDetectionParams Breakout { get; set; } = new();
        public RetestAnalysisParams Retest { get; set; } = new();
        public FollowThroughParams FollowThrough { get; set; } = new();
        public ScoringParams Scoring { get; set; } = new();
        public DataRequirements DataRequirements { get; set; } = new();

        public static BreakoutQualityParameters CreateDefault() => new();

        public BreakoutQualityParameters Clone()
        {
            return new BreakoutQualityParameters
            {
                Breakout = Breakout.Clone(),
                Retest = Retest.Clone(),
                FollowThrough = FollowThrough.Clone(),
                Scoring = Scoring.Clone(),
                DataRequirements = DataRequirements.Clone()
            };
        }

        public void EnsureDefaults()
        {
            Breakout ??= new BreakoutDetectionParams();
            Retest ??= new RetestAnalysisParams();
            FollowThrough ??= new FollowThroughParams();
            Scoring ??= new ScoringParams();
            DataRequirements ??= new DataRequirements();
        }
    }

    public sealed class BreakoutDetectionParams
    {
        public int SwingLookback { get; set; } = 40;
        public int ConfirmationBars { get; set; } = 4;
        public double MinBodyRatio { get; set; } = 0.55;
        public double TargetBodyRatio { get; set; } = 0.85;
        public double MinRangeFactor { get; set; } = 1.10;
        public double TargetRangeFactor { get; set; } = 1.80;
        public double MinVolumeSpike { get; set; } = 1.25;
        public double TargetVolumeSpike { get; set; } = 1.80;
        public double LevelBufferAtrMultiplier { get; set; } = 0.25;
        public int MomentumLookback { get; set; } = 5;
        public double MomentumRocThreshold { get; set; } = 3.0;

        public BreakoutDetectionParams Clone()
        {
            return (BreakoutDetectionParams)MemberwiseClone();
        }
    }

    public sealed class RetestAnalysisParams
    {
        public bool RequireRetest { get; set; } = false;
        public int RetestWindow { get; set; } = 6;
        public double MinDepthFraction { get; set; } = 0.15;
        public double MaxDepthFraction { get; set; } = 0.65;
        public double IdealDepthMin { get; set; } = 0.25;
        public double IdealDepthMax { get; set; } = 0.45;
        public double VolumeDropThreshold { get; set; } = 0.75;

        public double IdealDepthMid => (IdealDepthMin + IdealDepthMax) / 2.0;
        public double IdealDepthSpan => Math.Max(0.05, IdealDepthMax - IdealDepthMin);

        public RetestAnalysisParams Clone()
        {
            return (RetestAnalysisParams)MemberwiseClone();
        }
    }

    public sealed class FollowThroughParams
    {
        public int EvaluationWindow { get; set; } = 8;
        public double MinExtensionAtr { get; set; } = 0.8;
        public double StrongExtensionAtr { get; set; } = 1.6;
        public double FailureGivebackFraction { get; set; } = 0.55;

        public FollowThroughParams Clone()
        {
            return (FollowThroughParams)MemberwiseClone();
        }
    }

    public sealed class ScoringParams
    {
        public double Baseline { get; set; } = 50.0;
        public double BreakoutWeight { get; set; } = 20.0;
        public double RetestWeight { get; set; } = 15.0;
        public double FollowThroughWeight { get; set; } = 20.0;
        public double ConfidenceBase { get; set; } = 0.55;
        public double ConfidenceBreakoutWeight { get; set; } = 0.12;
        public double ConfidenceRetestWeight { get; set; } = 0.08;
        public double ConfidenceFollowThroughWeight { get; set; } = 0.12;
        public double RetestDeepPenalty { get; set; } = 18.0;
        public double RetestShallowPenalty { get; set; } = 8.0;
        public double RetestMissingPenalty { get; set; } = 6.0;
        public double FailurePenalty { get; set; } = 18.0;

        public ScoringParams Clone()
        {
            return (ScoringParams)MemberwiseClone();
        }
    }

    public sealed class DataRequirements
    {
        public TimeFrame PrimaryTimeFrame { get; set; } = TimeFrame.H1;
        public TimeFrame ConfirmationTimeFrame { get; set; } = TimeFrame.H4;
        public int AnalysisBars { get; set; } = 160;
        public int MinimumBars { get; set; } = 80;
        public int HigherTimeframeBars { get; set; } = 80;
        public int HigherTimeframeMinBars { get; set; } = 20;
        public int HigherTimeframeSlopeWindow { get; set; } = 5;
        public int AtrPeriod { get; set; } = 14;
        public int VolumeLookback { get; set; } = 20;
        public int RangeLookback { get; set; } = 20;
        public double MomentumDistanceFloor { get; set; } = 0.0005;

        public DataRequirements Clone()
        {
            return (DataRequirements)MemberwiseClone();
        }
    }

    internal sealed class BreakoutInfo
    {
        public bool IsValid { get; init; }
        public BreakoutDirection Direction { get; init; }
        public int Index { get; init; }
        public double Level { get; init; }
        public double BreakoutClose { get; init; }
        public double Range { get; init; }
        public double BodyRatio { get; init; }
        public double RangeFactor { get; init; }
        public double VolumeSpike { get; init; }
        public double DistanceFromLevel { get; init; }
        public double ConfirmationDistance { get; init; }
        public double Quality { get; init; }
        public bool MomentumConfirmed { get; init; }
        public string DiagnosticsReason { get; init; } = string.Empty;

        public static BreakoutInfo Invalid(string reason)
        {
            return new BreakoutInfo
            {
                IsValid = false,
                DiagnosticsReason = reason
            };
        }
    }

    internal sealed class RetestAnalysis
    {
        public static RetestAnalysis Missing(bool required)
        {
            return new RetestAnalysis
            {
                Status = required ? "RetestMissing" : "RetestOptional",
                Quality = required ? 0.2 : 0.45,
                HasRetest = false,
                ConfidenceDelta = required ? -0.08 : -0.02,
                VolumeDrop = false
            };
        }

        public static RetestAnalysis Deep(double depth, int index)
        {
            return new RetestAnalysis
            {
                Status = "RetestTooDeep",
                DepthFraction = depth,
                Quality = 0.1,
                HasRetest = true,
                Index = index,
                ConfidenceDelta = -0.1,
                VolumeDrop = false
            };
        }

        public static RetestAnalysis Shallow(double depth, int index)
        {
            return new RetestAnalysis
            {
                Status = "RetestTooShallow",
                DepthFraction = depth,
                Quality = 0.35,
                HasRetest = true,
                Index = index,
                ConfidenceDelta = -0.03,
                VolumeDrop = false
            };
        }

        public static RetestAnalysis Success(double depth, int index, double quality, bool volumeDrop)
        {
            return new RetestAnalysis
            {
                Status = "RetestValid",
                DepthFraction = depth,
                Quality = Math.Clamp(quality, 0, 1),
                HasRetest = true,
                Index = index,
                ConfidenceDelta = 0.05,
                VolumeDrop = volumeDrop
            };
        }

        public string Status { get; init; } = "RetestTooShallow";
        public double DepthFraction { get; init; }
        public bool HasRetest { get; init; }
        public int Index { get; init; } = -1;
        public double Quality { get; init; } = 0.4;
        public double ConfidenceDelta { get; init; }
        public bool VolumeDrop { get; init; }
    }

    internal sealed class FollowThroughAnalysis
    {
        public static FollowThroughAnalysis Empty { get; } = new()
        {
            Quality = 0.4,
            ExtensionAtr = 0,
            ConfidenceBonus = 0,
            FailurePenalty = 0,
            HasMomentum = false,
            HasFailure = false,
            HigherTimeframeAligned = false
        };

        public double Quality { get; init; }
        public double ExtensionAtr { get; init; }
        public bool HasMomentum { get; init; }
        public bool HasFailure { get; init; }
        public double ConfidenceBonus { get; init; }
        public double FailurePenalty { get; init; }
        public bool HigherTimeframeAligned { get; init; }
    }
}
