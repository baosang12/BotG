using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;
using Microsoft.Extensions.Logging;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers
{
    /// <summary>
    /// Lớp nền cung cấp logging + diagnostics chung cho các layer.
    /// </summary>
    public abstract class BaseLayerCalculator : ILayerCalculator
    {
        private TrendAnalyzerConfig _config = new();
        private readonly List<string> _confirmations = new();
        private readonly List<string> _warnings = new();
        private readonly Dictionary<string, object> _diagnostics = new(StringComparer.OrdinalIgnoreCase);

        protected BaseLayerCalculator(ILogger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected ILogger Logger { get; }

        protected TrendAnalyzerConfig Config => _config;

        public abstract string LayerName { get; }

        public virtual bool IsEnabled => _config.Enabled;

        public abstract double CalculateScore(PreprocessorSnapshot snapshot, SnapshotDataAccessor accessor);

        public virtual void UpdateConfig(TrendAnalyzerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public virtual IReadOnlyDictionary<string, object> GetDiagnostics()
        {
            var snapshot = new Dictionary<string, object>(_diagnostics, StringComparer.OrdinalIgnoreCase)
            {
                ["Confirmations"] = _confirmations.ToArray(),
                ["Warnings"] = _warnings.ToArray()
            };

            return snapshot;
        }

        protected void ResetDiagnostics()
        {
            _confirmations.Clear();
            _warnings.Clear();
            _diagnostics.Clear();
        }

        protected void AddConfirmation(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _confirmations.Add($"[{LayerName}] {message}");
            }
        }

        protected void AddWarning(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _warnings.Add($"[{LayerName}] {message}");
            }
        }

        protected void AddDiagnostic(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _diagnostics[key] = value ?? string.Empty;
        }
    }
}
