using System;
using System.Collections.Generic;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.Aggregators;

/// <summary>
/// Tiền xử lý tick: chuẩn hóa timestamp và loại bỏ duplicate.
/// </summary>
public sealed class TickPreProcessor
{
    private readonly HashSet<long> _seenTimestamps = new();

    public event EventHandler<Tick>? TickCleaned;

    public void Process(Tick tick)
    {
        var normalizedTimestamp = DateTime.SpecifyKind(tick.TimestampUtc, DateTimeKind.Utc);
        var normalized = tick with { TimestampUtc = normalizedTimestamp };
        if (!_seenTimestamps.Add(normalized.TimestampUtc.Ticks))
        {
            return;
        }

        TickCleaned?.Invoke(this, normalized);
    }
}
