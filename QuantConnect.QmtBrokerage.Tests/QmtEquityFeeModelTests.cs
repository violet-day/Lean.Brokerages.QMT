using System;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtEquityFeeModelTests
    {
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();

        [Test]
        public void ChargesDefaultShenzhenRoundTripFees()
        {
            var security = CreateSecurity("003043.SZ");
            var feeModel = new QmtEquityFeeModel();

            security.SetMarketPrice(new Tick(DateTime.UtcNow, security.Symbol, 81.99m, 81.99m));
            var buyFee = GetFee(feeModel, security, 100);
            security.SetMarketPrice(new Tick(DateTime.UtcNow, security.Symbol, 82.29m, 82.29m));
            var sellFee = GetFee(feeModel, security, -100);

            Assert.Multiple(() =>
            {
                Assert.That(buyFee.Amount, Is.EqualTo(5.08199m));
                Assert.That(sellFee.Amount, Is.EqualTo(9.19679m));
                Assert.That(buyFee.Currency, Is.EqualTo(QmtMarket.AccountCurrency));
                Assert.That(sellFee.Currency, Is.EqualTo(QmtMarket.AccountCurrency));
            });
        }

        [TestCase("600000.SH")]
        [TestCase("000001.SZ")]
        [TestCase("920037.BJ")]
        public void AppliesTransferFeeToBuysInEveryMarket(string brokerageSymbol)
        {
            var security = CreateSecurity(brokerageSymbol);
            security.SetMarketPrice(new Tick(DateTime.UtcNow, security.Symbol, 100m, 100m));

            var fee = GetFee(new QmtEquityFeeModel(0.001m), security, 100);

            Assert.That(fee.Amount, Is.EqualTo(10.1m));
        }

        [TestCase("600000.SH")]
        [TestCase("000001.SZ")]
        [TestCase("920037.BJ")]
        public void ChargesStampDutyAndTransferFeeOnSells(string brokerageSymbol)
        {
            var security = CreateSecurity(brokerageSymbol);
            security.SetMarketPrice(new Tick(DateTime.UtcNow, security.Symbol, 100m, 100m));

            var fee = GetFee(new QmtEquityFeeModel(0.001m), security, -100);

            Assert.That(fee.Amount, Is.EqualTo(15.1m));
        }

        [Test]
        public void SupportsAccountSpecificCommissionTerms()
        {
            var security = CreateSecurity("000001.SZ");
            security.SetMarketPrice(new Tick(DateTime.UtcNow, security.Symbol, 100m, 100m));

            var fee = GetFee(new QmtEquityFeeModel(0.0001m, 0m), security, 100);

            Assert.That(fee.Amount, Is.EqualTo(1.1m));
        }

        [TestCase(-0.0001, 5)]
        [TestCase(0.00023, -1)]
        public void RejectsNegativeCommissionTerms(double commissionRate, double minimumCommission)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new QmtEquityFeeModel((decimal)commissionRate, (decimal)minimumCommission));
        }

        private Security CreateSecurity(string brokerageSymbol)
        {
            var symbol = _symbolMapper.GetLeanSymbol(
                brokerageSymbol,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
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

        private static CashAmount GetFee(
            QmtEquityFeeModel feeModel,
            Security security,
            decimal quantity)
        {
            var order = new MarketOrder(security.Symbol, quantity, DateTime.UtcNow);
            return feeModel.GetOrderFee(new OrderFeeParameters(security, order)).Value;
        }
    }
}
