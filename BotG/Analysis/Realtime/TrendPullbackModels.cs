using Strategies;

namespace Analysis.Realtime
{
    public sealed record TrendAssessment(
        TradeAction Direction,
        double SeparationRatio,
        double FastEma,
        double SlowEma,
        double LatestClose,
        double Strength)
    {
        public bool IsActionable => Direction == TradeAction.Buy || Direction == TradeAction.Sell;

        public static TrendAssessment Empty { get; } = new TrendAssessment(TradeAction.None, 0.0, 0.0, 0.0, 0.0, 0.0);
    }

    public sealed record RsiTriggerResult(
        bool IsTriggered,
        double Score,
        double Current,
        double Previous,
        string Reason)
    {
        public static RsiTriggerResult None { get; } = new RsiTriggerResult(false, 0.0, 50.0, 50.0, "none");
    }
}
