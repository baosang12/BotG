using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
    /// <summary>
    /// Represents a step in the Price Action pipeline.
    /// </summary>
    public interface IPriceActionStep
    {
        /// <summary>
        /// Executes this step asynchronously, supports cancellation and error handling.
        /// </summary>
        /// <summary>
        /// Executes this step synchronously.
        /// </summary>
        void Execute(IDictionary<string, IList<Bar>> multiTfBars,
                     IList<Bar> currentBars,
                     PriceActionContext context);
    }
}
