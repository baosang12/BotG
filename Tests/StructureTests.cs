using Xunit;
using Analysis.Structure;
using System.Linq;

public class StructureTests
{
    [Fact]
    public void Detects_BOS_Bull_Simple()
    {
        // Test với data rõ ràng hơn: uptrend với breakout
        var highs = new double[] {1.0, 1.2, 1.1, 1.5, 1.3, 1.8}; 
        var lows  = new double[] {0.8, 1.0, 0.9, 1.2, 1.1, 1.4};
        var swings = MarketStructureDetector.DetectSwings(highs, lows, 1);
        
        // Với close price > highest swing high → BOS Bull
        var evt = MarketStructureDetector.DetectEvent(swings, highs.Length-1, 1.9);
        Assert.True(evt==StructureEvent.BOS_Bull || evt==StructureEvent.ChoCH_Bull);
    }

    [Fact]
    public void Detects_Swings_With_Fractal()
    {
        var highs = new double[] {1.0, 1.5, 1.2, 1.8, 1.1};
        var lows  = new double[] {0.8, 1.0, 0.9, 1.3, 0.7};
        var swings = MarketStructureDetector.DetectSwings(highs, lows, 1);
        Assert.True(swings.Count >= 1);
        Assert.Contains(swings, s => s.IsHigh && s.Price == 1.8);
    }

    [Fact]
    public void Event_None_When_No_Breakout()
    {
        var highs = new double[] {1.0, 1.1, 1.05, 1.08};
        var lows  = new double[] {0.9, 0.95, 0.92, 0.94};
        var swings = MarketStructureDetector.DetectSwings(highs, lows, 1);
        var evt = MarketStructureDetector.DetectEvent(swings, highs.Length-1, 1.06);
        Assert.Equal(StructureEvent.None, evt);
    }
}