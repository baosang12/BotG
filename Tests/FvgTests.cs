using Xunit;
using Analysis.Imbalance;
using System.Linq;

public class FvgTests
{
    [Fact]
    public void Detects_Bullish_FVG()
    {
        var highs = new double[] {1.0, 1.1, 1.2};
        var lows  = new double[] {0.9, 1.0, 1.15}; // Low[2] > High[0] => bullish FVG ở i=1
        var fvgs = FairValueGapDetector.Detect(highs, lows);
        Assert.True(fvgs.Any(f=>f.IsBullish && f.Index==1));
    }

    [Fact]
    public void Detects_Bearish_FVG()
    {
        var highs = new double[] {1.2, 1.1, 0.95}; // High[2] < Low[0] => bearish FVG ở i=1
        var lows  = new double[] {1.0, 0.9, 0.85};
        var fvgs = FairValueGapDetector.Detect(highs, lows);
        Assert.True(fvgs.Any(f=>!f.IsBullish && f.Index==1));
    }

    [Fact]
    public void No_FVG_When_No_Gap()
    {
        var highs = new double[] {1.0, 1.1, 1.05};
        var lows  = new double[] {0.9, 0.95, 0.98};
        var fvgs = FairValueGapDetector.Detect(highs, lows);
        Assert.Empty(fvgs);
    }

    [Fact]
    public void FVG_Has_Correct_Gap_Range()
    {
        var highs = new double[] {1.0, 1.1, 1.25};
        var lows  = new double[] {0.9, 1.0, 1.15}; // Gap: Low[2]=1.15 > High[0]=1.0
        var fvgs = FairValueGapDetector.Detect(highs, lows);
        var bullFvg = fvgs.First(f=>f.IsBullish);
        Assert.Equal(1.0, bullFvg.GapLow);    // High[i-1] = High[0]
        Assert.Equal(1.15, bullFvg.GapHigh);  // Low[i+1] = Low[2]
    }
}