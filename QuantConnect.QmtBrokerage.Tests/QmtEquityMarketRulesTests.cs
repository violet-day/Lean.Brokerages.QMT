using System;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtEquityMarketRulesTests
    {
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();

        [TestCase("688001.SH", 200)]
        [TestCase("689001.SH", 200)]
        [TestCase("600000.SH", 100)]
        [TestCase("300001.SZ", 100)]
        [TestCase("920037.BJ", 100)]
        [TestCase("688001.SZ", 100)]
        public void ReturnsMinimumBuyOrderQuantity(
            string brokerageSymbol,
            int expectedMinimumBuyOrderQuantity)
        {
            var symbol = GetLeanSymbol(brokerageSymbol);
            var expectedMinimumBuyOrderQuantityAsDecimal = (decimal)expectedMinimumBuyOrderQuantity;

            Assert.AreEqual(
                expectedMinimumBuyOrderQuantityAsDecimal,
                QmtEquityMarketRules.GetMinimumBuyOrderQuantity(symbol));
        }

        [TestCase("920037.BJ", 3000)]
        [TestCase("430047.BJ", 3000)]
        [TestCase("688001.SH", 2000)]
        [TestCase("689001.SH", 2000)]
        [TestCase("300001.SZ", 2000)]
        [TestCase("301001.SZ", 2000)]
        [TestCase("600000.SH", 1000)]
        [TestCase("002792.SZ", 1000)]
        [TestCase("300001.SH", 1000)]
        public void ReturnsDailyPriceLimitPercentage(
            string brokerageSymbol,
            int expectedDailyPriceLimitBasisPoints)
        {
            var symbol = GetLeanSymbol(brokerageSymbol);
            var expectedDailyPriceLimitPercentage = expectedDailyPriceLimitBasisPoints / 10000m;

            Assert.AreEqual(
                expectedDailyPriceLimitPercentage,
                QmtEquityMarketRules.GetDailyPriceLimitPercentage(symbol));
        }

        [Test]
        public void RejectsNullAndNonQmtSymbols()
        {
            var usaSecurityIdentifier = SecurityIdentifier.GenerateEquity(
                SecurityIdentifier.DefaultDate,
                "AAPL",
                Market.USA);
            var usaSymbol = new Symbol(usaSecurityIdentifier, "AAPL");

            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentNullException>(() =>
                    QmtEquityMarketRules.GetMinimumBuyOrderQuantity(null!));
                Assert.Throws<ArgumentNullException>(() =>
                    QmtEquityMarketRules.GetDailyPriceLimitPercentage(null!));
                Assert.Throws<ArgumentException>(() =>
                    QmtEquityMarketRules.GetMinimumBuyOrderQuantity(usaSymbol));
                Assert.Throws<ArgumentException>(() =>
                    QmtEquityMarketRules.GetDailyPriceLimitPercentage(usaSymbol));
            });
        }

        private Symbol GetLeanSymbol(string brokerageSymbol)
        {
            return _symbolMapper.GetLeanSymbol(
                brokerageSymbol,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
        }
    }
}
