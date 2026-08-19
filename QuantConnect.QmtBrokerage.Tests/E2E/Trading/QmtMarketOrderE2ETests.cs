using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Trading
{
    [TestFixture]
    [Explicit("Requires the real QMT simulation account outside its order session.")]
    [Category(QmtE2ETestCategories.TradingRepeatable)]
    [NonParallelizable]
    public class QmtMarketOrderE2ETests
    {
        private QmtTradingTestContext? _context;

        [TearDown]
        public void Disconnect()
        {
            _context?.Dispose();
        }

        [Test]
        [Timeout(120000)]
        public void RejectsExplicitMarketStyleOutsideSimulationHours()
        {
            if (QmtTradingTestContext.IsSimulationSessionOpen())
            {
                QmtTradingTestContext.Skip(
                    "Requires a time outside the QMT simulation session 10:00-17:00 Asia/Shanghai.");
            }
            _context = QmtTradingTestContext.Connect();
            _context.Run(() =>
            {
                var order = _context.CreateMarketOrder(
                    QmtTradingTestContext.TradingQuantity,
                    QmtMarketOrderStyle.LatestPrice);
                _context.WriteStage(
                    "market-order",
                    "start",
                    $"stock_code={QmtTradingTestContext.TradingStockCode} quantity={order.Quantity} " +
                    "market_order_style=latest-price");

                var exception = Assert.Throws<QmtOrderSubmissionException>(() =>
                    _context.Brokerage.PlaceOrder(order));

                Assert.That(exception!.ErrorCode, Is.EqualTo("MarketClosed"));
                Assert.That(
                    _context.FindOrderSnapshot(order),
                    Is.Null,
                    "The locally rejected market order reached query_orders.");
                _context.WriteStage(
                    "market-order",
                    "ok",
                    "error_code=MarketClosed order_found=false");
            });
        }
    }
}
