using System.Globalization;
using BotG.Harness.Models;

namespace BotG.Harness.Data
{
    public class ReplayCsvProvider : IMarketDataProvider
    {
        private readonly Dictionary<string, Queue<Bar>> _barQueues = new();
        private readonly Dictionary<string, string> _filePaths = new();
        private DateTime _currentTime = DateTime.MinValue;
        private bool _initialized = false;

        public bool HasMore 
        { 
            get 
            {
                if (!_initialized)
                {
                    Initialize();
                    _initialized = true;
                }
                return _barQueues.Values.Any(q => q.Count > 0);
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

            if (!_barQueues.ContainsKey(timeframe) || _barQueues[timeframe].Count == 0)
                return null;

            var bar = _barQueues[timeframe].Dequeue();
            
            // Update current time to latest bar time
            if (bar.Timestamp > _currentTime)
                _currentTime = bar.Timestamp;

            return bar;
        }

        private void Initialize()
        {
            foreach (var tf in _filePaths.Keys)
            {
                _barQueues[tf] = new Queue<Bar>();
                
                if (!File.Exists(_filePaths[tf]))
                {
                    Console.WriteLine($"WARNING: CSV file not found: {_filePaths[tf]}");
                    continue;
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
                            _barQueues[tf].Enqueue(new Bar(timestamp, open, high, low, close, volume));
                        }
                    }

                    Console.WriteLine($"Loaded {_barQueues[tf].Count} bars for {tf} from {_filePaths[tf]}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading {tf} data: {ex.Message}");
                }
            }

            // Set initial time to earliest bar
            var allBars = _barQueues.Values.SelectMany(q => q).ToList();
            if (allBars.Any())
                _currentTime = allBars.Min(b => b.Timestamp);
        }

        public void Reset()
        {
            _barQueues.Clear();
            _currentTime = DateTime.MinValue;
            _initialized = false;
        }

        public void Dispose()
        {
            _barQueues.Clear();
        }
    }
}