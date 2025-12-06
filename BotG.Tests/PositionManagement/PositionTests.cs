using System;
using System.Collections.Generic;
using Xunit;
using cAlgo.API;
using BotG.PositionManagement;
using Position = BotG.PositionManagement.Position;

namespace BotG.Tests.PositionManagement
{
    public class PositionTests
    {
        [Fact]
        public void UpdateUnrealizedPnL_Buy_ProfitableMove_PositivePnL()
        {
            // Arrange
            var position = new Position
            {
                Id = "test1",
                Symbol = "EURUSD",
                Direction = TradeType.Buy,
                EntryPrice = 1.1000,
                VolumeInUnits = 1000,
                OpenTime = DateTime.UtcNow,
                Status = PositionStatus.Open
            };

            double pointValue = 10.0; // $10 per point for 1000 units
            double currentPrice = 1.1050; // +50 pips profit

            // Act
            position.UpdateUnrealizedPnL(currentPrice, pointValue);

            // Assert
            Assert.Equal(currentPrice, position.CurrentPrice);
            Assert.Equal(50.0, position.UnrealizedPnL, 5); // (1.1050 - 1.1000) * 1000 * 10 = 50
        }

        [Fact]
        public void UpdateUnrealizedPnL_Sell_ProfitableMove_PositivePnL()
        {
            // Arrange
            var position = new Position
            {
                Id = "test2",
                Symbol = "EURUSD",
                Direction = TradeType.Sell,
                EntryPrice = 1.1050,
                VolumeInUnits = 1000,
                OpenTime = DateTime.UtcNow,
                Status = PositionStatus.Open
            };

            double pointValue = 10.0;
            double currentPrice = 1.1000; // -50 pips (profitable for sell)

            // Act
            position.UpdateUnrealizedPnL(currentPrice, pointValue);

            // Assert
            Assert.Equal(50.0, position.UnrealizedPnL, 5); // (1.1050 - 1.1000) * 1000 * 10 = 50
        }

        [Fact]
        public void Close_Buy_Position_RealizedPnLCalculated()
        {
            // Arrange
            var position = new Position
            {
                Id = "test3",
                Symbol = "BTCUSD",
                Direction = TradeType.Buy,
                EntryPrice = 50000,
                VolumeInUnits = 1000,
                OpenTime = DateTime.UtcNow,
                Status = PositionStatus.Open,
                PointValue = 0.01 // BTC point value
            };

            double closePrice = 51000;
            DateTime closeTime = DateTime.UtcNow.AddHours(1);

            // Act
            position.Close(closePrice, closeTime);

            // Assert
            Assert.Equal(PositionStatus.Closed, position.Status);
            Assert.Equal(closePrice, position.ClosePrice);
            Assert.Equal(closeTime, position.CloseTime);
            Assert.Equal(10000.0, position.RealizedPnL); // (51000 - 50000) * 1000 * 0.01 = 10,000
            Assert.Equal(0, position.UnrealizedPnL); // Reset to 0 khi close
        }

        [Fact]
        public void BarsHeld_CorrectCalculation()
        {
            // Arrange
            var openTime = new DateTime(2025, 11, 10, 10, 0, 0, DateTimeKind.Utc);
            var position = new Position
            {
                Id = "test4",
                Symbol = "EURUSD",
                Direction = TradeType.Buy,
                EntryPrice = 1.1000,
                VolumeInUnits = 1000,
                OpenTime = openTime,
                Status = PositionStatus.Open
            };

            // Act
            var currentTime = openTime.AddHours(3); // 3 hours later
            int bars = position.BarsHeld(currentTime, 3600); // 1 bar = 3600s (H1)

            // Assert
            Assert.Equal(3, bars); // 3 hours = 3 bars on H1
        }
    }

    public class ExitParametersTests
    {
        [Fact]
        public void UpdateTrailingStop_Buy_PriceMovesUp_TrailingUpdates()
        {
            // Arrange
            var exitParams = new ExitParameters
            {
                TrailingStopDistance = 0.0020 // 20 pips
            };

            double currentPrice1 = 1.1050; // +50 pips
            double currentPrice2 = 1.1100; // +100 pips

            // Act
            exitParams.UpdateTrailingStop(currentPrice1, TradeType.Buy);
            double trailing1 = exitParams.TrailingStopPrice.Value;

            exitParams.UpdateTrailingStop(currentPrice2, TradeType.Buy);
            double trailing2 = exitParams.TrailingStopPrice.Value;

            // Assert
            Assert.Equal(1.1030, trailing1, 4); // 1.1050 - 0.0020 = 1.1030
            Assert.Equal(1.1080, trailing2, 4); // 1.1100 - 0.0020 = 1.1080 (updated)
        }

        [Fact]
        public void CheckBreakeven_ProfitReachesTrigger_BreakevenActivated()
        {
            // Arrange
            var exitParams = new ExitParameters
            {
                StopLossPrice = 1.0950, // Original SL
                BreakevenTriggerPercent = 0.005 // 0.5% profit trigger
            };

            double entryPrice = 1.1000;
            double currentPrice = 1.1060; // 0.54% profit

            // Act
            exitParams.CheckBreakeven(
                "EURUSD",
                currentPrice,
                entryPrice,
                0.0001,
                0,
                TradeType.Buy);

            // Assert
            Assert.True(exitParams.BreakevenActivated);
            Assert.Equal(entryPrice, exitParams.StopLossPrice); // SL moved to breakeven
        }

        [Fact]
        public void CheckBreakeven_AppliesFeeBuffer_WhenConfigured()
        {
            var exitParams = new ExitParameters
            {
                StopLossPrice = 1.0950,
                InitialRiskPips = 50,
                BreakevenFeePips = 1.5 // ensure SL moves past entry to cover fees
            };

            double entryPrice = 1.1000;
            double currentPrice = 1.1060; // > 0.75R

            exitParams.CheckBreakeven(
                "EURUSD",
                currentPrice,
                entryPrice,
                pipSize: 0.0001,
                minFeeBufferPips: 0,
                direction: TradeType.Buy);

            double expectedStop = entryPrice + (1.5 * 0.0001);
            Assert.True(exitParams.BreakevenActivated);
            Assert.Equal(expectedStop, exitParams.StopLossPrice!.Value, 5);
        }

        [Fact]
        public void ApplyMultiLevelTrailing_HitsFirstLevel_MovesStopToHalfR()
        {
            var exitParams = new ExitParameters
            {
                InitialRiskPips = 50,
                TrailingLevels = new List<TrailingLevel>
                {
                    new TrailingLevel { TriggerR = 1.0, StopOffsetR = 0.5 }
                },
                TakeProfitPrice = 1.1100
            };

            exitParams.TrailingHysteresisR = 0;
            exitParams.TrailingCooldownSeconds = 0;

            Assert.Single(exitParams.TrailingLevels);

            double entryPrice = 1.1000;
            double pipSize = 0.0001;
            double currentPrice = entryPrice + (50 * pipSize); // 1R traveled

            exitParams.ApplyMultiLevelTrailing(
                "EURUSD",
                currentPrice,
                entryPrice,
                pipSize,
                riskPips: 50,
                direction: TradeType.Buy,
                timestamp: DateTime.UtcNow,
                takeProfitPrice: exitParams.TakeProfitPrice);

            double expectedStop = entryPrice + (25 * pipSize); // 0.5R
            Assert.True(exitParams.StopLossPrice.HasValue, "StopLoss không được cập nhật cho mốc 1R");
            Assert.True(exitParams.LastTrailingTriggerRApplied.HasValue, "Trigger R không được ghi nhận cho mốc 1R");
            Assert.Equal(expectedStop, exitParams.StopLossPrice!.Value, 5);
        }

        [Fact]
        public void ApplyMultiLevelTrailing_HitsHigherLevel_MovesStopForward()
        {
            var exitParams = new ExitParameters
            {
                InitialRiskPips = 50,
                TrailingLevels = new List<TrailingLevel>
                {
                    new TrailingLevel { TriggerR = 1.0, StopOffsetR = 0.5 },
                    new TrailingLevel { TriggerR = 1.5, StopOffsetR = 1.0 }
                },
                TakeProfitPrice = 1.1150
            };

            exitParams.TrailingHysteresisR = 0;
            exitParams.TrailingCooldownSeconds = 0;

            double entryPrice = 1.1000;
            double pipSize = 0.0001;
            var now = DateTime.UtcNow;

            // Hit first level
            double priceAt1R = entryPrice + (50 * pipSize);
            exitParams.ApplyMultiLevelTrailing("EURUSD", priceAt1R, entryPrice, pipSize, 50, TradeType.Buy, now, exitParams.TakeProfitPrice);

            // Hit second level later
            double priceAt15R = entryPrice + (75 * pipSize);
            exitParams.ApplyMultiLevelTrailing("EURUSD", priceAt15R, entryPrice, pipSize, 50, TradeType.Buy, now.AddSeconds(2), exitParams.TakeProfitPrice);

            double expectedStop = entryPrice + (50 * pipSize); // 1R offset
            Assert.Equal(expectedStop, exitParams.StopLossPrice!.Value, 5);
        }

        [Fact]
        public void ApplyMultiLevelTrailing_AboveTwoR_TrailsRelativeToTp()
        {
            var exitParams = new ExitParameters
            {
                InitialRiskPips = 50,
                TakeProfitPrice = 1.1200 // 2R target
            };

            exitParams.TrailingHysteresisR = 0;
            exitParams.TrailingCooldownSeconds = 0;

            double entryPrice = 1.1000;
            double pipSize = 0.0001;

            // Price at 2.2R
            double currentPrice = entryPrice + (110 * pipSize);

            exitParams.ApplyMultiLevelTrailing(
                "EURUSD",
                currentPrice,
                entryPrice,
                pipSize,
                riskPips: 50,
                direction: TradeType.Buy,
                timestamp: DateTime.UtcNow,
                takeProfitPrice: exitParams.TakeProfitPrice);

            // SL = TP - 0.5R => entry + 100pip - 25pip = entry + 75pip
            double expectedStop = entryPrice + (75 * pipSize);
            Assert.Equal(expectedStop, exitParams.StopLossPrice!.Value, 5);
        }

        [Fact]
        public void ApplyBrokerFeeBuffer_EurUsd_CommissionAndSwapAdded()
        {
            var exitParams = new ExitParameters();

            exitParams.ApplyBrokerFeeBuffer(
                symbolName: "EURUSD",
                pipSize: 0.0001,
                lotSize: 100000,
                pipValuePerLot: 10, // $10 per pip per standard lot
                tickSize: 0.00001,
                tickValue: 0.00001,
                volumeInUnits: 100000,
                direction: TradeType.Buy);

            Assert.True(exitParams.BreakevenFeePips > 0);
            Assert.Equal(1.53, exitParams.BreakevenFeePips, 2); // 0.6 commission + 0.93 swap
        }

        [Fact]
        public void CreateDefault_ValidExitParameters()
        {
            // Act
            var exitParams = ExitParameters.CreateDefault("EURUSD", 1.1000, TradeType.Buy, 10000.0);

            // Assert
            Assert.NotNull(exitParams.StopLossPrice);
            Assert.NotNull(exitParams.TakeProfitPrice);
            Assert.Equal(50, exitParams.MaxBarsHold); // default from CreateDefault
            Assert.Equal(2.0, exitParams.RiskRewardRatio); // 2:1 R:R
        }
    }
}
