using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Text.Json;

[TestClass]
public class TradeModeConfigTests
{
    [TestMethod]
    public void DefaultTradeMode_Should_Be_Strict()
    {
        // Test that the default config has TradeMode = "Strict"
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "config.xauusd_mtf.json");
        
        // If config file doesn't exist at expected path, try alternative paths
        if (!File.Exists(configPath))
        {
            var altPaths = new[]
            {
                "config.xauusd_mtf.json",
                Path.Combine("..", "config.xauusd_mtf.json"),
                Path.Combine("..", "..", "config.xauusd_mtf.json"),
                Path.Combine("..", "..", "..", "config.xauusd_mtf.json")
            };
            
            foreach (var path in altPaths)
            {
                if (File.Exists(path))
                {
                    configPath = path;
                    break;
                }
            }
        }
        
        if (!File.Exists(configPath))
        {
            Assert.Inconclusive($"Config file not found. Searched paths including: {configPath}");
            return;
        }

        var configContent = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(configContent);
        
        // Check if tradeMode exists and is "Strict"
        if (doc.RootElement.TryGetProperty("tradeMode", out var tradeModeElement))
        {
            var tradeMode = tradeModeElement.GetString();
            Assert.AreEqual("Strict", tradeMode, 
                "Default TradeMode must be 'Strict'. Test mode should never be committed as default.");
        }
        else
        {
            Assert.Fail("tradeMode property is missing from config. Please add 'tradeMode': 'Strict' to the config.");
        }
    }
}