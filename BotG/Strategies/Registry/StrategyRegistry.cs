using System;
using System.Collections.Generic;
using System.Text.Json;
using Strategies.Breakout;
using Strategies.Config;
using Strategies.Confirmation;

namespace Strategies.Registry
{
    public sealed class StrategyRegistry
    {
        private readonly string _configPath;
        private readonly StrategyFactoryResolver _resolver = new StrategyFactoryResolver();

        public StrategyRegistry(string configPath)
        {
            _configPath = configPath;
        }

        public StrategyRegistryResult BuildStrategies(StrategyFactoryContext context)
        {
            var document = StrategyRegistryDocument.Load(_configPath);
            var diagnostics = new List<StrategyLoadDiagnostic>();
            var strategies = new List<IStrategy>();

            foreach (var definition in document.Strategies)
            {
                if (definition == null)
                {
                    continue;
                }

                var identifier = GetStrategyIdentifier(definition);
                if (!definition.Enabled)
                {
                    diagnostics.Add(new StrategyLoadDiagnostic(identifier, "disabled", "DisabledViaConfig"));
                    continue;
                }

                if (!DependenciesSatisfied(definition, context, out var dependencyReason))
                {
                    diagnostics.Add(new StrategyLoadDiagnostic(identifier, "skipped", dependencyReason));
                    continue;
                }

                try
                {
                    var strategy = _resolver.Create(definition, context);
                    if (strategy == null)
                    {
                        diagnostics.Add(new StrategyLoadDiagnostic(identifier, "error", "FactoryReturnedNull"));
                        continue;
                    }

                    strategies.Add(strategy);
                    diagnostics.Add(new StrategyLoadDiagnostic(identifier, "enabled", null));
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new StrategyLoadDiagnostic(identifier, "error", ex.Message));
                }
            }

            return new StrategyRegistryResult(strategies, diagnostics, DateTime.UtcNow);
        }

        private static bool DependenciesSatisfied(StrategyDefinition definition, StrategyFactoryContext context, out string? reason)
        {
            if (definition.Dependencies == null || definition.Dependencies.Length == 0)
            {
                reason = null;
                return true;
            }

            foreach (var dependency in definition.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(dependency))
                {
                    continue;
                }

                if (dependency.Equals("MultiTimeframe", StringComparison.OrdinalIgnoreCase))
                {
                    if (context.TimeframeManager == null || context.TimeframeSynchronizer == null || context.SessionAnalyzer == null)
                    {
                        reason = "Dependency:MultiTimeframe";
                        return false;
                    }
                }
                else if (dependency.Equals("Regime", StringComparison.OrdinalIgnoreCase) ||
                         dependency.Equals("RegimeDetector", StringComparison.OrdinalIgnoreCase))
                {
                    if (context.RegimeDetector == null)
                    {
                        reason = "Dependency:Regime";
                        return false;
                    }
                }
                else if (dependency.Equals("Session", StringComparison.OrdinalIgnoreCase) ||
                         dependency.Equals("SessionAnalyzer", StringComparison.OrdinalIgnoreCase))
                {
                    if (context.SessionAnalyzer == null)
                    {
                        reason = "Dependency:SessionAnalyzer";
                        return false;
                    }
                }
            }

            reason = null;
            return true;
        }

        private static string GetStrategyIdentifier(StrategyDefinition definition)
        {
            if (!string.IsNullOrWhiteSpace(definition.Name))
            {
                return definition.Name;
            }

            if (!string.IsNullOrWhiteSpace(definition.Type))
            {
                return definition.Type;
            }

            return "unknown";
        }
    }

    internal sealed class StrategyFactoryResolver
    {
        private readonly Dictionary<string, Func<StrategyDefinition, StrategyFactoryContext, IStrategy>> _factories;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public StrategyFactoryResolver()
        {
            _factories = new Dictionary<string, Func<StrategyDefinition, StrategyFactoryContext, IStrategy>>(StringComparer.OrdinalIgnoreCase);

            Register((_, _) => new SmaCrossoverStrategy(),
                "SmaCrossover", "SmaCrossoverStrategy", "Strategies.SmaCrossoverStrategy");

            Register((_, _) => new RsiStrategy(),
                "Rsi", "RsiStrategy", "Strategies.RsiStrategy");

            Register((_, _) => new PriceActionStrategy(),
                "PriceAction", "PriceActionStrategy", "Strategies.PriceActionStrategy");

            Register((_, _) => new VolatilityStrategy(),
                "Volatility", "VolatilityStrategy", "Strategies.VolatilityStrategy");

            Register(CreateBreakoutStrategy,
                "Breakout", "BreakoutStrategy", "Strategies.Breakout.BreakoutStrategy");

            Register(CreateTrendFollowingStrategy,
                "TrendFollowing", "TrendFollowingStrategy", "Strategies.TrendFollowingStrategy");

            Register(CreateEma200BreakoutStrategy,
                "Ema200Breakout", "Ema200BreakoutStrategy", "Strategies.Ema200BreakoutStrategy");
        }

        public IStrategy? Create(StrategyDefinition definition, StrategyFactoryContext context)
        {
            var key = definition.Type ?? definition.Name;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Strategy definition missing type or name identifier.");
            }

            if (_factories.TryGetValue(key, out var factory))
            {
                return factory(definition, context);
            }

            throw new InvalidOperationException($"No factory registered for strategy '{key}'.");
        }

        private void Register(Func<StrategyDefinition, StrategyFactoryContext, IStrategy> factory, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                _factories[key] = factory;
            }
        }

        private IStrategy CreateBreakoutStrategy(StrategyDefinition definition, StrategyFactoryContext context)
        {
            if (context.TimeframeManager == null || context.TimeframeSynchronizer == null || context.SessionAnalyzer == null)
            {
                throw new InvalidOperationException("BreakoutStrategy requires multi-timeframe components");
            }

            BreakoutStrategyConfig? breakoutConfig = null;
            ConfirmationConfig? confirmation = null;

            if (definition.Parameters.HasValue && definition.Parameters.Value.ValueKind == JsonValueKind.Object)
            {
                var parameters = definition.Parameters.Value;
                try
                {
                    breakoutConfig = parameters.Deserialize<BreakoutStrategyConfig>(_jsonOptions);
                }
                catch
                {
                    // ignore malformed breakout config
                }

                if (parameters.TryGetProperty("Confirmation", out var confirmationElement))
                {
                    try
                    {
                        confirmation = confirmationElement.Deserialize<ConfirmationConfig>(_jsonOptions);
                    }
                    catch
                    {
                        confirmation = null;
                    }
                }
            }

            return new BreakoutStrategy(
                context.TimeframeManager,
                context.TimeframeSynchronizer,
                context.SessionAnalyzer,
                breakoutConfig,
                confirmation);
        }

        private IStrategy CreateTrendFollowingStrategy(StrategyDefinition definition, StrategyFactoryContext context)
        {
            if (context.TimeframeManager == null || context.TimeframeSynchronizer == null || context.SessionAnalyzer == null)
            {
                throw new InvalidOperationException("TrendFollowingStrategy requires multi-timeframe components");
            }

            TrendFollowingStrategyConfig? config = null;

            if (definition.Parameters.HasValue && definition.Parameters.Value.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    config = definition.Parameters.Value.Deserialize<TrendFollowingStrategyConfig>(_jsonOptions);
                }
                catch
                {
                    config = null;
                }
            }

            return new TrendFollowingStrategy(
                context.TimeframeManager,
                context.TimeframeSynchronizer,
                context.SessionAnalyzer,
                config);
        }

        private IStrategy CreateEma200BreakoutStrategy(StrategyDefinition definition, StrategyFactoryContext context)
        {
            if (context.TimeframeManager == null || context.TimeframeSynchronizer == null || context.SessionAnalyzer == null)
            {
                throw new InvalidOperationException("Ema200BreakoutStrategy requires multi-timeframe components");
            }

            Ema200BreakoutStrategyConfig? config = null;

            if (definition.Parameters.HasValue && definition.Parameters.Value.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    config = definition.Parameters.Value.Deserialize<Ema200BreakoutStrategyConfig>(_jsonOptions);
                }
                catch
                {
                    config = null;
                }
            }

            return new Ema200BreakoutStrategy(
                context.TimeframeManager,
                context.TimeframeSynchronizer,
                context.SessionAnalyzer,
                config);
        }
    }
}
