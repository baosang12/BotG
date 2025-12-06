using System;
using System.Collections.Generic;

namespace Config
{
    // Minimal configuration shims to satisfy references
    public class PAConfig
    {
        public string[]? EnabledSteps { get; set; }
        public string CurrentTf { get; set; } = "H1";
        public IList<string> HigherTimeframes { get; set; } = new List<string>();
        public int MinConfirmCount { get; set; } = 1;
    }

    public class BotConfig
    {
        public bool ShowBreakerBlocks { get; set; }
        public bool ShowMitigationBlocks { get; set; }
        public bool ShowTrendlineLiquidity { get; set; }
        public bool ShowVolumeImbalance { get; set; }
        public bool ShowReversalBlocks { get; set; }
        public bool ShowLiquiditySweeps { get; set; }
        public bool ShowInternalOrderBlocks { get; set; }
        public bool ShowFairValueGaps { get; set; }

        // Lookback/threshold parameters required by detectors
        public int BreakerBlockLookback { get; set; } = 50;
        public int MitigationBlockLookback { get; set; } = 50;
        public int ReversalBlockLookback { get; set; } = 50;
        public int LiquiditySweepLookback { get; set; } = 50;
        public int TrendlineLiquidityLookback { get; set; } = 50;
        public int VolumeImbalanceLookback { get; set; } = 50;

        public double VolumeImbalanceThreshold { get; set; } = 0.65; // 65% of volume on one side

        // FVG options
        public bool FairValueGapsAutoThreshold { get; set; } = true;
        public double FairValueGapsExtend { get; set; } = 0.0;

        // Internal OB options
        public int InternalOrderBlocksSize { get; set; } = 5;
        public string OrderBlockFilter { get; set; } = "Any";
    }
}
