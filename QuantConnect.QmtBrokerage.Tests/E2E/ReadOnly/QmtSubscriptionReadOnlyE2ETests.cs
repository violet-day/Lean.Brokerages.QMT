using System;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Data.Market;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.ReadOnly
{
    [TestFixture]
    [Explicit("Subscribes through a running real QMT Gateway.")]
    [Category(QmtE2ETestCategories.ReadOnly)]
    [NonParallelizable]
    public class QmtSubscriptionReadOnlyE2ETests : QmtReadOnlyE2ETestBase
    {
        [Test]
        public void CanResubscribeAfterUnsubscribe()
        {
            Context.Run("subscription-lifecycle", () =>
            {
                var configuration = Context.CreateTradeSubscriptionConfiguration();
                using (var firstEnumerator = Context.Brokerage.Subscribe(configuration, (_, _) => { }))
                {
                    Assert.That(firstEnumerator, Is.Not.Null);
                    Context.Brokerage.Unsubscribe(configuration);
                }
                using (var secondEnumerator = Context.Brokerage.Subscribe(configuration, (_, _) => { }))
                {
                    Assert.That(secondEnumerator, Is.Not.Null);
                    Context.Brokerage.Unsubscribe(configuration);
                }
                Context.WriteStage(
                    "subscription-lifecycle",
                    "ok",
                    "first_subscribe=true unsubscribe=true resubscribe=true");
            });
        }

        [Test]
        [Timeout(120000)]
        public void StreamsValidTradeTickDuringMarketHours()
        {
            if (!Context.IsMarketOpen())
            {
                Context.Skip("live-data", "Requires an open China A-share exchange session.");
            }
            Context.Run("live-data", () =>
            {
                var configuration = Context.CreateTradeSubscriptionConfiguration();
                using var dataAvailable = new ManualResetEventSlim(false);
                using var enumerator = Context.Brokerage.Subscribe(
                    configuration,
                    (_, _) => dataAvailable.Set());
                try
                {
                    Assert.That(enumerator, Is.Not.Null);
                    Assert.That(
                        dataAvailable.Wait(TimeSpan.FromSeconds(90)),
                        Is.True,
                        "QMT did not publish a trade tick within 90 seconds.");
                    Assert.That(enumerator!.MoveNext(), Is.True);
                    Assert.That(enumerator.Current, Is.TypeOf<Tick>());
                    Assert.That(enumerator.Current.Symbol, Is.EqualTo(Context.Symbol));
                    Assert.That(enumerator.Current.Value, Is.GreaterThan(0m));
                    Context.WriteStage(
                        "live-data",
                        "ok",
                        $"tick_received=true symbol={enumerator.Current.Symbol.Value} value={enumerator.Current.Value}");
                }
                finally
                {
                    Context.Brokerage.Unsubscribe(configuration);
                }
            });
        }
    }
}
