using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.ReadOnly
{
    [TestFixture]
    [Explicit("Sends concurrent queries through a running real QMT Gateway.")]
    [Category(QmtE2ETestCategories.ReadOnly)]
    [NonParallelizable]
    public class QmtGatewayConcurrencyE2ETests : QmtReadOnlyE2ETestBase
    {
        [Test]
        public Task CorrelatesConcurrentAccountPositionAndOrderResponses()
        {
            return Context.RunAsync("concurrent-queries", async () =>
            {
                var accountRequestTask = Context.GatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryAccount);
                var positionsRequestTask = Context.GatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryPositions);
                var ordersRequestTask = Context.GatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryOrders);
                await Task.WhenAll(accountRequestTask, positionsRequestTask, ordersRequestTask);

                Assert.That(accountRequestTask.Result.ToPayload<QmtQueryAccountPayload>().Accounts, Is.Not.Empty);
                Assert.That(positionsRequestTask.Result.ToPayload<QmtQueryPositionsPayload>().Positions, Is.Not.Null);
                Assert.That(ordersRequestTask.Result.ToPayload<QmtQueryOrdersPayload>().Orders, Is.Not.Null);
                Context.WriteStage("concurrent-queries", "ok", "account=ok positions=ok orders=ok");
            });
        }
    }
}
