using System;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Trading
{
    [TestFixture]
    [Explicit("Buys 100 shares and verifies the real QMT simulation counter rejects their same-day sale.")]
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
        [Timeout(240000)]
        public void MarketBuyIncreasesHoldingAndSameDaySellIsRejected()
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
                var initialAvailableQuantity = _context.GetTradingAvailableQuantity();
                Assert.Multiple(() =>
                {
                    Assert.That(initialAvailableQuantity, Is.GreaterThanOrEqualTo(0m));
                    Assert.That(initialAvailableQuantity, Is.LessThanOrEqualTo(initialHoldingQuantity));
                });

                var buyOrder = _context.CreateMarketOrder(
                    QmtTradingTestContext.TradingQuantity,
                    QmtMarketOrderStyle.FiveLevelImmediateOrCancel);
                _context.WriteStage(
                    "t-plus-one-buy",
                    "start",
                    $"stock_code={QmtTradingTestContext.TradingStockCode} quantity={buyOrder.Quantity} " +
                    $"initial_holding={initialHoldingQuantity} initial_available={initialAvailableQuantity}");
                Assert.That(
                    _context.Brokerage.PlaceOrder(buyOrder),
                    Is.True,
                    "QMT rejected the T+1 setup market buy request.");
                Assert.That(
                    _context.WaitForStatus(
                        buyOrder,
                        TimeSpan.FromSeconds(60),
                        OrderStatus.Filled,
                        OrderStatus.Invalid,
                        OrderStatus.Canceled),
                    Is.EqualTo(OrderStatus.Filled),
                    "The T+1 setup market buy did not reach Filled.");
                var filledBuySnapshot = _context.WaitForOrderSnapshot(
                    buyOrder,
                    TimeSpan.FromSeconds(15),
                    orderSnapshot =>
                        QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status) == OrderStatus.Filled);
                Assert.That(filledBuySnapshot, Is.Not.Null, "query_orders did not report the T+1 setup buy as filled.");
                var confirmedBuySnapshot = filledBuySnapshot!;
                Assert.That(confirmedBuySnapshot.TradedVolume, Is.EqualTo(QmtTradingTestContext.TradingQuantity));

                var holdingQuantityAfterBuy = initialHoldingQuantity + QmtTradingTestContext.TradingQuantity;
                Assert.That(
                    _context.WaitForTradingHoldingQuantity(
                        holdingQuantityAfterBuy,
                        TimeSpan.FromSeconds(15)),
                    Is.EqualTo(holdingQuantityAfterBuy),
                    "QMT positions did not include the filled same-day buy.");
                var availableQuantityAfterBuy = _context.WaitForTradingAvailableQuantity(
                    initialAvailableQuantity,
                    TimeSpan.FromSeconds(15));
                Assert.That(
                    availableQuantityAfterBuy,
                    Is.EqualTo(initialAvailableQuantity),
                    "The same-day buy incorrectly increased the sellable position quantity.");
                _context.WriteStage(
                    "t-plus-one-buy",
                    "ok",
                    $"native_order_id={confirmedBuySnapshot.OrderId} final_status=Filled " +
                    $"holding_after_buy={holdingQuantityAfterBuy} available_after_buy={availableQuantityAfterBuy}");

                var attemptedSellQuantity = initialAvailableQuantity + QmtTradingTestContext.TradingQuantity;
                Assert.That(attemptedSellQuantity, Is.LessThanOrEqualTo(holdingQuantityAfterBuy));
                var sellOrder = _context.CreateMarketOrder(
                    -attemptedSellQuantity,
                    QmtMarketOrderStyle.FiveLevelImmediateOrCancel);
                _context.WriteStage(
                    "t-plus-one-sell",
                    "start",
                    $"stock_code={QmtTradingTestContext.TradingStockCode} quantity={sellOrder.Quantity} " +
                    $"holding={holdingQuantityAfterBuy} available={availableQuantityAfterBuy} " +
                    $"unavailable_quantity={holdingQuantityAfterBuy - availableQuantityAfterBuy}");
                Assert.That(
                    _context.Brokerage.PlaceOrder(sellOrder),
                    Is.True,
                    "The Gateway did not submit the intentional T+1 violation to QMT.");
                var rejectedSellEvent = _context.WaitForOrderEvent(
                    sellOrder,
                    TimeSpan.FromSeconds(60),
                    OrderStatus.Invalid,
                    OrderStatus.PartiallyFilled,
                    OrderStatus.Filled,
                    OrderStatus.Canceled);
                Assert.That(rejectedSellEvent, Is.Not.Null, "QMT did not return a terminal result for the same-day sell.");
                Assert.That(
                    rejectedSellEvent!.Status,
                    Is.EqualTo(OrderStatus.Invalid),
                    "QMT did not reject the sell quantity that exceeded the available T+1 position.");
                Assert.That(
                    rejectedSellEvent.Message,
                    Does.Contain("cancel_information=[COUNTER][251005][证券可用数量不足]"),
                    "QMT rejected the same-day sell for a reason other than the A-share available-position rule.");
                var confirmedRejectedSellEvent = rejectedSellEvent!;

                var rejectedSellSnapshot = _context.WaitForOrderSnapshot(
                    sellOrder,
                    TimeSpan.FromSeconds(15),
                    orderSnapshot =>
                        QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status) == OrderStatus.Invalid);
                Assert.That(rejectedSellSnapshot, Is.Not.Null, "query_orders did not report the same-day sell as Invalid.");
                var confirmedRejectedSellSnapshot = rejectedSellSnapshot!;
                Assert.That(confirmedRejectedSellSnapshot.TradedVolume, Is.EqualTo(0m));
                Assert.Multiple(() =>
                {
                    Assert.That(
                        _context.WaitForTradingHoldingQuantity(
                            holdingQuantityAfterBuy,
                            TimeSpan.FromSeconds(15)),
                        Is.EqualTo(holdingQuantityAfterBuy),
                        "The rejected same-day sell changed the total position.");
                    Assert.That(
                        _context.WaitForTradingAvailableQuantity(
                            initialAvailableQuantity,
                            TimeSpan.FromSeconds(15)),
                        Is.EqualTo(initialAvailableQuantity),
                        "The rejected same-day sell changed the available position.");
                    Assert.That(
                        _context.Brokerage.GetOpenOrders().Any(openOrder =>
                            openOrder.BrokerId.Contains(confirmedRejectedSellSnapshot.OrderId)),
                        Is.False,
                        "The rejected same-day sell is still returned as open.");
                });
                var rejectionMessage = confirmedRejectedSellEvent.Message
                    .Replace("\"", "'")
                    .Replace("\r", " ")
                    .Replace("\n", " ");
                _context.WriteStage(
                    "t-plus-one-sell",
                    "ok",
                    $"native_order_id={confirmedRejectedSellSnapshot.OrderId} final_status=Invalid traded_volume=0 " +
                    $"holding_unchanged={holdingQuantityAfterBuy} available_unchanged={initialAvailableQuantity} " +
                    $"rejection=\"{rejectionMessage}\"");
            });
        }
    }
}
