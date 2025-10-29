using System;
using System.Globalization;
using System.Threading.Tasks;
using BotG.Common.IO;

namespace BotG.Runtime.Logging;

/// <summary>
/// Writes telemetry data to telemetry.csv.
/// Columns (EXACT order): timestamp_iso,symbol,bid,ask
/// </summary>
public class TelemetryWriter
{
    private static readonly string[] Header = { "timestamp_iso", "symbol", "bid", "ask" };
    private readonly SafeCsvWriter _writer;

    public TelemetryWriter()
    {
        var path = PathUtil.GetFile("telemetry.csv");
        _writer = new SafeCsvWriter(path, Header);
    }

    /// <summary>
    /// Appends a telemetry row.
    /// </summary>
    public async Task AppendAsync(DateTime timestampUtc, string symbol, decimal bid, decimal ask)
    {
        var fields = new[]
        {
            timestampUtc.ToString("o", CultureInfo.InvariantCulture),
            symbol,
            bid.ToString(CultureInfo.InvariantCulture),
            ask.ToString(CultureInfo.InvariantCulture)
        };

        await _writer.AppendRowAsync(fields);
    }
}
