using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Strategies;

namespace Strategies.Registry
{
    public sealed class StrategyRegistryDocument
    {
        public List<StrategyDefinition> Strategies { get; set; } = new List<StrategyDefinition>();

        public static StrategyRegistryDocument Load(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return new StrategyRegistryDocument();
                }

                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var document = JsonSerializer.Deserialize<StrategyRegistryDocument>(json, options);
                return document ?? new StrategyRegistryDocument();
            }
            catch
            {
                return new StrategyRegistryDocument();
            }
        }
    }

    public sealed class StrategyDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string? Type { get; set; }
        public bool Enabled { get; set; } = true;
        public string[]? Dependencies { get; set; }
        public JsonElement? Parameters { get; set; }
    }

    public sealed record StrategyLoadDiagnostic(string StrategyName, string Status, string? Reason);

    public sealed record StrategyRegistryResult(
        IReadOnlyList<IStrategy> Strategies,
        IReadOnlyList<StrategyLoadDiagnostic> Diagnostics,
        DateTime LoadedAtUtc);
}
