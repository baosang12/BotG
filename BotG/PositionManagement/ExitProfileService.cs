using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;

namespace BotG.PositionManagement
{
    /// <summary>
    /// Builds <see cref="ExitParameters"/> from declarative profile definitions loaded at runtime.
    /// </summary>
    public sealed class ExitProfileService
    {
        private readonly Dictionary<string, ExitProfileDefinition> _profiles;
        private readonly Dictionary<string, string> _strategyOverrides;
        private readonly Dictionary<string, string> _symbolOverrides;
        private readonly string _defaultProfile;

        public ExitProfileService(ExitProfilesConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.EnsureDefaults();

            _profiles = new Dictionary<string, ExitProfileDefinition>(config.Profiles, StringComparer.OrdinalIgnoreCase);
            _strategyOverrides = new Dictionary<string, string>(config.StrategyOverrides, StringComparer.OrdinalIgnoreCase);
            _symbolOverrides = new Dictionary<string, string>(config.SymbolOverrides, StringComparer.OrdinalIgnoreCase);
            _defaultProfile = string.IsNullOrWhiteSpace(config.DefaultProfile)
                ? _profiles.Keys.FirstOrDefault() ?? "scalping_conservative"
                : config.DefaultProfile.Trim();
        }

        public int ProfileCount => _profiles.Count;

        public string DefaultProfileName => _defaultProfile;

        public string ResolveProfileName(string? strategyName, string? symbolName)
        {
            if (!string.IsNullOrWhiteSpace(strategyName) &&
                _strategyOverrides.TryGetValue(strategyName, out var strategyProfile) &&
                _profiles.ContainsKey(strategyProfile))
            {
                return strategyProfile;
            }

            if (!string.IsNullOrWhiteSpace(symbolName) &&
                _symbolOverrides.TryGetValue(symbolName, out var symbolProfile) &&
                _profiles.ContainsKey(symbolProfile))
            {
                return symbolProfile;
            }

            if (_profiles.ContainsKey(_defaultProfile))
            {
                return _defaultProfile;
            }

            return _profiles.Keys.FirstOrDefault() ?? _defaultProfile;
        }

        public ExitParameters CreateParameters(
            string? strategyName,
            string symbol,
            double entryPrice,
            TradeType direction,
            double pipSize,
            double accountBalance,
            out string resolvedProfile)
        {
            var profileName = ResolveProfileName(strategyName, symbol);
            resolvedProfile = profileName;

            if (!_profiles.TryGetValue(profileName, out var definition))
            {
                return ExitParameters.CreateDefault(symbol, entryPrice, direction, accountBalance);
            }

            if (!HasRiskConfiguration(definition))
            {
                return ExitParameters.CreateDefault(symbol, entryPrice, direction, accountBalance);
            }

            return BuildParameters(definition, symbol, entryPrice, direction, pipSize);
        }

        private static bool HasRiskConfiguration(ExitProfileDefinition definition)
        {
            double? stopLoss = definition.StopLossPips;
            double? risk = definition.InitialRiskPips;
            return (stopLoss.HasValue && stopLoss.Value > 0) || (risk.HasValue && risk.Value > 0);
        }

        private static ExitParameters BuildParameters(
            ExitProfileDefinition definition,
            string symbol,
            double entryPrice,
            TradeType direction,
            double pipSize)
        {
            double resolvedPipSize = pipSize > 0 ? pipSize : DefaultPipSize(symbol);
            double? stopLossPips = definition.StopLossPips;
            double? riskPips = definition.InitialRiskPips ?? stopLossPips;

            var exitParams = new ExitParameters();

            if (stopLossPips.HasValue && stopLossPips.Value > 0)
            {
                double distance = stopLossPips.Value * resolvedPipSize;
                exitParams.StopLossPrice = direction == TradeType.Buy
                    ? entryPrice - distance
                    : entryPrice + distance;
            }

            double? takeProfitPips = definition.TakeProfitPips;
            if (!takeProfitPips.HasValue && definition.RiskRewardRatio.HasValue && riskPips.HasValue)
            {
                takeProfitPips = riskPips.Value * definition.RiskRewardRatio.Value;
            }

            if (takeProfitPips.HasValue && takeProfitPips.Value > 0)
            {
                double distance = takeProfitPips.Value * resolvedPipSize;
                exitParams.TakeProfitPrice = direction == TradeType.Buy
                    ? entryPrice + distance
                    : entryPrice - distance;
            }

            if (definition.TrailingStopPips.HasValue && definition.TrailingStopPips.Value > 0)
            {
                exitParams.TrailingStopDistance = definition.TrailingStopPips.Value * resolvedPipSize;
            }

            exitParams.MaxBarsHold = definition.MaxBarsHold.HasValue && definition.MaxBarsHold.Value > 0
                ? definition.MaxBarsHold.Value
                : exitParams.MaxBarsHold;

            exitParams.RiskRewardRatio = definition.RiskRewardRatio ?? exitParams.RiskRewardRatio;
            exitParams.InitialRiskPips = riskPips ?? exitParams.InitialRiskPips;
            exitParams.BreakevenTriggerRMultiple = definition.BreakevenTriggerRMultiple ?? exitParams.BreakevenTriggerRMultiple;
            exitParams.BreakevenTriggerPercent = definition.BreakevenTriggerPercent ?? exitParams.BreakevenTriggerPercent;
            exitParams.BreakevenFeePips = definition.BreakevenFeePips.HasValue && definition.BreakevenFeePips.Value > 0
                ? definition.BreakevenFeePips.Value
                : exitParams.BreakevenFeePips;
            exitParams.MultiLevelTrailingEnabled = definition.MultiLevelTrailingEnabled ?? exitParams.MultiLevelTrailingEnabled;
            exitParams.TrailingDynamicTriggerR = definition.TrailingDynamicTriggerR ?? exitParams.TrailingDynamicTriggerR;
            exitParams.TrailingDynamicOffsetR = definition.TrailingDynamicOffsetR ?? exitParams.TrailingDynamicOffsetR;
            exitParams.TrailingHysteresisR = definition.TrailingHysteresisR ?? exitParams.TrailingHysteresisR;
            exitParams.TrailingCooldownSeconds = definition.TrailingCooldownSeconds ?? exitParams.TrailingCooldownSeconds;

            if (definition.TrailingLevels != null && definition.TrailingLevels.Count > 0)
            {
                exitParams.TrailingLevels = definition.TrailingLevels
                    .Where(level => level != null && level.TriggerR > 0 && level.StopOffsetR > 0)
                    .Select(level => new TrailingLevel
                    {
                        TriggerR = level.TriggerR,
                        StopOffsetR = level.StopOffsetR
                    })
                    .ToList();
            }

            return exitParams;
        }

        private static double DefaultPipSize(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                return 0.0001;
            }

            return symbol.IndexOf("JPY", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.01 : 0.0001;
        }
    }
}
