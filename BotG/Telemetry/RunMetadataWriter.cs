using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Telemetry
{
    public class RunMetadataWriter
    {
        private readonly string _filePath;
        public RunMetadataWriter(string folder, string fileName = "run_metadata.json")
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
        }

        public void WriteOnce(object metadata)
        {
            try
            {
                if (File.Exists(_filePath)) return;
                var line = JsonSerializer.Serialize(metadata, new JsonSerializerOptions{ WriteIndented = true });
                File.WriteAllText(_filePath, line);
            }
            catch { }
        }
    }
}
