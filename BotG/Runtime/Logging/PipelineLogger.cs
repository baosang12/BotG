using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BotG.Runtime.Logging
{
    /// <summary>
    /// Pipeline logger writes JSON-formatted log events to both Console and pipeline.log file.
    /// Thread-safe, asynchronous writes to avoid blocking runtime.
    /// </summary>
    public static class PipelineLogger
    {
        private static readonly string _logPath = Path.Combine("D:\\botg\\logs", "pipeline.log");
        private static readonly Encoding _utf8NoBom = new UTF8Encoding(false);
        private static readonly object _fileLock = new object();
        private static bool _initialized = false;

        public static void Initialize()
        {
            if (_initialized) return;
            try
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                _initialized = true;
            }
            catch { /* Silent fail - Console logging will still work */ }
        }

        /// <summary>
        /// Log a pipeline event with JSON formatting.
        /// </summary>
        /// <param name="module">Module name (e.g., EXECUTOR, TRADE, ORDER, WRITER, BOOT)</param>
        /// <param name="evt">Event name (e.g., Start, Ready, ProcessTick, REQUEST, ACK, FILL)</param>
        /// <param name="msg">Human-readable message</param>
        /// <param name="data">Optional additional data to include in JSON</param>
        /// <param name="printFunc">Optional Print function from cAlgo Robot (null for standalone use)</param>
        public static void Log(string module, string evt, string msg, object? data = null, Action<string>? printFunc = null)
        {
            try
            {
                Initialize();

                var logEntry = new
                {
                    ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    lvl = "INFO",
                    mod = module,
                    evt = evt,
                    msg = msg,
                    data = data
                };

                var json = JsonSerializer.Serialize(logEntry);

                // Console output (using Print if available, otherwise Console.WriteLine)
                var consoleMsg = $"[PIPE][{module}] {msg}";
                if (printFunc != null)
                {
                    printFunc(consoleMsg);
                }
                else
                {
                    Console.WriteLine(consoleMsg);
                }

                // File output (async to avoid blocking)
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        lock (_fileLock)
                        {
                            File.AppendAllText(_logPath, json + Environment.NewLine, _utf8NoBom);
                        }
                    }
                    catch { /* Silent fail */ }
                });
            }
            catch { /* Silent fail - don't crash bot on logging errors */ }
        }
    }
}
