using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

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

        public void UpsertSourceMetadata(string dataSource, string brokerName, string server, string accountId)
        {
            try
            {
                JsonObject root;
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
                }
                else
                {
                    root = new JsonObject();
                }

                root["data_source"] = dataSource;
                root["broker_name"] = brokerName;
                root["server"] = server;
                root["account_id"] = accountId;
                root["timestamp_connect"] = DateTime.UtcNow.ToString("o");

                File.WriteAllText(_filePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // swallow to avoid interrupting trading runtime
            }
        }
    }
}
