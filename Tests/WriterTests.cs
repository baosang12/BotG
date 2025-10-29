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
}
