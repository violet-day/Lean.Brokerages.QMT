using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.ReadOnly
{
    [TestFixture]
    [Explicit("Reconnects through a running real QMT Gateway.")]
    [Category(QmtE2ETestCategories.ReadOnly)]
    [NonParallelizable]
    public class QmtConnectionReadOnlyE2ETests : QmtReadOnlyE2ETestBase
    {
        [Test]
        public void ReconnectsAndQueriesAccount()
        {
            Context.Run("connection-reopen", () =>
            {
                Context.Brokerage.Disconnect();
                Assert.That(Context.Brokerage.IsConnected, Is.False);
                Context.Brokerage.Connect();
                Assert.That(Context.Brokerage.IsConnected, Is.True);
                Assert.That(Context.Brokerage.GetCashBalance(), Is.Not.Empty);
                Context.WriteStage("connection-reopen", "ok", "account_query=ok");
            });
        }
    }
}
