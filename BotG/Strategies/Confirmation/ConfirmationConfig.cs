using System;
using System.Collections.Generic;
using System.Linq;

namespace Strategies.Confirmation
{
    public sealed class ConfirmationConfig
    {
        private static readonly IReadOnlyDictionary<string, double> DefaultWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["TrendAlignment"] = 0.35,
            ["KeyLevelConfirmation"] = 0.30,
            ["VolumeConfirmation"] = 0.20,
            ["MomentumConfirmation"] = 0.15
        };

        public ConfirmationConfig()
        {
            ConfirmationWeights = new Dictionary<string, double>(DefaultWeights, StringComparer.OrdinalIgnoreCase);
        }

        public double MinimumConfirmationThreshold { get; set; } = 0.7;
        public Dictionary<string, double> ConfirmationWeights { get; set; }
            = new Dictionary<string, double>(DefaultWeights, StringComparer.OrdinalIgnoreCase);
        public int RequiredTimeframeAlignment { get; set; } = 2;
        public bool EnableMultiTimeframeConfirmation { get; set; } = true;

        public int TrendFastEma { get; set; } = 21;
        public int TrendSlowEma { get; set; } = 55;
        public double TrendAlignmentTolerance { get; set; } = 0.0002;

        public int KeyLevelLookback { get; set; } = 60;
        public double KeyLevelTolerance { get; set; } = 0.0010;
        public int PivotRadius { get; set; } = 3;

        public int VolumeSmaPeriodH1 { get; set; } = 20;
        public int VolumeSmaPeriodM15 { get; set; } = 20;
        public double VolumeSpikeMultiplierH1 { get; set; } = 1.8;
        public double VolumeSpikeMultiplierM15 { get; set; } = 2.0;
        public double VolumeTrendMinimumSlope { get; set; } = 0.05;

        public int MomentumRsiPeriod { get; set; } = 14;
        public int MomentumAtrPeriod { get; set; } = 14;
        public int MomentumLookbackBars { get; set; } = 5;
        public double MomentumPriceSlopeThreshold { get; set; } = 0.0005;

        public ConfirmationConfig Clone()
        {
            return new ConfirmationConfig
            {
                MinimumConfirmationThreshold = MinimumConfirmationThreshold,
                ConfirmationWeights = new Dictionary<string, double>(ConfirmationWeights, StringComparer.OrdinalIgnoreCase),
                RequiredTimeframeAlignment = RequiredTimeframeAlignment,
                EnableMultiTimeframeConfirmation = EnableMultiTimeframeConfirmation,
                TrendFastEma = TrendFastEma,
                TrendSlowEma = TrendSlowEma,
                TrendAlignmentTolerance = TrendAlignmentTolerance,
                KeyLevelLookback = KeyLevelLookback,
                KeyLevelTolerance = KeyLevelTolerance,
                PivotRadius = PivotRadius,
                VolumeSmaPeriodH1 = VolumeSmaPeriodH1,
                VolumeSmaPeriodM15 = VolumeSmaPeriodM15,
                VolumeSpikeMultiplierH1 = VolumeSpikeMultiplierH1,
                VolumeSpikeMultiplierM15 = VolumeSpikeMultiplierM15,
                VolumeTrendMinimumSlope = VolumeTrendMinimumSlope,
                MomentumRsiPeriod = MomentumRsiPeriod,
                MomentumAtrPeriod = MomentumAtrPeriod,
                MomentumLookbackBars = MomentumLookbackBars,
                MomentumPriceSlopeThreshold = MomentumPriceSlopeThreshold
            };
        }

        public void Normalize()
        {
            MinimumConfirmationThreshold = Math.Clamp(MinimumConfirmationThreshold, 0.0, 1.0);
            RequiredTimeframeAlignment = Math.Max(1, RequiredTimeframeAlignment);
            TrendFastEma = Math.Max(2, TrendFastEma);
            TrendSlowEma = Math.Max(TrendFastEma + 1, TrendSlowEma);
            TrendAlignmentTolerance = Math.Max(0.0, TrendAlignmentTolerance);
            KeyLevelLookback = Math.Max(10, KeyLevelLookback);
            KeyLevelTolerance = Math.Max(0.0001, KeyLevelTolerance);
            PivotRadius = Math.Clamp(PivotRadius, 2, 10);
            VolumeSmaPeriodH1 = Math.Max(5, VolumeSmaPeriodH1);
            VolumeSmaPeriodM15 = Math.Max(5, VolumeSmaPeriodM15);
            VolumeSpikeMultiplierH1 = Math.Max(0.5, VolumeSpikeMultiplierH1);
            VolumeSpikeMultiplierM15 = Math.Max(0.5, VolumeSpikeMultiplierM15);
            VolumeTrendMinimumSlope = Math.Max(0.0, VolumeTrendMinimumSlope);
            MomentumRsiPeriod = Math.Max(5, MomentumRsiPeriod);
            MomentumAtrPeriod = Math.Max(5, MomentumAtrPeriod);
            MomentumLookbackBars = Math.Max(2, MomentumLookbackBars);
            MomentumPriceSlopeThreshold = Math.Max(0.00005, MomentumPriceSlopeThreshold);

            EnsureWeights();
        }

        private void EnsureWeights()
        {
            if (ConfirmationWeights == null || ConfirmationWeights.Count == 0)
            {
                ConfirmationWeights = new Dictionary<string, double>(DefaultWeights, StringComparer.OrdinalIgnoreCase);
                return;
            }

            foreach (var key in DefaultWeights.Keys)
            {
                if (!ConfirmationWeights.ContainsKey(key))
                {
                    ConfirmationWeights[key] = DefaultWeights[key];
                }
            }

            var total = ConfirmationWeights.Where(kvp => DefaultWeights.ContainsKey(kvp.Key)).Sum(kvp => Math.Max(kvp.Value, 0));
            if (total <= 0)
            {
                ConfirmationWeights = new Dictionary<string, double>(DefaultWeights, StringComparer.OrdinalIgnoreCase);
                return;
            }

            var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in ConfirmationWeights)
            {
                if (!DefaultWeights.ContainsKey(kvp.Key))
                {
                    continue;
                }

                normalized[kvp.Key] = Math.Max(kvp.Value, 0) / total;
            }

            ConfirmationWeights = normalized;
        }
    }
}
