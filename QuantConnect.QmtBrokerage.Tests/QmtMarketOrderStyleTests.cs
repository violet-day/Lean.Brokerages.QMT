using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtMarketOrderStyleTests
    {
        [TestCase("latest-price", QmtMarketOrderStyle.LatestPrice)]
        [TestCase("five-level-immediate-or-cancel", QmtMarketOrderStyle.FiveLevelImmediateOrCancel)]
        [TestCase("five-level-immediate-to-limit", QmtMarketOrderStyle.FiveLevelImmediateToLimit)]
        [TestCase("counterparty-best", QmtMarketOrderStyle.CounterpartyBest)]
        [TestCase("own-best", QmtMarketOrderStyle.OwnBest)]
        [TestCase("immediate-or-cancel", QmtMarketOrderStyle.ImmediateOrCancel)]
        [TestCase("fill-or-kill", QmtMarketOrderStyle.FillOrKill)]
        public void ParsesConfigurationValue(
            string configurationValue,
            QmtMarketOrderStyle expectedMarketOrderStyle)
        {
            Assert.That(
                QmtMarketOrderStyleResolver.TryParse(configurationValue, out var marketOrderStyle),
                Is.True);
            Assert.That(marketOrderStyle, Is.EqualTo(expectedMarketOrderStyle));
            Assert.That(
                QmtMarketOrderStyleResolver.GetConfigurationValue(marketOrderStyle),
                Is.EqualTo(configurationValue));
        }

        [Test]
        public void RejectsUnknownConfigurationValue()
        {
            Assert.That(
                QmtMarketOrderStyleResolver.TryParse("market", out _),
                Is.False);
        }
    }
}
