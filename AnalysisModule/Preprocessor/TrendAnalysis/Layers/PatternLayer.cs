using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;
using Microsoft.Extensions.Logging;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers
{
    /// <summary>
    /// Tổng hợp kết quả từ nhiều pattern detector để phát hiện dấu chân smart money.
    /// </summary>
    public sealed class PatternLayer : BaseLayerCalculator
    {
        private readonly List<IPatternDetector> _detectors;
        private readonly double _baselineScore;
        private static readonly IReadOnlyDictionary<string, object> EmptyDiagnostics =
            new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));
        public PatternLayerTelemetrySnapshot? LastTelemetrySnapshot { get; private set; }
        private bool ShouldEmitPatternTelemetry => Config?.FeatureFlags?.EnableTelemetry ?? false;

        public PatternLayer(ILogger logger)
            : this(logger, null, baselineScore: 50.0)
        {
        }

        public PatternLayer(
            ILogger logger,
            IEnumerable<IPatternDetector>? detectors,
            double baselineScore) : base(logger)
        {
            _baselineScore = baselineScore;
            _detectors = detectors?.Where(d => d != null).ToList()
                        ?? new List<IPatternDetector>
                        {
                            new LiquidityAnalyzer(),
                            new BreakoutQualityEvaluator(),
                            new MarketStructureDetector(),
                            new AccumulationDistributionDetector(),
                            new VolumeProfileDetector()
                        };
        }

        public override string LayerName => "Patterns";

        public IReadOnlyList<IPatternDetector> Detectors => _detectors;

        public override double CalculateScore(PreprocessorSnapshot snapshot, SnapshotDataAccessor accessor)
        {
            ResetDiagnostics();

            if (snapshot == null)
            {
                AddWarning("Snapshot null, trả về baseline.");
                return _baselineScore;
            }

            if (accessor == null)
            {
                AddWarning("SnapshotDataAccessor null, trả về baseline.");
                return _baselineScore;
            }

            return CalculateCompositeScore(accessor);
        }

        private double CalculateCompositeScore(SnapshotDataAccessor accessor)
        {
            double weightedSum = 0;
            double totalWeight = 0;
            var emitTelemetry = ShouldEmitPatternTelemetry;
            var allFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double>? detectorScores = emitTelemetry
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : null;
            Dictionary<string, IReadOnlyList<string>>? detectorFlags = emitTelemetry
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                : null;
            Dictionary<string, IReadOnlyDictionary<string, object>>? detectorDiagnostics = emitTelemetry
                ? new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase)
                : null;
            double? accumulationScore = null;
            double? accumulationConfidence = null;
            IReadOnlyList<string>? accumulationFlagSnapshot = null;
            string? accumulationPhase = null;
            double? marketStructureScore = null;
            string? marketStructureState = null;
            int? marketStructureTrend = null;
            bool? structureBreakDetected = null;
            int? structureSwingPoints = null;
            double? lastSwingHigh = null;
            double? lastSwingLow = null;
            double? volumeProfileScore = null;
            double? volumeProfilePoc = null;
            double? volumeProfileVaHigh = null;
            double? volumeProfileVaLow = null;
            string? volumeProfileFlagSnapshot = null;
            int? volumeProfileHvnCount = null;
            int? volumeProfileLvnCount = null;
            double? volumeProfileConcentration = null;

            foreach (var detector in _detectors)
            {
                if (detector == null || !detector.IsEnabled)
                {
                    continue;
                }

                var weight = Math.Max(0, detector.Weight);
                if (weight <= 0)
                {
                    continue;
                }

                PatternDetectionResult result;
                try
                {
                    result = detector.Detect(accessor) ?? PatternDetectionResult.Neutral();
                }
                catch (Exception ex)
                {
                    AddWarning($"Detector {detector.Name} lỗi: {ex.Message}.");
                    continue;
                }

                if (emitTelemetry && detectorScores != null)
                {
                    detectorScores[detector.Name] = result.Score;
                }
                if (result.Flags?.Count > 0)
                {
                    var perDetectorFlags = result.Flags
                        .Where(flag => !string.IsNullOrWhiteSpace(flag))
                        .Select(flag => flag.Trim())
                        .ToArray();
                    if (perDetectorFlags.Length > 0)
                    {
                        if (emitTelemetry && detectorFlags != null)
                        {
                            detectorFlags[detector.Name] = perDetectorFlags;
                        }
                        foreach (var flag in perDetectorFlags)
                        {
                            allFlags.Add(flag);
                        }
                    }
                }

                if (emitTelemetry && result.Diagnostics?.Count > 0 && detectorDiagnostics != null)
                {
                    detectorDiagnostics[detector.Name] = CloneDiagnostics(result.Diagnostics);
                }

                if (emitTelemetry && detector.Name.Equals("AccumulationDistribution", StringComparison.OrdinalIgnoreCase))
                {
                    accumulationScore = result.Score;
                    accumulationConfidence = result.Confidence;
                    if (result.Flags?.Count > 0)
                    {
                        accumulationFlagSnapshot = result.Flags.ToArray();
                    }

                    if (result.Diagnostics != null && result.Diagnostics.TryGetValue("Phase", out var phaseValue))
                    {
                        accumulationPhase = phaseValue?.ToString();
                    }
                }
                else if (emitTelemetry && detector.Name.Equals("MarketStructure", StringComparison.OrdinalIgnoreCase))
                {
                    marketStructureScore = result.Score;
                    var diagnostics = result.Diagnostics;
                    if (diagnostics != null)
                    {
                        marketStructureState = diagnostics.TryGetValue("Structure", out var structureValue)
                            ? structureValue?.ToString()
                            : null;

                        if (diagnostics.TryGetValue("TrendDirection", out var trendDirectionObj)
                            && int.TryParse(trendDirectionObj?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTrend))
                        {
                            marketStructureTrend = parsedTrend;
                        }

                        if (diagnostics.TryGetValue("BreakDetected", out var breakDetectedObj)
                            && bool.TryParse(breakDetectedObj?.ToString(), out var parsedBreak))
                        {
                            structureBreakDetected = parsedBreak;
                        }

                        if (diagnostics.TryGetValue("SwingPoints", out var swingPointsObj)
                            && int.TryParse(Convert.ToString(swingPointsObj, CultureInfo.InvariantCulture), out var parsedSwing))
                        {
                            structureSwingPoints = parsedSwing;
                        }

                        if (diagnostics.TryGetValue("LastSwingHigh", out var swingHighObj)
                            && double.TryParse(Convert.ToString(swingHighObj, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHigh)
                            && double.IsFinite(parsedHigh))
                        {
                            lastSwingHigh = parsedHigh;
                        }

                        if (diagnostics.TryGetValue("LastSwingLow", out var swingLowObj)
                            && double.TryParse(Convert.ToString(swingLowObj, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLow)
                            && double.IsFinite(parsedLow))
                        {
                            lastSwingLow = parsedLow;
                        }
                    }
                }
                else if (emitTelemetry && detector.Name.Equals("VolumeProfile", StringComparison.OrdinalIgnoreCase))
                {
                    volumeProfileScore = result.Score;
                    if (result.Flags?.Count > 0)
                    {
                        volumeProfileFlagSnapshot = string.Join('|', result.Flags);
                    }
                    else
                    {
                        volumeProfileFlagSnapshot = null;
                    }

                    var diagnostics = result.Diagnostics;
                    if (diagnostics != null)
                    {
                        if (diagnostics.TryGetValue("POCPrice", out var pocObj)
                            && double.TryParse(Convert.ToString(pocObj, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPoc)
                            && double.IsFinite(parsedPoc))
                        {
                            volumeProfilePoc = parsedPoc;
                        }

                        if (diagnostics.TryGetValue("VAHigh", out var vaHighObj)
                            && double.TryParse(Convert.ToString(vaHighObj, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedVaHigh)
                            && double.IsFinite(parsedVaHigh))
                        {
                            volumeProfileVaHigh = parsedVaHigh;
                        }

                        if (diagnostics.TryGetValue("VALow", out var vaLowObj)
                            && double.TryParse(Convert.ToString(vaLowObj, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedVaLow)
                            && double.IsFinite(parsedVaLow))
                        {
                            volumeProfileVaLow = parsedVaLow;
                        }

                        if (diagnostics.TryGetValue("Concentration", out var concentrationObj)
                            && double.TryParse(Convert.ToString(concentrationObj, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedConcentration)
                            && double.IsFinite(parsedConcentration))
                        {
                            volumeProfileConcentration = parsedConcentration;
                        }

                        if (diagnostics.TryGetValue("HVN_Count", out var hvnObj)
                            && int.TryParse(Convert.ToString(hvnObj, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHvn))
                        {
                            volumeProfileHvnCount = parsedHvn;
                        }

                        if (diagnostics.TryGetValue("LVN_Count", out var lvnObj)
                            && int.TryParse(Convert.ToString(lvnObj, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLvn))
                        {
                            volumeProfileLvnCount = parsedLvn;
                        }
                    }
                }

                weightedSum += result.Score * weight;
                totalWeight += weight;

                AddConfirmation($"Detector {detector.Name} score={result.Score:F1} w={weight:F2}.");
                AddDiagnostic($"[{detector.Name}]Score", result.Score);
                if (result.Diagnostics?.Count > 0)
                {
                    AddDiagnostic($"[{detector.Name}]Diagnostics", result.Diagnostics);
                }
            }

            if (allFlags.Count > 0)
            {
                AddDiagnostic("PatternFlags", allFlags.ToArray());
            }

            if (totalWeight <= 0)
            {
                if (emitTelemetry)
                {
                    LastTelemetrySnapshot = new PatternLayerTelemetrySnapshot(
                        DateTime.UtcNow,
                        _baselineScore,
                        new ReadOnlyDictionary<string, double>(detectorScores ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)),
                        new ReadOnlyDictionary<string, IReadOnlyList<string>>(detectorFlags ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)),
                        new ReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>(detectorDiagnostics ?? new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase)),
                        Array.Empty<string>(),
                        accumulationScore,
                        accumulationFlagSnapshot,
                        accumulationConfidence,
                        accumulationPhase,
                        marketStructureScore,
                        marketStructureState,
                        marketStructureTrend,
                        structureBreakDetected,
                        structureSwingPoints,
                        lastSwingHigh,
                        lastSwingLow,
                        volumeProfileScore,
                        volumeProfilePoc,
                        volumeProfileVaHigh,
                        volumeProfileVaLow,
                        volumeProfileFlagSnapshot,
                        volumeProfileHvnCount,
                        volumeProfileLvnCount,
                        volumeProfileConcentration,
                        telemetryVersion: 4);
                }
                else
                {
                    LastTelemetrySnapshot = null;
                }
                return _baselineScore;
            }

            var baselineWeight = Math.Max(0, 1.0 - totalWeight);
            var finalScore = weightedSum + _baselineScore * baselineWeight;
            finalScore = Math.Clamp(finalScore, 0, 100);

            if (emitTelemetry)
            {
                LastTelemetrySnapshot = new PatternLayerTelemetrySnapshot(
                    DateTime.UtcNow,
                    finalScore,
                    new ReadOnlyDictionary<string, double>(detectorScores ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)),
                    new ReadOnlyDictionary<string, IReadOnlyList<string>>(detectorFlags ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)),
                    new ReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>(detectorDiagnostics ?? new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase)),
                    allFlags.ToArray(),
                    accumulationScore,
                    accumulationFlagSnapshot,
                    accumulationConfidence,
                    accumulationPhase,
                    marketStructureScore,
                    marketStructureState,
                    marketStructureTrend,
                    structureBreakDetected,
                    structureSwingPoints,
                    lastSwingHigh,
                    lastSwingLow,
                    volumeProfileScore,
                    volumeProfilePoc,
                    volumeProfileVaHigh,
                    volumeProfileVaLow,
                    volumeProfileFlagSnapshot,
                    volumeProfileHvnCount,
                    volumeProfileLvnCount,
                    volumeProfileConcentration,
                    telemetryVersion: 4);
            }
            else
            {
                LastTelemetrySnapshot = null;
            }

            return finalScore;
        }

        public override void UpdateConfig(TrendAnalyzerConfig config)
        {
            base.UpdateConfig(config);
            ApplyPatternConfig(config?.PatternLayer);
        }

        public void UpdateDetectorConfig(string detectorName, bool isEnabled, double? weight = null)
        {
            if (string.IsNullOrWhiteSpace(detectorName))
            {
                return;
            }

            var detector = _detectors.FirstOrDefault(d =>
                detectorName.Equals(d.Name, StringComparison.OrdinalIgnoreCase));

            if (detector == null)
            {
                return;
            }

            detector.IsEnabled = isEnabled;
            if (weight.HasValue && weight.Value >= 0)
            {
                detector.Weight = weight.Value;
            }
        }

        private void ApplyPatternConfig(PatternLayerConfig? patternConfig)
        {
            if (patternConfig == null)
            {
                return;
            }

            patternConfig.EnsureDefaults();
            ApplyDetectorSettings("Liquidity", patternConfig.Liquidity);
            ApplyDetectorSettings("BreakoutQuality", patternConfig.BreakoutQuality);
            ApplyDetectorSettings("AccumulationDistribution", patternConfig.AccumulationDistribution);
            ApplyDetectorSettings("MarketStructure", patternConfig.MarketStructure);
            ApplyDetectorSettings("VolumeProfile", patternConfig.VolumeProfile);
        }

        private void ApplyDetectorSettings(string detectorName, PatternDetectorConfig? detectorConfig)
        {
            if (detectorConfig == null || string.IsNullOrWhiteSpace(detectorName))
            {
                return;
            }

            var detector = _detectors.FirstOrDefault(d =>
                detectorName.Equals(d.Name, StringComparison.OrdinalIgnoreCase));

            if (detector == null)
            {
                return;
            }

            detector.IsEnabled = detectorConfig.Enabled;
            if (detectorConfig.Weight >= 0)
            {
                detector.Weight = detectorConfig.Weight;
            }

            if (detector is BreakoutQualityEvaluator breakout && detectorConfig is BreakoutQualityDetectorConfig typedConfig && typedConfig.Parameters != null)
            {
                breakout.UpdateParameters(typedConfig.Parameters);
            }
            else if (detector is AccumulationDistributionDetector accumulation && detectorConfig is AccumulationDistributionDetectorConfig accumulationConfig && accumulationConfig.Parameters != null)
            {
                accumulation.UpdateParameters(accumulationConfig.Parameters);
            }
            else if (detector is MarketStructureDetector marketStructure
                     && detectorConfig is MarketStructureDetectorConfig marketStructureConfig
                     && marketStructureConfig.Parameters != null)
            {
                marketStructure.UpdateParameters(marketStructureConfig.Parameters);
            }
            else if (detector is VolumeProfileDetector volumeProfile
                     && detectorConfig is VolumeProfileConfig volumeProfileConfig
                     && volumeProfileConfig.Parameters != null)
            {
                volumeProfile.UpdateParameters(volumeProfileConfig.Parameters);
            }
        }

        private static IReadOnlyDictionary<string, object> CloneDiagnostics(IReadOnlyDictionary<string, object>? diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0)
            {
                return EmptyDiagnostics;
            }

            var copy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in diagnostics)
            {
                copy[pair.Key] = pair.Value ?? string.Empty;
            }

            return new ReadOnlyDictionary<string, object>(copy);
        }
    }

    /// <summary>
    /// Snapshot telemetry của PatternLayer để phục vụ logger/debugger.
    /// </summary>
    public sealed class PatternLayerTelemetrySnapshot
    {
        private static readonly IReadOnlyDictionary<string, double> EmptyDetectorScores =
            new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

        private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyDetectorFlags =
            new ReadOnlyDictionary<string, IReadOnlyList<string>>(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> EmptyDetectorDiagnostics =
            new ReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>(new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase));

        private static readonly IReadOnlyList<string> EmptyFlags = Array.Empty<string>();

        private static readonly IReadOnlyDictionary<string, object> EmptyDiagnosticsDictionary =
            new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        public PatternLayerTelemetrySnapshot(
            DateTime timestampUtc,
            double finalScore,
            IReadOnlyDictionary<string, double>? detectorScores,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? detectorFlags,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>? detectorDiagnostics,
            IReadOnlyList<string>? patternFlags,
            double? accumulationScore = null,
            IReadOnlyList<string>? accumulationFlags = null,
            double? accumulationConfidence = null,
            string? marketPhase = null,
            double? marketStructureScore = null,
            string? marketStructureState = null,
            int? marketStructureTrendDirection = null,
            bool? marketStructureBreakDetected = null,
            int? marketStructureSwingPoints = null,
            double? lastSwingHigh = null,
            double? lastSwingLow = null,
            double? volumeProfileScore = null,
            double? volumeProfilePoc = null,
            double? volumeProfileVaHigh = null,
            double? volumeProfileVaLow = null,
            string? volumeProfileFlags = null,
            int? hvnCount = null,
            int? lvnCount = null,
            double? volumeConcentration = null,
            int telemetryVersion = 4)
        {
            TimestampUtc = timestampUtc;
            FinalScore = finalScore;
            DetectorScores = detectorScores ?? EmptyDetectorScores;
            DetectorFlags = detectorFlags ?? EmptyDetectorFlags;
            DetectorDiagnostics = detectorDiagnostics ?? EmptyDetectorDiagnostics;
            PatternFlags = patternFlags ?? EmptyFlags;
            AccumulationDistributionScore = accumulationScore;
            AccumulationDistributionFlags = accumulationFlags ?? EmptyFlags;
            AccumulationDistributionConfidence = accumulationConfidence;
            MarketPhase = marketPhase;
            MarketStructureScore = marketStructureScore;
            MarketStructureState = marketStructureState;
            MarketStructureTrendDirection = marketStructureTrendDirection;
            MarketStructureBreakDetected = marketStructureBreakDetected;
            MarketStructureSwingPoints = marketStructureSwingPoints;
            LastSwingHigh = lastSwingHigh;
            LastSwingLow = lastSwingLow;
            VolumeProfileScore = volumeProfileScore;
            VolumeProfilePOC = volumeProfilePoc;
            VolumeProfileVAHigh = volumeProfileVaHigh;
            VolumeProfileVALow = volumeProfileVaLow;
            VolumeProfileFlags = volumeProfileFlags;
            HVNCount = hvnCount;
            LVNCount = lvnCount;
            VolumeConcentration = volumeConcentration;
            TelemetryVersion = telemetryVersion;
        }

        public DateTime TimestampUtc { get; }
        public double FinalScore { get; }
        public IReadOnlyDictionary<string, double> DetectorScores { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> DetectorFlags { get; }
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> DetectorDiagnostics { get; }
        public IReadOnlyList<string> PatternFlags { get; }
        public double? AccumulationDistributionScore { get; }
        public IReadOnlyList<string> AccumulationDistributionFlags { get; }
        public double? AccumulationDistributionConfidence { get; }
        public string? MarketPhase { get; }
        public double? MarketStructureScore { get; }
        public string? MarketStructureState { get; }
        public int? MarketStructureTrendDirection { get; }
        public bool? MarketStructureBreakDetected { get; }
        public int? MarketStructureSwingPoints { get; }
        public double? LastSwingHigh { get; }
        public double? LastSwingLow { get; }
        public double? VolumeProfileScore { get; }
        public double? VolumeProfilePOC { get; }
        public double? VolumeProfileVAHigh { get; }
        public double? VolumeProfileVALow { get; }
        public string? VolumeProfileFlags { get; }
        public int? HVNCount { get; }
        public int? LVNCount { get; }
        public double? VolumeConcentration { get; }
        public int TelemetryVersion { get; }

        public double GetDetectorScoreOrDefault(string detectorName)
        {
            if (string.IsNullOrWhiteSpace(detectorName))
            {
                return 0;
            }

            return DetectorScores.TryGetValue(detectorName, out var score) ? score : 0;
        }

        public IReadOnlyList<string> GetDetectorFlagsOrDefault(string detectorName)
        {
            if (string.IsNullOrWhiteSpace(detectorName))
            {
                return EmptyFlags;
            }

            return DetectorFlags.TryGetValue(detectorName, out var flags) ? flags : EmptyFlags;
        }

        public IReadOnlyDictionary<string, object> GetDetectorDiagnosticsOrDefault(string detectorName)
        {
            if (string.IsNullOrWhiteSpace(detectorName))
            {
                return EmptyDiagnosticsDictionary;
            }

            return DetectorDiagnostics.TryGetValue(detectorName, out var diagnostics)
                ? diagnostics
                : EmptyDiagnosticsDictionary;
        }
    }
}
