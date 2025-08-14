using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DataFetcher.Utils
{
    public static class FileExporter
    {
        /// <summary>
        /// Xuất dữ liệu ra file CSV (debug/backtest).
        /// </summary>
        public static void ExportCsv<T>(IEnumerable<T> data, string path)
        {
            var props = typeof(T).GetProperties();
            var sb = new StringBuilder();
            // Header
            sb.AppendLine(string.Join(",", Array.ConvertAll(props, p => p.Name)));
            // Rows
            foreach (var item in data)
            {
                var row = string.Join(",", Array.ConvertAll(props, p => p.GetValue(item)?.ToString() ?? ""));
                sb.AppendLine(row);
            }
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>
        /// Xuất dữ liệu ra file JSON.
        /// </summary>
        public static void ExportJson<T>(IEnumerable<T> data, string path)
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
