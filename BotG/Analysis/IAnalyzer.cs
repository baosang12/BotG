namespace Analysis
{
    /// <summary>
    /// Generic interface for analyzing combined indicator data.
    /// </summary>
    /// <typeparam name="TInput">Type of input data.</typeparam>
    /// <typeparam name="TOutput">Type of analysis output.</typeparam>
    public interface IAnalyzer<TInput, TOutput>
    {
        /// <summary>
        /// Analyze input and return output.
        /// </summary>
        TOutput Analyze(TInput input);
    }
}
