using System;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Trading
{
    [TestFixture]
    [Explicit("Submits latest-price market orders through the current real QMT simulation account.")]
    [NonParallelizable]
    public class QmtTradingMarketOrderE2ETests
    {
        private QmtTradingTestContext? _context;

        [TearDown]
        public void Disconnect()
        {
            _context?.Dispose();
        }

        [Test]
        [Category(QmtE2ETestCategories.TradingRepeatable)]
        [Timeout(120000)]
        public void RejectsLatestPriceMarketBuyOutsideSimulationHours()
        {
            Assume.That(
                QmtTradingTestContext.IsSimulationSessionOpen(),
                Is.False,
                "Requires a time outside the QMT simulation session 10:00-17:00 Asia/Shanghai.");
            _context = QmtTradingTestContext.Connect();
            _context.Run(() =>
            {
                var order = _context.CreateMarketOrder(QmtTradingTestContext.TradingQuantity);
                _context.WriteStage(
                    "market-order",
                    "start",
                    $"stock_code={QmtTradingTestContext.TradingStockCode} quantity={order.Quantity} " +
                    "market_order_style=latest-price qmt_price_type=5");
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
                        "QMT did not reject the latest-price market order outside simulation hours.");
                }
                var orderSnapshot = _context.FindOrderSnapshot(order);
                Assert.That(
                    orderSnapshot == null ||
                        QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status) == OrderStatus.Invalid,
                    Is.True,
                    "query_orders contains a non-rejected latest-price market order submitted outside simulation hours.");
                _context.WriteStage(
                    "market-order",
                    "ok",
                    $"request_accepted={requestAccepted.ToString().ToLowerInvariant()} " +
                    $"final_status={(requestAccepted ? "Invalid" : "RejectedBeforeGateway")}");
            });
        }

        [Test]
        [Category(QmtE2ETestCategories.TradingInventory)]
        [Timeout(180000)]
        public void FillsLatestPriceMarketBuyDuringSimulationHours()
        {
            Assume.That(
                QmtTradingTestContext.IsSimulationSessionOpen(),
                Is.True,
                "Requires the QMT simulation session between 10:00 and 17:00 Asia/Shanghai.");
            _context = QmtTradingTestContext.Connect();
            _context.Run(() =>
            {
                var order = _context.CreateMarketOrder(QmtTradingTestContext.TradingQuantity);
                _context.WriteStage(
                    "market-order",
                    "start",
                    $"stock_code={QmtTradingTestContext.TradingStockCode} quantity={order.Quantity} " +
                    "market_order_style=latest-price qmt_price_type=5 inventory_effect=t-plus-zero-buy");
                Assert.That(
                    _context.Brokerage.PlaceOrder(order),
                    Is.True,
                    "QMT rejected the latest-price market buy request.");
                Assert.That(
                    _context.WaitForStatus(
                        order,
                        TimeSpan.FromSeconds(60),
                        OrderStatus.Filled,
                        OrderStatus.Invalid,
                        OrderStatus.Canceled),
                    Is.EqualTo(OrderStatus.Filled),
                    "The latest-price market buy did not reach Filled.");
                var filledOrderSnapshot = _context.WaitForOrderSnapshot(
                    order,
                    TimeSpan.FromSeconds(15),
                    orderSnapshot =>
                        QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status) == OrderStatus.Filled);
                Assert.That(filledOrderSnapshot, Is.Not.Null, "query_orders did not report the market buy as filled.");
                Assert.That(filledOrderSnapshot!.TradedVolume, Is.EqualTo(QmtTradingTestContext.TradingQuantity));
                Assert.That(filledOrderSnapshot.TradedPrice, Is.GreaterThan(0m));
                _context.WriteStage(
                    "market-order",
                    "ok",
                    $"native_order_id={filledOrderSnapshot.OrderId} final_status=Filled " +
                    $"traded_volume={filledOrderSnapshot.TradedVolume} traded_price={filledOrderSnapshot.TradedPrice}");
            });
        }
    }
}
