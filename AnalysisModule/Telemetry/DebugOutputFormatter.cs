using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AnalysisModule.Telemetry
{
    /// <summary>
    /// Hỗ trợ định dạng nội dung debug thân thiện cho console cTrader.
    /// </summary>
    public static class DebugOutputFormatter
    {
        /// <summary>
        /// Tạo bảng đơn giản từ dictionary để in ra console.
        /// </summary>
        public static string FormatTable(IDictionary<string, double>? data, string? title = null)
        {
            if (data == null || data.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.AppendLine(title);
                sb.AppendLine(new string('-', 40));
            }

            foreach (var item in data)
            {
                sb.Append("  ");
                sb.Append(item.Key.PadRight(25));
                sb.Append(" : ");
                sb.AppendLine(item.Value.ToString("F2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Tạo progress bar ASCII đơn giản.
        /// </summary>
        public static string FormatProgressBar(double value, double max = 100, int width = 20)
        {
            if (max <= 0)
            {
                max = 100;
            }

            if (width < 1)
            {
                width = 10;
            }

            var ratio = Math.Max(0, Math.Min(1, value / max));
            var filled = (int)Math.Round(ratio * width);
            if (filled > width)
            {
                filled = width;
            }

            return FormattableString.Invariant($"[{new string('█', filled)}{new string('░', Math.Max(0, width - filled))}] {value:F1}/{max}");
        }

        /// <summary>
        /// Định dạng danh sách flag cùng biểu tượng trực quan.
        /// </summary>
        public static string FormatFlags(IList<string>? flags)
        {
            if (flags == null || flags.Count == 0)
            {
                return "None";
            }

            var formatted = new List<string>(flags.Count);
            foreach (var flag in flags)
            {
                var indicator = GetIndicator(flag);
                formatted.Add($"{indicator} {flag}");
            }

            return string.Join(", ", formatted);
        }

        private static string GetIndicator(string? flag)
        {
            var normalized = flag?.ToUpperInvariant();
            return normalized switch
            {
                "LIQUIDITYGRAB" => "📌",
                "CLEANBREAKOUT" => "🚀",
                "FAILEDBREAKOUT" => "💥",
                _ => "🏷️"
            };
        }
    }
}
