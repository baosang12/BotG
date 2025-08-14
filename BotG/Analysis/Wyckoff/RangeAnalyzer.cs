using System;
using System.Collections.Generic;
using System.Linq;
using DataFetcher.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Analysis.Wyckoff
{
    public class ExpansionEvent
    {
        public int Index { get; set; }
        public double NewBound { get; set; }
        public string Source { get; set; } // P1Break / STBreak / SpringBreak / UTADBreak / FalseExpansionST
        public double ExpansionDelta { get; set; } // fraction of base width
        public bool Hard { get; set; }
        public double PreviousBound { get; set; }
        public bool Reverted { get; set; }
        public string Classification { get; set; } // Soft/Hard/False
    }

    public class RangeState
    {
        public int ClimaxIndex { get; set; }
        public double ClimaxExtreme { get; set; }
        public bool IsSellingClimax { get; set; }
        public int? P1Index { get; set; }
        public double? P1Price { get; set; }
        public double? P1RetraceFrac { get; set; }
        public bool P1StructureBreak { get; set; }
        public double? P1Score { get; set; }
        public int? P2Index { get; set; }
        public double? P2Price { get; set; }
        public double? PullbackDepth { get; set; }
        public double? BaseRangeWidth { get; set; }
        public double? CurrentUpperBound { get; set; }
        public double? CurrentLowerBound { get; set; }
        public List<ExpansionEvent> ExpansionEvents { get; set; } = new();
        public bool PendingP2 { get; set; } = true;
        public string TypeClassification { get; set; }
        public bool LowConfidence { get; set; }
        public bool FinalRangeLocked { get; set; }
        public int? FinalLockIndex { get; set; }
        // Dynamic metrics
        public double? CurrentWidth { get; set; }
        public double WidthVelocity { get; set; }
        public double DriftBias { get; set; }
        public double VolatilityCompression { get; set; }
        public int TouchCountUpper { get; set; }
        public int TouchCountLower { get; set; }
        public int? LastUpdateIndex { get; set; }
        // Spring / UTAD
        public int? SpringCandidateIndex { get; set; }
        public double? SpringCandidatePenetration { get; set; }
        public int? SpringConfirmIndex { get; set; }
        public string SpringType { get; set; } // Spring / UTAD
        // Pending validation ST expansions
        public List<ExpansionEvent> PendingValidationExpansions { get; set; } = new();
    // Enhanced metrics for lock qualification
    public double CoreOccupancy { get; set; } // fraction of closes inside core zone recent window
    public bool BothSidesTested { get; set; } // at least one touch/expansion each side
        // Phase / pattern probing
        public PhaseState PhaseState { get; set; } = PhaseState.ProbeInit;
        public bool PotentialMini { get; set; }
        public bool IsMiniPattern { get; set; }
        public bool DirectBreakoutArmed { get; set; }
        public double? SuggestedPositionSizeFactor { get; set; }
        public int? LastStateChangeIndex { get; set; }
        public int? DirectTriggerIndex { get; set; }
        public int? FailBreakoutIndex { get; set; }
        public int? LastExpansionIndex { get; set; }
    public int? FirstExpansionIndex { get; set; } // first expansion index for gating
    public int? LastSummaryIndex { get; set; } // bar index of last summary emission
    public string LastSummaryType { get; set; } // Early/Lock/Periodic/Final
    // Shadow scoring (Phase 1) – no behavioral impact yet
    public double? RangeScore { get; set; }
    public double? StructureSubScore { get; set; }
    public double? MaturitySubScore { get; set; }
    public double? CompressionSubScore { get; set; }
    public double? SpringSubScore { get; set; }
    public double? CleanlinessSubScore { get; set; }
    public double? DriftSubScore { get; set; }
    public double? ExpCountNorm { get; set; }
    public double? TouchesNorm { get; set; }
    public double? MaturityBarsNorm { get; set; }
    public double? CompNorm { get; set; }
    public double? DriftAbsNorm { get; set; }
    public double? OccNorm { get; set; }
    public double? WidthStabilityNorm { get; set; }
    public double? CleanlinessNorm { get; set; }
    public double? SweepClarityNorm { get; set; }
    // Spring gating diagnostics counters
    public int SpringGateBlockExpCount { get; set; } // times blocked due to insufficient expansions
    public int SpringGateBlockAge { get; set; } // times blocked due to insufficient bars since first expansion
    public int SpringGateRelaxExpActivations { get; set; } // times exp requirement relaxed
    public int SpringGateRelaxAgeActivations { get; set; } // times age requirement relaxed
    // Penetration diagnostics counters
    public int PenProbeCount { get; set; } // total probes logged (spring side)
    public int PenNearMissCount { get; set; } // probes that were within 80% of required min but not accepted
    public int PenTooShallowCount { get; set; } // probes below required min
    public int PenTooDeepCount { get; set; } // probes exceeding max penetration
    public int PenPipFailCount { get; set; } // probes failing absolute pip constraints
    // Last penetration probe detailed metrics (for structured logging)
    public double? LastPenProbeFrac { get; set; }
    public double? LastPenProbePips { get; set; }
    public double? LastPenAdaptiveMinFrac { get; set; }
    public double? LastPenMaxFrac { get; set; }
    public double? LastPenNeededMinFrac { get; set; } // UTAD dynamic min or spring adaptive min actually used
    public string LastPenProbeType { get; set; } // SpringSide / UTADSide
    public string LastPenProbeClass { get; set; } // TooShallow / NearMiss / TooDeep / PipFail
    // Derived metrics
    public double? VolatilityCompressionInverse { get; set; }
    public double DriftBiasNormalized { get; set; }
    // Last trigger metadata
    public string LastTriggerType { get; set; }
    public string LastTriggerDirection { get; set; }
    public double? LastTriggerBodyRatio { get; set; }
    public double? LastTriggerRangeFactor { get; set; }
    public double? LastTriggerVolSpike { get; set; }
    }

    public enum PhaseState
    {
        ProbeInit,
        BaseForming,
        PhaseB_Candidate,
        CompressionWatch,
        PhaseC_Candidate,
        DirectBreakoutReady,
        BreakoutExecuted,
        FailAbort
    }

    public class RangeAnalyzer
    {
        private readonly Action<string> _logger;
    private RangeState _activeRangeState; // for structured logging
    // Snapshot for throttled evolve logs
    private PhaseState? _ePrevPhase;
    private double? _ePrevWidth;
    private double? _ePrevDrift;
    private double? _ePrevComp;
    private double? _ePrevOcc;
    private int? _ePrevIndex;
        public bool Verbose { get; set; } = true; // new toggle
    // Optional structured logger: (rawLine, currentRangeState)
    public Action<string, RangeState> StructuredLogger { get; set; }
        public int WindowAfterClimax { get; set; } = 12;
        public int PivotLR { get; set; } = 2;
        public double MinRetraceFrac { get; set; } = 0.45;
        public double MinRetraceFracCluster { get; set; } = 0.40;
        public double StructureBreakBufferFrac { get; set; } = 0.05;
        public double PenetrationMax { get; set; } = 0.15;
        public double PullbackDepthMax { get; set; } = 0.60;
        public double DeepRetraceFrac { get; set; } = 0.65;
        public double WeakRetraceFrac { get; set; } = 0.50;
        public double MinExpansionFrac { get; set; } = 0.10;
        public double HardExpansionFrac { get; set; } = 0.30;
    public double EscapeLockFrac { get; set; } = 1.10; // raised from 1.0 to reduce premature auto-lock on first full-width escape
    public bool DeferSTExpansion { get; set; } = true; // only apply STBreak after validation
        public int VolRecentWindow { get; set; } = 5;
        public int VolBaseWindow { get; set; } = 8;
        public double TouchEpsilonFrac { get; set; } = 0.02;
        public int LockLookbackBars { get; set; } = 12;
    public int MinBarsSinceP1ForLock { get; set; } = 20; // guard against very early lock
    public int MinTouchesPerSideForLock { get; set; } = 1; // ensure both sides interacted
    public double CoreZoneFrac { get; set; } = 0.30; // percentage of width defining central zone
    public int CoreOccupancyWindow { get; set; } = 15; // bars lookback for occupancy calc
    public double CoreOccupancyMin { get; set; } = 0.40; // adjusted from 0.55 to increase lock qualification early
    // Mini / direct breakout parameters
    public int MiniPatternBarWindowLow { get; set; } = 15;
    public int MiniPatternBarWindowHigh { get; set; } = 25;
    public int MiniMaxExpansionCount { get; set; } = 1;
    public double MiniCoreOccupancyMin { get; set; } = 0.55; // adjusted from 0.70 to allow more mini patterns
    public double MiniCompressionMax { get; set; } = 0.55;
    public double DriftBiasBreakoutMinStd { get; set; } = 0.25; // adjusted from 0.18 to require stronger drift
    public double DriftBiasBreakoutMinMini { get; set; } = 0.30; // adjusted from 0.25
    public double BreakoutATRFactor { get; set; } = 1.5;
    public double BreakoutCloseOffsetFrac { get; set; } = 0.30;
    public int ConsecutiveDriveBars { get; set; } = 2;
    public int EarlyCompressionLookback { get; set; } = 10;
    public int ReevalTimeoutBars { get; set; } = 30;
    public double BasePositionSizeFactor { get; set; } = 1.0;
    public double MiniSizeFactor { get; set; } = 0.7;
    public double DirectBreakoutSizeFactor { get; set; } = 0.6;
    // Size factor mapping (can be tuned later)
    public double FactorBaseForming { get; set; } = 1.0;
    public double FactorPhaseB { get; set; } = 1.0;
    public double FactorCompressionWatch { get; set; } = 0.90;
    public double FactorPhaseC { get; set; } = 0.85;
    public double FactorDirectMini { get; set; } = 0.60;
    public double FactorDirectNonMini { get; set; } = 0.70;
    public int FailBreakoutCooldownBars { get; set; } = 5;
    public double DriftDecayTolerance { get; set; } = 0.07;
    // Breakout quality filters
    public double BreakoutBodyRatioMin { get; set; } = 0.55; // real body / range
    public double BreakoutRangeFactorMin { get; set; } = 1.20; // bar range / recentAvgRange
    public double BreakoutVolumeSpikeMin { get; set; } = 1.30; // bar volume / recentAvgVolume
    // Spring / UTAD penetration window (adjusted defaults after diagnostics showed infeasible overlap with UTADMinPenBase)
    public double SpringMinPenetrationFrac { get; set; } = 0.12; // was 0.16
    public double SpringMaxPenetrationFrac { get; set; } = 0.50; // was 0.40 (must exceed UTADMinPenBase to allow UTAD candidates)
    // Optional absolute (pip) based overrides – allow detecting small 4-8 pip wicks on wide ranges
    public double SpringMinPenetrationPips { get; set; } = 5.0; // absolute wick allowance (pips) to enable detection of small sweeps in wide ranges
    public double SpringMaxPenetrationPips { get; set; } = 0.0; // if >0, enforce absolute max also
    public double PipSize { get; set; } = 0.0001; // must be set by caller (Symbol.PipSize)
    public bool EnableFastSpringConfirm { get; set; } = true; // allow same-bar confirm if reclaim satisfied
    public double FastConfirmReclaimFrac { get; set; } = 0.50; // fraction of penetration recovered for instant confirm
    public int SpringConfirmationBars { get; set; } = 5; // increased for stricter confirmation
    public double SpringReclaimFrac { get; set; } = 0.35; // adjusted from 0.25 for stricter spring confirmation
        public int FalseExpansionValidationBars { get; set; } = 3;
        public double FalseExpansionRevertFrac { get; set; } = 0.5; // price reverts >50% of expansion delta
    // Gating & advanced controls
    public int MinExpForUTAD { get; set; } = 2;
    public int MinExpForSpring { get; set; } = 1;
    public int MinBarsSinceFirstExpansionForUTAD { get; set; } = 18;
    public int MinBarsSinceFirstExpansionForSpring { get; set; } = 10;
    public int MinBarsInPhaseB { get; set; } = 10; // dwell requirement before PhaseC unless deep compression
    public double CompDeepForFastC { get; set; } = 0.42;
    public int MinPhaseBarsBeforeTimeout { get; set; } = 5; // guard against instant timeout
    public double UTADMinPenBase { get; set; } = 0.43; // dynamic min penetration for UTAD (reduced from 0.45 after probes showed excessive TooShallow cluster)
    public double UTADMinPenLogDecay { get; set; } = 0.05; // subtract 0.05*log1p(expCount)
    public int SummarySuppressWindowBars { get; set; } = 2;
    // Adaptive spring gating relaxation
    public bool EnableAdaptiveSpringGating { get; set; } = true;
    public int SpringGateExpBlockThreshold { get; set; } = 3; // after N exp count blocks, relax by 1
    public int SpringGateAgeBlockThreshold { get; set; } = 3; // after N age blocks, relax by 2 bars
    public int SpringGateRelaxExpStep { get; set; } = 1;
    public int SpringGateRelaxAgeStep { get; set; } = 2;
    public int SpringGateMinExpFloor { get; set; } = 0; // won't relax below this
    public int SpringGateMinAgeFloor { get; set; } = 4; // guard minimum structural age
    // Penetration diagnostics
    public bool EnablePenetrationDiagnostics { get; set; } = true; // master switch
    public double PenetrationProbeMinFrac { get; set; } = 0.05; // log probes beyond this frac of base width
    public double PenetrationNearMissFrac { get; set; } = 0.80; // fraction of required min marking a near miss

        public RangeAnalyzer(Action<string> logger = null)
        {
            _logger = logger;
        }

        private void ValidatePenetrationConfig()
        {
            try
            {
                bool changed = false;
                // Ensure max > min
                if (SpringMaxPenetrationFrac <= SpringMinPenetrationFrac)
                {
                    double oldMax = SpringMaxPenetrationFrac;
                    SpringMaxPenetrationFrac = SpringMinPenetrationFrac + 0.02; // small buffer
                    changed = true;
                    StructuredLogger?.Invoke("[ParamAdjust] RaisedSpringMaxForOrdering", _activeRangeState);
                    _logger?.Invoke($"[ParamAdjust] springMaxPenFrac {oldMax:0.###}->{SpringMaxPenetrationFrac:0.###} (ordering)");
                }
                // Ensure UTAD window feasible
                if (UTADMinPenBase >= SpringMaxPenetrationFrac)
                {
                    double oldMax = SpringMaxPenetrationFrac;
                    SpringMaxPenetrationFrac = UTADMinPenBase + 0.05; // expand window just beyond UTAD base min
                    changed = true;
                    StructuredLogger?.Invoke("[ParamAdjust] RaisedSpringMaxForUTAD", _activeRangeState);
                    _logger?.Invoke($"[ParamAdjust] springMaxPenFrac {oldMax:0.###}->{SpringMaxPenetrationFrac:0.###} to exceed UTADMinPenBase {UTADMinPenBase:0.###}");
                }
                // Always emit effective snapshot once (idempotent per initialization)
                StructuredLogger?.Invoke($"[ParamAdjustEffective] springMin={SpringMinPenetrationFrac:0.###} springMax={SpringMaxPenetrationFrac:0.###} utadMinBase={UTADMinPenBase:0.###} utadDecay={UTADMinPenLogDecay:0.###} changed={changed}", _activeRangeState);
                _logger?.Invoke($"[ParamAdjustEffective] springMin={SpringMinPenetrationFrac:0.###} springMax={SpringMaxPenetrationFrac:0.###} utadMinBase={UTADMinPenBase:0.###} utadDecay={UTADMinPenLogDecay:0.###} changed={changed}");
            }
            catch { /* swallow safeguard */ }
        }

    // Expose the currently active (most recently analyzed/evolved) RangeState for visualization/overlay
    public RangeState ActiveRangeState => _activeRangeState;

        public RangeState AnalyzeInitialRange(IList<Bar> bars, ClimaxEvent climax)
        {
            var rs = new RangeState
            {
                ClimaxIndex = climax.Index,
                ClimaxExtreme = climax.Type == ClimaxType.SellingClimax ? climax.Bar.Low : climax.Bar.High,
                IsSellingClimax = climax.Type == ClimaxType.SellingClimax
            };
            // Set active state early so validation logging can attach snapshot
            _activeRangeState = rs;
            // One-time parameter feasibility validation (prevents silent zero-candidate runs)
            ValidatePenetrationConfig();
            int n = bars.Count;
            int start = climax.Index + 1;
            if (start >= n) return rs;
            int end = Math.Min(n - 1, climax.Index + WindowAfterClimax);
            double climaxRange = Math.Max(1e-9, climax.Bar.High - climax.Bar.Low);
            double clusterHigh = climax.Bar.High;
            double clusterLow = climax.Bar.Low;
            for (int k = climax.Index - 1; k >= 0 && k >= climax.Index - 5; k--)
            {
                clusterHigh = Math.Max(clusterHigh, bars[k].High);
                clusterLow = Math.Min(clusterLow, bars[k].Low);
            }

            var p1Candidates = new List<(int idx, double price, double retraceFrac, bool breakStruct, double score)>();
            double minRetrace = climax.IsCluster ? MinRetraceFracCluster : MinRetraceFrac;
            for (int i = start + PivotLR; i <= end - PivotLR; i++)
            {
                if (IsPivot(bars, i, PivotLR, opposite: true, sellingClimax: rs.IsSellingClimax))
                {
                    double price = rs.IsSellingClimax ? bars[i].High : bars[i].Low;
                    double retrace = rs.IsSellingClimax ? (price - climax.Bar.Low) / climaxRange : (climax.Bar.High - price) / climaxRange;
                    bool breakStruct = rs.IsSellingClimax
                        ? price >= clusterHigh + climaxRange * StructureBreakBufferFrac
                        : price <= clusterLow - climaxRange * StructureBreakBufferFrac;
                    if (retrace < minRetrace && !breakStruct)
                    {
                        Log($"[P1Skip] idx={i} retrace={retrace:0.00} < {minRetrace:0.00} break={breakStruct}");
                        continue;
                    }
                    var legBars = bars.Skip(start).Take(i - start + 1).ToList();
                    double impulse = legBars.Average(b => { double rg = Math.Max(1e-9, b.High - b.Low); return Math.Abs(b.Close - b.Open) / rg; });
                    double score = retrace * 0.4 + (breakStruct ? 0.25 : 0) + impulse * 0.2;
                    p1Candidates.Add((i, price, retrace, breakStruct, score));
                    Log($"[P1Select] cand idx={i} retrace={retrace:0.00} break={breakStruct} impulse={impulse:0.00} score={score:0.00}");
                }
            }
            if (p1Candidates.Count == 0)
            {
                rs.LowConfidence = true;
                return rs;
            }
            var chosen = p1Candidates.OrderByDescending(c => c.score).First();
            rs.P1Index = chosen.idx;
            rs.P1Price = chosen.price;
            // Clamp retrace >1.0 to 1.0 and mark low confidence if over-extended
            rs.P1RetraceFrac = Math.Min(1.0, chosen.retraceFrac);
            if (chosen.retraceFrac > 1.0) rs.LowConfidence = true;
            rs.P1StructureBreak = chosen.breakStruct;
            rs.P1Score = chosen.score;
            rs.BaseRangeWidth = Math.Abs(chosen.price - rs.ClimaxExtreme);
            if (rs.IsSellingClimax)
            {
                rs.CurrentLowerBound = rs.ClimaxExtreme;
                rs.CurrentUpperBound = rs.P1Price;
            }
            else
            {
                rs.CurrentUpperBound = rs.ClimaxExtreme;
                rs.CurrentLowerBound = rs.P1Price;
            }
            rs.CurrentWidth = rs.CurrentUpperBound - rs.CurrentLowerBound;
            // Initialize phase machine BEFORE logging so phase + sizeFactor appear in structured log
            rs.PhaseState = PhaseState.BaseForming;
            rs.LastStateChangeIndex = rs.P1Index;
            rs.SuggestedPositionSizeFactor = BasePositionSizeFactor;
            _activeRangeState = rs; // set active
            Log($"[P1Select] chosen idx={rs.P1Index} retrace={rs.P1RetraceFrac:0.00} break={rs.P1StructureBreak} baseWidth={rs.BaseRangeWidth:0.00000}");

            int p1Idx = rs.P1Index.Value;
            for (int i = p1Idx + PivotLR; i <= end - PivotLR; i++)
            {
                if (IsPivot(bars, i, PivotLR, opposite: false, sellingClimax: rs.IsSellingClimax))
                {
                    double price = rs.IsSellingClimax ? bars[i].Low : bars[i].High;
                    double penetration = rs.IsSellingClimax
                        ? (rs.ClimaxExtreme - price) / rs.BaseRangeWidth.Value
                        : (price - rs.ClimaxExtreme) / rs.BaseRangeWidth.Value;
                    if (penetration > PenetrationMax)
                        break;
                    double pullbackDepth = Math.Abs((rs.P1Price.Value - price) / rs.BaseRangeWidth.Value);
                    if (pullbackDepth > PullbackDepthMax) continue;
                    rs.P2Index = i;
                    rs.P2Price = price;
                    rs.PullbackDepth = pullbackDepth;
                    rs.PendingP2 = false;
                    Log($"[P2Confirm] idx={i} pullbackDepth={pullbackDepth:0.00} penetration={penetration:0.00}");
                    break;
                }
            }

            if (rs.P1RetraceFrac.HasValue)
            {
                if (rs.P1RetraceFrac >= DeepRetraceFrac && rs.PullbackDepth.HasValue && rs.PullbackDepth <= 0.35)
                    rs.TypeClassification = "Deep";
                else if (rs.P1RetraceFrac >= WeakRetraceFrac)
                    rs.TypeClassification = "Balanced";
                else
                    rs.TypeClassification = "Weak";
            }
            return rs;
        }

        public void EvolveRangeState(IList<Bar> bars, RangeState rs)
        {
            if (rs.BaseRangeWidth == null || rs.BaseRangeWidth.Value <= 0) return;
            // capture prev snapshot
            _ePrevPhase = rs.PhaseState;
            _ePrevWidth = rs.CurrentWidth;
            _ePrevDrift = rs.DriftBias;
            _ePrevComp = rs.VolatilityCompression;
            _ePrevOcc = rs.CoreOccupancy;
            _ePrevIndex = rs.LastUpdateIndex;
            int start = (rs.P2Index ?? rs.P1Index ?? rs.ClimaxIndex) + 1;
            if (start >= bars.Count) return;
            _activeRangeState = rs; // ensure active for structured logging
            // Incremental: begin from next bar after last update to avoid reprocessing events
            int iterStart = Math.Max(start, (rs.LastUpdateIndex ?? (start - 1)) + 1);
            if (iterStart >= bars.Count) return;
            double baseWidth = rs.BaseRangeWidth.Value;
            int baseStart = rs.ClimaxIndex;
            int baseEnd = Math.Min(bars.Count - 1, rs.P1Index.HasValue ? rs.P1Index.Value : rs.ClimaxIndex + 5);
            var baseSeg = bars.Skip(baseStart).Take(Math.Max(1, baseEnd - baseStart + 1)).ToList();
            double baseAvgRange = baseSeg.Average(b => b.High - b.Low);

            for (int i = iterStart; i < bars.Count; i++)
            {
                var bar = bars[i];
                // Touch counts
                if (rs.CurrentUpperBound.HasValue)
                {
                    double upper = rs.CurrentUpperBound.Value;
                    if (bar.High >= upper * (1 - TouchEpsilonFrac)) rs.TouchCountUpper++;
                }
                if (rs.CurrentLowerBound.HasValue)
                {
                    double lower = rs.CurrentLowerBound.Value;
                    if (bar.Low <= lower * (1 + TouchEpsilonFrac)) rs.TouchCountLower++;
                }

                // Spring / UTAD candidate detection (deep same-direction penetration beyond PenetrationMax)
                if (rs.IsSellingClimax && rs.CurrentLowerBound.HasValue)
                {
                    double penetrationFrac = (rs.CurrentLowerBound.Value - bar.Low) / baseWidth;
                    double penetrationPips = (rs.CurrentLowerBound.Value - bar.Low) / PipSize;
                    // Adaptive min: if absolute pip threshold specified, allow smaller frac if wick small relative to wide base
                    double adaptiveMinFrac = SpringMinPenetrationFrac;
                    if (SpringMinPenetrationPips > 0 && baseWidth > 0)
                    {
                        double absFrac = (SpringMinPenetrationPips * PipSize) / baseWidth;
                        adaptiveMinFrac = Math.Min(SpringMinPenetrationFrac, absFrac);
                    }
                    bool fracOk = penetrationFrac >= adaptiveMinFrac && penetrationFrac <= SpringMaxPenetrationFrac;
                    bool pipsOk = true;
                    if (SpringMinPenetrationPips > 0) pipsOk = penetrationPips >= SpringMinPenetrationPips;
                    if (pipsOk && SpringMaxPenetrationPips > 0) pipsOk = penetrationPips <= SpringMaxPenetrationPips;
                    if (EnablePenetrationDiagnostics && baseWidth > 0 && penetrationFrac >= PenetrationProbeMinFrac)
                    {
                        rs.PenProbeCount++;
                        rs.LastPenProbeFrac = penetrationFrac;
                        rs.LastPenProbePips = penetrationPips;
                        rs.LastPenAdaptiveMinFrac = adaptiveMinFrac;
                        rs.LastPenMaxFrac = SpringMaxPenetrationFrac;
                        rs.LastPenNeededMinFrac = adaptiveMinFrac;
                        rs.LastPenProbeType = "SpringSide";
                        rs.LastPenProbeClass = null;
                        if (!pipsOk)
                        {
                            rs.PenPipFailCount++;
                            rs.LastPenProbeClass = "PipFail";
                        }
                        else if (penetrationFrac > SpringMaxPenetrationFrac)
                        {
                            rs.PenTooDeepCount++;
                            rs.LastPenProbeClass = "TooDeep";
                        }
                        else if (penetrationFrac < adaptiveMinFrac)
                        {
                            double rel = adaptiveMinFrac > 0 ? penetrationFrac / adaptiveMinFrac : 0;
                            if (rel >= PenetrationNearMissFrac)
                            {
                                rs.PenNearMissCount++;
                                rs.LastPenProbeClass = "NearMiss";
                            }
                            else
                            {
                                rs.PenTooShallowCount++;
                                rs.LastPenProbeClass = "TooShallow";
                            }
                        }
                        // Only emit structured log if a classification (i.e., non-accept) occurred
                        if (rs.LastPenProbeClass != null)
                        {
                            StructuredLogger?.Invoke("PenProbe", rs);
                        }
                    }
                    if (rs.SpringCandidateIndex == null && fracOk && pipsOk)
                    {
                        rs.SpringCandidateIndex = i;
                        rs.SpringCandidatePenetration = penetrationFrac;
                        rs.SpringType = "Spring";
                        Log($"[SpringCand] idx={i} penFrac={penetrationFrac:0.00} penPips={penetrationPips:0.##} minFrac={adaptiveMinFrac:0.00}");
                        // Fast confirm path (same bar) if enabled and close reclaimed enough
                        if (EnableFastSpringConfirm && rs.CurrentLowerBound.HasValue)
                        {
                            double penetrationDist = penetrationFrac * baseWidth;
                            double reclaimed = bar.Close - rs.CurrentLowerBound.Value;
                            if (reclaimed > 0 && penetrationDist > 0)
                            {
                                double recovFracFast = reclaimed / penetrationDist;
                                if (recovFracFast >= FastConfirmReclaimFrac)
                                {
                                    rs.SpringConfirmIndex = i;
                                    rs.LastTriggerType = "Spring";
                                    rs.LastTriggerDirection = rs.IsSellingClimax ? "Long" : "Short";
                                    Log($"[SpringConfirm] type=Spring idx={i} cand={rs.SpringCandidateIndex} pen={penetrationFrac:0.00} fast=1 recov={recovFracFast:0.00}");
                                }
                            }
                        }
                    }
                }
                else if (!rs.IsSellingClimax && rs.CurrentUpperBound.HasValue)
                {
                    double penetrationFrac = (bar.High - rs.CurrentUpperBound.Value) / baseWidth;
                    double penetrationPips = (bar.High - rs.CurrentUpperBound.Value) / PipSize;
                    int expCountDyn = rs.ExpansionEvents.Count;
                    double utadMinPen = UTADMinPenBase - UTADMinPenLogDecay * Math.Log(1 + expCountDyn);
                    if (utadMinPen < SpringMinPenetrationFrac) utadMinPen = SpringMinPenetrationFrac;
                    // Apply adaptive absolute wick allowance similarly
                    if (SpringMinPenetrationPips > 0 && baseWidth > 0)
                    {
                        double absFrac = (SpringMinPenetrationPips * PipSize) / baseWidth;
                        utadMinPen = Math.Min(utadMinPen, absFrac);
                    }
                    bool fracOk = penetrationFrac >= utadMinPen && penetrationFrac <= SpringMaxPenetrationFrac;
                    bool pipsOk = true;
                    if (SpringMinPenetrationPips > 0) pipsOk = penetrationPips >= SpringMinPenetrationPips;
                    if (pipsOk && SpringMaxPenetrationPips > 0) pipsOk = penetrationPips <= SpringMaxPenetrationPips;
                    if (EnablePenetrationDiagnostics && baseWidth > 0 && penetrationFrac >= PenetrationProbeMinFrac)
                    {
                        rs.PenProbeCount++;
                        rs.LastPenProbeFrac = penetrationFrac;
                        rs.LastPenProbePips = penetrationPips;
                        rs.LastPenAdaptiveMinFrac = null; // adaptive min not used separately here
                        rs.LastPenMaxFrac = SpringMaxPenetrationFrac;
                        rs.LastPenNeededMinFrac = utadMinPen;
                        rs.LastPenProbeType = "UTADSide";
                        rs.LastPenProbeClass = null;
                        if (!pipsOk)
                        {
                            rs.PenPipFailCount++;
                            rs.LastPenProbeClass = "PipFail";
                        }
                        else if (penetrationFrac > SpringMaxPenetrationFrac)
                        {
                            rs.PenTooDeepCount++;
                            rs.LastPenProbeClass = "TooDeep";
                        }
                        else if (penetrationFrac < utadMinPen)
                        {
                            double rel = utadMinPen > 0 ? penetrationFrac / utadMinPen : 0;
                            if (rel >= PenetrationNearMissFrac)
                            {
                                rs.PenNearMissCount++;
                                rs.LastPenProbeClass = "NearMiss";
                            }
                            else
                            {
                                rs.PenTooShallowCount++;
                                rs.LastPenProbeClass = "TooShallow";
                            }
                        }
                        if (rs.LastPenProbeClass != null)
                        {
                            StructuredLogger?.Invoke("PenProbe", rs);
                        }
                    }
                    if (rs.SpringCandidateIndex == null && fracOk && pipsOk)
                    {
                        rs.SpringCandidateIndex = i;
                        rs.SpringCandidatePenetration = penetrationFrac;
                        rs.SpringType = "UTAD";
                        Log($"[UTADCand] idx={i} penFrac={penetrationFrac:0.00} penPips={penetrationPips:0.##} dynMinFrac={utadMinPen:0.00}");
                        if (EnableFastSpringConfirm && rs.CurrentUpperBound.HasValue)
                        {
                            double penetrationDist = penetrationFrac * baseWidth;
                            double reclaimed = rs.CurrentUpperBound.Value - bar.Close;
                            if (reclaimed > 0 && penetrationDist > 0)
                            {
                                double recovFracFast = reclaimed / penetrationDist;
                                if (recovFracFast >= FastConfirmReclaimFrac)
                                {
                                    rs.SpringConfirmIndex = i;
                                    rs.LastTriggerType = "UTAD";
                                    rs.LastTriggerDirection = rs.IsSellingClimax ? "Long" : "Short";
                                    Log($"[SpringConfirm] type=UTAD idx={i} cand={rs.SpringCandidateIndex} pen={penetrationFrac:0.00} fast=1 recov={recovFracFast:0.00}");
                                }
                            }
                        }
                    }
                }
                // Spring confirmation
                if (rs.SpringCandidateIndex.HasValue && rs.SpringConfirmIndex == null)
                {
                    int age = i - rs.SpringCandidateIndex.Value;
                    if (age > 0 && age <= SpringConfirmationBars)
                    {
                        bool reclaimed = rs.IsSellingClimax
                            ? bar.Close >= rs.CurrentLowerBound
                            : bar.Close <= rs.CurrentUpperBound;
                        if (reclaimed)
                        {
                            // penetration recovered fraction?
                            double recovFrac = 0;
                            if (rs.SpringCandidatePenetration.HasValue && rs.SpringCandidatePenetration.Value > 0)
                            {
                                double penetrationDist = rs.SpringCandidatePenetration.Value * baseWidth;
                                if (penetrationDist > 0)
                                {
                                    double reclaimedDist = rs.IsSellingClimax
                                        ? (bar.Close - rs.CurrentLowerBound.Value)
                                        : (rs.CurrentUpperBound.Value - bar.Close);
                                    recovFrac = Math.Max(0, Math.Min(1, reclaimedDist / penetrationDist));
                                }
                            }
                            bool gatingOk = true;
                            int expCount = rs.ExpansionEvents.Count;
                            int barsSinceFirstExp = 0;
                            if (rs.ExpansionEvents.Count > 0)
                            {
                                int firstIdx = rs.ExpansionEvents.First().Index;
                                barsSinceFirstExp = i - firstIdx;
                            }
                            if (rs.SpringType == "UTAD")
                            {
                                if (expCount < MinExpForUTAD) {
                                    gatingOk = false; rs.SpringGateBlockExpCount++; Log($"[SpringGateBlock] type=UTAD idx={i} reason=expCount({expCount}<{MinExpForUTAD})"); StructuredLogger?.Invoke("SpringGateBlock", rs);
                                    if (EnableAdaptiveSpringGating && rs.SpringGateBlockExpCount % SpringGateExpBlockThreshold == 0) { MinExpForUTAD = Math.Max(SpringGateMinExpFloor, MinExpForUTAD - SpringGateRelaxExpStep); rs.SpringGateRelaxExpActivations++; Log($"[SpringGateRelax] type=UTAD newMinExp={MinExpForUTAD}"); StructuredLogger?.Invoke("SpringGateRelax", rs); }
                                }
                                else if (barsSinceFirstExp < MinBarsSinceFirstExpansionForUTAD) {
                                    gatingOk = false; rs.SpringGateBlockAge++; Log($"[SpringGateBlock] type=UTAD idx={i} reason=ageFirstExp({barsSinceFirstExp}<{MinBarsSinceFirstExpansionForUTAD})"); StructuredLogger?.Invoke("SpringGateBlock", rs);
                                    if (EnableAdaptiveSpringGating && rs.SpringGateBlockAge % SpringGateAgeBlockThreshold == 0) { MinBarsSinceFirstExpansionForUTAD = Math.Max(SpringGateMinAgeFloor, MinBarsSinceFirstExpansionForUTAD - SpringGateRelaxAgeStep); rs.SpringGateRelaxAgeActivations++; Log($"[SpringGateRelax] type=UTAD newMinAge={MinBarsSinceFirstExpansionForUTAD}"); StructuredLogger?.Invoke("SpringGateRelax", rs); }
                                }
                            }
                            else // Spring
                            {
                                if (expCount < MinExpForSpring) {
                                    gatingOk = false; rs.SpringGateBlockExpCount++; Log($"[SpringGateBlock] type=Spring idx={i} reason=expCount({expCount}<{MinExpForSpring})"); StructuredLogger?.Invoke("SpringGateBlock", rs);
                                    if (EnableAdaptiveSpringGating && rs.SpringGateBlockExpCount % SpringGateExpBlockThreshold == 0) { MinExpForSpring = Math.Max(SpringGateMinExpFloor, MinExpForSpring - SpringGateRelaxExpStep); rs.SpringGateRelaxExpActivations++; Log($"[SpringGateRelax] type=Spring newMinExp={MinExpForSpring}"); StructuredLogger?.Invoke("SpringGateRelax", rs); }
                                }
                                else if (barsSinceFirstExp < MinBarsSinceFirstExpansionForSpring) {
                                    gatingOk = false; rs.SpringGateBlockAge++; Log($"[SpringGateBlock] type=Spring idx={i} reason=ageFirstExp({barsSinceFirstExp}<{MinBarsSinceFirstExpansionForSpring})"); StructuredLogger?.Invoke("SpringGateBlock", rs);
                                    if (EnableAdaptiveSpringGating && rs.SpringGateBlockAge % SpringGateAgeBlockThreshold == 0) { MinBarsSinceFirstExpansionForSpring = Math.Max(SpringGateMinAgeFloor, MinBarsSinceFirstExpansionForSpring - SpringGateRelaxAgeStep); rs.SpringGateRelaxAgeActivations++; Log($"[SpringGateRelax] type=Spring newMinAge={MinBarsSinceFirstExpansionForSpring}"); StructuredLogger?.Invoke("SpringGateRelax", rs); }
                                }
                            }
                            if (recovFrac >= SpringReclaimFrac && gatingOk)
                            {
                                rs.SpringConfirmIndex = i;
                                // Assign trigger metadata BEFORE logging so it's present on bracket line
                                rs.LastTriggerType = rs.SpringType == "UTAD" ? "UTAD" : "Spring";
                                rs.LastTriggerDirection = rs.IsSellingClimax ? "Long" : "Short";
                                rs.LastTriggerBodyRatio = null;
                                rs.LastTriggerRangeFactor = null;
                                rs.LastTriggerVolSpike = null;
                                Log($"[SpringConfirm] type={rs.SpringType} idx={i} cand={rs.SpringCandidateIndex} pen={rs.SpringCandidatePenetration:0.00} fast=0 recov={recovFrac:0.00}");
                                // Removed separate StructuredLogger SpringConfirm event to avoid duplicate plain line.
                            }
                        }
                    }
                    else if (age > SpringConfirmationBars)
                    {
                        // timeout -> reset candidate
                        rs.SpringCandidateIndex = null;
                        rs.SpringCandidatePenetration = null;
                        rs.SpringType = null;
                    }
                }

                // Track whether expansion added this bar
                int preExpCount = rs.ExpansionEvents.Count;

                // Pivot-based expansion detection (disabled once locked)
                if (!rs.FinalRangeLocked && i >= PivotLR && i < bars.Count - PivotLR)
                {
                    // Opposite side pivot (potential break on P1 side)
                    if (IsPivot(bars, i, PivotLR, opposite: true, sellingClimax: rs.IsSellingClimax))
                    {
                        double price = rs.IsSellingClimax ? bar.High : bar.Low;
                        if (rs.IsSellingClimax && price > rs.CurrentUpperBound)
                        {
                            double delta = (price - rs.CurrentUpperBound.Value) / baseWidth;
                            if (delta >= MinExpansionFrac)
                                RegisterExpansion(rs, i, price, delta, "P1Break", delta >= HardExpansionFrac, rs.CurrentUpperBound.Value);
                        }
                        else if (!rs.IsSellingClimax && price < rs.CurrentLowerBound)
                        {
                            double delta = (rs.CurrentLowerBound.Value - price) / baseWidth;
                            if (delta >= MinExpansionFrac)
                                RegisterExpansion(rs, i, price, delta, "P1Break", delta >= HardExpansionFrac, rs.CurrentLowerBound.Value);
                        }
                    }
                    // Same-direction pivot (ST external expansion) - deferred application
                    if (IsPivot(bars, i, PivotLR, opposite: false, sellingClimax: rs.IsSellingClimax))
                    {
                        double price = rs.IsSellingClimax ? bar.Low : bar.High;
                        if (rs.IsSellingClimax && price < rs.CurrentLowerBound)
                        {
                            double delta = (rs.CurrentLowerBound.Value - price) / baseWidth;
                            if (delta >= MinExpansionFrac && delta <= SpringMinPenetrationFrac)
                            {
                                if (DeferSTExpansion)
                                {
                                    var pendingEv = new ExpansionEvent
                                    {
                                        Index = i,
                                        NewBound = price,
                                        PreviousBound = rs.CurrentLowerBound.Value,
                                        ExpansionDelta = delta,
                                        Source = "STBreakPending",
                                        Hard = delta >= HardExpansionFrac
                                    };
                                    rs.PendingValidationExpansions.Add(pendingEv);
                                    Log($"[STBreakPending] idx={i} delta={delta:0.00} hard={pendingEv.Hard} newBound={price:0.00000}");
                                }
                                else
                                {
                                    RegisterExpansion(rs, i, price, delta, "STBreak", delta >= HardExpansionFrac, rs.CurrentLowerBound.Value);
                                }
                            }
                        }
                        else if (!rs.IsSellingClimax && price > rs.CurrentUpperBound)
                        {
                            double delta = (price - rs.CurrentUpperBound.Value) / baseWidth;
                            if (delta >= MinExpansionFrac && delta <= SpringMinPenetrationFrac)
                            {
                                if (DeferSTExpansion)
                                {
                                    var pendingEv = new ExpansionEvent
                                    {
                                        Index = i,
                                        NewBound = price,
                                        PreviousBound = rs.CurrentUpperBound.Value,
                                        ExpansionDelta = delta,
                                        Source = "STBreakPending",
                                        Hard = delta >= HardExpansionFrac
                                    };
                                    rs.PendingValidationExpansions.Add(pendingEv);
                                    Log($"[STBreakPending] idx={i} delta={delta:0.00} hard={pendingEv.Hard} newBound={price:0.00000}");
                                }
                                else
                                {
                                    RegisterExpansion(rs, i, price, delta, "STBreak", delta >= HardExpansionFrac, rs.CurrentUpperBound.Value);
                                }
                            }
                        }
                    }
                }
                bool expansionThisBar = rs.ExpansionEvents.Count > preExpCount;

                // Validate pending STBreak expansions for false expansion / acceptance
                if (rs.PendingValidationExpansions.Count > 0)
                {
                    var toRemove = new List<ExpansionEvent>();
                    foreach (var pending in rs.PendingValidationExpansions)
                    {
                        int age = i - pending.Index;
                        if (age <= 0) continue;
                        bool reverted = false;
                        bool extended = false;
                        if (rs.IsSellingClimax && (pending.Source == "STBreak" || pending.Source == "STBreakPending"))
                        {
                            double currentExt = (pending.PreviousBound - bar.Low) / baseWidth;
                            extended = currentExt > pending.ExpansionDelta * 1.3;
                            reverted = bar.Close > pending.PreviousBound && currentExt < pending.ExpansionDelta * FalseExpansionRevertFrac;
                        }
                        else if (!rs.IsSellingClimax && (pending.Source == "STBreak" || pending.Source == "STBreakPending"))
                        {
                            double currentExt = (bar.High - pending.PreviousBound) / baseWidth;
                            extended = currentExt > pending.ExpansionDelta * 1.3;
                            reverted = bar.Close < pending.PreviousBound && currentExt < pending.ExpansionDelta * FalseExpansionRevertFrac;
                        }
                        if (reverted)
                        {
                            pending.Reverted = true;
                            pending.Source = "FalseExpansionST";
                            pending.Classification = "False";
                            // If we had applied (old immediate logic) ensure bound restored; with deferred logic bound unchanged
                            if (pending.Source != "STBreakPending")
                            {
                                if (rs.IsSellingClimax)
                                    rs.CurrentLowerBound = pending.PreviousBound;
                                else
                                    rs.CurrentUpperBound = pending.PreviousBound;
                                rs.CurrentWidth = rs.CurrentUpperBound - rs.CurrentLowerBound;
                            }
                            Log($"[FalseExpansionST] idx={pending.Index} revertAt={i} prevBound={pending.PreviousBound:0.00000}");
                            toRemove.Add(pending);
                        }
                        else if (extended || age >= FalseExpansionValidationBars)
                        {
                            // accept expansion (if not yet applied)
                            if (pending.Source == "STBreakPending")
                            {
                                pending.Source = "STBreak";
                                RegisterDeferredAcceptance(rs, pending);
                            }
                            pending.Classification = pending.Hard ? "Hard" : "Soft";
                            toRemove.Add(pending);
                        }
                    }
                    foreach (var rem in toRemove) rs.PendingValidationExpansions.Remove(rem);
                }

                // Metrics update
                rs.CurrentWidth = (rs.CurrentUpperBound - rs.CurrentLowerBound);
                if (rs.CurrentWidth.HasValue && rs.CurrentWidth.Value > 0)
                {
                    int barsSinceP1 = (i - (rs.P1Index ?? rs.ClimaxIndex));
                    if (barsSinceP1 > 0)
                        rs.WidthVelocity = (rs.CurrentWidth.Value - baseWidth) / baseWidth / barsSinceP1;
                    if (rs.P1Price.HasValue && rs.CurrentUpperBound.HasValue && rs.CurrentLowerBound.HasValue)
                    {
                        double baseMid = (rs.ClimaxExtreme + rs.P1Price.Value) / 2.0;
                        double currentMid = (rs.CurrentUpperBound.Value + rs.CurrentLowerBound.Value) / 2.0;
                        rs.DriftBias = (currentMid - baseMid) / baseWidth;
                    }
                }
                int recentStart = Math.Max(start, i - VolRecentWindow + 1);
                var recent = bars.Skip(recentStart).Take(i - recentStart + 1).ToList();
                double recentAvg = recent.Average(b => b.High - b.Low);
                rs.VolatilityCompression = baseAvgRange > 0 ? recentAvg / baseAvgRange : 1;
                rs.VolatilityCompressionInverse = rs.VolatilityCompression > 0 ? (double?)(1.0 / rs.VolatilityCompression) : null;
                if (rs.CurrentWidth.HasValue && rs.CurrentWidth.Value > 0)
                {
                    rs.DriftBiasNormalized = rs.DriftBias / rs.CurrentWidth.Value;
                    if (rs.DriftBiasNormalized > 5) rs.DriftBiasNormalized = 5; // cap extremes
                    if (rs.DriftBiasNormalized < -5) rs.DriftBiasNormalized = -5;
                }

                // Update core occupancy & both-sides-tested flags
                if (rs.CurrentUpperBound.HasValue && rs.CurrentLowerBound.HasValue && rs.CurrentWidth > 0)
                {
                    double mid = (rs.CurrentUpperBound.Value + rs.CurrentLowerBound.Value) / 2.0;
                    double halfWidth = rs.CurrentWidth.Value / 2.0;
                    double coreHalf = halfWidth * CoreZoneFrac / 2.0; // zone size fraction of width
                    int occStart = Math.Max(start, i - CoreOccupancyWindow + 1);
                    var occBars = bars.Skip(occStart).Take(i - occStart + 1).ToList();
                    int inside = occBars.Count(b => b.Close >= mid - coreHalf && b.Close <= mid + coreHalf);
                    rs.CoreOccupancy = occBars.Count > 0 ? (double)inside / occBars.Count : 0;
                }
                rs.BothSidesTested = (rs.TouchCountUpper >= MinTouchesPerSideForLock && rs.TouchCountLower >= MinTouchesPerSideForLock);

                if (!rs.FinalRangeLocked && rs.ExpansionEvents.Count > 0)
                {
                    var lastExp = rs.ExpansionEvents.Last();
                    bool ageOk = i - lastExp.Index >= LockLookbackBars;
                    int barsSinceP1 = i - (rs.P1Index ?? rs.ClimaxIndex);
                    bool ageSinceP1Ok = barsSinceP1 >= MinBarsSinceP1ForLock;
                    bool compressionOk = rs.VolatilityCompression < 0.65 && Math.Abs(rs.WidthVelocity) < 0.01;
                    bool coreOk = rs.CoreOccupancy >= CoreOccupancyMin;
                    bool noPending = rs.PendingValidationExpansions.Count == 0;
                    bool noSpringPending = !rs.SpringCandidateIndex.HasValue || (rs.SpringCandidateIndex.HasValue && (i - rs.SpringCandidateIndex.Value) > SpringConfirmationBars);
                    if (ageOk && ageSinceP1Ok && compressionOk && coreOk && noPending && rs.BothSidesTested && noSpringPending)
                    {
                        rs.FinalRangeLocked = true;
                        rs.FinalLockIndex = i;
                        Log($"[RangeLock] idx={i} width={rs.CurrentWidth:0.00000} comp={rs.VolatilityCompression:0.00} vel={rs.WidthVelocity:0.0000} occ={rs.CoreOccupancy:0.00} touches={rs.TouchCountLower}/{rs.TouchCountUpper}");
                    }
                }

                rs.LastUpdateIndex = i;

                // Shadow scoring (does not change behavior Phase 1)
                ComputeShadowRangeScore(rs, i, bars);

                // ---------------- Phase / Mini / Direct Breakout Logic ----------------
                int barsSinceP1Phase = i - (rs.P1Index ?? rs.ClimaxIndex);
                int expansionCount = rs.ExpansionEvents.Count;
                // Potential mini detection (early compression & occupancy)
                if (!rs.PotentialMini && rs.PhaseState == PhaseState.BaseForming && barsSinceP1Phase >= EarlyCompressionLookback && rs.CoreOccupancy >= 0.65 && expansionCount <= MiniMaxExpansionCount && rs.VolatilityCompression <= 0.65)
                {
                    rs.PotentialMini = true;
                    Log($"[MiniFlag] idx={i} occ={rs.CoreOccupancy:0.00} comp={rs.VolatilityCompression:0.00} exp={expansionCount}");
                }
                // State transitions
                switch (rs.PhaseState)
                {
                    case PhaseState.BaseForming:
                        if (expansionThisBar && rs.VolatilityCompression > 0.65)
                            SetPhaseState(rs, PhaseState.PhaseB_Candidate, i, "firstExpansion");
                        else if (rs.PotentialMini)
                            SetPhaseState(rs, PhaseState.CompressionWatch, i, "earlyCompression");
                        break;
                    case PhaseState.PhaseB_Candidate:
                        if (rs.SpringCandidateIndex.HasValue)
                        {
                            int phaseAge = rs.LastStateChangeIndex.HasValue ? i - rs.LastStateChangeIndex.Value : 0;
                            if (phaseAge >= MinBarsInPhaseB || rs.VolatilityCompression <= CompDeepForFastC)
                                SetPhaseState(rs, PhaseState.PhaseC_Candidate, i, "springCandidate");
                            else
                                Log($"[SpringHold] idx={i} phaseAge={phaseAge} comp={rs.VolatilityCompression:0.00}");
                        }
                        else if (rs.VolatilityCompression <= 0.60 && Math.Abs(rs.WidthVelocity) < 0.01)
                            SetPhaseState(rs, PhaseState.CompressionWatch, i, "compressionEmerging");
                        break;
                    case PhaseState.CompressionWatch:
                        if (expansionThisBar)
                        {
                            rs.PotentialMini = false; // expansion invalidates early mini bias
                            SetPhaseState(rs, PhaseState.PhaseB_Candidate, i, "newExpansion");
                        }
                        break;
                    case PhaseState.PhaseC_Candidate:
                        if (rs.SpringConfirmIndex.HasValue && rs.CoreOccupancy >= CoreOccupancyMin)
                            SetPhaseState(rs, PhaseState.DirectBreakoutReady, i, "springConfirmedCompression");
                        break;
                    case PhaseState.DirectBreakoutReady:
                        // handled below for triggers / fails
                        break;
                    case PhaseState.BreakoutExecuted:
                        // monitor for fail reversion
                        break;
                }

                // Determine readiness for direct breakout (both in CompressionWatch or PhaseC_Candidate confirmed, or mini scenario)
                if (rs.PhaseState == PhaseState.CompressionWatch || rs.PhaseState == PhaseState.PhaseC_Candidate)
                {
                    bool miniCriteria = barsSinceP1Phase >= MiniPatternBarWindowLow && expansionCount <= MiniMaxExpansionCount && rs.CoreOccupancy >= MiniCoreOccupancyMin && rs.VolatilityCompression <= MiniCompressionMax;
                    double driftAbs = Math.Abs(rs.DriftBias);
                    bool driftOk = driftAbs >= (miniCriteria ? DriftBiasBreakoutMinMini : DriftBiasBreakoutMinStd);
                    bool cooldown = rs.FailBreakoutIndex.HasValue && (i - rs.FailBreakoutIndex.Value) < FailBreakoutCooldownBars;
                    if (miniCriteria && driftOk && !cooldown)
                    {
                        if (rs.PhaseState != PhaseState.DirectBreakoutReady)
                        {
                            rs.IsMiniPattern = true;
                            SetPhaseState(rs, PhaseState.DirectBreakoutReady, i, "directCriteria");
                        }
                    }
                }

                // Direct breakout trigger logic
                if (rs.PhaseState == PhaseState.DirectBreakoutReady)
                {
                    // choose direction by climax type
                    bool bullish = rs.IsSellingClimax; // expecting SOS upward
                    if (rs.CurrentUpperBound.HasValue && rs.CurrentLowerBound.HasValue && rs.CurrentWidth > 0)
                    {
                        double range = bar.High - bar.Low;
                        double recentATR = rs.VolatilityCompression * baseAvgRange; // approximate ATR proxy
                        double neededRange = BreakoutATRFactor * recentATR;
                        double mid = (rs.CurrentUpperBound.Value + rs.CurrentLowerBound.Value) / 2.0;
                        double coreHalf = (rs.CurrentWidth.Value * CoreZoneFrac) / 2.0;
                        double coreLow = mid - coreHalf;
                        double coreHigh = mid + coreHalf;
                        double offset = rs.CurrentWidth.Value * BreakoutCloseOffsetFrac * 0.5;
                        bool closeOutside = bullish
                            ? bar.Close >= rs.CurrentUpperBound.Value + offset
                            : bar.Close <= rs.CurrentLowerBound.Value - offset;
                        bool notBackInCore = bar.Close > coreHigh || bar.Close < coreLow;
                        // Quality filters
                        double realBody = Math.Abs(bar.Close - bar.Open);
                        double bodyRatio = range > 0 ? realBody / range : 0;
                        double rangeFactor = recentATR > 0 ? range / recentATR : 0;
                        // recentAvg and recent derived earlier; recompute lightweight stats for volume if needed
                        double volSpike = 0;
                        double recentAvgVol = 0;
                        if (BreakoutVolumeSpikeMin > 0)
                        {
                            int volLookbackStart = Math.Max(start, i - VolRecentWindow + 1);
                            var volRecent = bars.Skip(volLookbackStart).Take(i - volLookbackStart + 1).ToList();
                            recentAvgVol = volRecent.Count > 0 ? volRecent.Average(b => b.Volume) : 0;
                            if (recentAvgVol > 0) volSpike = bar.Volume / recentAvgVol;
                        }
                        bool bodyOk = bodyRatio >= BreakoutBodyRatioMin;
                        bool rangeOk = rangeFactor >= BreakoutRangeFactorMin && range >= neededRange;
                        bool volumeOk = BreakoutVolumeSpikeMin <= 0 || (recentAvgVol > 0 && volSpike >= BreakoutVolumeSpikeMin);
                        if (closeOutside && notBackInCore && bodyOk && rangeOk && volumeOk)
                        {
                            rs.DirectTriggerIndex = i;
                            rs.LastTriggerType = "DirectBreakout";
                            rs.LastTriggerDirection = bullish ? "Long" : "Short";
                            rs.LastTriggerBodyRatio = bodyRatio;
                            rs.LastTriggerRangeFactor = rangeFactor;
                            rs.LastTriggerVolSpike = volSpike > 0 ? volSpike : null;
                            SetPhaseState(rs, PhaseState.BreakoutExecuted, i, "directTrigger");
                            Log(string.Format(CultureInfo.InvariantCulture,
                                "[DirectTrigger] idx={0} rngFac={1:0.00} body={2:0.00} volSpike={3:0.00} occ={4:0.00} drift={5:0.00}",
                                i, rangeFactor, bodyRatio, volSpike, rs.CoreOccupancy, rs.DriftBias));
                            // Removed separate StructuredLogger DirectTrigger event to avoid duplicate plain line.
                        }
                        else if (closeOutside && notBackInCore)
                        {
                            // borderline attempt that failed quality filters
                            Log(string.Format(CultureInfo.InvariantCulture,
                                "[DirectCheckFail] idx={0} closeOutside={1} body={2:0.00}/{3:0.00} rngFac={4:0.00}/{5:0.00} volSpike={6:0.00}/{7:0.00}",
                                i, closeOutside, bodyRatio, BreakoutBodyRatioMin, rangeFactor, BreakoutRangeFactorMin, volSpike, BreakoutVolumeSpikeMin));
                        }
                        // timeout / decay
                        int ageReady = rs.LastStateChangeIndex.HasValue ? i - rs.LastStateChangeIndex.Value : 0;
                        if (barsSinceP1Phase >= ReevalTimeoutBars && ageReady >= MinPhaseBarsBeforeTimeout)
                        {
                            SetPhaseState(rs, PhaseState.FailAbort, i, "timeout");
                        }
                    }
                }

                // Fail breakout detection (simple reversion into core quickly after trigger)
                if (rs.PhaseState == PhaseState.BreakoutExecuted && rs.DirectTriggerIndex.HasValue && i > rs.DirectTriggerIndex.Value)
                {
                    int age = i - rs.DirectTriggerIndex.Value;
                    if (age <= 3 && rs.CurrentUpperBound.HasValue && rs.CurrentLowerBound.HasValue && rs.CurrentWidth > 0)
                    {
                        double mid = (rs.CurrentUpperBound.Value + rs.CurrentLowerBound.Value) / 2.0;
                        double coreHalf = (rs.CurrentWidth.Value * CoreZoneFrac) / 2.0;
                        if (bar.Close >= mid - coreHalf && bar.Close <= mid + coreHalf)
                        {
                            rs.FailBreakoutIndex = i;
                            SetPhaseState(rs, PhaseState.FailAbort, i, "revertCore");
                            Log($"[BreakoutFail] idx={i} revertCore occ={rs.CoreOccupancy:0.00}");
                        }
                    }
                }
            }
                // After processing all bars, emit summary
                if (rs.ExpansionEvents.Count > 0)
                {
                    double maxDelta = rs.ExpansionEvents.Max(e => e.ExpansionDelta);
                    if (!(rs.LastSummaryIndex.HasValue && rs.LastUpdateIndex.HasValue && (rs.LastUpdateIndex.Value - rs.LastSummaryIndex.Value) < SummarySuppressWindowBars))
                    {
                        rs.LastSummaryIndex = rs.LastUpdateIndex;
                        if (string.IsNullOrEmpty(rs.LastSummaryType)) rs.LastSummaryType = rs.FinalRangeLocked ? "Lock" : "Periodic";
                        Log($"[RangeSummary] climax={rs.ClimaxIndex} P1={rs.P1Index} expCount={rs.ExpansionEvents.Count} maxDelta={maxDelta:0.00} locked={rs.FinalRangeLocked} type={rs.LastSummaryType}");
                    }
                }
                // decide evolve structured snapshot
                bool changed = _ePrevPhase != rs.PhaseState
                                || DiffChanged(_ePrevWidth, rs.CurrentWidth, 1e-6)
                                || DiffChanged(_ePrevDrift, rs.DriftBias, 5e-3)
                                || DiffChanged(_ePrevComp, rs.VolatilityCompression, 5e-3)
                                || DiffChanged(_ePrevOcc, rs.CoreOccupancy, 5e-3)
                                || (_ePrevIndex != rs.LastUpdateIndex && rs.LastUpdateIndex % 10 == 0);
                if (changed)
                {
                    StructuredLogger?.Invoke("EvolveRangeState", rs);
                }
        }

        private void RegisterExpansion(RangeState rs, int index, double newBound, double deltaFrac, string source, bool hard, double previousBound)
        {
            var ev = new ExpansionEvent
            {
                Index = index,
                NewBound = newBound,
                ExpansionDelta = deltaFrac,
                Source = source,
                Hard = hard,
                PreviousBound = previousBound
            };
            rs.ExpansionEvents.Add(ev);
            rs.LastExpansionIndex = index;
            if (!rs.FirstExpansionIndex.HasValue) rs.FirstExpansionIndex = index;
            if (rs.IsSellingClimax)
            {
                if (source == "P1Break") rs.CurrentUpperBound = newBound; else if (source == "STBreak") rs.CurrentLowerBound = newBound;
            }
            else
            {
                if (source == "P1Break") rs.CurrentLowerBound = newBound; else if (source == "STBreak") rs.CurrentUpperBound = newBound;
            }
            rs.CurrentWidth = rs.CurrentUpperBound - rs.CurrentLowerBound;
            Log(string.Format(CultureInfo.InvariantCulture,
                "[Expansion] src={0} idx={1} delta={2:0.00} hard={3} newBound={4:0.00000}",
                source, index, deltaFrac, hard, newBound));
            if (source == "STBreak") rs.PendingValidationExpansions.Add(ev);
            // Auto-lock if expansion exceeds full base width
            if (!rs.FinalRangeLocked && deltaFrac >= EscapeLockFrac)
            {
                rs.FinalRangeLocked = true;
                rs.FinalLockIndex = index;
                Log(string.Format(CultureInfo.InvariantCulture,
                    "[RangeLock] idx={0} reason=Escape delta={1:0.00}", index, deltaFrac));
                EmitEarlySummary(rs);
            }
        }

        private void RegisterDeferredAcceptance(RangeState rs, ExpansionEvent pending)
        {
            // Apply bound now
            if (rs.IsSellingClimax)
                rs.CurrentLowerBound = pending.NewBound; // same-direction for selling climax extends lower bound
            else
                rs.CurrentUpperBound = pending.NewBound; // buying climax extends upper bound
            rs.CurrentWidth = rs.CurrentUpperBound - rs.CurrentLowerBound;
            rs.ExpansionEvents.Add(pending);
            rs.LastExpansionIndex = pending.Index;
            if (!rs.FirstExpansionIndex.HasValue) rs.FirstExpansionIndex = pending.Index;
            Log(string.Format(CultureInfo.InvariantCulture,
                "[Expansion] src=STBreak idx={0} delta={1:0.00} hard={2} newBound={3:0.00000} acceptAge={4}",
                pending.Index, pending.ExpansionDelta, pending.Hard, pending.NewBound,
                (rs.LastUpdateIndex.HasValue ? (rs.LastUpdateIndex.Value - pending.Index) : 0)));
            // Escape auto-lock check using original delta
            if (!rs.FinalRangeLocked && pending.ExpansionDelta >= EscapeLockFrac)
            {
                rs.FinalRangeLocked = true;
                rs.FinalLockIndex = pending.Index;
                Log(string.Format(CultureInfo.InvariantCulture,
                    "[RangeLock] idx={0} reason=Escape delta={1:0.00}", pending.Index, pending.ExpansionDelta));
                EmitEarlySummary(rs);
            }
        }

        private void EmitEarlySummary(RangeState rs)
        {
            if (rs == null || rs.ExpansionEvents == null || rs.ExpansionEvents.Count == 0) return;
            double maxDelta = rs.ExpansionEvents.Max(e => e.ExpansionDelta);
            if (rs.LastSummaryIndex.HasValue && rs.LastUpdateIndex.HasValue && (rs.LastUpdateIndex.Value - rs.LastSummaryIndex.Value) < SummarySuppressWindowBars)
                return;
            rs.LastSummaryIndex = rs.LastUpdateIndex;
            rs.LastSummaryType = rs.FinalRangeLocked ? "Lock" : "Early";
            Log(string.Format(CultureInfo.InvariantCulture,
                "[RangeSummary] climax={0} P1={1} expCount={2} maxDelta={3:0.00} locked={4} type={5}",
                rs.ClimaxIndex, rs.P1Index, rs.ExpansionEvents.Count, maxDelta, rs.FinalRangeLocked, rs.LastSummaryType));
            // Removed plain "RangeSummary" structured event to avoid duplicate line following bracketed summary.
        }

        public void AnalyzeAndEvolve(IList<Bar> bars, ClimaxEvent climax, RangeState rs)
        {
            if (rs == null) rs = AnalyzeInitialRange(bars, climax);
            EvolveRangeState(bars, rs);
        }

        private bool IsPivot(IList<Bar> bars, int i, int lr, bool opposite, bool sellingClimax)
        {
            if (opposite)
            {
                if (sellingClimax)
                {
                    for (int k = i - lr; k <= i + lr; k++)
                    {
                        if (k == i) continue; if (k < 0 || k >= bars.Count) return false; if (bars[k].High >= bars[i].High) return false;
                    }
                    return true;
                }
                else
                {
                    for (int k = i - lr; k <= i + lr; k++)
                    {
                        if (k == i) continue; if (k < 0 || k >= bars.Count) return false; if (bars[k].Low <= bars[i].Low) return false;
                    }
                    return true;
                }
            }
            else
            {
                if (sellingClimax)
                {
                    for (int k = i - lr; k <= i + lr; k++)
                    {
                        if (k == i) continue; if (k < 0 || k >= bars.Count) return false; if (bars[k].Low <= bars[i].Low) return false;
                    }
                    return true;
                }
                else
                {
                    for (int k = i - lr; k <= i + lr; k++)
                    {
                        if (k == i) continue; if (k < 0 || k >= bars.Count) return false; if (bars[k].High >= bars[i].High) return false;
                    }
                    return true;
                }
            }
        }

        private void SetPhaseState(RangeState rs, PhaseState newState, int index, string reason)
        {
            if (rs.PhaseState == newState) return;
            rs.PhaseState = newState;
            rs.LastStateChangeIndex = index;
            var prev = rs.SuggestedPositionSizeFactor;
            // Phase-based size factor mapping
            switch (newState)
            {
                case PhaseState.BaseForming:
                    rs.SuggestedPositionSizeFactor = FactorBaseForming; break;
                case PhaseState.PhaseB_Candidate:
                    rs.SuggestedPositionSizeFactor = FactorPhaseB; break;
                case PhaseState.CompressionWatch:
                    rs.SuggestedPositionSizeFactor = FactorCompressionWatch; break;
                case PhaseState.PhaseC_Candidate:
                    rs.SuggestedPositionSizeFactor = FactorPhaseC; break;
                case PhaseState.DirectBreakoutReady:
                    rs.SuggestedPositionSizeFactor = rs.IsMiniPattern ? FactorDirectMini : FactorDirectNonMini; break;
                case PhaseState.BreakoutExecuted:
                    // retain factor from ready state
                    break;
                case PhaseState.FailAbort:
                    rs.SuggestedPositionSizeFactor = null; break;
                default:
                    rs.SuggestedPositionSizeFactor = BasePositionSizeFactor; break;
            }
            if (prev != rs.SuggestedPositionSizeFactor)
            {
                StructuredLogger?.Invoke("SizeFactorChange", rs);
            }
            Log(string.Format(CultureInfo.InvariantCulture, "[PhaseEnter] state={0} idx={1} reason={2}", newState, index, reason));
            if (newState == PhaseState.FailAbort || newState == PhaseState.BreakoutExecuted)
            {
                rs.LastSummaryType = "Final";
                EmitEarlySummary(rs);
            }
            // Removed plain "PhaseEnter" structured event to eliminate duplicate; bracketed log already emitted.
        }

        private void Log(string msg)
        {
            // Normalize numeric decimal separator inside free-form log line so JSONL 'line' field is culture invariant.
            // We only transform when current culture uses a comma as decimal separator.
            try
            {
                var decSep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                if (decSep == ",")
                {
                    // Replace comma that appears between digits with a dot. Avoid touching thousands separators (space or . in such locales) and other punctuation.
                    msg = Regex.Replace(msg, @"(?<=\d),(?=\d)", ".");
                }
            }
            catch { /* non-fatal */ }
            if (Verbose) _logger?.Invoke(msg);
            StructuredLogger?.Invoke(msg, _activeRangeState);
        }

        // ---------------- Shadow Scoring Phase 1 ----------------
        private void ComputeShadowRangeScore(RangeState rs, int i, IList<Bar> bars)
        {
            try
            {
                // Parameters (could later be configurable)
                const double expTarget = 4.0;
                const double touchTarget = 3.0;
                const double maturityBarsTarget = 40.0;
                const double compLow = 0.45;
                const double compHigh = 0.90;
                const double driftReq = 0.25;
                const double widthVelMax = 0.02;

                int expCount = rs.ExpansionEvents.Count;
                int touchesMin = Math.Min(rs.TouchCountLower, rs.TouchCountUpper);
                int barsSinceP1 = i - (rs.P1Index ?? rs.ClimaxIndex);
                double comp = rs.VolatilityCompression;
                double occ = rs.CoreOccupancy;
                double driftAbsNorm = 0;
                if (rs.DriftBiasNormalized != 0 && rs.CurrentWidth > 0)
                {
                    driftAbsNorm = Math.Min(Math.Abs(rs.DriftBiasNormalized) / driftReq, 1.0);
                }
                double expCountNorm = Math.Min(expCount / expTarget, 1.0);
                double touchesNorm = Math.Min(touchesMin / touchTarget, 1.0);
                double maturityBarsNorm = Math.Min(barsSinceP1 / maturityBarsTarget, 1.0);
                double compNorm = 0;
                if (compHigh > compLow)
                    compNorm = Math.Max(0, Math.Min(1, (compHigh - comp) / (compHigh - compLow)));
                double occNorm = Math.Max(0, Math.Min(1, occ));
                double widthStabilityNorm = 1.0;
                if (rs.WidthVelocity != 0)
                {
                    widthStabilityNorm = Math.Max(0, Math.Min(1, 1 - Math.Abs(rs.WidthVelocity) / widthVelMax));
                }
                // Cleanliness proxy
                int falseExp = rs.ExpansionEvents.Count(e => e.Source == "FalseExpansionST");
                double falseRate = expCount > 0 ? (double)falseExp / expCount : 0;
                double cleanlinessNorm = Math.Max(0, Math.Min(1, 1 - falseRate));
                // Sweep clarity placeholder: assume 0.5 until we implement distribution tracking
                double sweepClarityNorm = 0.5;
                // Spring sub-score placeholder
                double springSub = (rs.SpringConfirmIndex.HasValue ? 0.7 : (rs.SpringCandidateIndex.HasValue ? 0.3 : 0));

                double structureSub = 0.5 * expCountNorm + 0.5 * touchesNorm;
                double maturitySub = 0.6 * maturityBarsNorm + 0.4 * widthStabilityNorm;
                double compressionSub = 0.5 * compNorm + 0.5 * occNorm;
                double cleanlinessSub = 0.6 * cleanlinessNorm + 0.4 * sweepClarityNorm;
                double driftSub = driftAbsNorm * widthStabilityNorm;

                const double wS = 0.30, wM = 0.15, wC = 0.25, wSp = 0.15, wCl = 0.10, wD = 0.05;
                double rangeScore = wS * structureSub + wM * maturitySub + wC * compressionSub + wSp * springSub + wCl * cleanlinessSub + wD * driftSub;

                rs.ExpCountNorm = expCountNorm;
                rs.TouchesNorm = touchesNorm;
                rs.MaturityBarsNorm = maturityBarsNorm;
                rs.CompNorm = compNorm;
                rs.DriftAbsNorm = driftAbsNorm;
                rs.OccNorm = occNorm;
                rs.WidthStabilityNorm = widthStabilityNorm;
                rs.CleanlinessNorm = cleanlinessNorm;
                rs.SweepClarityNorm = sweepClarityNorm;
                rs.StructureSubScore = structureSub;
                rs.MaturitySubScore = maturitySub;
                rs.CompressionSubScore = compressionSub;
                rs.SpringSubScore = springSub;
                rs.CleanlinessSubScore = cleanlinessSub;
                rs.DriftSubScore = driftSub;
                rs.RangeScore = rangeScore;

                // Optional: log every 15 bars for visibility
                if (i % 15 == 0)
                {
                    StructuredLogger?.Invoke("ShadowScore", rs);
                }
            }
            catch { /* safe fail */ }
        }

        private bool DiffChanged(double? a, double? b, double eps)
        {
            if (!a.HasValue && b.HasValue) return true;
            if (a.HasValue && !b.HasValue) return true;
            if (!a.HasValue && !b.HasValue) return false;
            return Math.Abs(a.Value - b.Value) > eps;
        }
    }
}
