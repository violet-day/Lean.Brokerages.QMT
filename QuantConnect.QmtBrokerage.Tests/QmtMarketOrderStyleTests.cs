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
        public void MapsToProtocolValue(
            string protocolValue,
            QmtMarketOrderStyle expectedMarketOrderStyle)
        {
            Assert.That(
                QmtMarketOrderStyleResolver.GetProtocolValue(expectedMarketOrderStyle),
                Is.EqualTo(protocolValue));
        }
    }
}
