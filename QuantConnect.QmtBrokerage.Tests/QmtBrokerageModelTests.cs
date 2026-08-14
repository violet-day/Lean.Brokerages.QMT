using System;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtBrokerageModelTests
    {
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();

        [TestCase(OrderType.Market)]
        [TestCase(OrderType.Limit)]
        public void AcceptsSupportedAshareOrders(OrderType orderType)
        {
            var symbol = _symbolMapper.GetLeanSymbol("600000.SH", SecurityType.Equity, QmtSymbolMapper.MarketName);
            var security = CreateSecurity(symbol);
            Order order = orderType == OrderType.Market
                ? new MarketOrder(symbol, 100, DateTime.UtcNow)
                : new LimitOrder(symbol, 100, 10m, DateTime.UtcNow);

            var accepted = new QmtBrokerageModel().CanSubmitOrder(security, order, out var message);

            Assert.IsTrue(accepted);
            Assert.IsNull(message);
        }

        [Test]
        public void RejectsUnsupportedSecurityAndOrderType()
        {
            var usaSymbol = new Symbol(
                SecurityIdentifier.GenerateEquity(SecurityIdentifier.DefaultDate, "AAPL", Market.USA),
                "AAPL");
            var usaSecurity = CreateSecurity(usaSymbol);
            var usaOrder = new MarketOrder(usaSymbol, 100, DateTime.UtcNow);
            var model = new QmtBrokerageModel();

            Assert.IsFalse(model.CanSubmitOrder(usaSecurity, usaOrder, out var wrongMarketMessage));
            Assert.AreEqual("UnsupportedSecurity", wrongMarketMessage.Code);

            var qmtSymbol = _symbolMapper.GetLeanSymbol("600000.SH", SecurityType.Equity, QmtSymbolMapper.MarketName);
            var qmtSecurity = CreateSecurity(qmtSymbol);
            var stopOrder = new StopMarketOrder(qmtSymbol, 100, 9m, DateTime.UtcNow);
            Assert.IsFalse(model.CanSubmitOrder(qmtSecurity, stopOrder, out var orderTypeMessage));
            Assert.AreEqual("UnsupportedOrderType", orderTypeMessage.Code);
        }

        [Test]
        public void RejectsFractionalSharesAndUpdates()
        {
            var symbol = _symbolMapper.GetLeanSymbol("600000.SH", SecurityType.Equity, QmtSymbolMapper.MarketName);
            var security = CreateSecurity(symbol);
            var order = new MarketOrder(symbol, 1.5m, DateTime.UtcNow);
            var model = new QmtBrokerageModel();

            Assert.IsFalse(model.CanSubmitOrder(security, order, out var quantityMessage));
            Assert.AreEqual("InvalidQuantity", quantityMessage.Code);
            Assert.IsFalse(model.CanUpdateOrder(security, order, null, out var updateMessage));
            Assert.AreEqual("UpdateNotSupported", updateMessage.Code);
        }

        [Test]
        public void UsesCashAccountAndOneTimesLeverage()
        {
            var symbol = _symbolMapper.GetLeanSymbol("600000.SH", SecurityType.Equity, QmtSymbolMapper.MarketName);
            var security = CreateSecurity(symbol);
            var model = new QmtBrokerageModel();

            Assert.AreEqual(AccountType.Cash, model.AccountType);
            Assert.AreEqual(QmtSymbolMapper.MarketName, model.DefaultMarkets[SecurityType.Equity]);
            Assert.AreEqual(1m, model.GetLeverage(security));
        }

        [Test]
        public void DefaultBenchmarkDoesNotAddUsdSecurity()
        {
            var benchmark = new QmtBrokerageModel().GetBenchmark(null);

            Assert.AreEqual(0m, benchmark.Evaluate(DateTime.UtcNow));
        }

        private static Security CreateSecurity(Symbol symbol)
        {
            return new Security(
                SecurityExchangeHours.AlwaysOpen(TimeZones.Shanghai),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    symbol,
                    Resolution.Minute,
                    TimeZones.Shanghai,
                    TimeZones.Shanghai,
                    false,
                    false,
                    false),
                new Cash("CNY", 0m, 1m),
                SymbolProperties.GetDefault("CNY"),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache());
        }
    }
}
