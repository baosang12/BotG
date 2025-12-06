using ModelTimeFrame = DataFetcher.Models.TimeFrame;

namespace BotG.Runtime.Preprocessor;

public static class PreprocessorIndicatorNames
{
    public static string Atr(ModelTimeFrame timeframe, int period) => $"ATR({Format(timeframe)},{period})";
    public static string Rsi(ModelTimeFrame timeframe, int period) => $"RSI({Format(timeframe)},{period})";
    public static string Sma(ModelTimeFrame timeframe, int period) => $"SMA({Format(timeframe)},{period})";
    public static string Ema(ModelTimeFrame timeframe, int period) => $"EMA({Format(timeframe)},{period})";

    private static string Format(ModelTimeFrame timeframe) => timeframe.ToString();
}
