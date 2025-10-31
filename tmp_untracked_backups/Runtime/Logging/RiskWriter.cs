using System;
using System.Globalization;
using System.Threading.Tasks;
using BotG.Common.IO;

namespace BotG.Runtime.Logging;

/// <summary>
/// Writes risk snapshot data to risk_snapshots.csv.
/// Columns (EXACT order): timestamp_iso,equity,balance,floating
/// </summary>
public class RiskWriter
{
    private static readonly string[] Header = { "timestamp_iso", "equity", "balance", "floating" };
    private readonly SafeCsvWriter _writer;

    public RiskWriter()
    {
        var path = PathUtil.GetFile("risk_snapshots.csv");
        _writer = new SafeCsvWriter(path, Header);
    }

    /// <summary>
    /// Appends a risk snapshot row.
    /// </summary>
    public async Task AppendAsync(DateTime timestampUtc, decimal equity, decimal balance, decimal floating)
    {
        var fields = new[]
        {
            timestampUtc.ToString("o", CultureInfo.InvariantCulture),
            equity.ToString(CultureInfo.InvariantCulture),
            balance.ToString(CultureInfo.InvariantCulture),
            floating.ToString(CultureInfo.InvariantCulture)
        };

        await _writer.AppendRowAsync(fields);
    }
}
