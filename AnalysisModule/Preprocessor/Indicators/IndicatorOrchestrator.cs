#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.Indicators.Interfaces;

namespace AnalysisModule.Preprocessor.Indicators;

/// <summary>
/// Orchestrator mặc định: chạy calculators tuần tự (có thể nâng cấp parallel ở phase sau).
/// </summary>
public sealed class IndicatorOrchestrator : IIndicatorOrchestrator
{
    private readonly List<IIndicatorCalculator> _calculators = new();
    private readonly object _sync = new();

    public void RegisterCalculator(IIndicatorCalculator calculator)
    {
        if (calculator is null)
        {
            throw new ArgumentNullException(nameof(calculator));
        }

        lock (_sync)
        {
            if (_calculators.Any(c => string.Equals(c.IndicatorName, calculator.IndicatorName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Indicator '{calculator.IndicatorName}' đã được đăng ký.");
            }

            _calculators.Add(calculator);
        }
    }

    public async ValueTask<IReadOnlyDictionary<string, IndicatorResult>> CalculateAsync(PreprocessorContext context, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IndicatorResult>(StringComparer.OrdinalIgnoreCase);
        IIndicatorCalculator[] snapshot;

        lock (_sync)
        {
            snapshot = _calculators.Where(c => c.IsEnabled).ToArray();
        }

        foreach (var calculator in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indicatorResult = await calculator.CalculateAsync(context, cancellationToken);
            result[indicatorResult.IndicatorName] = indicatorResult;
        }

        return result;
    }
}
