using System.Reflection;
using System.Runtime.Serialization;
using BotG.MarketRegime;
using Xunit;

namespace BotG.Tests.MarketRegime
{
    public class MarketRegimeDetectorTests
    {
        [Fact]
        public void TrendingRule_HasHighestPriority()
        {
            var config = new RegimeConfiguration();
            var detector = CreateDetector(config);

            var regime = InvokeClassify(detector, adx: 30.0, currentAtr: 2.0, averageAtr: 1.0, bollingerWidth: 1.0);

            Assert.Equal(RegimeType.Trending, regime);
        }

        [Fact]
        public void VolatileRule_AppliesWhenAtrExceedsThreshold()
        {
            var config = new RegimeConfiguration();
            var detector = CreateDetector(config);

            var regime = InvokeClassify(detector, adx: 18.0, currentAtr: 1.8, averageAtr: 1.0, bollingerWidth: 4.0);

            Assert.Equal(RegimeType.Volatile, regime);
        }

        [Fact]
        public void CalmRule_AppliesWhenAtrDropsBelowThreshold()
        {
            var config = new RegimeConfiguration();
            var detector = CreateDetector(config);

            var regime = InvokeClassify(detector, adx: 18.0, currentAtr: 0.4, averageAtr: 1.0, bollingerWidth: 2.0);

            Assert.Equal(RegimeType.Calm, regime);
        }

        [Fact]
        public void RangingRule_AppliesWhenAdxWeak()
        {
            var config = new RegimeConfiguration();
            var detector = CreateDetector(config);

            var regime = InvokeClassify(detector, adx: 15.0, currentAtr: 1.0, averageAtr: 1.0, bollingerWidth: 3.0);

            Assert.Equal(RegimeType.Ranging, regime);
        }

        [Fact]
        public void Uncertain_WhenNoRuleMatches()
        {
            var config = new RegimeConfiguration();
            var detector = CreateDetector(config);

            var regime = InvokeClassify(detector, adx: 22.0, currentAtr: 1.1, averageAtr: 1.0, bollingerWidth: 3.5);

            Assert.Equal(RegimeType.Uncertain, regime);
        }

        [Fact]
        public void BollingerWidth_CanTriggerVolatile()
        {
            var config = new RegimeConfiguration
            {
                UseBollingerInClassification = true,
                BollingerVolatilityThreshold = 5.0,
                BollingerCalmThreshold = 2.0
            };
            var detector = CreateDetector(config);

            var regime = InvokeClassify(detector, adx: 21.0, currentAtr: 1.0, averageAtr: 1.0, bollingerWidth: 6.0);

            Assert.Equal(RegimeType.Volatile, regime);
        }

        [Fact]
        public void BollingerWidth_CanTriggerCalm()
        {
            var config = new RegimeConfiguration
            {
                UseBollingerInClassification = true,
                BollingerVolatilityThreshold = 5.0,
                BollingerCalmThreshold = 2.0
            };
            var detector = CreateDetector(config);

            var regime = InvokeClassify(detector, adx: 21.0, currentAtr: 1.0, averageAtr: 1.0, bollingerWidth: 1.5);

            Assert.Equal(RegimeType.Calm, regime);
        }

        private static MarketRegimeDetector CreateDetector(RegimeConfiguration config)
        {
            var detector = (MarketRegimeDetector)FormatterServices.GetUninitializedObject(typeof(MarketRegimeDetector));

            var configField = typeof(MarketRegimeDetector).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance);
            configField!.SetValue(detector, config);

            return detector;
        }

        private static RegimeType InvokeClassify(MarketRegimeDetector detector, double adx, double currentAtr, double averageAtr, double bollingerWidth)
        {
            var method = typeof(MarketRegimeDetector).GetMethod("ClassifyRegime", BindingFlags.NonPublic | BindingFlags.Instance);
            return (RegimeType)method!.Invoke(detector, new object[] { adx, currentAtr, averageAtr, bollingerWidth });
        }
    }
}
