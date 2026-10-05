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
        public void ReturnsFirstBuyTimeOfCurrentOpenPosition()
        {
            var symbol = CreateSymbol("603565.SH");
            var otherSymbol = CreateSymbol("600000.SH");
            var openingTime = new DateTime(2026, 8, 24, 10, 1, 2);

            QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-1",
                symbol,
                openingTime,
                100m);
            QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-2",
                "deal-2",
                symbol,
                openingTime.AddMinutes(1),
                100m);
            QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-3",
                otherSymbol,
                openingTime.AddMinutes(2),
                100m);
            QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-4",
                symbol,
                openingTime.AddMinutes(3),
                200m);
            QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-5",
                symbol,
                openingTime.AddDays(1),
                -200m);

            var investedAt = QmtOrderEventStore.GetInvestedAt(
                _recordFilePath,
                "account-1",
                symbol);

            Assert.That(investedAt, Is.EqualTo(openingTime));
        }

        [Test]
        public void ResetsInvestmentTimeAfterPositionIsClosed()
        {
            var symbol = CreateSymbol("603565.SH");
            var firstOpeningTime = new DateTime(2026, 8, 24, 10, 1, 2);
            var secondOpeningTime = new DateTime(2026, 9, 18, 9, 35, 4);

            QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-1",
                symbol,
                firstOpeningTime,
                100m);
            QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-2",
                symbol,
                firstOpeningTime.AddDays(1),
                -100m);
            QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-3",
                symbol,
                secondOpeningTime,
                100m);

            var investedAt = QmtOrderEventStore.GetInvestedAt(
                _recordFilePath,
                "account-1",
                symbol);

            Assert.That(investedAt, Is.EqualTo(secondOpeningTime));
        }

        [Test]
        public void IgnoresDuplicateDeal()
        {
            var symbol = CreateSymbol("603565.SH");
            var openingTime = new DateTime(2026, 8, 24, 10, 1, 2);

            var firstRecordAdded = QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-1",
                symbol,
                openingTime,
                100m);
            var duplicateRecordAdded = QmtOrderEventStore.RecordDeal(
                _recordFilePath,
                "account-1",
                "deal-1",
                symbol,
                openingTime,
                100m);

            Assert.Multiple(() =>
            {
                Assert.That(firstRecordAdded, Is.True);
                Assert.That(duplicateRecordAdded, Is.False);
                Assert.That(File.ReadLines(_recordFilePath), Has.Exactly(1).Items);
            });
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
