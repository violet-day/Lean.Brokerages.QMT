using System;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.ReadOnly
{
    [TestFixture]
    [Explicit("Queries history through a running real QMT Gateway.")]
    [Category(QmtE2ETestCategories.ReadOnly)]
    [NonParallelizable]
    public class QmtHistoryReadOnlyE2ETests : QmtReadOnlyE2ETestBase
    {
        [Test]
        public void ReturnsOrderedDailyHistory()
        {
            AssertOrderedHistory(Resolution.Daily, TimeSpan.FromDays(15), "daily-history");
        }

        [Test]
        public void ReturnsOrderedMinuteHistory()
        {
            AssertOrderedHistory(Resolution.Minute, TimeSpan.FromDays(7), "minute-history");
        }

        private void AssertOrderedHistory(Resolution resolution, TimeSpan duration, string operation)
        {
            Context.Run(operation, () =>
            {
                var history = Context.GetHistory(resolution, duration);
                Assert.That(history, Has.Count.GreaterThanOrEqualTo(5));
                Assert.That(history.Select(bar => bar.EndTime), Is.Ordered);
                Context.WriteStage(
                    operation,
                    "ok",
                    $"resolution={resolution} bars={history.Count} ordered=true");
            });
        }
    }
}
