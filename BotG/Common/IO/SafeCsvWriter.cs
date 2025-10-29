using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace BotG.Common.IO;

/// <summary>
/// Thread-safe CSV writer with idempotent headers, retry logic, and UTF-8 (no BOM) encoding.
/// Uses FileShare.ReadWrite to allow concurrent reads.
/// Enforces canonical Gate2 headers for orders.csv and risk_snapshots.csv.
/// </summary>
public class SafeCsvWriter
{
    private readonly string _path;
    private readonly string[] _header;
    private readonly string _headerString;
    private readonly UTF8Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly object _lock = new object();

    // Gate2 canonical headers (frozen spec)
    private static readonly string OrdersCanonicalHeader = 
        "event,status,reason,latency,price_requested,price_filled,order_id,side,requested_lots,filled_lots";
    private static readonly string RiskCanonicalHeader = 
        "timestamp_iso,equity,R_used,exposure,drawdown";

    public SafeCsvWriter(string path, string[] header)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _header = header ?? throw new ArgumentNullException(nameof(header));
        
        // Coerce canonical headers for known Gate2 files
        _headerString = GetCanonicalHeader(path) ?? string.Join(",", _header);
    }

    /// <summary>
    /// Returns canonical header for known Gate2 files (orders.csv, risk_snapshots.csv).
    /// Returns null for other files (telemetry.csv uses provided header).
    /// </summary>
    private static string? GetCanonicalHeader(string path)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        return fileName switch
        {
            "orders.csv" => OrdersCanonicalHeader,
            "risk_snapshots.csv" => RiskCanonicalHeader,
            _ => null
        };
    }

    /// <summary>
    /// Ensures the CSV file exists and has the correct canonical header (idempotent).
    /// For known Gate2 files (orders.csv, risk_snapshots.csv), enforces canonical header.
    /// If existing file has wrong header, truncates and rewrites with correct header.
    /// </summary>
    public async Task EnsureHeaderAsync()
    {
        await RetryAsync(async () =>
        {
            lock (_lock)
            {
                // Check if file exists and validate header
                if (File.Exists(_path))
                {
                    using var reader = new StreamReader(_path, _encoding, detectEncodingFromByteOrderMarks: true);
                    var firstLine = reader.ReadLine()?.Trim();
                    
                    // If header matches, do nothing (idempotent)
                    if (string.Equals(firstLine, _headerString, StringComparison.Ordinal))
                    {
                        return;
                    }
                    
                    // Header mismatch: truncate and rewrite canonical header
                    // (This ensures Gate2 compliance by correcting legacy/wrong headers)
                }

                // File doesn't exist OR header mismatch: write canonical header
                using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, _encoding);
                writer.WriteLine(_headerString);
                writer.Flush();
            }
        });
    }

    /// <summary>
    /// Appends a single row to the CSV file.
    /// </summary>
    public async Task AppendRowAsync(string[] fields)
    {
        await AppendRowsAsync(new[] { fields });
    }

    /// <summary>
    /// Appends multiple rows to the CSV file.
    /// </summary>
    public async Task AppendRowsAsync(IEnumerable<string[]> rows)
    {
        await EnsureHeaderAsync();

        await RetryAsync(async () =>
        {
            lock (_lock)
            {
                using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                
                // Seek to end before appending
                stream.Seek(0, SeekOrigin.End);

                var sb = new StringBuilder();
                foreach (var row in rows)
                {
                    sb.Append(EscapeCsvRow(row));
                    sb.Append("\r\n");
                }

                var bytes = _encoding.GetBytes(sb.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        });
    }

    /// <summary>
    /// Escapes a CSV row according to RFC 4180.
    /// Fields containing comma, quote, or newline are quoted and quotes are doubled.
    /// </summary>
    private string EscapeCsvRow(string[] fields)
    {
        var escaped = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i] ?? string.Empty;
            
            // Quote if contains comma, quote, or newline
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                escaped[i] = $"\"{field.Replace("\"", "\"\"")}\"";
            }
            else
            {
                escaped[i] = field;
            }
        }
        
        return string.Join(",", escaped);
    }

    /// <summary>
    /// Retries an operation with exponential backoff.
    /// Retries on IOException and UnauthorizedAccessException.
    /// </summary>
    private async Task RetryAsync(Func<Task> action)
    {
        var delays = new[] { 50, 100, 200, 400, 800 };
        
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(delays[attempt]);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(delays[attempt]);
            }
        }
    }
}
