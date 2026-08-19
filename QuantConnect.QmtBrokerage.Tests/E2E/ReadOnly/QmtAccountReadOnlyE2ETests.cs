using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.ReadOnly
{
    [TestFixture]
    [Explicit("Queries account state through a running real QMT Gateway.")]
    [Category(QmtE2ETestCategories.ReadOnly)]
    [NonParallelizable]
    public class QmtAccountReadOnlyE2ETests : QmtReadOnlyE2ETestBase
    {
        [Test]
        public void ReturnsCnyCashBalance()
        {
            Context.Run("cash", () =>
            {
                var cashBalances = Context.Brokerage.GetCashBalance();
                Assert.That(cashBalances.Count(cash => cash.Currency == "CNY"), Is.EqualTo(1));
                Context.WriteStage("cash", "ok", $"cash_accounts={cashBalances.Count}");
            });
        }

        [Test]
        public void ReturnsMappedHoldings()
        {
            Context.Run("holdings", () =>
            {
                var holdings = Context.Brokerage.GetAccountHoldings();
                Assert.That(
                    holdings.All(holding =>
                        holding.Symbol.SecurityType == SecurityType.Equity &&
                        holding.Symbol.ID.Market == QmtSymbolMapper.MarketName &&
                        holding.Quantity != 0m),
                    Is.True);
                Context.WriteStage("holdings", "ok", $"holdings={holdings.Count} mapped=true");
            });
        }

        [Test]
        public void ReturnsMappedOpenOrders()
        {
            Context.Run("open-orders", () =>
            {
                var openOrders = Context.Brokerage.GetOpenOrders();
                Assert.That(
                    openOrders.All(order =>
                        order.Symbol.SecurityType == SecurityType.Equity &&
                        order.Symbol.ID.Market == QmtSymbolMapper.MarketName &&
                        order.Status.IsOpen()),
                    Is.True);
                Context.WriteStage("open-orders", "ok", $"open_orders={openOrders.Count} mapped=true");
            });
        }
    }
}
