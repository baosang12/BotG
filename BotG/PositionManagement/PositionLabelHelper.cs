using System;
using System.Text;

namespace BotG.PositionManagement
{
    /// <summary>
    /// Provides utilities to create and parse position labels that encode the originating strategy name.
    /// </summary>
    public static class PositionLabelHelper
    {
        public const string StrategyLabelPrefix = "BotG|";
        private const int MaxLabelLength = 50;

        public static string BuildStrategyLabel(string? strategyName)
        {
            var sanitized = Sanitize(strategyName);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "Strategy";
            }

            var label = StrategyLabelPrefix + sanitized;
            if (label.Length <= MaxLabelLength)
            {
                return label;
            }

            return label.Substring(0, MaxLabelLength);
        }

        public static string? TryParseStrategyName(string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            if (!label.StartsWith(StrategyLabelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var payload = label.Substring(StrategyLabelPrefix.Length);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            var end = payload.IndexOfAny(new[] { '|', '/', '\\' });
            if (end >= 0)
            {
                payload = payload.Substring(0, end);
            }

            payload = payload.Trim('_');
            return string.IsNullOrWhiteSpace(payload) ? null : payload;
        }

        private static string Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
                else if (ch == '-' || ch == '_')
                {
                    sb.Append(ch);
                }
                else if (char.IsWhiteSpace(ch))
                {
                    sb.Append('_');
                }
            }

            return sb.ToString().Trim('_');
        }
    }
}
