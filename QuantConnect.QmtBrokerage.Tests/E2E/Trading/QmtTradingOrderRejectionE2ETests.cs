using System;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Trading
{
    [TestFixture]
    [Explicit("Validates rejected orders against the current real QMT Gateway connection.")]
    [Category(QmtE2ETestCategories.TradingRepeatable)]
    [NonParallelizable]
    public class QmtTradingOrderRejectionE2ETests
    {
        private QmtTradingTestContext _context = null!;

        [SetUp]
        public void Connect()
        {
            _context = QmtTradingTestContext.Connect();
        }

        [TearDown]
        public void Disconnect()
        {
            _context?.Dispose();
        }

        [Test]
        public void RejectsZeroQuantity()
        {
            _context.Run(() =>
            {
                var order = new MarketOrder(_context.TradingSymbol, 0m, DateTime.UtcNow);
                Assert.That(_context.Brokerage.PlaceOrder(order), Is.False);
            });
        }

        [Test]
        public void RejectsFractionalQuantity()
        {
            _context.Run(() =>
            {
                var order = new MarketOrder(_context.TradingSymbol, 0.5m, DateTime.UtcNow);
                Assert.That(_context.Brokerage.PlaceOrder(order), Is.False);
            });
        }

        [Test]
        public void RejectsUnsupportedOrderUpdate()
        {
            _context.Run(() =>
            {
                var order = new LimitOrder(_context.TradingSymbol, 100m, 1m, DateTime.UtcNow);
                Assert.That(_context.Brokerage.UpdateOrder(order), Is.False);
            });
        }

        [Test]
        public void RejectsCancellationWithoutNativeOrderId()
        {
            _context.Run(() =>
            {
                var order = new LimitOrder(_context.TradingSymbol, 100m, 1m, DateTime.UtcNow);
                Assert.That(_context.Brokerage.CancelOrder(order), Is.False);
            });
        }
    }
}
