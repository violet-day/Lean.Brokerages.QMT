using System;
using System.Globalization;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtAccountPropertiesTests
    {
        [TestCase(true, QmtMarketOrderStyle.LatestPrice)]
        [TestCase(false, QmtMarketOrderStyle.FiveLevelImmediateOrCancel)]
        public void SelectsMarketOrderStyleFromAccount(
            bool isSimulation,
            QmtMarketOrderStyle expectedMarketOrderStyle)
        {
            var accountProperties = new QmtAccountProperties(isSimulation);

            Assert.Multiple(() =>
            {
                Assert.That(accountProperties.IsSimulation, Is.EqualTo(isSimulation));
                Assert.That(accountProperties.MarketOrderStyle, Is.EqualTo(expectedMarketOrderStyle));
            });
        }

        [TestCase("2026-08-19T01:59:59Z", false)]
        [TestCase("2026-08-19T02:00:00Z", true)]
        [TestCase("2026-08-19T08:59:59Z", true)]
        [TestCase("2026-08-19T09:00:00Z", false)]
        [TestCase("2026-08-22T04:00:00Z", false)]
        public void EnforcesSimulationOrderSession(string utcTimeText, bool expectedAllowed)
        {
            var utcTime = DateTime.Parse(
                utcTimeText,
                null,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

            Assert.That(
                new QmtAccountProperties(true).IsOrderSubmissionAllowed(utcTime),
                Is.EqualTo(expectedAllowed));
        }

        [Test]
        public void DoesNotRestrictLiveAccountOrderSession()
        {
            Assert.That(
                new QmtAccountProperties(false).IsOrderSubmissionAllowed(
                    new DateTime(2026, 8, 22, 4, 0, 0, DateTimeKind.Utc)),
                Is.True);
        }
    }
}
