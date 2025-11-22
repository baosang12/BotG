using System;
using System.Collections.Generic;

namespace BotG.PositionManagement
{
    /// <summary>
    /// Root configuration object describing exit profiles and routing overrides.
    /// </summary>
    public sealed class ExitProfilesConfig
    {
        public string DefaultProfile { get; set; } = "scalping_conservative";

        public Dictionary<string, ExitProfileDefinition> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> StrategyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> SymbolOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public void EnsureDefaults()
        {
            if (string.IsNullOrWhiteSpace(DefaultProfile))
            {
                DefaultProfile = "scalping_conservative";
            }

            Profiles ??= new Dictionary<string, ExitProfileDefinition>(StringComparer.OrdinalIgnoreCase);
            StrategyOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SymbolOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Declarative parameters that can be mapped into <see cref="ExitParameters"/> instances.
    /// </summary>
    public sealed class ExitProfileDefinition
    {
        public double? StopLossPips { get; set; }
        public double? TakeProfitPips { get; set; }
        public double? TrailingStopPips { get; set; }
        public int? MaxBarsHold { get; set; }
        public double? RiskRewardRatio { get; set; }
        public double? InitialRiskPips { get; set; }
        public double? BreakevenTriggerRMultiple { get; set; }
        public double? BreakevenTriggerPercent { get; set; }
        public double? BreakevenFeePips { get; set; }
        public bool? MultiLevelTrailingEnabled { get; set; }
        public List<TrailingLevel>? TrailingLevels { get; set; }
        public double? TrailingDynamicTriggerR { get; set; }
        public double? TrailingDynamicOffsetR { get; set; }
        public double? TrailingHysteresisR { get; set; }
        public double? TrailingCooldownSeconds { get; set; }
    }
}
