using NUnit.Framework;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtOrderStatusMapperTests
    {
        [TestCase(48, OrderStatus.Submitted)]
        [TestCase(49, OrderStatus.Submitted)]
        [TestCase(50, OrderStatus.Submitted)]
        [TestCase(51, OrderStatus.CancelPending)]
        [TestCase(52, OrderStatus.CancelPending)]
        [TestCase(53, OrderStatus.Canceled)]
        [TestCase(54, OrderStatus.Canceled)]
        [TestCase(55, OrderStatus.PartiallyFilled)]
        [TestCase(56, OrderStatus.Filled)]
        [TestCase(57, OrderStatus.Invalid)]
        [TestCase(86, OrderStatus.Submitted)]
        [TestCase(255, OrderStatus.None)]
        [TestCase(999, OrderStatus.None)]
        public void MapsQmtOrderStatusToLeanOrderStatus(int qmtOrderStatus, OrderStatus expectedLeanOrderStatus)
        {
            Assert.AreEqual(expectedLeanOrderStatus, QmtOrderStatusMapper.GetLeanOrderStatus(qmtOrderStatus));
        }
    }
}
