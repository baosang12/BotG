using System;
using System.Globalization;
using System.Threading.Tasks;
using BotG.Common.IO;

namespace BotG.Runtime.Logging;

/// <summary>
/// Writes order lifecycle data to orders.csv.
/// Columns (EXACT order): timestamp_iso,action,symbol,qty,price,status,reason,latency_ms,price_requested,price_filled
/// </summary>
public class OrdersWriter
{
    private static readonly string[] Header = 
    { 
        "timestamp_iso", "action", "symbol", "qty", "price", 
        "status", "reason", "latency_ms", "price_requested", "price_filled" 
    };
    
    private readonly SafeCsvWriter _writer;

    public OrdersWriter()
    {
        var path = PathUtil.GetFile("orders.csv");
        _writer = new SafeCsvWriter(path, Header);
    }

    /// <summary>
    /// Appends an order row.
    /// </summary>
    public async Task AppendAsync(
        DateTime timestampUtc,
        string action,
        string symbol,
        decimal qty,
        decimal price,
        string status,
        string reason,
        int latencyMs,
        decimal priceRequested,
        decimal priceFilled)
    {
        var fields = new[]
        {
            timestampUtc.ToString("o", CultureInfo.InvariantCulture),
            action,
            symbol,
            qty.ToString(CultureInfo.InvariantCulture),
            price.ToString(CultureInfo.InvariantCulture),
            status,
            reason,
            latencyMs.ToString(CultureInfo.InvariantCulture),
            priceRequested.ToString(CultureInfo.InvariantCulture),
            priceFilled.ToString(CultureInfo.InvariantCulture)
        };

        await _writer.AppendRowAsync(fields);
    }
}
