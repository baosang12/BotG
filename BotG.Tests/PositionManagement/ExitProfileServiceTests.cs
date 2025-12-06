using System;
using System.Collections.Generic;
using BotG.PositionManagement;
using cAlgo.API;
using Xunit;

namespace BotG.Tests.PositionManagement
{
    public class ExitProfileServiceTests
    {
        [Fact]
        public void CreateParameters_UsesStrategyOverride()
        {
            var config = new ExitProfilesConfig
            {
                DefaultProfile = "default",
                Profiles = new Dictionary<string, ExitProfileDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new ExitProfileDefinition
                    {
                        StopLossPips = 10,
                        TakeProfitPips = 20,
                        InitialRiskPips = 10,
                        MultiLevelTrailingEnabled = true
                    },
                    ["breakout"] = new ExitProfileDefinition
                    {
                        StopLossPips = 25,
                        InitialRiskPips = 25,
                        RiskRewardRatio = 2.0,
                        TrailingLevels = new List<TrailingLevel>
                        {
                            new TrailingLevel { TriggerR = 1.0, StopOffsetR = 0.5 }
                        },
                        TrailingDynamicTriggerR = 1.5,
                        TrailingDynamicOffsetR = 0.4
                    }
                },
                StrategyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BreakoutStrategy"] = "breakout"
                }
            };

            var service = new ExitProfileService(config);

            var exitParams = service.CreateParameters(
                "BreakoutStrategy",
                "EURUSD",
                1.1000,
                TradeType.Buy,
                0.0001,
                10000,
                out var profileName);

            Assert.Equal("breakout", profileName, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(1.0975, exitParams.StopLossPrice!.Value, 5);
            Assert.Equal(1.1050, exitParams.TakeProfitPrice!.Value, 5);
            Assert.Equal(25, exitParams.InitialRiskPips);
            Assert.NotNull(exitParams.TrailingLevels);
            Assert.Single(exitParams.TrailingLevels);
        }

        [Fact]
        public void CreateParameters_UsesSymbolOverrideWhenStrategyMissing()
        {
            var config = new ExitProfilesConfig
            {
                DefaultProfile = "default",
                Profiles = new Dictionary<string, ExitProfileDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new ExitProfileDefinition { StopLossPips = 15, TakeProfitPips = 30, InitialRiskPips = 15 },
                    ["xau_wide"] = new ExitProfileDefinition { StopLossPips = 300, TakeProfitPips = 450, InitialRiskPips = 300 }
                },
                SymbolOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["XAUUSD"] = "xau_wide"
                }
            };

            var service = new ExitProfileService(config);

            var exitParams = service.CreateParameters(
                null,
                "XAUUSD",
                1900,
                TradeType.Sell,
                0.01,
                10000,
                out var profileName);

            Assert.Equal("xau_wide", profileName, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(1903.0, exitParams.StopLossPrice!.Value, 5); // 300 pips on sell => entry + (300 * 0.01)
            Assert.Equal(1895.5, exitParams.TakeProfitPrice!.Value, 5);
        }
    }

    public class PositionLabelHelperTests
    {
        [Fact]
        public void BuildAndParseStrategyLabel_RoundTrips()
        {
            var label = PositionLabelHelper.BuildStrategyLabel("Breakout Strategy #1!");
            Assert.StartsWith(PositionLabelHelper.StrategyLabelPrefix, label);

            var parsed = PositionLabelHelper.TryParseStrategyName(label);
            Assert.Equal("Breakout_Strategy_1", parsed);
        }

        [Fact]
        public void TryParseStrategyName_IgnoresUnknownPrefixes()
        {
            var parsed = PositionLabelHelper.TryParseStrategyName("BotG_SMOKE");
            Assert.Null(parsed);
        }
    }
}
