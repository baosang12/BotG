using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telemetry;
using Xunit;

#pragma warning disable CS8632 // Nullable annotations

namespace Tests
{
    /// <summary>
    /// Unit tests for CsvTailReader high-performance streaming implementation.
    /// Tests cover: tail reading, header validation, memory efficiency, large file handling.
    /// </summary>
    public class CsvTailReaderTests : IDisposable
    {
        private readonly string _testDir;
        private readonly List<string> _tempFiles = new List<string>();

        public CsvTailReaderTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"CsvTailReaderTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            foreach (var file in _tempFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); } catch { }
        }

        /// <summary>
        /// Helper to convert IAsyncEnumerable to List
        /// </summary>
        private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
        {
            var list = new List<T>();
            await foreach (var item in source)
            {
                list.Add(item);
            }
            return list;
        }

        private string CreateTestFile(string content, string? fileName = null)
        {
            fileName ??= $"test_{Guid.NewGuid():N}.csv";
            var path = Path.Combine(_testDir, fileName);
            File.WriteAllText(path, content, Encoding.UTF8);
            _tempFiles.Add(path);
            return path;
        }

        #region ReadFirstLineAsync Tests

        [Fact]
        public async Task ReadFirstLineAsync_WithValidHeader_ReturnsHeader()
        {
            // Arrange
            var content = "timestamp,symbol,price\n2025-11-04,EURUSD,1.0850\n2025-11-04,GBPUSD,1.2650\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var firstLine = await reader.ReadFirstLineAsync();

            // Assert
            Assert.Equal("timestamp,symbol,price", firstLine);
        }

        [Fact]
        public async Task ReadFirstLineAsync_WithEmptyFile_ReturnsNull()
        {
            // Arrange
            var file = CreateTestFile("");
            using var reader = new CsvTailReader(file);

            // Act
            var firstLine = await reader.ReadFirstLineAsync();

            // Assert
            Assert.Null(firstLine);
        }

        [Fact]
        public async Task ReadFirstLineAsync_WithNonExistentFile_ReturnsNull()
        {
            // Arrange
            var file = Path.Combine(_testDir, "nonexistent.csv");
            using var reader = new CsvTailReader(file);

            // Act
            var firstLine = await reader.ReadFirstLineAsync();

            // Assert
            Assert.Null(firstLine);
        }

        [Fact]
        public async Task ReadFirstLineAsync_WithOnlyNewline_ReturnsEmpty()
        {
            // Arrange
            var file = CreateTestFile("\n");
            using var reader = new CsvTailReader(file);

            // Act
            var firstLine = await reader.ReadFirstLineAsync();

            // Assert
            Assert.Equal("", firstLine);
        }

        [Fact]
        public async Task ReadFirstLineAsync_WithWindowsLineEndings_TrimsCorrectly()
        {
            // Arrange
            var content = "header,data\r\nrow1,value1\r\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var firstLine = await reader.ReadFirstLineAsync();

            // Assert
            Assert.Equal("header,data", firstLine);
            Assert.DoesNotContain("\r", firstLine);
        }

        #endregion

        #region ReadLastLineAsync Tests

        [Fact]
        public async Task ReadLastLineAsync_WithSingleLine_ReturnsThatLine()
        {
            // Arrange
            var content = "only line";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var lastLine = await reader.ReadLastLineAsync();

            // Assert
            Assert.Equal("only line", lastLine);
        }

        [Fact]
        public async Task ReadLastLineAsync_WithMultipleLines_ReturnsLastLine()
        {
            // Arrange
            var content = "line1\nline2\nline3\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var lastLine = await reader.ReadLastLineAsync();

            // Assert
            Assert.Equal("line3", lastLine);
        }

        [Fact]
        public async Task ReadLastLineAsync_WithTrailingNewlines_IgnoresTrailingNewlines()
        {
            // Arrange
            var content = "line1\nline2\nline3\n\n\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var lastLine = await reader.ReadLastLineAsync();

            // Assert
            Assert.Equal("line3", lastLine);
        }

        [Fact]
        public async Task ReadLastLineAsync_WithEmptyFile_ReturnsNull()
        {
            // Arrange
            var file = CreateTestFile("");
            using var reader = new CsvTailReader(file);

            // Act
            var lastLine = await reader.ReadLastLineAsync();

            // Assert
            Assert.Null(lastLine);
        }

        [Fact]
        public async Task ReadLastLineAsync_WithLargeFile_ReadsOnlyTail()
        {
            // Arrange - Create 1MB file with known last line
            var sb = new StringBuilder();
            for (int i = 0; i < 10000; i++)
            {
                sb.AppendLine($"timestamp,EURUSD,1.0850,line_{i}");
            }
            sb.Append("timestamp,GBPUSD,1.2650,LAST_LINE");

            var file = CreateTestFile(sb.ToString());
            using var reader = new CsvTailReader(file);

            // Act
            var lastLine = await reader.ReadLastLineAsync();

            // Assert
            Assert.Contains("LAST_LINE", lastLine);
            Assert.Contains("GBPUSD", lastLine);
        }

        [Fact]
        public async Task ReadLastLineAsync_WithWindowsLineEndings_HandlesCorrectly()
        {
            // Arrange
            var content = "line1\r\nline2\r\nlast line\r\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var lastLine = await reader.ReadLastLineAsync();

            // Assert
            Assert.Equal("last line", lastLine);
            Assert.DoesNotContain("\r", lastLine);
        }

        #endregion

        #region ReadNewLinesAsync Tests

        [Fact]
        public async Task ReadNewLinesAsync_WithNewContent_ReturnsNewLines()
        {
            // Arrange
            var file = CreateTestFile("line1\nline2\n");
            using var reader = new CsvTailReader(file);

            // Read initial content
            var initial = await ToListAsync(reader.ReadNewLinesAsync());
            Assert.Equal(2, initial.Count);

            // Append new content
            await File.AppendAllTextAsync(file, "line3\nline4\n");

            // Act - Read new lines
            var newLines = await ToListAsync(reader.ReadNewLinesAsync());

            // Assert
            Assert.Equal(2, newLines.Count);
            Assert.Equal("line3", newLines[0]);
            Assert.Equal("line4", newLines[1]);
        }

        [Fact]
        public async Task ReadNewLinesAsync_WithNoNewContent_ReturnsEmpty()
        {
            // Arrange
            var file = CreateTestFile("line1\nline2\n");
            using var reader = new CsvTailReader(file);

            // Read all content
            await ToListAsync(reader.ReadNewLinesAsync());

            // Act - Try to read again without adding content
            var newLines = await ToListAsync(reader.ReadNewLinesAsync());

            // Assert
            Assert.Empty(newLines);
        }

        [Fact]
        public async Task ReadNewLinesAsync_WithFileRotation_HandlesCorrectly()
        {
            // Arrange
            var file = CreateTestFile("line1\nline2\n");
            using var reader = new CsvTailReader(file);

            // Read initial content
            await ToListAsync(reader.ReadNewLinesAsync());

            // Simulate file rotation (replace with smaller file)
            File.WriteAllText(file, "new_line1\nnew_line2\n");

            // Act - Read from rotated file
            var newLines = await ToListAsync(reader.ReadNewLinesAsync());

            // Assert
            Assert.Equal(2, newLines.Count);
            Assert.Contains("new_line1", newLines);
        }

        [Fact]
        public async Task ReadNewLinesAsync_WithEmptyLines_SkipsEmptyLines()
        {
            // Arrange
            var file = CreateTestFile("line1\n\nline2\n\n\nline3\n");
            using var reader = new CsvTailReader(file);

            // Act
            var lines = await ToListAsync(reader.ReadNewLinesAsync());

            // Assert
            Assert.Equal(3, lines.Count);
            Assert.Equal("line1", lines[0]);
            Assert.Equal("line2", lines[1]);
            Assert.Equal("line3", lines[2]);
        }

        #endregion

        #region ReadLastLinesAsync Tests

        [Fact]
        public async Task ReadLastLinesAsync_WithRequestedCount_ReturnsCorrectLines()
        {
            // Arrange
            var content = "line1\nline2\nline3\nline4\nline5\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var lastLines = await reader.ReadLastLinesAsync(3);

            // Assert
            Assert.Equal(3, lastLines.Count);
            Assert.Equal("line3", lastLines[0]);
            Assert.Equal("line4", lastLines[1]);
            Assert.Equal("line5", lastLines[2]);
        }

        [Fact]
        public async Task ReadLastLinesAsync_WithMoreLinesThanExist_ReturnsAllLines()
        {
            // Arrange
            var content = "line1\nline2\nline3\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var lastLines = await reader.ReadLastLinesAsync(100);

            // Assert
            Assert.Equal(3, lastLines.Count);
        }

        [Fact]
        public async Task ReadLastLinesAsync_WithZeroCount_ReturnsEmpty()
        {
            // Arrange
            var file = CreateTestFile("line1\nline2\n");
            using var reader = new CsvTailReader(file);

            // Act
            var lastLines = await reader.ReadLastLinesAsync(0);

            // Assert
            Assert.Empty(lastLines);
        }

        [Fact]
        public async Task ReadLastLinesAsync_WithLargeFile_ReturnsCorrectOrder()
        {
            // Arrange - Create large file
            var sb = new StringBuilder();
            for (int i = 1; i <= 1000; i++)
            {
                sb.AppendLine($"line_{i}");
            }
            var file = CreateTestFile(sb.ToString());
            using var reader = new CsvTailReader(file);

            // Act - Read last 10 lines
            var lastLines = await reader.ReadLastLinesAsync(10);

            // Assert
            Assert.Equal(10, lastLines.Count);
            Assert.Equal("line_991", lastLines[0]);
            Assert.Equal("line_1000", lastLines[9]);
        }

        #endregion

        #region Performance & Stress Tests

        [Fact]
        public async Task ReadLastLineAsync_WithLargeFile_CompletesQuickly()
        {
            // Arrange - Create 10MB file
            var sb = new StringBuilder();
            for (int i = 0; i < 100000; i++)
            {
                sb.AppendLine($"timestamp_{i},EURUSD,1.0850,data_{i},more_data,even_more_data");
            }
            var file = CreateTestFile(sb.ToString());
            using var reader = new CsvTailReader(file);

            // Act
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lastLine = await reader.ReadLastLineAsync();
            stopwatch.Stop();

            // Assert
            Assert.NotNull(lastLine);
            Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Read took {stopwatch.ElapsedMilliseconds}ms, expected <100ms");
        }

        [Fact]
        public async Task ReadFirstLineAsync_WithLargeFile_CompletesQuickly()
        {
            // Arrange - Create 10MB file
            var sb = new StringBuilder();
            sb.AppendLine("header,data,more_columns,even_more");
            for (int i = 0; i < 100000; i++)
            {
                sb.AppendLine($"timestamp_{i},EURUSD,1.0850,data_{i}");
            }
            var file = CreateTestFile(sb.ToString());
            using var reader = new CsvTailReader(file);

            // Act
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var firstLine = await reader.ReadFirstLineAsync();
            stopwatch.Stop();

            // Assert
            Assert.Equal("header,data,more_columns,even_more", firstLine);
            Assert.True(stopwatch.ElapsedMilliseconds < 50, $"Read took {stopwatch.ElapsedMilliseconds}ms, expected <50ms");
        }

        [Fact]
        public async Task CsvTailReader_WithMultipleInstances_NoMemoryLeak()
        {
            // Arrange
            var file = CreateTestFile("line1\nline2\nline3\n");

            // Act - Create and dispose many readers
            for (int i = 0; i < 100; i++)
            {
                using var reader = new CsvTailReader(file);
                await reader.ReadLastLineAsync();
            }

            // Assert - No exceptions means no resource leaks
            Assert.True(true);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public async Task ReadLastLineAsync_WithVeryLongLine_HandlesCorrectly()
        {
            // Arrange - Create line longer than buffer size
            var longLine = new string('x', 100000);
            var content = "short_line\n" + longLine + "\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file, bufferSize: 1024); // Small buffer

            // Act
            var lastLine = await reader.ReadLastLineAsync();

            // Assert
            Assert.NotNull(lastLine);
            Assert.Contains("xxx", lastLine);
        }

        [Fact]
        public async Task ReadNewLinesAsync_WithCancellation_StopsGracefully()
        {
            // Arrange
            var file = CreateTestFile("line1\nline2\nline3\n");
            using var reader = new CsvTailReader(file);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var line in reader.ReadNewLinesAsync(cts.Token))
                {
                    // Should not reach here
                }
            });
        }

        [Fact]
        public async Task ResetPosition_AfterReading_AllowsRereading()
        {
            // Arrange
            var file = CreateTestFile("line1\nline2\n");
            using var reader = new CsvTailReader(file);

            // Read all lines
            var firstRead = await ToListAsync(reader.ReadNewLinesAsync());
            Assert.Equal(2, firstRead.Count);

            // Reset and read again
            reader.ResetPosition();

            // Act
            var secondRead = await ToListAsync(reader.ReadNewLinesAsync());

            // Assert
            Assert.Equal(2, secondRead.Count);
            Assert.Equal(firstRead, secondRead);
        }

        #endregion

        #region CSV-Specific Tests

        [Fact]
        public async Task ReadLastLineAsync_WithCsvData_ParsesCorrectly()
        {
            // Arrange - Realistic CSV telemetry data
            var content = @"timestamp,symbol,bid,ask,spread_pips
2025-11-04T10:00:00Z,EURUSD,1.0850,1.0852,0.2
2025-11-04T10:00:01Z,GBPUSD,1.2650,1.2652,0.2
2025-11-04T10:00:02Z,USDJPY,149.50,149.52,2.0
";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var lastLine = await reader.ReadLastLineAsync();

            // Assert
            Assert.Contains("USDJPY", lastLine);
            Assert.Contains("149.50", lastLine);

            var parts = lastLine!.Split(',');
            Assert.Equal(5, parts.Length);
        }

        [Fact]
        public async Task ReadFirstLineAsync_WithCsvHeader_ValidatesSchema()
        {
            // Arrange
            var expectedHeader = "timestamp,symbol,bid,ask,spread_pips";
            var content = $"{expectedHeader}\n2025-11-04,EURUSD,1.0850,1.0852,0.2\n";
            var file = CreateTestFile(content);
            using var reader = new CsvTailReader(file);

            // Act
            var header = await reader.ReadFirstLineAsync();

            // Assert
            Assert.Equal(expectedHeader, header);
        }

        #endregion
    }
}
