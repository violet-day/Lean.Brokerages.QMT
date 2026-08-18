using System;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Trading
{
    [TestFixture]
    [Explicit("Submits an expected-to-fail order outside the QMT simulation session.")]
    [Category(QmtE2ETestCategories.TradingRepeatable)]
    [NonParallelizable]
    public class QmtTradingClosedSessionE2ETests
    {
        private QmtTradingTestContext _context = null!;

        [SetUp]
        public void Connect()
        {
            Assume.That(
                QmtTradingTestContext.IsSimulationSessionOpen(),
                Is.False,
                "Requires a time outside the QMT simulation session 10:00-17:00 Asia/Shanghai.");
            _context = QmtTradingTestContext.Connect();
        }

        [TearDown]
        public void Disconnect()
        {
            _context?.Dispose();
        }

        [Test]
        [Timeout(120000)]
        public void RejectsLimitOrderOutsideSimulationHours()
        {
            _context.Run(() =>
            {
                var limitPrice = _context.GetNonMarketableBuyPriceFromDailyHistory();
                var order = _context.CreateLimitOrder(QmtTradingTestContext.TradingQuantity, limitPrice);
                _context.WriteStage(
                    "outside-session-order",
                    "start",
                    $"stock_code={QmtTradingTestContext.TradingStockCode} quantity={order.Quantity} limit_price={limitPrice}");
                var requestAccepted = _context.Brokerage.PlaceOrder(order);
                if (requestAccepted)
                {
                    Assert.That(
                        _context.WaitForStatus(
                            order,
                            TimeSpan.FromSeconds(30),
                            OrderStatus.Invalid,
                            OrderStatus.Submitted,
                            OrderStatus.PartiallyFilled,
                            OrderStatus.Filled),
                        Is.EqualTo(OrderStatus.Invalid),
                        "QMT did not reject the order outside simulation hours.");
                }
                var orderSnapshot = _context.FindOrderSnapshot(order);
                Assert.That(
                    orderSnapshot == null ||
                        QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status) == OrderStatus.Invalid,
                    Is.True,
                    "query_orders contains a non-rejected order submitted outside simulation hours.");
                if (orderSnapshot != null)
                {
                    Assert.That(
                        _context.Brokerage.GetOpenOrders().Any(openOrder =>
                            openOrder.BrokerId.Contains(orderSnapshot.OrderId)),
                        Is.False);
                }
                _context.WriteStage(
                    "outside-session-order",
                    "ok",
                    $"request_accepted={requestAccepted.ToString().ToLowerInvariant()} " +
                    $"final_status={(requestAccepted ? "Invalid" : "RejectedBeforeGateway")} open_order=false");
            });
        }
    }
}
