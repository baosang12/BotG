using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BotG.Common.IO;
using Xunit;

namespace BotG.Tests;

public class WriterTests
{
    [Fact]
    public async Task SafeCsvWriter_WritesIdempotentHeader()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"botg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, "test.csv");
            var header = new[] { "timestamp", "value" };
            var writer = new SafeCsvWriter(testFile, header);

            // Act - append two rows
            await writer.AppendRowAsync(new[] { "2025-10-29T00:00:00Z", "100" });
            await writer.AppendRowAsync(new[] { "2025-10-29T00:01:00Z", "200" });

            // Assert - header should appear only once
            var lines = File.ReadAllLines(testFile);
            Assert.Equal(3, lines.Length); // header + 2 data rows
            Assert.Equal("timestamp,value", lines[0]);
            Assert.Equal("2025-10-29T00:00:00Z,100", lines[1]);
            Assert.Equal("2025-10-29T00:01:00Z,200", lines[2]);

            // Verify header is idempotent - append again
            await writer.AppendRowAsync(new[] { "2025-10-29T00:02:00Z", "300" });
            lines = File.ReadAllLines(testFile);
            Assert.Equal(4, lines.Length); // still only one header
            Assert.Equal("timestamp,value", lines[0]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SafeCsvWriter_AllowsConcurrentReads()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"botg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, "concurrent.csv");
            var header = new[] { "id", "data" };
            var writer = new SafeCsvWriter(testFile, header);

            await writer.AppendRowAsync(new[] { "1", "first" });

            // Act - open file for reading while appending
            using var readStream = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(readStream);
            
            var firstLine = reader.ReadLine();
            Assert.Equal("id,data", firstLine);

            // Append while file is open for reading
            await writer.AppendRowAsync(new[] { "2", "second" });

            // Assert - read operation should succeed
            var secondLine = reader.ReadLine();
            Assert.Equal("1,first", secondLine);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SafeCsvWriter_EncodesUtf8WithoutBOM()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"botg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, "encoding.csv");
            var header = new[] { "text" };
            var writer = new SafeCsvWriter(testFile, header);

            await writer.AppendRowAsync(new[] { "test" });

            // Assert - no BOM (UTF-8 BOM is EF BB BF)
            var bytes = File.ReadAllBytes(testFile);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "File should not have UTF-8 BOM");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SafeCsvWriter_EscapesCommasAndQuotes()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"botg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, "escape.csv");
            var header = new[] { "field1", "field2" };
            var writer = new SafeCsvWriter(testFile, header);

            // Act - write fields with comma and quote
            await writer.AppendRowAsync(new[] { "value,with,commas", "value\"with\"quotes" });

            // Assert - fields should be properly escaped
            var lines = File.ReadAllLines(testFile);
            Assert.Equal(2, lines.Length);
            Assert.Equal("\"value,with,commas\",\"value\"\"with\"\"quotes\"", lines[1]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SafeCsvWriter_EnforcesCanonicalHeaderForOrders()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"botg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, "orders.csv");
            var wrongHeader = new[] { "timestamp", "action", "symbol" }; // Wrong header
            var writer = new SafeCsvWriter(testFile, wrongHeader);

            // Act - initialize file (should enforce canonical header)
            await writer.AppendRowAsync(new[] { "REQUEST", "PENDING", "test", "50", "1.1000", "1.1001", "ORD123", "BUY", "0.01", "0.01" });

            // Assert - header should be canonical Gate2 spec, not provided header
            var lines = File.ReadAllLines(testFile);
            Assert.True(lines.Length >= 1);
            Assert.Equal("event,status,reason,latency,price_requested,price_filled,order_id,side,requested_lots,filled_lots", lines[0]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SafeCsvWriter_EnforcesCanonicalHeaderForRisk()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"botg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, "risk_snapshots.csv");
            var wrongHeader = new[] { "time", "balance", "equity" }; // Wrong header
            var writer = new SafeCsvWriter(testFile, wrongHeader);

            // Act - initialize file (should enforce canonical header)
            await writer.AppendRowAsync(new[] { "2025-10-29T13:00:00Z", "10000.00", "0.5", "500.00", "0.00" });

            // Assert - header should be canonical Gate2 spec
            var lines = File.ReadAllLines(testFile);
            Assert.True(lines.Length >= 1);
            Assert.Equal("timestamp_iso,equity,R_used,exposure,drawdown", lines[0]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SafeCsvWriter_TruncatesFileWithWrongHeader()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"botg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, "orders.csv");
            
            // Create file with wrong header and old data
            File.WriteAllLines(testFile, new[] {
                "phase,timestamp_iso,action,symbol",  // Wrong header
                "ACCUMULATION,2025-10-29T12:00:00Z,BUY,EURUSD",
                "ACCUMULATION,2025-10-29T12:01:00Z,SELL,GBPUSD"
            });

            var writer = new SafeCsvWriter(testFile, new[] { "dummy" });

            // Act - append new row (should truncate and rewrite with canonical header)
            await writer.AppendRowAsync(new[] { "REQUEST", "PENDING", "test", "50", "1.1000", "1.1001", "ORD123", "BUY", "0.01", "0.01" });

            // Assert - old data gone, canonical header enforced
            var lines = File.ReadAllLines(testFile);
            Assert.Equal(2, lines.Length); // Header + 1 new row (old data deleted)
            Assert.Equal("event,status,reason,latency,price_requested,price_filled,order_id,side,requested_lots,filled_lots", lines[0]);
            Assert.Contains("REQUEST", lines[1]);
            Assert.DoesNotContain("ACCUMULATION", string.Join("\n", lines)); // Old data removed
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
