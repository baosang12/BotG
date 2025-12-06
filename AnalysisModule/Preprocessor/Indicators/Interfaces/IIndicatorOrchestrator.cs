#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;

namespace AnalysisModule.Preprocessor.Indicators.Interfaces;

public interface IIndicatorOrchestrator
{
    void RegisterCalculator(IIndicatorCalculator calculator);

    ValueTask<IReadOnlyDictionary<string, IndicatorResult>> CalculateAsync(PreprocessorContext context, CancellationToken cancellationToken = default);
}
