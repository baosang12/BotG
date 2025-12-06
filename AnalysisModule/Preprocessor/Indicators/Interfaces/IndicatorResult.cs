#nullable enable
using System.Collections.Generic;

namespace AnalysisModule.Preprocessor.Indicators.Interfaces;

public sealed record IndicatorResult(
    string IndicatorName,
    double? Value,
    IReadOnlyDictionary<string, object>? Metadata = null);
