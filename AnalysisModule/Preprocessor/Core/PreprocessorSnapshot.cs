#nullable enable
using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.Core;

/// <summary>
/// Snapshot hoàn chỉnh gửi tới Analysis hub sau mỗi vòng xử lý.
/// </summary>
public sealed record PreprocessorSnapshot(
    DateTime TimestampUtc,
    IReadOnlyDictionary<string, double> Indicators,
    IReadOnlyDictionary<TimeFrame, Bar> LatestBars,
    AccountInfo? Account,
    bool IsDegraded = false,
    string? DegradedReason = null);
