using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    [Explicit("Requires a running real QMT Gateway and QMT_E2E_ACCOUNT_ID.")]
    public class QmtReadOnlyE2ETests
    {
        private const string StockCode = "600000.SH";
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();
        private QmtGatewayClient _gatewayClient = null!;
        private QmtBrokerage _brokerage = null!;

        [SetUp]
        public void Connect()
        {
            var accountId = Environment.GetEnvironmentVariable("QMT_E2E_ACCOUNT_ID");
            Assert.That(accountId, Is.Not.Null.And.Not.Empty);

            var gatewayHost = Environment.GetEnvironmentVariable("QMT_E2E_GATEWAY_HOST") ?? "127.0.0.1";
            var gatewayPortText = Environment.GetEnvironmentVariable("QMT_E2E_GATEWAY_PORT") ?? "17890";
            Assert.That(int.TryParse(gatewayPortText, out var gatewayPort), Is.True);

            _gatewayClient = new QmtGatewayClient(
                gatewayHost,
                gatewayPort,
                accountId!,
                TimeSpan.FromSeconds(10));
            var algorithm = new QCAlgorithm();
            _brokerage = new QmtBrokerage(
                _gatewayClient,
                algorithm.Transactions,
                localTradingEnabled: false);
            _brokerage.Connect();

            Assert.That(_brokerage.IsConnected, Is.True);
            Assert.That(_gatewayClient.ServerInformation?.AccountId, Is.EqualTo(accountId));
            Assert.That(_gatewayClient.ServerInformation?.TradingEnabled, Is.False);
            WriteEvidence("connect", "account_match=true trading_enabled=false");
        }

        [TearDown]
        public void Disconnect()
        {
            _brokerage?.Dispose();
        }

        [Test]
        [Timeout(180000)]
        public void RunsReadOnlyBrokerageEndToEnd()
        {
            var cashBalances = _brokerage.GetCashBalance();
            var holdings = _brokerage.GetAccountHoldings();
            var openOrders = _brokerage.GetOpenOrders();
            Assert.That(cashBalances.Count(cash => cash.Currency == "CNY"), Is.EqualTo(1));
            Assert.That(holdings, Is.Not.Null);
            Assert.That(openOrders, Is.Not.Null);
            WriteEvidence(
                "account",
                $"cash_accounts={cashBalances.Count} holdings={holdings.Count} open_orders={openOrders.Count}");

            var symbol = _symbolMapper.GetLeanSymbol(
                StockCode,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            var dailyHistory = GetHistory(symbol, Resolution.Daily, TimeSpan.FromDays(15));
            var minuteHistory = GetHistory(symbol, Resolution.Minute, TimeSpan.FromDays(7));
            Assert.That(dailyHistory, Has.Count.GreaterThanOrEqualTo(5));
            Assert.That(minuteHistory, Has.Count.GreaterThanOrEqualTo(5));
            Assert.That(dailyHistory.Select(bar => bar.EndTime), Is.Ordered);
            Assert.That(minuteHistory.Select(bar => bar.EndTime), Is.Ordered);
            WriteEvidence(
                "history",
                $"daily_bars={dailyHistory.Count} minute_bars={minuteHistory.Count} ordered=true");

            var subscriptionConfiguration = new SubscriptionDataConfig(
                typeof(Tick),
                symbol,
                Resolution.Tick,
                TimeZones.Shanghai,
                TimeZones.Shanghai,
                false,
                false,
                false,
                false,
                TickType.Trade);
            using var dataAvailable = new ManualResetEventSlim(false);
            using var enumerator = _brokerage.Subscribe(
                subscriptionConfiguration,
                (_, _) => dataAvailable.Set());
            Assert.That(enumerator, Is.Not.Null);
            WriteEvidence("subscription", "subscribed=true");

            try
            {
                var chinaTime = DateTime.UtcNow.ConvertFromUtc(TimeZones.Shanghai);
                if (IsChinaMarketOpen(chinaTime))
                {
                    Assert.That(
                        dataAvailable.Wait(TimeSpan.FromSeconds(90)),
                        Is.True,
                        "QMT did not publish a trade tick within 90 seconds.");
                    Assert.That(enumerator!.MoveNext(), Is.True);
                    Assert.That(enumerator.Current, Is.TypeOf<Tick>());
                    WriteEvidence("live-data", "tick_received=true");
                }
                else
                {
                    WriteEvidence("live-data", "reason=market_closed", "skipped");
                }
            }
            finally
            {
                _brokerage.Unsubscribe(subscriptionConfiguration);
            }
            WriteEvidence("subscription", "unsubscribed=true");

            _brokerage.Disconnect();
            Assert.That(_brokerage.IsConnected, Is.False);
            _brokerage.Connect();
            Assert.That(_brokerage.IsConnected, Is.True);
            Assert.That(_brokerage.GetCashBalance(), Is.Not.Empty);
            WriteEvidence("reconnect", "account_query=ok");
            WriteEvidence("complete", "trading=disabled");
        }

        private System.Collections.Generic.List<BaseData> GetHistory(
            Symbol symbol,
            Resolution resolution,
            TimeSpan duration)
        {
            var endTimeUtc = DateTime.UtcNow;
            var historyRequest = new HistoryRequest(
                endTimeUtc.Subtract(duration),
                endTimeUtc,
                typeof(TradeBar),
                symbol,
                resolution,
                SecurityExchangeHours.AlwaysOpen(TimeZones.Shanghai),
                TimeZones.Shanghai,
                null,
                false,
                false,
                DataNormalizationMode.Raw,
                TickType.Trade);
            return _brokerage.GetHistory(historyRequest).ToList();
        }

        private static bool IsChinaMarketOpen(DateTime chinaTime)
        {
            if (chinaTime.DayOfWeek == DayOfWeek.Saturday ||
                chinaTime.DayOfWeek == DayOfWeek.Sunday)
            {
                return false;
            }

            var timeOfDay = chinaTime.TimeOfDay;
            return (timeOfDay >= TimeSpan.FromHours(9.5) && timeOfDay < TimeSpan.FromHours(11.5)) ||
                (timeOfDay >= TimeSpan.FromHours(13) && timeOfDay < TimeSpan.FromHours(15));
        }

        private static void WriteEvidence(
            string stage,
            string details,
            string status = "ok")
        {
            TestContext.Progress.WriteLine($"[qmt-e2e] stage={stage} status={status} {details}");
        }
    }
}
