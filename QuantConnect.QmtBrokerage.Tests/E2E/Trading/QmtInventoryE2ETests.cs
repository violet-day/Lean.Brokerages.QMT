using System;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Trading
{
    [TestFixture]
    [Explicit("Buys 100 shares through the real QMT simulation account.")]
    [Category(QmtE2ETestCategories.TradingInventory)]
    [NonParallelizable]
    public class QmtInventoryE2ETests
    {
        private QmtTradingTestContext? _context;

        [TearDown]
        public void Disconnect()
        {
            _context?.Dispose();
        }

        [Test]
        [Timeout(180000)]
        public void MarketBuyIncreasesHoldingByFilledQuantity()
        {
            if (!QmtTradingTestContext.IsSimulationSessionOpen())
            {
                QmtTradingTestContext.Skip(
                    "Requires the QMT simulation session between 10:00 and 17:00 Asia/Shanghai.");
            }
            _context = QmtTradingTestContext.Connect();
            _context.Run(() =>
            {
                var initialHoldingQuantity = _context.GetTradingHoldingQuantity();
                var order = _context.CreateMarketOrder(
                    QmtTradingTestContext.TradingQuantity,
                    QmtMarketOrderStyle.LatestPrice);
                _context.WriteStage(
                    "market-order",
                    "start",
                    $"stock_code={QmtTradingTestContext.TradingStockCode} quantity={order.Quantity} " +
                    $"market_order_style=latest-price initial_holding={initialHoldingQuantity} " +
                    "inventory_effect=t-plus-zero-buy");
                Assert.That(
                    _context.Brokerage.PlaceOrder(order),
                    Is.True,
                    "QMT rejected the market buy request.");
                Assert.That(
                    _context.WaitForStatus(
                        order,
                        TimeSpan.FromSeconds(60),
                        OrderStatus.Filled,
                        OrderStatus.Invalid,
                        OrderStatus.Canceled),
                    Is.EqualTo(OrderStatus.Filled),
                    "The market buy did not reach Filled.");
                var filledOrderSnapshot = _context.WaitForOrderSnapshot(
                    order,
                    TimeSpan.FromSeconds(15),
                    orderSnapshot =>
                        QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status) == OrderStatus.Filled);
                Assert.That(filledOrderSnapshot, Is.Not.Null, "query_orders did not report the market buy as filled.");
                Assert.That(filledOrderSnapshot!.TradedVolume, Is.EqualTo(QmtTradingTestContext.TradingQuantity));
                Assert.That(filledOrderSnapshot.TradedPrice, Is.GreaterThan(0m));

                var expectedHoldingQuantity = initialHoldingQuantity + QmtTradingTestContext.TradingQuantity;
                var finalHoldingQuantity = _context.WaitForTradingHoldingQuantity(
                    expectedHoldingQuantity,
                    TimeSpan.FromSeconds(15));
                Assert.That(
                    finalHoldingQuantity,
                    Is.EqualTo(expectedHoldingQuantity),
                    "QMT positions did not increase by the filled market-buy quantity.");
                _context.WriteStage(
                    "market-order",
                    "ok",
                    $"native_order_id={filledOrderSnapshot.OrderId} final_status=Filled " +
                    $"traded_volume={filledOrderSnapshot.TradedVolume} traded_price={filledOrderSnapshot.TradedPrice} " +
                    $"initial_holding={initialHoldingQuantity} final_holding={finalHoldingQuantity}");
            });
        }
    }
}
