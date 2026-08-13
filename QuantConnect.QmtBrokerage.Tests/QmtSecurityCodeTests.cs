using System;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtSecurityCodeTests
    {
        [TestCase("600000.SH", "600000", QmtExchange.Shanghai)]
        [TestCase("000001.SZ", "000001", QmtExchange.Shenzhen)]
        [TestCase("430047.BJ", "430047", QmtExchange.Beijing)]
        [TestCase("600000.sh", "600000", QmtExchange.Shanghai)]
        public void ParsesAndFormatsQmtStockCodes(string brokerageSymbol, string expectedTicker, QmtExchange expectedExchange)
        {
            var securityCode = QmtSecurityCode.Parse(brokerageSymbol);

            Assert.AreEqual(expectedTicker, securityCode.Ticker);
            Assert.AreEqual(expectedExchange, securityCode.Exchange);
            Assert.AreEqual(brokerageSymbol.ToUpperInvariant(), securityCode.ToString());
        }

        [TestCase("")]
        [TestCase("600000")]
        [TestCase("600000.HK")]
        [TestCase("ABCDEF.SH")]
        [TestCase("60000.SH")]
        public void RejectsInvalidQmtStockCodes(string brokerageSymbol)
        {
            Assert.Throws<ArgumentException>(() => QmtSecurityCode.Parse(brokerageSymbol));
        }
    }
}
