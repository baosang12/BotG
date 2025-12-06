#nullable enable
using System;

namespace AnalysisModule.Preprocessor.DataModels;

public sealed record Tick(
    DateTime TimestampUtc,
    double Bid,
    double Ask,
    long Volume);
