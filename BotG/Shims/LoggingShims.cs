using System;

namespace Logging
{
    // Minimal logger shim to satisfy references from WyckoffPatternAnalyzer
    public class RangeJsonLogger
    {
        private readonly Action<string> _console;
        public RangeJsonLogger(Action<string>? console = null)
        {
            _console = console ?? Console.WriteLine;
        }

        public void LogEvent(string label, object? state)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new { label, state, ts = DateTimeOffset.UtcNow });
                _console(json);
            }
            catch
            {
                // swallow
            }
        }
    }
}
