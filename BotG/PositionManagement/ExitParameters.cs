using System;
using System.Collections.Generic;
using System.Linq;
using BotG.Runtime.Logging;

namespace BotG.PositionManagement
{
    /// <summary>
    /// Tham số điều kiện thoát lệnh (exit) cho một position
    /// </summary>
    public class ExitParameters
    {
        /// <summary>Giá stop loss (null = không dùng SL cố định)</summary>
        public double? StopLossPrice { get; set; }

        /// <summary>Giá take profit (null = không dùng TP cố định)</summary>
        public double? TakeProfitPrice { get; set; }

        /// <summary>Khoảng cách trailing stop (pips/points), null nếu không dùng trailing</summary>
        public double? TrailingStopDistance { get; set; }

        /// <summary>Giá trailing stop hiện tại (cập nhật khi giá di chuyển có lợi)</summary>
        public double? TrailingStopPrice { get; set; }

        /// <summary>Số bars tối đa giữ position (null = không giới hạn time-based)</summary>
        public int? MaxBarsHold { get; set; }

        /// <summary>Thời điểm tối đa giữ position (null = không giới hạn theo timestamp)</summary>
        public DateTime? MaxHoldUntil { get; set; }

        /// <summary>Tỷ lệ Risk:Reward (ví dụ 2.0 = TP = 2x SL distance)</summary>
        public double? RiskRewardRatio { get; set; }

        /// <summary>Khoảng cách rủi ro ban đầu tính theo pip (entry – stop) do chiến lược cung cấp</summary>
        public double? InitialRiskPips { get; set; }

        /// <summary>Bội số R kích hoạt breakeven (mặc định 0.75R)</summary>
        public double BreakevenTriggerRMultiple { get; set; } = 0.75;

        /// <summary>Tổng phí cần cộng thêm khi đặt SL về B/E, tính theo pip</summary>
        public double BreakevenFeePips { get; set; } = 0.0;

        /// <summary>% lãi tối thiểu để kích hoạt breakeven stop (null = không dùng)</summary>
        public double? BreakevenTriggerPercent { get; set; }

        /// <summary>Đã kích hoạt breakeven chưa (chuyển SL về entry price)</summary>
        public bool BreakevenActivated { get; set; }

        /// <summary>Bật/tắt trailing stop nhiều mức dựa trên R</summary>
        public bool MultiLevelTrailingEnabled { get; set; } = true;

        /// <summary>Các mốc trailing theo bội số R</summary>
        public List<TrailingLevel> TrailingLevels { get; set; } = new List<TrailingLevel>
        {
            new TrailingLevel { TriggerR = 1.0, StopOffsetR = 0.5 },
            new TrailingLevel { TriggerR = 1.5, StopOffsetR = 1.0 },
            new TrailingLevel { TriggerR = 2.0, StopOffsetR = 1.5 }
        };

        /// <summary>Bội số R bắt đầu áp dụng trailing động theo TP</summary>
        public double TrailingDynamicTriggerR { get; set; } = 2.0;

        /// <summary>Khoảng cách SL so với TP (tính theo R) ở vùng dynamic</summary>
        public double TrailingDynamicOffsetR { get; set; } = 0.5;

        /// <summary>Biên hysteresis R để tránh rung stop quanh mốc</summary>
        public double TrailingHysteresisR { get; set; } = 0.05;

        /// <summary>Thời gian tối thiểu giữa hai lần cập nhật SL (giây)</summary>
        public double TrailingCooldownSeconds { get; set; } = 1.0;

        /// <summary>Lần cuối cùng SL được cập nhật bởi trailing</summary>
        public DateTime? LastTrailingUpdateTime { get; private set; }

        /// <summary>Mốc trigger R cao nhất đã áp dụng</summary>
        public double? LastTrailingTriggerRApplied { get; private set; }

        /// <summary>
        /// Tạo exit params mặc định cho một symbol với risk management
        /// SCALPING CONSERVATIVE: 15 pips SL, 30 pips TP, 2h max hold
        /// </summary>
        public static ExitParameters CreateDefault(string symbol, double entryPrice, cAlgo.API.TradeType direction, double accountBalance)
        {
            // SCALPING CONSERVATIVE SETTINGS (Alpha Arena Proven)
            // SL = 15 pips, TP = 30 pips (Risk:Reward = 1:2)
            double pipSize = symbol.Contains("JPY") ? 0.01 : 0.0001;

            double stopDistance = 15 * pipSize; // 15 pips SL
            double takeProfitDistance = 30 * pipSize; // 30 pips TP (1:2 RR)

            double slPrice = direction == cAlgo.API.TradeType.Buy
                ? entryPrice - stopDistance
                : entryPrice + stopDistance;

            double tpPrice = direction == cAlgo.API.TradeType.Buy
                ? entryPrice + takeProfitDistance
                : entryPrice - takeProfitDistance;

            return new ExitParameters
            {
                StopLossPrice = slPrice,
                TakeProfitPrice = tpPrice,
                TrailingStopDistance = null, // DISABLED for scalping - use fixed SL/TP only
                MaxBarsHold = 50, // allow longer holds during backtests
                RiskRewardRatio = 2.0, // 1:2 risk-reward
                InitialRiskPips = 15,
                BreakevenTriggerPercent = 20 * pipSize / entryPrice // Breakeven at +20 pips
            };
        }

        /// <summary>
        /// Cập nhật trailing stop nếu giá di chuyển có lợi
        /// </summary>
        public void UpdateTrailingStop(double currentPrice, cAlgo.API.TradeType direction)
        {
            if (!TrailingStopDistance.HasValue) return;

            double newTrailingStop = direction == cAlgo.API.TradeType.Buy
                ? currentPrice - TrailingStopDistance.Value
                : currentPrice + TrailingStopDistance.Value;

            // Chỉ cập nhật nếu trailing stop mới tốt hơn (bảo vệ lợi nhuận nhiều hơn)
            if (!TrailingStopPrice.HasValue)
            {
                TrailingStopPrice = newTrailingStop;
            }
            else
            {
                if (direction == cAlgo.API.TradeType.Buy && newTrailingStop > TrailingStopPrice.Value)
                {
                    TrailingStopPrice = newTrailingStop;
                }
                else if (direction == cAlgo.API.TradeType.Sell && newTrailingStop < TrailingStopPrice.Value)
                {
                    TrailingStopPrice = newTrailingStop;
                }
            }
        }

        /// <summary>
        /// Kích hoạt breakeven stop nếu đủ điều kiện
        /// </summary>
        public void CheckBreakeven(
            string symbolName,
            double currentPrice,
            double entryPrice,
            double pipSize,
            double minFeeBufferPips,
            cAlgo.API.TradeType direction)
        {
            if (BreakevenActivated)
            {
                return;
            }

            if (pipSize <= 0)
            {
                return;
            }

            double? riskPips = InitialRiskPips;
            if ((!riskPips.HasValue || riskPips <= 0) && StopLossPrice.HasValue)
            {
                riskPips = Math.Abs(entryPrice - StopLossPrice.Value) / pipSize;
            }

            if (!riskPips.HasValue || riskPips.Value <= 0)
            {
                return;
            }

            double traveledPips = direction == cAlgo.API.TradeType.Buy
                ? (currentPrice - entryPrice) / pipSize
                : (entryPrice - currentPrice) / pipSize;

            if (traveledPips <= 0)
            {
                return;
            }

            if (traveledPips >= riskPips.Value * BreakevenTriggerRMultiple)
            {
                double feeOffsetPips = Math.Max(Math.Max(BreakevenFeePips, minFeeBufferPips), 0);
                double feeOffsetPrice = feeOffsetPips * pipSize;

                StopLossPrice = direction == cAlgo.API.TradeType.Buy
                    ? entryPrice + feeOffsetPrice
                    : entryPrice - feeOffsetPrice;

                BreakevenActivated = true;

                PipelineLogger.Log(
                    "EXIT",
                    "BREAKEVEN",
                    "Breakeven stop activated",
                    new
                    {
                        symbol = symbolName,
                        direction = direction.ToString(),
                        entryPrice,
                        stopLossPrice = StopLossPrice,
                        traveledPips,
                        riskPips,
                        triggerMultiple = BreakevenTriggerRMultiple,
                        feeOffsetPips,
                        minFeeBufferPips,
                        configuredFeePips = BreakevenFeePips
                    });
            }
        }

        /// <summary>
        /// Áp dụng trailing stop nhiều cấp dựa trên bội số R và TP
        /// </summary>
        public void ApplyMultiLevelTrailing(
            string symbolName,
            double currentPrice,
            double entryPrice,
            double pipSize,
            double? riskPips,
            cAlgo.API.TradeType direction,
            DateTime timestamp,
            double? takeProfitPrice)
        {
            if (!MultiLevelTrailingEnabled)
            {
                return;
            }

            if (pipSize <= 0 || !riskPips.HasValue || riskPips.Value <= 0)
            {
                return;
            }

            const double rTolerance = 1e-9;

            double traveledPips = direction == cAlgo.API.TradeType.Buy
                ? (currentPrice - entryPrice) / pipSize
                : (entryPrice - currentPrice) / pipSize;

            if (traveledPips <= 0)
            {
                return;
            }

            double traveledR = traveledPips / riskPips.Value;
            if (traveledR <= 0)
            {
                return;
            }

            var orderedLevels = TrailingLevels ?? new List<TrailingLevel>();
            double? targetTrigger = null;
            double? targetOffsetR = null;
            string targetMode = null;

            foreach (var level in orderedLevels.OrderBy(l => l.TriggerR))
            {
                if (level.TriggerR <= 0 || level.StopOffsetR <= 0)
                {
                    continue;
                }

                if (LastTrailingTriggerRApplied.HasValue && level.TriggerR <= LastTrailingTriggerRApplied.Value + rTolerance)
                {
                    continue;
                }

                double triggerWithHysteresis = level.TriggerR + TrailingHysteresisR;
                if (traveledR + rTolerance >= triggerWithHysteresis)
                {
                    targetTrigger = level.TriggerR;
                    targetOffsetR = level.StopOffsetR;
                }
            }

            double? targetPrice = null;

            if (targetOffsetR.HasValue)
            {
                double offsetPrice = targetOffsetR.Value * riskPips.Value * pipSize;
                targetPrice = direction == cAlgo.API.TradeType.Buy
                    ? entryPrice + offsetPrice
                    : entryPrice - offsetPrice;
                targetMode = "LEVEL";
            }
            else if (traveledR + rTolerance >= (TrailingDynamicTriggerR + TrailingHysteresisR))
            {
                double dynamicOffsetPrice = TrailingDynamicOffsetR * riskPips.Value * pipSize;
                double referencePrice;
                bool usedTakeProfit = takeProfitPrice.HasValue;

                if (takeProfitPrice.HasValue)
                {
                    referencePrice = takeProfitPrice.Value;
                }
                else
                {
                    referencePrice = currentPrice;
                }

                targetPrice = direction == cAlgo.API.TradeType.Buy
                    ? referencePrice - dynamicOffsetPrice
                    : referencePrice + dynamicOffsetPrice;

                targetTrigger = traveledR;
                targetMode = usedTakeProfit ? "DYNAMIC_TP" : "DYNAMIC_PRICE";
            }

            if (!targetPrice.HasValue)
            {
                var minTrigger = orderedLevels.Count > 0
                    ? orderedLevels.Where(l => l.TriggerR > 0).Select(l => l.TriggerR).DefaultIfEmpty().Min()
                    : (double?)null;

                if (minTrigger.HasValue && traveledR >= minTrigger.Value - TrailingHysteresisR)
                {
                    PipelineLogger.Log(
                        "EXIT",
                        "TRAIL_SKIP",
                        "Trailing threshold reached but no level applied",
                        new
                        {
                            symbol = symbolName,
                            direction = direction.ToString(),
                            entryPrice,
                            currentPrice,
                            traveledR,
                            lastTrigger = LastTrailingTriggerRApplied,
                            minTrigger
                        });
                }
                return;
            }

            bool betterStop = direction == cAlgo.API.TradeType.Buy
                ? (!StopLossPrice.HasValue || targetPrice.Value > StopLossPrice.Value + 1e-10)
                : (!StopLossPrice.HasValue || targetPrice.Value < StopLossPrice.Value - 1e-10);

            if (!betterStop)
            {
                return;
            }

            bool cooldownActive = TrailingCooldownSeconds > 0 && LastTrailingUpdateTime.HasValue &&
                (timestamp - LastTrailingUpdateTime.Value).TotalSeconds < TrailingCooldownSeconds;

            if (cooldownActive)
            {
                double improvement = StopLossPrice.HasValue
                    ? Math.Abs(targetPrice.Value - StopLossPrice.Value) / pipSize
                    : double.MaxValue;

                if (improvement < 0.1) // yêu cầu cải thiện tối thiểu 0.1 pip nếu vẫn trong cooldown
                {
                    return;
                }
            }

            StopLossPrice = targetPrice;
            LastTrailingUpdateTime = timestamp;

            if (targetMode == "LEVEL" && targetTrigger.HasValue)
            {
                LastTrailingTriggerRApplied = targetTrigger;
            }
            else if (targetTrigger.HasValue)
            {
                LastTrailingTriggerRApplied = Math.Max(LastTrailingTriggerRApplied ?? 0, targetTrigger.Value);
            }

            PipelineLogger.Log(
                "EXIT",
                "TRAIL",
                "Trailing stop updated",
                new
                {
                    symbol = symbolName,
                    direction = direction.ToString(),
                    entryPrice,
                    currentPrice,
                    stopLossPrice = StopLossPrice,
                    traveledR,
                    targetTrigger,
                    targetMode,
                    offsetR = targetOffsetR ?? TrailingDynamicOffsetR,
                    takeProfitPrice,
                    dynamicTrigger = TrailingDynamicTriggerR
                });
        }

        /// <summary>
        /// Gán thêm buffer phí dựa trên cấu hình broker để đảm bảo SL về B/E vẫn >= 0 sau khi trừ commission + swap.
        /// </summary>
        public void ApplyBrokerFeeBuffer(
            string symbolName,
            double pipSize,
            double lotSize,
            double pipValuePerLot,
            double tickSize,
            double tickValue,
            double volumeInUnits,
            cAlgo.API.TradeType direction)
        {
            if (pipSize <= 0 || volumeInUnits == 0)
            {
                return;
            }

            if (!BrokerFeeTable.TryGetProfile(symbolName, out var profile))
            {
                return;
            }

            double resolvedLotSize = lotSize > 0 ? lotSize : 100000.0;
            double volumeInLots = Math.Abs(volumeInUnits) / resolvedLotSize;
            if (volumeInLots <= 0)
            {
                return;
            }

            double resolvedPipValuePerLot = pipValuePerLot;
            if (resolvedPipValuePerLot <= 0 && tickSize > 0)
            {
                double pointValue = tickValue / tickSize;
                resolvedPipValuePerLot = pipSize * pointValue * resolvedLotSize;
            }

            if (resolvedPipValuePerLot <= 0)
            {
                return;
            }

            double pipValueUsd = resolvedPipValuePerLot * volumeInLots;
            if (pipValueUsd <= 0)
            {
                return;
            }

            double notionalPerLotUsd = pipSize > 0 ? (resolvedPipValuePerLot / pipSize) : 0;
            double notionalUsd = notionalPerLotUsd * volumeInLots;

            double commissionPips = 0.0;
            if (profile.CommissionPerMillionUsdPerSide > 0 && notionalUsd > 0)
            {
                double commissionUsdRoundTrip = 2.0 * profile.CommissionPerMillionUsdPerSide * (notionalUsd / 1_000_000.0);
                commissionPips = commissionUsdRoundTrip / pipValueUsd;
            }

            double swapCostPips = direction == cAlgo.API.TradeType.Buy
                ? profile.OvernightSwapPipsBuyCost
                : profile.OvernightSwapPipsSellCost;

            if (swapCostPips < 0)
            {
                swapCostPips = 0;
            }

            double requiredBuffer = commissionPips + swapCostPips;

            if (requiredBuffer > BreakevenFeePips)
            {
                BreakevenFeePips = requiredBuffer;

                PipelineLogger.Log(
                    "EXIT",
                    "B/E_FEE",
                    "Applied broker fee buffer",
                    new
                    {
                        symbol = symbolName,
                        commissionPips,
                        swapCostPips,
                        bufferPips = requiredBuffer,
                        volumeInLots
                    });
            }
        }
    }

    public class TrailingLevel
    {
        public double TriggerR { get; set; }
        public double StopOffsetR { get; set; }
    }
}
