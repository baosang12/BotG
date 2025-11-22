using System.Collections.Generic;
using Xunit;

namespace BotG.Tests.Strategies
{
    public class BreakoutStrategyTestCases
    {
        public static TheoryData<BreakoutScenario> ValidBreakouts => new()
        {
            new BreakoutScenario(
                "London_Bias_Long",
                AtrRatio: 0.52,
                VolumeMultiplier: 2.0,
                TrendAligned: true,
                MultiTimeframeAligned: true,
                RetestPercent: 0.2,
                BarsElapsed: 1,
                ExpectSignal: true),
            new BreakoutScenario(
                "Overlap_Continuation",
                AtrRatio: 0.61,
                VolumeMultiplier: 2.4,
                TrendAligned: true,
                MultiTimeframeAligned: true,
                RetestPercent: 0.1,
                BarsElapsed: 2,
                ExpectSignal: true)
        };

        public static TheoryData<BreakoutScenario> FalseBreakouts => new()
        {
            new BreakoutScenario(
                "WeakVolume",
                AtrRatio: 0.4,
                VolumeMultiplier: 1.2,
                TrendAligned: true,
                MultiTimeframeAligned: true,
                RetestPercent: 0.2,
                BarsElapsed: 1,
                ExpectSignal: false),
            new BreakoutScenario(
                "RetestViolation",
                AtrRatio: 0.55,
                VolumeMultiplier: 2.2,
                TrendAligned: true,
                MultiTimeframeAligned: true,
                RetestPercent: 0.6,
                BarsElapsed: 1,
                ExpectSignal: false),
            new BreakoutScenario(
                "TrendMismatch",
                AtrRatio: 0.5,
                VolumeMultiplier: 2.0,
                TrendAligned: false,
                MultiTimeframeAligned: true,
                RetestPercent: 0.2,
                BarsElapsed: 1,
                ExpectSignal: false),
            new BreakoutScenario(
                "LateBreakout",
                AtrRatio: 0.48,
                VolumeMultiplier: 1.9,
                TrendAligned: true,
                MultiTimeframeAligned: false,
                RetestPercent: 0.2,
                BarsElapsed: 3,
                ExpectSignal: false)
        };

        [Theory]
        [MemberData(nameof(ValidBreakouts))]
        public void ValidScenariosMeetSpec(BreakoutScenario scenario)
        {
            Assert.True(scenario.ExpectSignal);
            Assert.True(scenario.AtrRatio >= 0.35);
            Assert.True(scenario.VolumeMultiplier >= 1.8);
            Assert.True(scenario.TrendAligned);
            Assert.True(scenario.MultiTimeframeAligned);
            Assert.InRange(scenario.RetestPercent, 0, 0.5);
            Assert.InRange(scenario.BarsElapsed, 1, 2);
        }

        [Theory]
        [MemberData(nameof(FalseBreakouts))]
        public void InvalidScenariosCaptureFailureModes(BreakoutScenario scenario)
        {
            Assert.False(scenario.ExpectSignal);
            Assert.True(scenario.AtrRatio >= 0); // sanity
            Assert.True(scenario.VolumeMultiplier >= 0);

            var failureReasons = new List<string>();
            if (scenario.VolumeMultiplier < 1.8) failureReasons.Add("volume");
            if (scenario.AtrRatio < 0.35) failureReasons.Add("strength");
            if (!scenario.TrendAligned) failureReasons.Add("trend");
            if (!scenario.MultiTimeframeAligned) failureReasons.Add("alignment");
            if (scenario.RetestPercent > 0.5) failureReasons.Add("retest");
            if (scenario.BarsElapsed > 2) failureReasons.Add("timing");

            Assert.NotEmpty(failureReasons);
        }

        public record BreakoutScenario(
            string Name,
            double AtrRatio,
            double VolumeMultiplier,
            bool TrendAligned,
            bool MultiTimeframeAligned,
            double RetestPercent,
            int BarsElapsed,
            bool ExpectSignal);
    }
}
