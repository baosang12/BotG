using System;

namespace AnalysisModule.Preprocessor.Core;

public sealed record PreprocessorStatus(
    PreprocessorState State,
    long ProcessedTicks,
    DateTime? LastTickTimestampUtc,
    bool IsDegraded,
    string? DegradedReason);
