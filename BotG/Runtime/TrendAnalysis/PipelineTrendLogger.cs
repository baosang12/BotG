using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using BotG.Runtime.Logging;

namespace BotG.Runtime.TrendAnalysis
{
    internal class PipelineTrendLogger : ILogger
    {
        private readonly string _module;
        private readonly string _category;
        private readonly Action<string>? _print;
        private readonly bool _enableDebug;

        public PipelineTrendLogger(string category, string module, Action<string>? print, bool enableDebug)
        {
            _category = string.IsNullOrWhiteSpace(category) ? "Trend" : category;
            _module = string.IsNullOrWhiteSpace(module) ? "TREND" : module;
            _print = print;
            _enableDebug = enableDebug;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel == LogLevel.Debug || logLevel == LogLevel.Trace)
            {
                return _enableDebug;
            }

            return logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter == null)
            {
                return;
            }

            var evt = logLevel switch
            {
                LogLevel.Critical => "TrendCritical",
                LogLevel.Error => "TrendError",
                LogLevel.Warning => "TrendWarning",
                LogLevel.Information => "TrendInfo",
                LogLevel.Debug => "TrendDebug",
                LogLevel.Trace => "TrendTrace",
                _ => "TrendInfo"
            };

            var payload = new Dictionary<string, object?>
            {
                ["category"] = _category,
                ["level"] = logLevel.ToString(),
                ["event_id"] = eventId.Id
            };

            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                payload["event_name"] = eventId.Name;
            }

            if (exception != null)
            {
                payload["error"] = exception.Message;
            }

            var message = formatter(state, exception);
            PipelineLogger.Log(_module, evt, message, payload, _print);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose()
            {
            }
        }
    }

    internal sealed class PipelineTrendLogger<T> : PipelineTrendLogger, ILogger<T>
    {
        public PipelineTrendLogger(string module, Action<string>? print, bool enableDebug)
            : base(typeof(T).Name, module, print, enableDebug)
        {
        }
    }
}
