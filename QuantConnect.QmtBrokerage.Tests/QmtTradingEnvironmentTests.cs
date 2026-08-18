using System;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtTradingEnvironmentTests
    {
        [TestCase("live", QmtTradingEnvironment.Live)]
        [TestCase("simulation", QmtTradingEnvironment.Simulation)]
        public void ParsesConfigurationValue(
            string configurationValue,
            QmtTradingEnvironment expectedTradingEnvironment)
        {
            Assert.That(
                QmtTradingEnvironmentResolver.TryParse(configurationValue, out var tradingEnvironment),
                Is.True);
            Assert.That(tradingEnvironment, Is.EqualTo(expectedTradingEnvironment));
            Assert.That(
                QmtTradingEnvironmentResolver.GetConfigurationValue(tradingEnvironment),
                Is.EqualTo(configurationValue));
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
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal);

            Assert.That(
                QmtTradingEnvironmentResolver.IsOrderSubmissionAllowed(
                    QmtTradingEnvironment.Simulation,
                    utcTime),
                Is.EqualTo(expectedAllowed));
        }

        [Test]
        public void DoesNotApplySimulationSessionToLiveAccount()
        {
            Assert.That(
                QmtTradingEnvironmentResolver.IsOrderSubmissionAllowed(
                    QmtTradingEnvironment.Live,
                    new DateTime(2026, 8, 22, 4, 0, 0, DateTimeKind.Utc)),
                Is.True);
        }
    }
}
