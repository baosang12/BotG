namespace AnalysisModule.Preprocessor.DataModels;

public sealed class AccountInfo
{
    public double Equity { get; init; }
    public double Balance { get; init; }
    public double Margin { get; init; }
    public int OpenPositions { get; init; }
}
