using System;
using System.Collections.Generic;
using System.Linq;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.TrendAnalysis
{
    /// <summary>
    /// Cung cấp API thống nhất để đọc dữ liệu bars/indicator từ PreprocessorSnapshot.
    /// </summary>
    public sealed class SnapshotDataAccessor
    {
        private static readonly IReadOnlyList<Bar> EmptyBars = Array.Empty<Bar>();
        private readonly PreprocessorSnapshot _snapshot;
        private readonly IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> _barHistory;
        private readonly IReadOnlyDictionary<string, double> _indicators;
        private readonly Dictionary<(TimeFrame TimeFrame, int Count), IReadOnlyList<Bar>> _barCache = new();

        public SnapshotDataAccessor(
            PreprocessorSnapshot snapshot,
            IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>>? barHistory = null)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _barHistory = barHistory ?? new Dictionary<TimeFrame, IReadOnlyList<Bar>>();
            _indicators = snapshot.Indicators ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Lấy danh sách bar mới nhất theo timeframe, ưu tiên lịch sử nếu có.
        /// </summary>
        public IReadOnlyList<Bar> GetBars(TimeFrame timeframe, int count = 100)
        {
            if (count <= 0)
            {
                return EmptyBars;
            }

            if (_barCache.TryGetValue((timeframe, count), out var cached))
            {
                return cached;
            }

            IReadOnlyList<Bar> bars;
            if (_barHistory.TryGetValue(timeframe, out var history) && history is { Count: > 0 })
            {
                bars = SliceTail(history, count);
            }
            else if (_snapshot.LatestBars != null && _snapshot.LatestBars.TryGetValue(timeframe, out var latest))
            {
                bars = new[] { latest };
            }
            else
            {
                bars = EmptyBars;
            }

            _barCache[(timeframe, count)] = bars;
            return bars;
        }

        /// <summary>
        /// Lấy giá close mới nhất cho timeframe (null nếu không có dữ liệu).
        /// </summary>
        public double? GetLatestPrice(TimeFrame timeframe)
        {
            var bars = GetBars(timeframe, 1);
            return bars.Count > 0 ? bars[^1].Close : null;
        }

        /// <summary>
        /// Truy xuất giá trị indicator theo tên; chấp nhận search theo timeframe.
        /// </summary>
        public double? GetIndicatorValue(string indicatorName, TimeFrame? timeframe = null)
        {
            if (string.IsNullOrWhiteSpace(indicatorName) || _indicators.Count == 0)
            {
                return null;
            }

            // Ưu tiên match tuyệt đối
            if (timeframe == null &&
                _indicators.TryGetValue(indicatorName, out var exact))
            {
                return exact;
            }

            var normalizedName = indicatorName.Trim();
            var tfToken = timeframe?.ToString();

            foreach (var pair in _indicators)
            {
                if (!pair.Key.Contains(normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (tfToken != null && !pair.Key.Contains(tfToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return pair.Value;
            }

            return null;
        }

        /// <summary>
        /// Gom data đa timeframe để các layer sử dụng đồng nhất.
        /// </summary>
        public IReadOnlyDictionary<TimeFrame, SnapshotTimeframeData> GetMultiTimeframeData()
        {
            var result = new Dictionary<TimeFrame, SnapshotTimeframeData>();
            var timeframes = new HashSet<TimeFrame>();

            if (_barHistory.Count > 0)
            {
                foreach (var tf in _barHistory.Keys)
                {
                    timeframes.Add(tf);
                }
            }

            if (_snapshot.LatestBars != null)
            {
                foreach (var tf in _snapshot.LatestBars.Keys)
                {
                    timeframes.Add(tf);
                }
            }

            foreach (var timeframe in timeframes)
            {
                var history = GetBars(timeframe, int.MaxValue);
                var latest = history.Count > 0 ? history[^1] : (_snapshot.LatestBars != null && _snapshot.LatestBars.TryGetValue(timeframe, out var bar) ? bar : null);
                var indicators = ExtractIndicatorsForTimeframe(timeframe);
                result[timeframe] = new SnapshotTimeframeData(timeframe, latest, history, indicators);
            }

            return result;
        }

        private IReadOnlyDictionary<string, double> ExtractIndicatorsForTimeframe(TimeFrame timeframe)
        {
            if (_indicators.Count == 0)
            {
                return new Dictionary<string, double>();
            }

            var tfToken = timeframe.ToString();
            var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _indicators)
            {
                if (pair.Key.Contains(tfToken, StringComparison.OrdinalIgnoreCase))
                {
                    dict[pair.Key] = pair.Value;
                }
            }

            return dict;
        }

        private static IReadOnlyList<Bar> SliceTail(IReadOnlyList<Bar> source, int count)
        {
            if (source.Count <= count)
            {
                return source;
            }

            var list = new List<Bar>(count);
            for (var i = source.Count - count; i < source.Count; i++)
            {
                list.Add(source[i]);
            }

            return list;
        }
    }

    public sealed class SnapshotTimeframeData
    {
        public SnapshotTimeframeData(
            TimeFrame timeframe,
            Bar? latestBar,
            IReadOnlyList<Bar> history,
            IReadOnlyDictionary<string, double> indicators)
        {
            TimeFrame = timeframe;
            LatestBar = latestBar;
            History = history ?? Array.Empty<Bar>();
            Indicators = indicators ?? new Dictionary<string, double>();
        }

        public TimeFrame TimeFrame { get; }
        public Bar? LatestBar { get; }
        public IReadOnlyList<Bar> History { get; }
        public IReadOnlyDictionary<string, double> Indicators { get; }
    }
}
