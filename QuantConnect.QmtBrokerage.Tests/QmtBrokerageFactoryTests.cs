using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtBrokerageFactoryTests
    {
        [OneTimeSetUp]
        public void SetUpMarketMetadataDatabases()
        {
            var repositoryDirectory = Path.GetFullPath(
                Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", ".."));
            var leanDataDirectory = Path.Combine(
                Directory.GetParent(repositoryDirectory)!.FullName,
                "Lean",
                "Data");
            Config.Set("data-folder", leanDataDirectory);
            Globals.Reset();
            MarketHoursDatabase.Reset();
            SymbolPropertiesDatabase.Reset();
        }

        [SetUp]
        public void SetUp()
        {
            Composer.Instance.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            Composer.Instance.Reset();
        }

        [Test]
        public void InitializesAlgorithmClockAndReportsCnyBaseCurrency()
        {
            var algorithm = new QCAlgorithm();
            var job = new LiveNodePacket
            {
                BrokerageData = new Dictionary<string, string>
                {
                    { "qmt-gateway-host", "127.0.0.1" },
                    { "qmt-gateway-port", "17890" },
                    { "qmt-account-id", "test-account" },
                    { "qmt-request-timeout", "10" }
                }
            };
            var utcTimeBeforeBrokerageCreation = DateTime.UtcNow;

            using var factory = new QmtBrokerageFactory();
            using var brokerage = factory.CreateBrokerage(job, algorithm);

            var utcTimeAfterBrokerageCreation = DateTime.UtcNow;
            Assert.That(
                algorithm.UtcTime,
                Is.InRange(utcTimeBeforeBrokerageCreation, utcTimeAfterBrokerageCreation));
            Assert.That(brokerage.AccountBaseCurrency, Is.EqualTo(QmtMarket.AccountCurrency));
        }
    }
}
