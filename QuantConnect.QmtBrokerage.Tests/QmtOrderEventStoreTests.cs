using System;
using System.IO;
using NUnit.Framework;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtOrderEventStoreTests
    {
        private string _temporaryDirectory = null!;
        private string _recordFilePath = null!;

        [SetUp]
        public void SetUp()
        {
            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"qmt-order-events-{Guid.NewGuid():N}");
            _recordFilePath = Path.Combine(_temporaryDirectory, "qmt-order-events.jsonl");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, true);
            }
        }

        [Test]
        public void ReturnsLatestInvestmentForMatchingAccountAndSymbol()
        {
            var symbol = CreateSymbol("603565.SH");
            var otherSymbol = CreateSymbol("600000.SH");
            var firstInvestmentTime = new DateTime(2026, 8, 24, 10, 1, 2);
            var latestInvestmentTime = new DateTime(2026, 9, 18, 9, 35, 4);

            QmtOrderEventStore.RecordInvestedAt(
                _recordFilePath,
                "account-1",
                symbol,
                firstInvestmentTime);
            QmtOrderEventStore.RecordInvestedAt(
                _recordFilePath,
                "account-2",
                symbol,
                latestInvestmentTime.AddDays(-1));
            QmtOrderEventStore.RecordInvestedAt(
                _recordFilePath,
                "account-1",
                otherSymbol,
                latestInvestmentTime.AddHours(-1));
            QmtOrderEventStore.RecordInvestedAt(
                _recordFilePath,
                "account-1",
                symbol,
                latestInvestmentTime);

            var investedAt = QmtOrderEventStore.GetInvestedAt(
                _recordFilePath,
                "account-1",
                symbol);

            Assert.That(investedAt, Is.EqualTo(latestInvestmentTime));
        }

        [Test]
        public void ReturnsNullWhenNoInvestmentWasRecorded()
        {
            var investedAt = QmtOrderEventStore.GetInvestedAt(
                _recordFilePath,
                "account-1",
                CreateSymbol("603565.SH"));

            Assert.That(investedAt, Is.Null);
        }

        private static Symbol CreateSymbol(string brokerageSymbol)
        {
            return new QmtSymbolMapper().GetLeanSymbol(
                brokerageSymbol,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
        }
    }
}
