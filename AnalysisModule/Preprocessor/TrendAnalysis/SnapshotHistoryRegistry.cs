using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.TrendAnalysis
{
    /// <summary>
    /// Tracks multi-timeframe bar history per snapshot so layers can access rich context when available.
    /// </summary>
    public static class SnapshotHistoryRegistry
    {
        private static readonly ConditionalWeakTable<PreprocessorSnapshot, IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>>> HistoryTable = new();
        private static readonly object Sync = new();

        public static void Attach(PreprocessorSnapshot snapshot, IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> history)
        {
            if (snapshot == null || history == null || history.Count == 0)
            {
                return;
            }

            lock (Sync)
            {
                HistoryTable.Remove(snapshot);
                HistoryTable.Add(snapshot, history);
            }
        }

        public static IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>>? TryGet(PreprocessorSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            lock (Sync)
            {
                return HistoryTable.TryGetValue(snapshot, out var history)
                    ? history
                    : null;
            }
        }
    }
}
