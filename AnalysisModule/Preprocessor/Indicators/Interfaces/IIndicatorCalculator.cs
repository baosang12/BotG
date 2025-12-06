#nullable enable
using System.Threading;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;

namespace AnalysisModule.Preprocessor.Indicators.Interfaces;

public interface IIndicatorCalculator
{
    string IndicatorName { get; }

    bool IsEnabled { get; set; }

    ValueTask<IndicatorResult> CalculateAsync(PreprocessorContext context, CancellationToken cancellationToken = default);
}
