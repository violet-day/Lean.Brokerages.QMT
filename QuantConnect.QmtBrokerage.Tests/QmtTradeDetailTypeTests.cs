using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtTradeDetailTypeTests
    {
        [Test]
        public void UsesBigQmtTradeDetailNames()
        {
            Assert.AreEqual("ACCOUNT", QmtTradeDetailType.Account);
            Assert.AreEqual("POSITION", QmtTradeDetailType.Position);
            Assert.AreEqual("ORDER", QmtTradeDetailType.Order);
            Assert.AreEqual("DEAL", QmtTradeDetailType.Deal);
        }
    }
}
