# Phase 2 Strategy Coordination Preparation (Agent A)

## 1. Multi-Timeframe Integration Status

- **Completion:** BotGRobot now ingests H4/H1/M15 closed bars each tick, updates `TimeframeSnapshot`, and gates the strategy pipeline when alignment or anti-repaint guards fail.
- **Latency Target:** Benchmarking via `Performance/MultiTimeframeBenchmark.cs` enforces the <50 ms SLA (150% alert threshold). Monitor `MTF|Benchmark` and `MTF|LatencyAlert` log entries.
- **Context Availability:** `BuildMarketContext` injects `mtf_context`, `mtf_session`, and alignment metadata so Agent B can retrieve `MultiTimeframeEvaluationContext` from `MarketContext.Metadata["mtf_context"]`.
- **Session Scaling:** `SessionAwareAnalyzer` multiplier is persisted in metadata (`metrics["mtf_session_multiplier"]`), enabling strategies to scale risk per trading session.

## 2. Bayesian Fusion Requirements Review

- **Input Set:** Strategy signals (`StrategyEvaluation`) enriched with multi-timeframe alignment, regime state, and session multiplier.
- **Fusion Goal:** Produce posterior confidence per action (long/short/exit) using Bayesian updating across strategies, factoring independent evidence and correlated signals.
- **Priors:** Configurable per action, seeded from historical hit rates (default 0.5 if absent). Should support environment overrides (e.g., regime-specific priors).
- **Likelihood Model:** Map each strategy's confidence × regime compatibility × multi-timeframe readiness to likelihood ratios. Leverage existing `SignalConfidenceCalculator` outputs as base likelihoods.
- **Penalty Terms:** Incorporate cooldown penalties, conflict penalties, and anti-correlation weights (negative evidence) before fusion.
- **Decision Rule:** Execute when posterior odds exceed adaptive threshold (ties resolved by existing `ConflictResolver`).
- **Telemetry:** Emit posterior, prior, likelihood, and contributing strategies for audit (`COORD|BayesFusion`).

## 3. Enhanced Strategy Coordinator Design (Draft)

```text
┌────────────────────┐
│ StrategyPipeline    │  (already collects StrategyEvaluation + MarketContext)
└─────────┬──────────┘
          │ evaluations
┌─────────▼──────────┐
│ EvidenceAssembler   │  (new) attaches mtf/session factors, normalises confidences
└─────────┬──────────┘
          │ evidence vector
┌─────────▼──────────┐
│ BayesianFusionCore │  (new) computes posterior odds per action
└─────────┬──────────┘
          │ posterior summary
┌─────────▼──────────┐
│ ConfidenceBooster  │  (reuse) adjusts based on risk/ops overrides
└─────────┬──────────┘
          │ filtered decisions
┌─────────▼──────────┐
│ ConflictResolver   │  (existing) ensures no conflicting fills
└─────────┬──────────┘
          │ final selections
┌─────────▼──────────┐
│ TradeManager       │
└────────────────────┘
```

- **Configuration:** Extend `StrategyCoordinationConfig` with `BayesianFusionConfig` (priors, action mapping, correlation matrix, confidence floor overrides).
- **State:** Keep rolling calibration stats (per-strategy reliability, session bias). Persist in coordinator for adaptive priors.
- **API Changes:** Introduce `EnhancedStrategyCoordinator : IStrategyCoordinator`. Backwards compatibility via feature flag (`config.EnableBayesianFusion`).
- **Testing:** Unit tests covering (a) independent evidence, (b) conflicting signals, (c) misaligned mtf gating, (d) posterior threshold tuning. Consider golden master from synthetic scenarios.

## 4. Performance Monitoring Plan

- **Telemetry Review:** Track `MTF|Benchmark` logs every ~600 ticks; investigate any `LatencyAlert` immediately.
- **Counters:** Add dashboard widgets for `mtf_alignment_ok`, `mtf_alignment_reason`, and benchmark percentiles (planned for Phase 2 dashboard work).
- **Fallback:** If alignment consistently fails, instruct Agent B to inspect `TimeframeSeriesStatus` within metadata (contains per-timeframe bar counts and close timestamps).
- **Coordination Metrics:** During Phase 2, extend telemetry with posterior distributions to ensure Bayesian fusion stays within execution budget (<3 ms per tick target).

## 5. Support Plan for Agent B

- Provide snippet for retrieving evaluation context:

  ```csharp
  var mtfContext = context.Metadata?["mtf_context"] as MultiTimeframeEvaluationContext;
  if (mtfContext?.Alignment.IsAligned == true) { /* proceed */ }
  ```

- Share log filters: `grep "MTF|"` for ingestion health, `grep "COORD"` for coordinator behaviour.
- Maintain availability for quick adjustments to ingestion or benchmarking thresholds during strategy testing.

## 6. Phase 2 Technical Spec Checklist

- [ ] Finalise `BayesianFusionConfig` schema (priors, correlation, thresholds).
- [ ] Define `EvidenceAssembler` contract (inputs, outputs, normalization rules).
- [ ] Draft pseudo-code for posterior calculation (log-odds accumulation to avoid floating-point underflow).
- [ ] Plan integration tests simulating mixed-strategy scenarios (long vs short conflict, multi-session).
- [ ] Align telemetry naming with existing coordinator logs to simplify analytics.

## 7. Next Steps (Day 4 Kick-off)

1. Lock EnhancedCoordinator architecture and update `StrategyCoordinator` implementation (feature flag path first).
2. Implement Bayesian fusion core with config-driven priors; add unit tests.
3. Instrument telemetry for posterior odds and decision traces.
4. Coordinate with Agent B on strategy onboarding sequence once fusion outputs validated.
