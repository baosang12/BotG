using System.Globalization;
using BotG.Harness.Models;

namespace BotG.Harness.Data
{
    public class ReplayCsvProvider : IMarketDataProvider
    {
        private readonly Dictionary<string, Queue<Bar>> _barQueues = new();
        private readonly Dictionary<string, string> _filePaths = new();
        private readonly List<Bar> _m15Bars = new();
        private readonly List<Bar> _h1BarsProcessed = new(); // Track processed H1 bars for arrays
        private readonly Queue<Bar> _aggregatedH1Bars = new();
        private DateTime _currentTime = DateTime.MinValue;
        private bool _initialized = false;
        private bool _useAggregatedH1 = false;
        private int _m15ProcessedForH1 = 0;

        public bool HasMore 
        { 
            get 
            {
                if (!_initialized)
                {
                    Initialize();
                    _initialized = true;
                }
                return _barQueues.Values.Any(q => q.Count > 0) || _aggregatedH1Bars.Count > 0;
            }
        }
        public DateTime CurrentTime => _currentTime;

        public ReplayCsvProvider(string symbol, string dataPath = "data/bars")
        {
            _filePaths["M15"] = Path.Combine(dataPath, $"{symbol}_M15.csv");
            _filePaths["H1"] = Path.Combine(dataPath, $"{symbol}_H1.csv");
        }

        public Bar? GetNextBar(string timeframe)
        {
            if (!_initialized)
            {
                Initialize();
                _initialized = true;
            }

            if (timeframe == "H1" && _useAggregatedH1)
            {
                return GetNextAggregatedH1Bar();
            }

            if (!_barQueues.ContainsKey(timeframe) || _barQueues[timeframe].Count == 0)
                return null;

            var bar = _barQueues[timeframe].Dequeue();
            
            // Update current time to latest bar time
            if (bar.Timestamp > _currentTime)
                _currentTime = bar.Timestamp;

            // If this is M15 and we're using aggregated H1, store for aggregation
            if (timeframe == "M15" && _useAggregatedH1)
            {
                _m15Bars.Add(bar);
            }

            return bar;
        }

        private Bar? GetNextAggregatedH1Bar()
        {
            // Check if we have a pre-aggregated H1 bar available
            if (_aggregatedH1Bars.Count > 0)
            {
                return _aggregatedH1Bars.Dequeue();
            }

            // Need to aggregate from M15 bars
            // We need 4 M15 bars to make 1 H1 bar
            var availableM15Count = _m15Bars.Count - _m15ProcessedForH1;
            if (availableM15Count < 4)
                return null;

            // Take 4 M15 bars starting from _m15ProcessedForH1
            var m15Slice = _m15Bars.Skip(_m15ProcessedForH1).Take(4).ToList();
            if (m15Slice.Count < 4)
                return null;

            // Aggregate into H1 bar
            var h1Bar = AggregateM15ToH1(m15Slice);
            _m15ProcessedForH1 += 4;

            if (h1Bar.Timestamp > _currentTime)
                _currentTime = h1Bar.Timestamp;

            // Track processed H1 bars for array access
            _h1BarsProcessed.Add(h1Bar);

            return h1Bar;
        }

        private Bar AggregateM15ToH1(List<Bar> m15Bars)
        {
            if (m15Bars.Count == 0)
                throw new ArgumentException("Cannot aggregate empty M15 bars");

            // H1 timestamp should be aligned to hour boundary
            var firstBar = m15Bars[0];
            var h1Timestamp = new DateTime(firstBar.Timestamp.Year, firstBar.Timestamp.Month, firstBar.Timestamp.Day, firstBar.Timestamp.Hour, 0, 0, DateTimeKind.Utc);

            var open = m15Bars[0].Open;
            var high = m15Bars.Max(b => b.High);
            var low = m15Bars.Min(b => b.Low);
            var close = m15Bars[^1].Close;  // Last bar's close
            var volume = m15Bars.Sum(b => b.Volume);

            return new Bar(h1Timestamp, open, high, low, close, volume);
        }

        private void Initialize()
        {
            foreach (var tf in _filePaths.Keys)
            {
                _barQueues[tf] = new Queue<Bar>();
                
                if (!File.Exists(_filePaths[tf]))
                {
                    if (tf == "H1")
                    {
                        // H1 file missing - we'll aggregate from M15
                        Console.WriteLine($"H1 CSV not found, will aggregate from M15: {_filePaths[tf]}");
                        _useAggregatedH1 = true;
                        continue;
                    }
                    else
                    {
                        Console.WriteLine($"WARNING: CSV file not found: {_filePaths[tf]}");
                        continue;
                    }
                }

                try
                {
                    var lines = File.ReadAllLines(_filePaths[tf]);
                    if (lines.Length <= 1) continue; // Skip if only header or empty

                    foreach (var line in lines.Skip(1)) // Skip header
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        
                        var parts = line.Split(',');
                        if (parts.Length < 5) continue;

                        if (DateTime.TryParse(parts[0], null, DateTimeStyles.RoundtripKind, out var timestamp) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var open) &&
                            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var high) &&
                            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var low) &&
                            double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var close))
                        {
                            var volume = parts.Length > 5 && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var vol) ? vol : 0;
                            var bar = new Bar(timestamp, open, high, low, close, volume);
                            _barQueues[tf].Enqueue(bar);
                            
                            // Track H1 bars for array access
                            if (tf == "H1")
                            {
                                _h1BarsProcessed.Add(bar);
                            }
                        }
                    }

                    Console.WriteLine($"Loaded {_barQueues[tf].Count} bars for {tf} from {_filePaths[tf]}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading {tf} data: {ex.Message}");
                }
            }

            // Special handling for M15 - if no M15 data available, cannot proceed
            if (!_barQueues.ContainsKey("M15") || _barQueues["M15"].Count == 0)
            {
                throw new InvalidOperationException("M15 data is required but not available");
            }

            // Set initial time to earliest bar
            var allBars = _barQueues.Values.SelectMany(q => q).ToList();
            if (allBars.Any())
                _currentTime = allBars.Min(b => b.Timestamp);
        }

        public void Reset()
        {
            _barQueues.Clear();
            _m15Bars.Clear();
            _h1BarsProcessed.Clear();
            _aggregatedH1Bars.Clear();
            _currentTime = DateTime.MinValue;
            _initialized = false;
            _useAggregatedH1 = false;
            _m15ProcessedForH1 = 0;
        }

        // Methods to expose H1 arrays for trend analysis
        public double[] GetH1Closes()
        {
            if (!_initialized)
            {
                Initialize();
                _initialized = true;
            }

            if (_useAggregatedH1)
            {
                return _h1BarsProcessed.Select(b => b.Close).ToArray();
            }
            else if (_barQueues.ContainsKey("H1"))
            {
                return _barQueues["H1"].Select(b => b.Close).ToArray();
            }
            
            return Array.Empty<double>();
        }

        public double[] GetH1Highs()
        {
            if (!_initialized)
            {
                Initialize();
                _initialized = true;
            }

            if (_useAggregatedH1)
            {
                return _h1BarsProcessed.Select(b => b.High).ToArray();
            }
            else if (_barQueues.ContainsKey("H1"))
            {
                return _barQueues["H1"].Select(b => b.High).ToArray();
            }
            
            return Array.Empty<double>();
        }

        public double[] GetH1Lows()
        {
            if (!_initialized)
            {
                Initialize();
                _initialized = true;
            }

            if (_useAggregatedH1)
            {
                return _h1BarsProcessed.Select(b => b.Low).ToArray();
            }
            else if (_barQueues.ContainsKey("H1"))
            {
                return _barQueues["H1"].Select(b => b.Low).ToArray();
            }
            
            return Array.Empty<double>();
        }

        public bool SupportsLive => false;

        public Task<bool> InitializeAsync()
        {
            if (!_initialized)
            {
                Initialize();
                _initialized = true;
            }
            return Task.FromResult(true);
        }

        public Task<bool> CheckHealthAsync()
        {
            return Task.FromResult(true);
        }

        public void Dispose()
        {
            _barQueues.Clear();
        }
    }
}