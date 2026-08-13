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

        [TestCase("600000.SH")]
        [TestCase("000001.SZ")]
        [TestCase("430047.BJ")]
        public void RoundTripsQmtEquitySymbols(string brokerageSymbol)
        {
            var symbol = _symbolMapper.GetLeanSymbol(
                brokerageSymbol,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);

            Assert.AreEqual(SecurityType.Equity, symbol.SecurityType);
            Assert.AreEqual(QmtSymbolMapper.MarketName, symbol.ID.Market);
            Assert.AreEqual(brokerageSymbol, _symbolMapper.GetBrokerageSymbol(symbol));
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
            Assert.AreEqual("CNY", security.SymbolProperties.QuoteCurrency);
            Assert.AreEqual(0.01m, security.SymbolProperties.MinimumPriceVariation);
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
