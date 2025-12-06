using System.Collections.Generic;

namespace BotG.Config;

public sealed class PreprocessorRuntimeConfig
{
    public bool Enabled { get; set; } = false;
    public bool UseForMultiTimeframe { get; set; } = false;
    public string[] Timeframes { get; set; } = new[] { "M1", "M5" };
    public int RecentTickCapacity { get; set; } = 2048;
    public int BarHistoryCapacity { get; set; } = 500;
    public int SnapshotDebounceMs { get; set; } = 25;
    public IList<PreprocessorIndicatorConfig> Indicators { get; set; } = new List<PreprocessorIndicatorConfig>
    {
        new() { Type = "SMA", Timeframe = "M1", Period = 14 },
        new() { Type = "RSI", Timeframe = "M5", Period = 14 },
        new() { Type = "ATR", Timeframe = "H1", Period = 14 }
    };
}

public sealed class PreprocessorIndicatorConfig
{
    public string Type { get; set; } = "SMA";
    public string Timeframe { get; set; } = "M1";
    public int Period { get; set; } = 14;
}
