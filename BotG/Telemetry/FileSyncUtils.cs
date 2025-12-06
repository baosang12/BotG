using System;
using System.IO;

namespace Telemetry
{
    public static class FileSyncUtils
    {
        public static void TryFsync(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!File.Exists(path)) return;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    fs.Seek(0, SeekOrigin.End);
                    try { fs.Flush(true); }
                    catch
                    {
                        try { fs.Flush(); } catch { }
                    }
                }
            }
            catch { /* best effort */ }
        }

        public static void TryFsyncMany(string folder, params string[] fileNames)
        {
            if (string.IsNullOrWhiteSpace(folder) || fileNames == null) return;
            foreach (var f in fileNames)
            {
                try
                {
                    var p = Path.Combine(folder, f);
                    TryFsync(p);
                }
                catch { }
            }
        }
    }
}
