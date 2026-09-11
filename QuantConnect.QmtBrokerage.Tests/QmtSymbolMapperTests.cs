using System;
using System.IO;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtSymbolMapperTests
    {
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();

        [TestCase("600000.SH", "600000")]
        [TestCase("000001.SZ", "000001")]
        [TestCase("430047.BJ", "430047")]
        public void RoundTripsQmtEquitySymbols(string brokerageSymbol, string ticker)
        {
            var symbol = _symbolMapper.GetLeanSymbol(
                brokerageSymbol,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);

            Assert.AreEqual(SecurityType.Equity, symbol.SecurityType);
            Assert.AreEqual(QmtSymbolMapper.MarketName, symbol.ID.Market);
            Assert.AreEqual(brokerageSymbol, symbol.Value);
            Assert.AreEqual(ticker, symbol.ID.Symbol);
            Assert.AreEqual(SecurityIdentifier.DefaultDate, symbol.ID.Date);
            Assert.AreEqual(brokerageSymbol, _symbolMapper.GetBrokerageSymbol(symbol));
        }

        [TestCase("600000", "SSE", "600000.SH")]
        [TestCase("600000", "SH", "600000.SH")]
        [TestCase("000001", "SZSE", "000001.SZ")]
        [TestCase("000001", "SZ", "000001.SZ")]
        [TestCase("430047", "BSE", "430047.BJ")]
        [TestCase("430047", "BJ", "430047.BJ")]
        public void CreatesCanonicalLeanSymbolFromScreenExchange(
            string ticker,
            string exchange,
            string expectedBrokerageSymbol)
        {
            var symbol = _symbolMapper.GetLeanSymbolFromExchange(ticker, exchange);

            Assert.AreEqual(SecurityType.Equity, symbol.SecurityType);
            Assert.AreEqual(QmtSymbolMapper.MarketName, symbol.ID.Market);
            Assert.AreEqual(expectedBrokerageSymbol, symbol.Value);
            Assert.AreEqual(ticker, symbol.ID.Symbol);
            Assert.AreEqual(SecurityIdentifier.DefaultDate, symbol.ID.Date);
            Assert.AreEqual(expectedBrokerageSymbol, _symbolMapper.GetBrokerageSymbol(symbol));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("60304")]
        [TestCase("6030420")]
        [TestCase("ABCDEF")]
        public void RejectsInvalidScreenTicker(string? ticker)
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                _symbolMapper.GetLeanSymbolFromExchange(ticker!, "SSE"));

            Assert.AreEqual("ticker", exception!.ParamName);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("NYSE")]
        public void RejectsMissingOrUnsupportedScreenExchange(string? exchange)
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                _symbolMapper.GetLeanSymbolFromExchange("603042", exchange!));

            Assert.AreEqual("exchange", exception!.ParamName);
        }

        [TestCase("600000", "600000.SH")]
        [TestCase("000001", "000001.SZ")]
        [TestCase("430047", "430047.BJ")]
        public void InfersExchangeFromSixDigitLeanTicker(string ticker, string expectedBrokerageSymbol)
        {
            var securityIdentifier = SecurityIdentifier.GenerateEquity(
                SecurityIdentifier.DefaultDate,
                ticker,
                QmtSymbolMapper.MarketName);
            var symbol = new Symbol(securityIdentifier, ticker);

            Assert.AreEqual(expectedBrokerageSymbol, _symbolMapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void RejectsNonEquityAndWrongMarket()
        {
            Assert.Throws<ArgumentException>(() => _symbolMapper.GetLeanSymbol(
                "600000.SH",
                SecurityType.Forex,
                QmtSymbolMapper.MarketName));

            var usaSymbol = new Symbol(
                SecurityIdentifier.GenerateEquity(SecurityIdentifier.DefaultDate, "AAPL", Market.USA),
                "AAPL");
            Assert.Throws<ArgumentException>(() => _symbolMapper.GetBrokerageSymbol(usaSymbol));
        }

        [Test]
        public void ChinaTradingCalendarIncludesExchangeHolidaysAndCoverageGuard()
        {
            Assert.Multiple(() =>
            {
                Assert.That(QmtMarket.IsTradingDay(new DateTime(2026, 8, 13)), Is.True);
                Assert.That(QmtMarket.IsTradingDay(new DateTime(2026, 2, 16)), Is.False);
                Assert.That(QmtMarket.IsTradingDay(new DateTime(2026, 10, 1)), Is.False);
                Assert.That(QmtMarket.IsTradingDay(new DateTime(2026, 8, 15)), Is.False);
                Assert.That(QmtMarket.CalendarCoverageStart, Is.EqualTo(new DateTime(2000, 1, 1)));
                Assert.That(QmtMarket.CalendarCoverageEnd, Is.EqualTo(new DateTime(2026, 12, 31)));
                Assert.Throws<InvalidOperationException>(() =>
                    QmtMarket.EnsureCalendarCovers(new DateTime(2027, 1, 1)));
                Assert.Throws<InvalidOperationException>(() =>
                    QmtMarket.IsMarketOpen(new DateTime(2027, 1, 1, 10, 0, 0)));
            });
        }

        [TestCase(2026, 8, 13, 9, 30, true)]
        [TestCase(2026, 8, 13, 11, 30, false)]
        [TestCase(2026, 8, 13, 13, 0, true)]
        [TestCase(2026, 8, 13, 15, 0, false)]
        [TestCase(2026, 10, 1, 10, 0, false)]
        public void ChinaMarketOpenUsesRegisteredExchangeHours(
            int year,
            int month,
            int day,
            int hour,
            int minute,
            bool expectedIsOpen)
        {
            Assert.That(
                QmtMarket.IsMarketOpen(new DateTime(year, month, day, hour, minute, 0)),
                Is.EqualTo(expectedIsOpen));
        }

        [Test]
        public void BrokerageModelConstructionRegistersMetadataOnce()
        {
            var repositoryDirectory = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", ".."));
            var leanDataDirectory = Path.Combine(Directory.GetParent(repositoryDirectory).FullName, "Lean", "Data");
            Config.Set("data-folder", leanDataDirectory);
            Globals.Reset();
            MarketHoursDatabase.Reset();
            SymbolPropertiesDatabase.Reset();

            _ = new QmtBrokerageModel();
            var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            var symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();
            var firstMarketHoursEntry = marketHoursDatabase.GetEntry(
                QmtMarket.Name,
                (string)null,
                SecurityType.Equity);
            var firstSymbolProperties = symbolPropertiesDatabase.GetSymbolProperties(
                QmtMarket.Name,
                null,
                SecurityType.Equity,
                QmtMarket.AccountCurrency);

            _ = new QmtBrokerageModel();
            var secondMarketHoursEntry = marketHoursDatabase.GetEntry(
                QmtMarket.Name,
                (string)null,
                SecurityType.Equity);
            var secondSymbolProperties = symbolPropertiesDatabase.GetSymbolProperties(
                QmtMarket.Name,
                null,
                SecurityType.Equity,
                QmtMarket.AccountCurrency);

            Assert.Multiple(() =>
            {
                Assert.That(secondMarketHoursEntry, Is.SameAs(firstMarketHoursEntry));
                Assert.That(secondSymbolProperties, Is.SameAs(firstSymbolProperties));
                Assert.That(firstMarketHoursEntry.ExchangeHours.TimeZone, Is.EqualTo(TimeZones.Shanghai));
                Assert.That(firstMarketHoursEntry.ExchangeHours.IsOpen(new DateTime(2026, 8, 13, 9, 30, 0), false), Is.True);
                Assert.That(firstSymbolProperties.QuoteCurrency, Is.EqualTo(QmtMarket.AccountCurrency));
                Assert.That(firstSymbolProperties.MinimumPriceVariation, Is.EqualTo(0.01m));
                Assert.That(firstSymbolProperties.LotSize, Is.EqualTo(100m));
            });
        }

        [Test]
        public void AddEquityUsesChinaMarketHoursAndProperties()
        {
            var repositoryDirectory = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", ".."));
            var leanDataDirectory = Path.Combine(Directory.GetParent(repositoryDirectory).FullName, "Lean", "Data");
            Config.Set("data-folder", leanDataDirectory);
            Globals.Reset();
            MarketHoursDatabase.Reset();
            SymbolPropertiesDatabase.Reset();
            QmtMarket.RegisterMetadata();
            Composer.Instance.AddPart<IMapFileProvider>(new EmptyMapFileProvider());
            var algorithm = new QCAlgorithm();
            var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            var symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();
            var securityService = new SecurityService(
                algorithm.Portfolio.CashBook,
                marketHoursDatabase,
                symbolPropertiesDatabase,
                algorithm,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCacheProvider(algorithm.Portfolio),
                algorithm: algorithm);
            algorithm.Securities.SetSecurityService(securityService);
            var dataPermissionManager = new DataPermissionManager();
            var dataProvider = new DefaultDataProvider();
            var dataFeed = new NullDataFeed { ShouldThrow = false };
            var dataManager = new DataManager(
                dataFeed,
                new UniverseSelection(algorithm, securityService, dataPermissionManager, dataProvider),
                algorithm,
                new TimeKeeper(DateTime.UtcNow, TimeZones.Shanghai),
                marketHoursDatabase,
                false,
                RegisteredSecurityDataTypesProvider.Null,
                dataPermissionManager);
            algorithm.SubscriptionManager.SetDataManager(dataManager);
            var security = algorithm.AddEquity("600000", Resolution.Minute, QmtSymbolMapper.MarketName);

            Assert.AreEqual("china", security.Symbol.ID.Market);
            Assert.AreEqual(TimeZones.Shanghai, security.Exchange.TimeZone);
            Assert.IsTrue(security.Exchange.Hours.IsOpen(new DateTime(2026, 8, 13, 10, 0, 0), false));
            Assert.IsFalse(security.Exchange.Hours.IsOpen(new DateTime(2026, 8, 13, 12, 0, 0), false));
            Assert.IsTrue(security.Exchange.Hours.IsOpen(new DateTime(2026, 8, 13, 14, 0, 0), false));
            Assert.IsFalse(security.Exchange.Hours.IsOpen(new DateTime(2026, 10, 1, 10, 0, 0), false));
            Assert.AreEqual("CNY", security.SymbolProperties.QuoteCurrency);
            Assert.AreEqual(0.01m, security.SymbolProperties.MinimumPriceVariation);
            Assert.AreEqual(100m, security.SymbolProperties.LotSize);
        }

        private sealed class EmptyMapFileProvider : IMapFileProvider
        {
            public void Initialize(IDataProvider dataProvider)
            {
            }

            public MapFileResolver Get(AuxiliaryDataKey auxiliaryDataKey)
            {
                return MapFileResolver.Empty;
            }
        }
    }
}
