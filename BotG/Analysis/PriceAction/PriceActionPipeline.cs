using System.Collections.Generic;
using DataFetcher.Models;
using Analysis.PriceAction;
using System;
using Config;

namespace Analysis.PriceAction
{
    /// <summary>
    /// Orchestrates execution of Price Action steps.
    /// </summary>
    public class PriceActionPipeline
    {
        private readonly IList<IPriceActionStep> _steps;
        private readonly string[] _enabledSteps;
        private readonly bool _enableLogging;

        /// <summary>
        /// Published when final signals are generated.
        /// </summary>
        public event EventHandler<IList<CandlePatternSignal>> FinalSignalsGenerated;

        public PriceActionPipeline(IEnumerable<IPriceActionStep> steps, PAConfig config, bool enableLogging = false)
        {
            _enabledSteps = config.EnabledSteps;
            _enableLogging = enableLogging;
            // filter and maintain order from registered steps
            _steps = new List<IPriceActionStep>();
            foreach (var step in steps)
            {
                var name = step.GetType().Name.Replace("Step", "");
                if (_enabledSteps == null || Array.Exists(_enabledSteps, s => s == name))
                    _steps.Add(step);
                else if (_enableLogging)
                    Console.WriteLine($"Skipping step {name} as disabled in config");
            }
        }

        /// <summary>
        /// Executes the pipeline synchronously and returns the context with final signals.
        /// </summary>
        public PriceActionContext Execute(IDictionary<string, IList<Bar>> multiTfBars, IList<Bar> currentBars)
        {
            var context = new PriceActionContext();
            if (_enableLogging)
                Console.WriteLine($"Starting PriceActionPipeline with {_steps.Count} steps");
            foreach (var step in _steps)
            {
                var stepName = step.GetType().Name;
                try
                {
                    if (_enableLogging)
                        Console.WriteLine($"Executing step {stepName}");
                    step.Execute(multiTfBars, currentBars, context);
                }
                catch (Exception ex)
                {
                    if (_enableLogging)
                        Console.WriteLine($"Error in step {stepName}, skipping to next: {ex.Message}");
                }
            }
            if (_enableLogging)
                Console.WriteLine($"Pipeline finished. FinalSignals count: {context.FinalSignals.Count}");
            FinalSignalsGenerated?.Invoke(this, context.FinalSignals);
            return context;
        }
    }
}
