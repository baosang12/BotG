using System;

namespace Analysis.Realtime
{
    /// <summary>
    /// Contract for realtime analyzers that transform raw bars into normalized feature sets.
    /// </summary>
    public interface IRealtimeAnalyzer
    {
        string Name { get; }

        RealtimeFeatureResult Analyze(RealtimeFeatureContext context);
    }
}
