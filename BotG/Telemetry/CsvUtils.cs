using System;
using System.IO;
using System.Text;

namespace Telemetry
{
    public static class CsvUtils
    {
        // Atomic-ish append with header-if-new using FileStream + StreamWriter to avoid interleaving
        public static void SafeAppendCsv(string path, string header, string line)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bool needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                fs.Seek(0, SeekOrigin.End);
                using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    if (needHeader && !string.IsNullOrEmpty(header)) sw.WriteLine(header);
                    sw.WriteLine(line);
                    sw.Flush();
                    try { fs.Flush(true); } catch { try { fs.Flush(); } catch { } }
                }
            }
        }
    }
}
