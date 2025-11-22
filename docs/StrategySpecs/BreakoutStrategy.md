# BreakoutStrategy Specification

## Purpose
Deliver high-quality breakout entries aligned with the DeepSeek champion heuristics. The module consumes multi-timeframe snapshots from `TimeframeManager`, evaluates structural levels, and emits signals via `IStrategy.EvaluateAsync`.

## Data Dependencies
- **Timeframes:** H4 (trend context), H1 (primary signal), M15 (execution confirmation).
- **Indicators:** ATR(14) on H1, EMA50/EMA200 on H4, Volume SMA(20) on H1 + M15.
- **Regime Inputs:** `RegimeType` to scale thresholds (volatile regimes relax ATR ratios slightly).
- **Sessions:** `SessionAwareAnalyzer` for volume normalization (London/Overlap boosts).

## Integration
The strategy inherits from `MultiTimeframeStrategyBase`, ensuring consistent access to:

- `TimeframeSnapshot` and `TimeframeAlignmentResult` for H4/H1/M15 stacks.
- `TradingSession` via `SessionAwareAnalyzer` (session multiplier logic).
- Alignment validation (anti-repaint and minimum aligned timeframe enforcement).

```csharp
public sealed class BreakoutStrategy : MultiTimeframeStrategyBase
{
    protected override Task<Signal?> EvaluateMultiTimeframeAsync(
        MultiTimeframeEvaluationContext ctx,
        CancellationToken ct)
    {
        // ctx.Snapshot for bar series, ctx.Alignment for confidence.
        // Return null when breakout criteria unmet.
    }
}
```

## Algorithm
1. **Key Level Detection**
   - Maintain rolling list of swing highs/lows per timeframe.
   - Validate a key level when ≥ 3 touches land inside ±0.20% price zone within last 5 days.
   - Confirm the zone captured ≥ 20% of weekly aggregated volume (aggregated via TimeframeManager metadata).
   - Order-block density: count unmitigated OBs inside zone; require density ≥ 2 for longs, ≥ 1 for shorts.

2. **Breakout Confirmation**
   - Primary candle (H1): closing distance from level must be ≥ 0.25 * ATR(H1).
   - Execution candle (M15) must close in same direction and above/below level.
   - Volume filter: `Volume(current)` ≥ `VolumeSMA20` * 1.8 on both H1 and M15.
   - Trend alignment: EMA50 > EMA200 on H4 for longs (inverse for shorts). Reject if not aligned.

3. **Strength Score**
   ```
   Strength = (
       ATR_DistanceRatio * 0.4 +
       VolumeMultiplier * 0.3 +
       MultiTimeframeAlignment * 0.2 +
       OrderBlockDensityScore * 0.1)
   ```
   - ATR distance ratio = (Close - KeyLevel)/ATR(H1); must be ≥ 0.35 before signal considered.
   - Volume multiplier normalized so 1.8x = 1.0 strength component.
   - Alignment score counts agreeing timeframes / total timeframes.
   - Orders failing minimum strength are discarded; borderline cases require higher confidence (≥0.5).

4. **Anti-Repaint & Timing**
   - Use only closed bars delivered via `TimeframeSynchronizer`.
   - Breakout must complete within ≤ 2 bars from initial level touch; track elapsed bars in metadata.
   - Reject signals if a retest of >50% of the breakout candle occurs within 3 bars.

5. **Outputs**
   - `Signal.Action`: Buy/Sell.
   - `Signal.Confidence`: derived from strength and regime multipliers.
   - Metadata keys: `key_level`, `atr_ratio`, `volume_multiplier`, `aligned_timeframes`, `session_multiplier`.

## Configuration Surface
Mapped to `BreakoutStrategyConfig`:
| Parameter | Default | Description |
|-----------|---------|-------------|
| `MinimumStrength` | 0.35 | Minimum ATR distance ratio. |
| `VolumeMultiplier` | 1.8 | Required volume multiple vs SMA20. |
| `RetestWindowBars` | 3 | Bars to monitor for >50% retests. |
| `MaxBreakoutBars` | 2 | Max bars allowed between detection and confirmation. |
| `TouchTolerancePercent` | 0.2 | Zone tolerance for touches. |
| `WeeklyVolumeThreshold` | 0.2 | Minimum share of weekly volume. |
| `OrderBlockDensityMin` | 1 | Minimum OB count in zone. |
| `TrendEmaFast` | 50 | Fast EMA length for H4. |
| `TrendEmaSlow` | 200 | Slow EMA length for H4. |

## Telemetry & Testing
- Emit structured logs for every rejection reason (volume, ATR, misalignment, retest).
- Unit tests cover: valid breakout, insufficient volume, failed EMA alignment, retest violation, late confirmation.
- Integration tests (pending Agent A hand-off) will validate multi-timeframe snapshots feed via `TimeframeManager`.
