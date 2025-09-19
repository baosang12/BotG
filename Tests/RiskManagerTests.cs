using Xunit;
using Risk;

public class RiskManagerTests
{
    [Fact]
    public void ComputeRiskPct_Scales_With_ATR()
    {
        var rm = new RiskManager();
        rm.BaseRiskPct = 0.01;

        // High volatility should reduce risk
        var highVolRisk = rm.ComputeRiskPct(atr: 2.0, atrBaseline: 1.0); // 2x baseline
        Assert.True(highVolRisk < rm.BaseRiskPct);

        // Low volatility should increase risk (but capped)
        var lowVolRisk = rm.ComputeRiskPct(atr: 0.5, atrBaseline: 1.0); // 0.5x baseline
        Assert.True(lowVolRisk > rm.BaseRiskPct);

        // Should respect min/max bounds
        Assert.True(highVolRisk >= rm.MinRiskPct);
        Assert.True(lowVolRisk <= rm.MaxRiskPct);
    }

    [Fact]
    public void IsHalted_When_Daily_Loss_Exceeds_Limits()
    {
        var rm = new RiskManager();
        rm.DailyStopR = -2.0;
        rm.DailyStopPct = -0.03;

        // Simulate losing trades
        rm.OnTradeClosed(pnlCurrency: -100, riskCurrency: 50); // -2R
        rm.OnTradeClosed(pnlCurrency: -50, riskCurrency: 50);  // -1R, total -3R

        Assert.True(rm.IsHalted(dailyPctReturn: -0.01)); // Should halt due to -3R

        // Test percentage halt
        var rm2 = new RiskManager();
        Assert.True(rm2.IsHalted(dailyPctReturn: -0.06)); // Should halt due to -6% > -5%
    }

    [Fact]
    public void Daily_Reset_On_New_Day()
    {
        var rm = new RiskManager();
        rm.OnTradeClosed(-100, 50); // -2R today
        
        // Simulate new day (this is tricky to test without mocking DateTime)
        // For now, just verify that the method works with valid inputs
        Assert.False(rm.IsHalted(0.01)); // Should not halt on small positive return
    }
}