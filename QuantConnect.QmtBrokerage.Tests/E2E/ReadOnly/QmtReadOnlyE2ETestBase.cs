using NUnit.Framework;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.ReadOnly
{
    public abstract class QmtReadOnlyE2ETestBase
    {
        protected QmtReadOnlyTestContext Context { get; private set; } = null!;

        [SetUp]
        public void Connect()
        {
            Context = QmtReadOnlyTestContext.Connect();
        }

        [TearDown]
        public void Disconnect()
        {
            Context?.Dispose();
        }
    }
}
