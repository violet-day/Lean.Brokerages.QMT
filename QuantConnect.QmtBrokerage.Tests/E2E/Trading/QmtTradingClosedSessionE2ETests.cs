using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;

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
                var exception = Assert.Throws<QmtOrderSubmissionException>(() =>
                    _context.Brokerage.PlaceOrder(order));
                Assert.That(exception!.ErrorCode, Is.EqualTo("MarketClosed"));
                var orderSnapshot = _context.FindOrderSnapshot(order);
                Assert.That(orderSnapshot, Is.Null, "The locally rejected limit order reached query_orders.");
                _context.WriteStage(
                    "outside-session-order",
                    "ok",
                    "error_code=MarketClosed gateway_received=false order_found=false");
            });
        }
    }
}
