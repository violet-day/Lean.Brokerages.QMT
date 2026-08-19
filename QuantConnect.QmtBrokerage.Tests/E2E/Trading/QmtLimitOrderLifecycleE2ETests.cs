using System;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Trading
{
    [TestFixture]
    [Explicit("Places and cancels real limit orders through the QMT simulation account.")]
    [Category(QmtE2ETestCategories.TradingRepeatable)]
    [NonParallelizable]
    public class QmtLimitOrderLifecycleE2ETests
    {
        private QmtTradingTestContext _context = null!;

        [SetUp]
        public void Connect()
        {
            if (!QmtTradingTestContext.IsSimulationSessionOpen())
            {
                QmtTradingTestContext.Skip(
                    "Requires the QMT simulation session between 10:00 and 17:00 Asia/Shanghai.");
            }
            _context = QmtTradingTestContext.Connect();
        }

        [TearDown]
        public void Disconnect()
        {
            _context?.Dispose();
        }

        [Test]
        [Timeout(180000)]
        public void PlacesAndCancelsNonMarketableLimitBuy()
        {
            _context.Run(() => PlaceAndCancelNonMarketableLimitBuy());
        }

        [Test]
        [Timeout(180000)]
        public void RejectsSecondCancellation()
        {
            _context.Run(() =>
            {
                var order = PlaceAndCancelNonMarketableLimitBuy();
                _context.WriteStage(
                    "second-cancel",
                    "start",
                    $"lean_order_id={order.Id} native_order_id={order.BrokerId.FirstOrDefault()}");
                Assert.That(
                    _context.Brokerage.CancelOrder(order),
                    Is.False,
                    "QMT accepted a second cancellation for an already canceled order.");
                var orderSnapshot = _context.FindOrderSnapshot(order);
                Assert.That(orderSnapshot, Is.Not.Null);
                Assert.That(
                    QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot!.Status),
                    Is.EqualTo(OrderStatus.Canceled));
                _context.WriteStage("second-cancel", "ok", "accepted=false final_status=Canceled");
            });
        }

        private Order PlaceAndCancelNonMarketableLimitBuy()
        {
            var limitPrice = _context.GetNonMarketableBuyPriceFromHistory();
            var order = _context.CreateLimitOrder(QmtTradingTestContext.TradingQuantity, limitPrice);

            _context.WriteStage(
                "place-order",
                "start",
                $"lean_order_id={order.Id} stock_code={QmtTradingTestContext.TradingStockCode} " +
                $"quantity={order.Quantity} limit_price={limitPrice}");
            Assert.That(
                _context.Brokerage.PlaceOrder(order),
                Is.True,
                "QMT rejected the non-marketable test limit order request.");
            Assert.That(
                _context.WaitForStatus(
                    order,
                    TimeSpan.FromSeconds(30),
                    OrderStatus.Submitted,
                    OrderStatus.Invalid,
                    OrderStatus.PartiallyFilled,
                    OrderStatus.Filled),
                Is.EqualTo(OrderStatus.Submitted),
                "The test order did not reach Submitted before a terminal or fill status.");
            var nativeOrderId = _context.WaitForNativeOrderId(order, TimeSpan.FromSeconds(5));
            Assert.That(nativeOrderId, Is.Not.Null.And.Not.Empty, "QMT did not return a native order ID.");
            _context.WriteStage(
                "place-order",
                "ok",
                $"lean_order_id={order.Id} native_order_id={nativeOrderId} callback=Submitted");

            _context.WriteStage("open-order-query", "start", $"native_order_id={nativeOrderId}");
            var submittedOrderSnapshot = _context.WaitForOrderSnapshot(
                order,
                TimeSpan.FromSeconds(15),
                orderSnapshot => QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status).IsOpen());
            Assert.That(submittedOrderSnapshot, Is.Not.Null, "The submitted order was not returned by query_orders.");
            _context.WriteStage(
                "open-order-query",
                "ok",
                $"native_order_id={nativeOrderId} status={submittedOrderSnapshot!.Status}");

            _context.WriteStage("cancel-order", "start", $"native_order_id={nativeOrderId}");
            Assert.That(
                _context.Brokerage.CancelOrder(order),
                Is.True,
                "QMT rejected the test cancellation request.");
            Assert.That(
                _context.WaitForStatus(
                    order,
                    TimeSpan.FromSeconds(30),
                    OrderStatus.Canceled,
                    OrderStatus.Invalid,
                    OrderStatus.PartiallyFilled,
                    OrderStatus.Filled),
                Is.EqualTo(OrderStatus.Canceled),
                "The test order did not reach Canceled before a terminal or fill status.");
            _context.WriteStage("cancel-order", "ok", $"native_order_id={nativeOrderId} callback=Canceled");

            var canceledOrderSnapshot = _context.WaitForOrderSnapshot(
                order,
                TimeSpan.FromSeconds(15),
                orderSnapshot =>
                    QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status) == OrderStatus.Canceled);
            Assert.That(canceledOrderSnapshot, Is.Not.Null, "query_orders did not report the order as canceled.");
            Assert.That(
                _context.Brokerage.GetOpenOrders().Any(openOrder => openOrder.BrokerId.Contains(nativeOrderId!)),
                Is.False,
                "The canceled order is still returned as open.");
            _context.WriteStage(
                "final-order-query",
                "ok",
                $"native_order_id={nativeOrderId} status=Canceled open_order=false");
            return order;
        }
    }
}
