using System;
using System.IO;
using System.Threading.Tasks;
using Telemetry;
using Xunit;

namespace BotG.Tests
{
    public class CsvUtilsFlushTests
    {
        [Fact]
        public void SafeAppendCsv_WritesHeaderAndLine_AndIsImmediatelyReadable()
        {
            var dir = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "flush_test.csv");
            string header = "col1,col2";
            string line = "a,b";
            CsvUtils.SafeAppendCsv(path, header, line);

            // Immediately open and read back
            Assert.True(File.Exists(path));
            var contents = File.ReadAllLines(path);
            Assert.True(contents.Length >= 2);
            Assert.Equal(header, contents[0]);
            Assert.Equal(line, contents[1]);
        }
    }
}
